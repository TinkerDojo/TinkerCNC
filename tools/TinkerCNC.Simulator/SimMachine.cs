using System.Globalization;

namespace TinkerCNC.Simulator;

/// <param name="Speed">Job playback speed factor (time-lapse for demos).</param>
/// <param name="StartJob">Start the demo job at startup, as if it was started on the machine itself.</param>
/// <param name="QueryReportsMd5">Whether the 0xB7 running-file reply includes the MD5 (<c>--query-path-only</c> turns it off).</param>
/// <param name="LegacyLineCounter">Report the line last handed to the planner like firmware before 2026-08-04
/// (<c>--legacy-line-counter</c>), instead of the feed move being executed.</param>
/// <param name="ManualToolChange">Wait in state <c>Tool</c> at every M6 until <c>M490.2</c>, like a machine without
/// an automatic tool changer (<c>--manual-tool-change</c>).</param>
/// <param name="PauseLiftsZ">After <c>suspend</c>, lift the head 5 mm like a machine configured with an
/// <c>after_suspend_gcode</c> (<c>--pause-lifts-z</c>).</param>
public sealed record SimulatorOptions(
    string Name, string Ip, int VideoPort, double Speed, bool StartJob, bool QueryReportsMd5, bool LegacyLineCounter,
    bool ManualToolChange, bool PauseLiftsZ)
{
    public static SimulatorOptions Parse(string[] args)
    {
        string? Arg(string key) => args.SkipWhile(a => a != key).Skip(1).FirstOrDefault();
        return new SimulatorOptions(
            Arg("--name") ?? "Simulator",
            Arg("--ip") ?? "127.0.0.1",
            int.Parse(Arg("--video-port") ?? "82"),
            double.Parse(Arg("--speed") ?? "10", System.Globalization.CultureInfo.InvariantCulture),
            args.Contains("--job"),
            !args.Contains("--query-path-only"),
            args.Contains("--legacy-line-counter"),
            args.Contains("--manual-tool-change"),
            args.Contains("--pause-lifts-z"));
    }
}

/// <summary>What command handlers can act on.</summary>
public sealed record SimContext(SimMachine Machine, JobPlayer Job, SimFileSystem Files, SimulatorOptions Options);

public readonly record struct Position(double X, double Y, double Z, double A)
{
    public static Position operator +(Position a, Position b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.A + b.A);
    public static Position operator -(Position a, Position b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.A - b.A);
    public Position Lerp(Position to, double t) => new(X + (to.X - X) * t, Y + (to.Y - Y) * t, Z + (to.Z - Z) * t, A + (to.A - A) * t);

    public string Format()
    {
        static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
        return $"{F(X)},{F(Y)},{F(Z)},{F(A)}";
    }
}

/// <summary>Simulated machine state shared by all connections.</summary>
public sealed class SimMachine
{
    private readonly object _gate = new();
    private int _clients;

    public string State { get; private set; } = "Idle";
    // Machine coordinates as on the real machine: homing goes to the far corner (0, 0, 0), everything else is negative.
    public Position MachinePosition { get; private set; } = new(-100, -120, -10, 0);
    public Position WorkOffset { get; private set; } = new(-185, -190, -60, 0);

    /// <summary>Travel in mm (machine coordinates run from −Travel to 0), as a compact machine reports it.</summary>
    public static readonly Position Travel = new(207, 206, 102, 0);

    /// <summary>
    /// Configuration answered to "config-get sd …", as read from a compact machine (only the settings the machine has).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Config = new Dictionary<string, string>
    {
        ["soft_endstop.x_min"] = "-207.0",
        ["soft_endstop.y_min"] = "-206.0",
        ["soft_endstop.z_min"] = "-102.0",
        ["coordinate.anchor1_x"] = "-190.3",
        ["coordinate.anchor1_y"] = "-194.114",
        ["coordinate.clearance_z"] = "-3.0",
    };
    public string? Alarm { get; private set; }
    public int ActiveTool { get; private set; } = 1;

    /// <summary>Compressed air (M7 / M9).</summary>
    public bool AirOn { get; set; }

    /// <summary>Tool a manual tool change waits for, or −1.</summary>
    public int TargetTool { get; private set; } = -1;

    public bool WaitingForTool => State == TinkerCNC.ManualToolChange.WaitingState;
    public bool Busy => _clients > 0;
    public bool InAlarm => Alarm is not null;

    /// <summary>
    /// Reported feed override. Playing faster than real time is what an override does, so the playback speed is
    /// reported here and remaining times and recorded operation times stay consistent.
    /// </summary>
    public double FeedOverridePercent { get; init; } = 100;

    public void ClientConnected() => Interlocked.Increment(ref _clients);
    public void ClientDisconnected() => Interlocked.Decrement(ref _clients);

    /// <summary>Status report; F is "current feed, programmed feed, override %".</summary>
    public string StatusReport(params string?[] extraFields)
    {
        lock (_gate)
        {
            var fields = new List<string?>
            {
                State, $"MPos:{MachinePosition.Format()}", $"WPos:{(MachinePosition - WorkOffset).Format()}",
                string.Create(CultureInfo.InvariantCulture, $"F:0,0,{FeedOverridePercent:0}"), "S:0,100",
                $"T:{ActiveTool},0.000,{TargetTool}", "L:0", "V:0",
            };
            fields.AddRange(extraFields);
            return "<" + string.Join('|', fields.OfType<string>()) + ">";
        }
    }

    public void SetWorkPosition(Position work)
    {
        lock (_gate) MachinePosition = work + WorkOffset;
    }

    public void SetMachinePosition(Position machine)
    {
        lock (_gate) MachinePosition = machine;
    }

    public void SetState(string state)
    {
        lock (_gate) State = state;
    }

    /// <summary>Changes state only if the machine is currently in <paramref name="from"/>.</summary>
    public void Transition(string from, string to)
    {
        lock (_gate) if (State == from) State = to;
    }

    /// <summary>M6 without a tool changer: wait in state <c>Tool</c> (firmware M490.1).</summary>
    public void RequestTool(int tool)
    {
        lock (_gate) { TargetTool = tool; State = TinkerCNC.ManualToolChange.WaitingState; }
    }

    /// <summary><c>M490.2</c>: the new tool is in, carry on; false when nothing was waiting.</summary>
    public bool ConfirmTool()
    {
        lock (_gate)
        {
            if (!WaitingForTool) return false;
            ActiveTool = TargetTool;
            TargetTool = -1;
            State = "Run";
            return true;
        }
    }

    public void RaiseAlarm(string message)
    {
        lock (_gate) { Alarm = message; State = "Alarm"; }
    }

    public void ClearAlarm()
    {
        lock (_gate) { Alarm = null; State = "Idle"; }
    }

    public void Reset()
    {
        lock (_gate) State = InAlarm ? "Alarm" : "Idle";
    }

    public void SetWorkOrigin()
    {
        lock (_gate) WorkOffset = MachinePosition;
    }

    /// <summary>G10 L2: the work offset's X/Y become these machine coordinates (an axis left out stays).</summary>
    public void SetWorkOffset(double? x, double? y)
    {
        lock (_gate) WorkOffset = WorkOffset with { X = x ?? WorkOffset.X, Y = y ?? WorkOffset.Y };
    }

    public void SetActiveTool(int tool)
    {
        lock (_gate) ActiveTool = tool;
    }

    /// <summary>
    /// Animates a move to <paramref name="target"/>, reporting <paramref name="state"/> meanwhile. While a program is
    /// paused the state stays <c>Pause</c>, as moves in between do not end the pause.
    /// </summary>
    public async Task MoveToAsync(Position target, string state)
    {
        Position start;
        string after;
        lock (_gate)
        {
            start = MachinePosition;
            after = State == JobSuspension.SuspendedState ? State : "Idle";
            State = after == JobSuspension.SuspendedState ? after : state;
        }
        const int steps = 12;
        for (var i = 1; i <= steps; i++)
        {
            lock (_gate) MachinePosition = start.Lerp(target, i / (double)steps);
            await Task.Delay(40);
        }
        SetState(after);
    }

    public Task MoveByAsync(Position delta, string state) => MoveToAsync(MachinePosition + delta, state);
}
