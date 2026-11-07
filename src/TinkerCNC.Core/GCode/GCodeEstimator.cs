namespace TinkerCNC.GCode;

/// <summary>Machine assumptions for the time estimate.</summary>
/// <param name="RapidFeedMmPerMin">G0 rate; the file's <c>;@MKR|MAXFEEDRATE</c> is used when present.</param>
/// <param name="ToolChangeSeconds">Time added for each M6 (automatic tool change).</param>
/// <param name="DefaultFeedMmPerMin">Feed used until the program sets one.</param>
public sealed record EstimatorSettings(
    double RapidFeedMmPerMin = 3000,
    double ToolChangeSeconds = 30,
    double DefaultFeedMmPerMin = 1000)
{
    public static readonly EstimatorSettings Default = new();
}

/// <summary>Modal machine state while walking through the program.</summary>
internal sealed class EstimatorState(EstimatorSettings settings)
{
    public EstimatorSettings Settings { get; } = settings;
    public Point4 Position { get; set; }
    public bool Absolute { get; set; } = true;
    public double UnitScale { get; set; } = 1;
    public double FeedMmPerMin { get; set; }
    public IMotionMode Motion { get; set; } = MotionModes.Rapid;
    public int MotionCode { get; set; }
    public int Plane { get; set; } = ModalState.Default.Plane;
    public int WorkOffset { get; set; } = ModalState.Default.WorkOffset;
    public int? Spindle { get; set; }
    public double SpindleSpeed { get; set; }
    public bool Mist { get; set; }
    public bool Flood { get; set; }
    public int? Tool { get; set; }
    public int? PendingTool { get; set; }

    /// <summary>The modal state as it is now, for a program that starts here.</summary>
    public ModalState Modal => new(UnitScale, Absolute, Plane, WorkOffset, FeedMmPerMin, MotionCode, Spindle, SpindleSpeed, Mist, Flood);

    // Per-line results
    public double LineSeconds { get; set; }
    public bool ToolChanged { get; set; }
    public bool SuppressMotion { get; set; }
    public bool FeedMove { get; set; }

    public double EffectiveFeed => FeedMmPerMin > 0 ? FeedMmPerMin : Settings.DefaultFeedMmPerMin;

    public void Move(IMotionMode motion, int code)
    {
        Motion = motion;
        MotionCode = code;
    }

    public Point4 TargetOf(GCodeLine line)
    {
        double Axis(char letter, double current) => line[letter] is { } v
            ? (Absolute ? v * Scale(letter) : current + v * Scale(letter))
            : current;
        return new Point4(Axis('X', Position.X), Axis('Y', Position.Y), Axis('Z', Position.Z), Axis('A', Position.A));
    }

    private double Scale(char letter) => letter == 'A' ? 1 : UnitScale; // rotary axis stays in degrees
}

/// <summary>
/// Estimates the duration of every line of a program from distances and feeds (no acceleration model).
/// When the file carries an estimate in its header (<c>;@MKR|TIME</c>), the result is scaled to match it.
/// </summary>
public static class GCodeEstimator
{
    private static readonly char[] AxisLetters = { 'X', 'Y', 'Z', 'A' };

    // Non-motion and modal G codes. Codes not listed are ignored for timing.
    private static readonly IReadOnlyDictionary<int, Action<EstimatorState, GCodeLine>> GCodes =
        new Dictionary<int, Action<EstimatorState, GCodeLine>>
        {
            [0] = (s, _) => s.Move(MotionModes.Rapid, 0),
            [1] = (s, _) => s.Move(MotionModes.Linear, 1),
            [2] = (s, _) => s.Move(MotionModes.ArcClockwise, 2),
            [3] = (s, _) => s.Move(MotionModes.ArcCounterClockwise, 3),
            [4] = (s, l) => { s.LineSeconds += DwellSeconds(l); s.SuppressMotion = true; },
            [10] = (s, _) => s.SuppressMotion = true,
            [17] = (s, _) => s.Plane = 17,
            [18] = (s, _) => s.Plane = 18,
            [19] = (s, _) => s.Plane = 19,
            [20] = (s, _) => s.UnitScale = 25.4,
            [21] = (s, _) => s.UnitScale = 1,
            [28] = (s, _) => s.SuppressMotion = true,
            [54] = (s, _) => s.WorkOffset = 54,
            [55] = (s, _) => s.WorkOffset = 55,
            [56] = (s, _) => s.WorkOffset = 56,
            [57] = (s, _) => s.WorkOffset = 57,
            [58] = (s, _) => s.WorkOffset = 58,
            [59] = (s, _) => s.WorkOffset = 59,
            [90] = (s, _) => s.Absolute = true,
            [91] = (s, _) => s.Absolute = false,
            [92] = (s, l) => { s.Position = s.TargetOf(l); s.SuppressMotion = true; },
        };

    private static readonly IReadOnlyDictionary<int, Action<EstimatorState, GCodeLine>> MCodes =
        new Dictionary<int, Action<EstimatorState, GCodeLine>>
        {
            [3] = (s, _) => s.Spindle = 3,
            [4] = (s, _) => s.Spindle = 4,
            [5] = (s, _) => s.Spindle = null,
            [6] = (s, _) =>
            {
                s.Tool = s.PendingTool;
                s.LineSeconds += s.Settings.ToolChangeSeconds;
                s.ToolChanged = true;
            },
            [7] = (s, _) => s.Mist = true,
            [8] = (s, _) => s.Flood = true,
            [9] = (s, _) => s.Mist = s.Flood = false,
        };

    public static GCodeAnalysis Analyze(string text, EstimatorSettings? settings = null) =>
        Analyze(text.Split('\n'), settings);

    /// <param name="rawLines">The file's lines without <c>\n</c> (a trailing <c>\r</c> is allowed).</param>
    public static GCodeAnalysis Analyze(IReadOnlyList<string> rawLines, EstimatorSettings? settings = null)
    {
        var machineLines = new MachineLineMap(rawLines);
        var lines = rawLines.Select(l => l.TrimEnd('\r')).ToList();
        var metadata = new HeaderMetadata();
        foreach (var line in lines) metadata.TryAdd(line);

        var effective = (settings ?? EstimatorSettings.Default) with
        {
            RapidFeedMmPerMin = metadata.MaxFeedRate ?? (settings ?? EstimatorSettings.Default).RapidFeedMmPerMin,
        };
        var state = new EstimatorState(effective);
        var steps = lines.Select(text => Step(state, GCodeLine.Parse(text))).ToList();
        return GCodeAnalysis.Build(lines, steps, metadata, machineLines);
    }

    private static LineStep Step(EstimatorState state, GCodeLine line)
    {
        state.LineSeconds = 0;
        state.ToolChanged = false;
        state.SuppressMotion = false;
        state.FeedMove = false;

        var modalBefore = line.Codes('M').Contains(6) ? state.Modal : null; // where a program restarted at this tool change begins
        if (line['F'] is { } feed) state.FeedMmPerMin = feed * state.UnitScale;
        if (line['T'] is { } tool) state.PendingTool = (int)Math.Round(tool);
        if (line['S'] is { } speed && !line.Codes('G').Contains(4)) state.SpindleSpeed = speed; // G4 S is a dwell

        foreach (var code in line.Codes('G'))
            GCodes.GetValueOrDefault(code)?.Invoke(state, line);
        foreach (var code in line.Codes('M'))
            MCodes.GetValueOrDefault(code)?.Invoke(state, line);

        IReadOnlyList<Point4> path = [];
        if (!state.SuppressMotion && AxisLetters.Any(line.Has))
        {
            var target = state.TargetOf(line);
            state.LineSeconds += state.Motion.Seconds(state, line, target);
            path = state.Motion.Path(state, line, target);
            state.Position = target;
            state.FeedMove = state.Motion.IsFeed;
        }

        return new LineStep(state.LineSeconds, state.Tool, state.ToolChanged, state.FeedMove, state.Position, path, modalBefore);
    }

    /// <summary>Smoothieware semantics: G4 P is milliseconds, G4 S is seconds.</summary>
    private static double DwellSeconds(GCodeLine line) => (line['S'] ?? 0) + (line['P'] ?? 0) / 1000.0;
}

/// <param name="Path">Points the line's move passes through after its start (empty without a move).</param>
/// <param name="ModalBefore">The modal state before the line, kept for lines with a tool change (null elsewhere).</param>
internal sealed record LineStep(
    double Seconds, int? Tool, bool ToolChanged, bool FeedMove, Point4 EndPosition, IReadOnlyList<Point4> Path, ModalState? ModalBefore);
