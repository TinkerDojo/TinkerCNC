using System.Globalization;
using System.Text.RegularExpressions;
using TinkerCNC.Protocol;

namespace TinkerCNC.Simulator;

/// <summary>One kind of text command (0xA2). Handlers are tried in order; the first match wins.</summary>
public interface ITextCommandHandler
{
    bool Matches(string command, SimContext context);
    Task HandleAsync(string command, SimContext context, IReplyChannel reply);
}

public static class TextCommandHandlers
{
    public static readonly IReadOnlyList<ITextCommandHandler> All =
    [
        new UnlockHandler(),
        new SimulateAlarmHandler(),
        new DiagnoseHandler(),
        new AlarmGateHandler(),
        new ConfirmToolHandler(),
        new AirHandler(),
        new ConfigGetHandler(),
        new SuspendHandler(),
        new ResumeHandler(),
        new BufferHandler(),
        new PlayHandler(),
        new ListHandler(),
        new WorkOffsetHandler(),
        new AbortHandler(),
        new HomeHandler(),
        new JogHandler(),
        new MachineMoveHandler(),
        new SetOriginHandler(),
        new AcknowledgeHandler(),
    ];
}

/// <summary>"$X" clears the alarm.</summary>
public sealed class UnlockHandler : ITextCommandHandler
{
    public bool Matches(string command, SimContext context) => command == "$X";

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        context.Machine.ClearAlarm();
        return reply.SendAsync(PacketType.NormalInfo, "[Caution: Unlocked]");
    }
}

/// <summary>"alarm" (simulator only) raises a hard-limit alarm.</summary>
public sealed class SimulateAlarmHandler : ITextCommandHandler
{
    private const string Message = "Simulated alarm: limit switch hit, reset needed";

    public bool Matches(string command, SimContext context) => command.Equals("alarm", StringComparison.OrdinalIgnoreCase);

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        context.Machine.RaiseAlarm(Message);
        return reply.SendAsync(PacketType.AlarmInfo, Message);
    }
}

public sealed class DiagnoseHandler : ITextCommandHandler
{
    public bool Matches(string command, SimContext context) => command == "diagnose";

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply) =>
        reply.SendAsync(PacketType.Diagnose, "{\"spindle\":0,\"vacuum\":0,\"light\":1,\"temp\":31.5}");
}

/// <summary>Rejects motion and other commands while an alarm is active.</summary>
public sealed class AlarmGateHandler : ITextCommandHandler
{
    public bool Matches(string command, SimContext context) => context.Machine.InAlarm;

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply) =>
        reply.SendAsync(PacketType.AlarmInfo, "Machine is in alarm state, unlock first");
}

/// <summary>"M490.2" ends a manual tool change wait (firmware: "Exit tool change waiting status").</summary>
public sealed class ConfirmToolHandler : ITextCommandHandler
{
    public bool Matches(string command, SimContext context) => command == TinkerCNC.ManualToolChange.ConfirmCommand;

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        if (context.Machine.ConfirmTool()) Console.WriteLine($"[job] tool T{context.Machine.ActiveTool} confirmed");
        return reply.SendAsync(PacketType.NormalInfo, "ok");
    }
}

/// <summary>
/// "M811 S.." / "M812" switch the spindle blower on and off, as on the simulated compact machine; "M7" / "M9" (compressed air on
/// other machines) are acknowledged but switch nothing, like on a real compact machine.
/// </summary>
public sealed class AirHandler : ITextCommandHandler
{
    private static readonly SpindleBlower Blower = new();
    private const string OnPrefix = "M811";

    public bool Matches(string command, SimContext context) =>
        command.StartsWith(OnPrefix, StringComparison.OrdinalIgnoreCase) || command.Equals(Blower.OffCommand, StringComparison.OrdinalIgnoreCase);

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        context.Machine.AirOn = command.StartsWith(OnPrefix, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"[air] {(context.Machine.AirOn ? "on" : "off")} ({command})");
        return reply.SendAsync(PacketType.NormalInfo, "ok");
    }
}

public sealed class HomeHandler : ITextCommandHandler
{
    public bool Matches(string command, SimContext context) => command == "$H";

    public async Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        await reply.SendAsync(PacketType.NormalInfo, "ok");
        await context.Machine.MoveToAsync(new Position(0, 0, 0, context.Machine.MachinePosition.A), "Home");
    }
}

/// <summary>"G91 G0 X.. Y.. Z.. A.." relative jog.</summary>
public sealed class JogHandler : ITextCommandHandler
{
    private const string Prefix = "G91 G0";

    public bool Matches(string command, SimContext context) => command.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public async Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        var words = command[Prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToDictionary(w => char.ToUpperInvariant(w[0]), w => double.Parse(w[1..], CultureInfo.InvariantCulture));
        double Axis(char a) => words.GetValueOrDefault(a);

        await reply.SendAsync(PacketType.NormalInfo, "ok");
        await context.Machine.MoveByAsync(new Position(Axis('X'), Axis('Y'), Axis('Z'), Axis('A')), "Jog");
    }
}

/// <summary>"G53 G0 X.. Y.. Z.." rapid move or "G53 G1 X.. Y.. Z.. F.." feed move in machine coordinates.</summary>
public sealed class MachineMoveHandler : ITextCommandHandler
{
    private static readonly string[] Prefixes = ["G53 G0", "G53 G1"];

    public bool Matches(string command, SimContext context) =>
        Prefixes.Any(p => command.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public async Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        var words = command[Prefixes[0].Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToDictionary(w => char.ToUpperInvariant(w[0]), w => double.Parse(w[1..], CultureInfo.InvariantCulture));
        var p = context.Machine.MachinePosition;
        double Axis(char a, double current) => words.TryGetValue(a, out var v) ? v : current;

        await reply.SendAsync(PacketType.NormalInfo, "ok");
        Console.WriteLine($"[move] {command}");
        await context.Machine.MoveToAsync(new Position(Axis('X', p.X), Axis('Y', p.Y), Axis('Z', p.Z), p.A), "Run");
    }
}

/// <summary>"G92 …" makes the current position the work origin.</summary>
public sealed class SetOriginHandler : ITextCommandHandler
{
    public bool Matches(string command, SimContext context) => command.StartsWith("G92", StringComparison.OrdinalIgnoreCase);

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        context.Machine.SetWorkOrigin();
        return reply.SendAsync(PacketType.NormalInfo, "ok");
    }
}

/// <summary>Anything else is acknowledged with "ok".</summary>
public sealed class AcknowledgeHandler : ITextCommandHandler
{
    public bool Matches(string command, SimContext context) => true;

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply) =>
        reply.SendAsync(PacketType.NormalInfo, "ok");
}

/// <summary>"play &lt;path&gt;" starts a job.</summary>
public sealed class PlayHandler : ITextCommandHandler
{
    private const string Prefix = "play ";

    public bool Matches(string command, SimContext context) => command.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        var path = TinkerCNC.Transfer.RemotePath.Decode(command[Prefix.Length..].Trim());
        return context.Job.Start(path)
            ? reply.SendAsync(PacketType.NormalInfo, $"Playing {path}")
            : reply.SendAsync(PacketType.NormalInfo, $"File not found: {path}");
    }
}

/// <summary>"abort" stops the job.</summary>
/// <summary>"config-get sd &lt;key&gt;" answers like the firmware, from <see cref="SimMachine.Config"/> and the pause routine option.</summary>
public sealed class ConfigGetHandler : ITextCommandHandler
{
    private const string Prefix = "config-get sd ";
    private const string LiftRoutine = "G91_G0Z5_G90";

    public bool Matches(string command, SimContext context) => command.StartsWith(Prefix, StringComparison.Ordinal);

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        var key = command[Prefix.Length..].Trim();
        var value = key == TinkerCNC.MachineConfig.AfterSuspendGCode
            ? (context.Options.PauseLiftsZ ? LiftRoutine : null)
            : SimMachine.Config.GetValueOrDefault(key);
        return reply.SendAsync(PacketType.NormalInfo, value is not null ? $"sd: {key} is set to {value}" : $"sd: {key} is not in config");
    }
}

/// <summary>"suspend" pauses the program so other commands can run.</summary>
public sealed class SuspendHandler : ITextCommandHandler
{
    public bool Matches(string command, SimContext context) => command == TinkerCNC.JobSuspension.SuspendCommand;

    public async Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        var result = await context.Job.SuspendAsync(context.Options.PauseLiftsZ);
        Console.WriteLine($"[job] {result}");
        await reply.SendAsync(PacketType.NormalInfo, result);
    }
}

/// <summary>"resume" continues a suspended program.</summary>
public sealed class ResumeHandler : ITextCommandHandler
{
    public bool Matches(string command, SimContext context) => command == TinkerCNC.JobSuspension.ResumeCommand;

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        var result = context.Job.Resume();
        Console.WriteLine($"[job] {result}");
        return reply.SendAsync(PacketType.NormalInfo, result);
    }
}

public sealed class AbortHandler : ITextCommandHandler
{
    public bool Matches(string command, SimContext context) => command.Equals("abort", StringComparison.OrdinalIgnoreCase);

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        context.Job.Stop();
        return reply.SendAsync(PacketType.NormalInfo, "ok");
    }
}

/// <summary>"buffer &lt;command&gt;" queues a command for the start of the next program.</summary>
public sealed class BufferHandler : ITextCommandHandler
{
    private const string Prefix = "buffer ";

    public bool Matches(string command, SimContext context) => command.StartsWith(Prefix, StringComparison.Ordinal);

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        var buffered = command[Prefix.Length..].Trim();
        context.Job.Buffer(buffered);
        return reply.SendAsync(PacketType.NormalInfo, $"Command buffered: {buffered}");
    }
}

/// <summary>"ls -s &lt;dir&gt;": one 0x83 packet of "name size yyyyMMddHHmmss" lines, then 0x84; 0x85 for an unknown directory.</summary>
public sealed class ListHandler : ITextCommandHandler
{
    private const string Prefix = "ls ";

    public bool Matches(string command, SimContext context) => command.StartsWith(Prefix, StringComparison.Ordinal);

    public async Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        var directory = TinkerCNC.Transfer.RemotePath.Decode(
            command[Prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(w => !w.StartsWith('-')) ?? "/sd");
        if (!context.Files.IsDirectory(directory))
        {
            await reply.SendAsync(PacketType.LoadError, "Could not open directory!\r\n");
            return;
        }
        var lines = context.Files.List(directory).Select(f =>
            $"{TinkerCNC.Transfer.RemotePath.Encode(f.Name)} {f.Size} {f.Modified.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}\r\n");
        await reply.SendAsync(PacketType.LoadInfo, string.Concat(lines));
        await reply.SendAsync(PacketType.LoadFinished, "Load directory finished.\r\n");
    }
}

/// <summary>"G10 L2 P0 X.. Y..": the work offset becomes those machine coordinates.</summary>
public sealed partial class WorkOffsetHandler : ITextCommandHandler
{
    public bool Matches(string command, SimContext context) =>
        Words(command) is { } words && words.GetValueOrDefault('G') == 10 && words.GetValueOrDefault('L') == 2;

    public Task HandleAsync(string command, SimContext context, IReplyChannel reply)
    {
        var words = Words(command)!;
        context.Machine.SetWorkOffset(words.TryGetValue('X', out var x) ? x : null, words.TryGetValue('Y', out var y) ? y : null);
        Console.WriteLine($"[origin] work offset now {context.Machine.WorkOffset.Format()} (machine)");
        return reply.SendAsync(PacketType.NormalInfo, "ok");
    }

    private static Dictionary<char, double>? Words(string command)
    {
        var matches = Word().Matches(command.ToUpperInvariant());
        return matches.Count == 0 ? null : matches
            .GroupBy(m => m.Groups[1].Value[0])
            .ToDictionary(g => g.Key, g => double.Parse(g.First().Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"([A-Z])\s*(-?\d+(?:\.\d+)?)")]
    private static partial Regex Word();
}
