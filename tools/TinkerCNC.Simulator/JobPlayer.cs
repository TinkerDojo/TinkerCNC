using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using TinkerCNC.GCode;

namespace TinkerCNC.Simulator;

/// <summary>Files on the simulated SD card.</summary>
public sealed class SimFileSystem
{
    private readonly ConcurrentDictionary<string, (byte[] Content, DateTime Modified)> _files = new(StringComparer.Ordinal)
    {
        [SampleJob.Path] = (System.Text.Encoding.UTF8.GetBytes(SampleJob.Generate()), new DateTime(2026, 1, 1, 12, 0, 0)),
    };

    public byte[]? Read(string path) => _files.TryGetValue(path, out var file) ? file.Content : null;

    public void Write(string path, byte[] content) => _files[path] = (content, DateTime.Now);

    /// <summary>The files directly in <paramref name="directory"/>: name, size and time of the last change.</summary>
    public IEnumerable<(string Name, long Size, DateTime Modified)> List(string directory)
    {
        var prefix = directory.TrimEnd('/') + "/";
        return _files
            .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && !f.Key[prefix.Length..].Contains('/'))
            .Select(f => (f.Key[prefix.Length..], (long)f.Value.Content.Length, f.Value.Modified));
    }

    public bool IsDirectory(string directory) => _files.Keys.Any(k => k.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal));

    public static string Md5Of(byte[] content) => Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();
}

/// <summary>
/// Plays a program: advances the current line along the estimated per-line times (sped up by
/// <see cref="Speed"/>), moves the simulated tool and exposes the <c>P</c> status field.
/// Like the firmware, <c>P</c> reports the G1/G2/G3 move being executed in the machine's numbering (empty and
/// over-long lines are not counted) and 0 during the automatic tool change; with <paramref name="legacyLineCounter"/>
/// it reports the current line instead, like firmware before 2026-08-04.
/// </summary>
/// With <paramref name="manualToolChange"/> every M6 waits in state <c>Tool</c> until <c>M490.2</c>.
public sealed class JobPlayer(
    SimMachine machine, SimFileSystem files, double speed, bool legacyLineCounter = false, bool manualToolChange = false)
{
    private int _lastFeedLine;
    private int _toolChangeDone; // line of the last M6 that was confirmed
    private Position? _suspendedAt;

    // States in which the program does not advance.
    private static readonly HashSet<string> HaltedStates = ["Hold", "Wait", "Pause", "Tool"];

    private CancellationTokenSource? _cts;
    private readonly ConcurrentQueue<string> _buffered = new();

    public double Speed { get; } = speed;
    public string? Path { get; private set; }
    public string? Md5 { get; private set; }
    public int Line { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public GCodeAnalysis? Analysis { get; private set; }
    public bool IsPlaying => _cts is { IsCancellationRequested: false };

    /// <summary>"P:line,percent,elapsed" while playing.</summary>
    public string? StatusField => IsPlaying && Analysis is { } a
        ? string.Create(CultureInfo.InvariantCulture, $"P:{ReportedLine(a)},{(int)(a.Times.Fraction(Line) * 100)},{(int)ElapsedSeconds}")
        : null;

    private int ReportedLine(GCodeAnalysis a) =>
        legacyLineCounter ? a.MachineLines.ToMachineLine(Line)
        : IsToolChange(a, Line) ? 0
        : a.MachineLines.ToMachineLine(_lastFeedLine);

    private static bool IsToolChange(GCodeAnalysis a, int line) => a.Segments.Any(s => s.ToolChangeSeconds > 0 && s.StartLine == line);

    /// <summary>"buffer &lt;command&gt;": runs once the next program has started, before its first line.</summary>
    public void Buffer(string command) => _buffered.Enqueue(command);

    public bool Start(string path)
    {
        if (files.Read(path) is not { } content) return false;
        EndProgram();
        Path = path;
        Md5 = SimFileSystem.Md5Of(content);
        Analysis = GCodeEstimator.Analyze(System.Text.Encoding.UTF8.GetString(content));
        Line = 1;
        _lastFeedLine = 0;
        _toolChangeDone = 0;
        _suspendedAt = null;
        ElapsedSeconds = 0;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
        return true;
    }

    /// <summary>
    /// "suspend": finish the queued moves (state Wait), store the position and pause (state Pause); with
    /// <paramref name="liftAfterwards"/> also lift the head, like a configured after_suspend_gcode.
    /// </summary>
    public async Task<string> SuspendAsync(bool liftAfterwards)
    {
        if (!IsPlaying) return "Can not suspend when not playing file!";
        if (_suspendedAt is not null) return "Already suspended!";
        machine.SetState("Wait");
        await Task.Delay(600);
        _suspendedAt = machine.MachinePosition;
        machine.SetState("Pause");
        if (liftAfterwards) machine.SetMachinePosition(_suspendedAt.Value + new Position(0, 0, 5, 0));
        return "Suspended, resume to continue playing";
    }

    /// <summary>"resume": back to the stored position, continue the program.</summary>
    public string Resume()
    {
        if (_suspendedAt is not { } position) return "Not suspended";
        machine.SetMachinePosition(position);
        _suspendedAt = null;
        machine.SetState("Run");
        return "Playing file resumed";
    }

    /// <summary>"abort" or a reset: the program ends and queued commands are dropped.</summary>
    public void Stop()
    {
        EndProgram();
        _buffered.Clear();
    }

    private void EndProgram()
    {
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>
    /// Runs the buffered commands like the firmware's automation would: probing (<c>M495</c>) first asks for the
    /// probe on a machine without a tool changer, then takes a moment; <c>M6 Tn</c> asks for that tool.
    /// </summary>
    private async Task RunBufferedAsync(CancellationToken ct)
    {
        while (_buffered.TryDequeue(out var command))
        {
            Console.WriteLine($"[auto] {command}");
            if (command.StartsWith("M495", StringComparison.OrdinalIgnoreCase))
            {
                if (machine.ActiveTool is not (0 or 9999)) await ChangeToolAsync(0, ct);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                Console.WriteLine("[auto] probing done");
            }
            else if (ToolOf(command) is { } tool)
            {
                await ChangeToolAsync(tool, ct);
            }
        }
    }

    private static int? ToolOf(string command)
    {
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 2 && words[0].Equals("M6", StringComparison.OrdinalIgnoreCase) && words[1].StartsWith('T')
            && int.TryParse(words[1][1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tool) ? tool : null;
    }

    private async Task ChangeToolAsync(int tool, CancellationToken ct)
    {
        if (!manualToolChange)
        {
            machine.SetActiveTool(tool);
            return;
        }
        Console.WriteLine($"[auto] please change the tool to: T{tool}");
        machine.RequestTool(tool);
        while (machine.WaitingForTool) await Task.Delay(100, ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var analysis = Analysis!;
        machine.SetState("Run");
        try
        {
            await RunBufferedAsync(ct);
            while (Line <= analysis.LineCount && !ct.IsCancellationRequested)
            {
                await Task.Delay(50, ct);
                if (HaltedStates.Contains(machine.State)) continue;
                if (manualToolChange && IsToolChange(analysis, Line) && _toolChangeDone != Line)
                {
                    _toolChangeDone = Line;
                    var tool = analysis.SegmentAt(Line)?.ToolNumber ?? 0;
                    Console.WriteLine($"[job] please change the tool to: T{tool}");
                    machine.RequestTool(tool);
                    continue;
                }
                ElapsedSeconds += 0.05 * Speed;
                while (Line < analysis.LineCount && analysis.Times.SecondsBefore(Line + 1) <= ElapsedSeconds)
                {
                    Line++;
                    if (analysis.IsFeedMove(Line)) _lastFeedLine = Line;
                }
                var p = analysis.EndPositions[Line - 1];
                machine.SetWorkPosition(new Position(p.X, p.Y, p.Z, p.A));
                if (Line == analysis.LineCount && ElapsedSeconds >= analysis.Times.Total) break;
            }
            Console.WriteLine($"[job] finished {Path}");
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cts = null;
            machine.SetState(machine.InAlarm ? "Alarm" : "Idle");
        }
    }
}
