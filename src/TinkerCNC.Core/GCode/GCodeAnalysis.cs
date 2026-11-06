namespace TinkerCNC.GCode;

/// <summary>A run of consecutive lines cut with one tool (between two tool changes).</summary>
/// <param name="StartLine">First line, 1-based (the <c>M6</c> line when the segment starts with a tool change).</param>
/// <param name="EndLine">Last line, 1-based, inclusive.</param>
/// <param name="CuttingStartLine">First line after the tool change; actual times are measured from here.</param>
/// <param name="LastFeedLine">
/// Last G1/G2/G3 line (<see cref="EndLine"/> without one). Since firmware 2026-08-04 the reported line is the feed
/// move being executed, so it stays here through the retract and the next tool change.
/// </param>
/// <param name="ToolChangeSeconds">Estimated time of the tool change itself (0 without one).</param>
/// <param name="Fingerprint">Hash of the segment's G-code, see <see cref="OperationFingerprint"/>.</param>
public sealed record ToolSegment(
    int Index, int? ToolNumber, ToolInfo? Tool, int StartLine, int EndLine, int CuttingStartLine, int LastFeedLine,
    double StartSeconds, double Seconds, double ToolChangeSeconds, string Fingerprint)
{
    public double EndSeconds => StartSeconds + Seconds;
    public double CuttingSeconds => Seconds - ToolChangeSeconds;

    public string Label => ToolNumber is null
        ? "No tool"
        : string.Join(" · ", new[] { $"T{ToolNumber}", Tool?.Name }.OfType<string>());

    public bool Contains(int line) => line >= StartLine && line <= EndLine;
}

/// <summary>Time estimate of a program: per-line durations, tool segments and header metadata.</summary>
public sealed class GCodeAnalysis
{
    private GCodeAnalysis(IReadOnlyList<string> lines, LineTimes times, Point4[] endPositions,
        IReadOnlyList<ToolSegment> segments, HeaderMetadata metadata, double? calibration, MachineLineMap machineLines,
        bool[] feedMoves, Toolpath toolpath)
    {
        Toolpath = toolpath;
        Lines = lines;
        _feedMoves = feedMoves;
        MachineLines = machineLines;
        Times = times;
        EndPositions = endPositions;
        Segments = segments;
        Metadata = metadata;
        CalibrationFactor = calibration;
    }

    private readonly bool[] _feedMoves;

    public IReadOnlyList<string> Lines { get; }

    /// <summary>Converts the line numbers the machine reports into lines of <see cref="Lines"/>.</summary>
    public MachineLineMap MachineLines { get; }

    /// <summary>Estimated duration of every line.</summary>
    public LineTimes Times { get; }

    /// <summary>Tool position after each line (work coordinates as programmed).</summary>
    public IReadOnlyList<Point4> EndPositions { get; }

    public IReadOnlyList<ToolSegment> Segments { get; }
    public HeaderMetadata Metadata { get; }

    /// <summary>The moves as drawn in the 3D view.</summary>
    public Toolpath Toolpath { get; }

    /// <summary>Factor applied to match the file's own <c>;@MKR|TIME</c> estimate, or null if it had none.</summary>
    public double? CalibrationFactor { get; }

    public int LineCount => Lines.Count;

    public ToolSegment? SegmentAt(int line) => Segments.FirstOrDefault(s => s.Contains(line));

    /// <summary>Whether the 1-based <paramref name="line"/> is a G1/G2/G3 move.</summary>
    public bool IsFeedMove(int line) => line >= 1 && line <= _feedMoves.Length && _feedMoves[line - 1];

    internal static GCodeAnalysis Build(
        IReadOnlyList<string> lines, IReadOnlyList<LineStep> steps, HeaderMetadata metadata, MachineLineMap machineLines)
    {
        var raw = steps.Select(s => s.Seconds).ToArray();
        var rawTotal = raw.Sum();
        double? calibration = metadata.EstimatedSeconds is { } expected && rawTotal > 0 ? expected / rawTotal : null;
        var times = new LineTimes(raw.Select(s => s * (calibration ?? 1)).ToArray());
        var segments = BuildSegments(lines, steps, times, metadata.Tools);
        return new GCodeAnalysis(lines, times, steps.Select(s => s.EndPosition).ToArray(), segments, metadata, calibration, machineLines,
            steps.Select(s => s.FeedMove).ToArray(), Toolpath.Build(steps));
    }

    /// <summary>Splits the program at every tool change; empty segments (e.g. the header) are dropped.</summary>
    private static List<ToolSegment> BuildSegments(
        IReadOnlyList<string> lines, IReadOnlyList<LineStep> steps, LineTimes times, IReadOnlyDictionary<int, ToolInfo> tools)
    {
        var starts = new List<int> { 0 };
        starts.AddRange(Enumerable.Range(0, steps.Count).Where(i => i > 0 && steps[i].ToolChanged));

        var segments = new List<ToolSegment>();
        for (var b = 0; b < starts.Count; b++)
        {
            var first = starts[b] + 1;                                           // 1-based
            var last = b + 1 < starts.Count ? starts[b + 1] : steps.Count;        // 1-based, inclusive
            var seconds = times.Between(first, last);
            if (last < first || seconds <= 0) continue;

            var startsWithToolChange = steps[first - 1].ToolChanged;
            var tool = steps[first - 1].Tool;
            segments.Add(new ToolSegment(
                segments.Count, tool, tool is { } n ? tools.GetValueOrDefault(n) : null,
                first, last,
                CuttingStartLine: startsWithToolChange ? first + 1 : first,
                LastFeedLine: Enumerable.Range(first, last - first + 1).LastOrDefault(l => steps[l - 1].FeedMove, last),
                StartSeconds: times.SecondsBefore(first),
                Seconds: seconds,
                ToolChangeSeconds: startsWithToolChange ? times.Seconds[first - 1] : 0,
                Fingerprint: OperationFingerprint.Compute(lines.Skip(first - 1).Take(last - first + 1))));
        }
        return segments;
    }
}
