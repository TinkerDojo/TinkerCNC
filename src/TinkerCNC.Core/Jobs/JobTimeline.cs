using TinkerCNC.GCode;
using TinkerCNC.History;

namespace TinkerCNC.Jobs;

/// <summary>A tool operation on the timeline, with its effective (measured or estimated) duration.</summary>
/// <param name="Measurement">Recorded runs of this operation; null when only the estimate is available.</param>
public sealed record TimelineSegment(ToolSegment Segment, double StartSeconds, double Seconds, OperationStatistics? Measurement)
{
    public bool IsMeasured => Measurement is not null;
    public double EstimatedSeconds => Segment.Seconds;
}

/// <summary>The probing before the program, with its effective (measured or estimated) duration.</summary>
/// <param name="Measurement">Recorded runs of the same preparation; null when only the estimate is available.</param>
public sealed record TimelinePreparation(JobPreparation Preparation, double Seconds, OperationStatistics? Measurement)
{
    public bool IsMeasured => Measurement is not null;
}

/// <summary>Where a running job is on its timeline.</summary>
/// <param name="Line">Line of the file being executed; null while the preparation (probing) runs.</param>
/// <param name="DoneSeconds">Time on the timeline before this point.</param>
/// <param name="RemainingSeconds">Time left until the end (the program's part scaled by the feed override).</param>
public sealed record TimelinePosition(int? Line, double DoneSeconds, double RemainingSeconds, double FeedOverridePercent)
{
    public bool Preparing => Line is null;
}

/// <summary>
/// The job's timeline: the probing before the program (<see cref="Preparation"/>), if any, then the program, where
/// measured operations replace the estimate. Within a measured operation the
/// per-line estimates are scaled to add up to the measured cutting time, so progress inside it stays smooth;
/// the tool change keeps its estimate.
/// </summary>
public sealed class JobTimeline
{
    private JobTimeline(GCodeAnalysis analysis, LineTimes times, IReadOnlyList<TimelineSegment> segments, TimelinePreparation? preparation = null)
    {
        Analysis = analysis;
        Times = times;
        Segments = segments;
        Preparation = preparation;
    }

    public GCodeAnalysis Analysis { get; }

    /// <summary>The program's line times, from its first line (without the preparation).</summary>
    public LineTimes Times { get; }

    /// <summary>The tool operations; their <see cref="TimelineSegment.StartSeconds"/> include the preparation.</summary>
    public IReadOnlyList<TimelineSegment> Segments { get; }

    public TimelinePreparation? Preparation { get; }
    public double PreparationSeconds => Preparation?.Seconds ?? 0;

    /// <summary>The whole job: preparation and program.</summary>
    public double TotalSeconds => PreparationSeconds + Times.Total;

    /// <summary>Time on the timeline before <paramref name="line"/> starts.</summary>
    public double SecondsBefore(int line) => PreparationSeconds + Times.SecondsBefore(line);

    public TimelinePosition At(int line, double feedOverridePercent) =>
        new(line, SecondsBefore(line), Times.Remaining(line, feedOverridePercent), feedOverridePercent);

    /// <summary>
    /// Position while the preparation runs, <paramref name="elapsedSeconds"/> after the start. When it takes longer
    /// than expected the position waits at its end.
    /// </summary>
    public TimelinePosition WhilePreparing(double elapsedSeconds, double feedOverridePercent) =>
        new(null, Math.Min(elapsedSeconds, PreparationSeconds),
            Math.Max(0, PreparationSeconds - elapsedSeconds) + Times.Remaining(1, feedOverridePercent), feedOverridePercent);

    public double Fraction(TimelinePosition position) => TotalSeconds > 0 ? Math.Clamp(position.DoneSeconds / TotalSeconds, 0, 1) : 0;

    /// <summary>The same program with <paramref name="preparation"/> before it (none when null).</summary>
    /// <param name="measurement">Recorded runs of that preparation, which replace its estimate.</param>
    public JobTimeline WithPreparation(JobPreparation? preparation, OperationStatistics? measurement)
    {
        var shown = preparation is null ? null : new TimelinePreparation(preparation, measurement?.MedianSeconds ?? preparation.EstimatedSeconds, measurement);
        var shift = (shown?.Seconds ?? 0) - PreparationSeconds;
        return new JobTimeline(Analysis, Times, Segments.Select(s => s with { StartSeconds = s.StartSeconds + shift }).ToList(), shown);
    }

    public int MeasuredCount => Segments.Count(s => s.IsMeasured);

    /// <summary>Operations that begin with a tool change (<c>M6</c>), in program order.</summary>
    public IEnumerable<TimelineSegment> ToolChanges => Segments.Where(s => s.Segment.ToolChangeSeconds > 0);

    public TimelineSegment? SegmentAt(int line) => Segments.FirstOrDefault(s => s.Segment.Contains(line));

    public static JobTimeline Estimated(GCodeAnalysis analysis) =>
        Create(analysis, new Dictionary<string, OperationStatistics>());

    public static JobTimeline Create(GCodeAnalysis analysis, IReadOnlyDictionary<string, OperationStatistics> measurements)
    {
        var seconds = analysis.Times.Seconds.ToArray();
        foreach (var segment in analysis.Segments)
        {
            if (!measurements.TryGetValue(segment.Fingerprint, out var measured) || segment.CuttingSeconds <= 0) continue;
            var scale = measured.MedianSeconds / segment.CuttingSeconds;
            for (var line = segment.CuttingStartLine; line <= segment.EndLine; line++) seconds[line - 1] *= scale;
        }

        var times = new LineTimes(seconds);
        var segments = analysis.Segments
            .Select(s => new TimelineSegment(s, times.SecondsBefore(s.StartLine), times.Between(s.StartLine, s.EndLine),
                measurements.GetValueOrDefault(s.Fingerprint)))
            .ToList();
        return new JobTimeline(analysis, times, segments);
    }
}
