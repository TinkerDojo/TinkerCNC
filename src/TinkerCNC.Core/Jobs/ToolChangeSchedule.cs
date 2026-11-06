using TinkerCNC.GCode;

namespace TinkerCNC.Jobs;

/// <summary>
/// Remembers when the running job actually reached each tool change (the <c>M6</c> line). The reported line stays on
/// the previous operation's last feed move through the change (see <see cref="OperationRecorder"/>), so a change is
/// taken as reached when that move is, plus the estimated time of the lines up to the <c>M6</c>.
/// </summary>
public sealed class ToolChangeLog(JobTimeline timeline)
{
    private readonly Dictionary<int, DateTime> _reached = new();
    private MachineSample? _previous;

    /// <summary>Clock time (UTC) each operation's tool change was reached, by segment index.</summary>
    public IReadOnlyDictionary<int, DateTime> Reached => _reached;

    public JobTimeline Timeline { get; private set; } = timeline;

    /// <summary>Keeps the observations when the timeline is rebuilt (e.g. after a new measurement).</summary>
    public void Use(JobTimeline updated) => Timeline = updated;

    /// <param name="sample">A sample with lines of the file (<see cref="MachineSample.InFileLines"/>).</param>
    public void Observe(MachineSample sample)
    {
        if (_previous is { } previous && sample.Playing
            && sample.TimestampUtc - previous.TimestampUtc <= OperationRecorder.MaxBoundaryGap)
        {
            foreach (var change in Timeline.ToolChanges)
            {
                var trigger = TriggerLine(change.Segment);
                if (previous.Line >= trigger || sample.Line < trigger) continue;
                var ahead = Timeline.Times.SecondsUntil(sample.Line, change.Segment.StartLine, sample.FeedOverridePercent);
                _reached[change.Segment.Index] = sample.TimestampUtc.AddSeconds(Math.Max(0, ahead));
            }
        }
        _previous = sample;
    }

    /// <summary>The previous operation's last feed move, or the <c>M6</c> line itself for the first operation.</summary>
    private int TriggerLine(ToolSegment change) =>
        Timeline.Segments.LastOrDefault(s => s.Segment.EndLine < change.StartLine)?.Segment.LastFeedLine ?? change.StartLine;
}

public enum ToolChangeTiming
{
    /// <summary>No job is running: only the offset from the start is known.</summary>
    Planned,
    /// <summary>Still ahead: <see cref="ScheduledToolChange.ClockUtc"/> is the predicted time.</summary>
    Upcoming,
    /// <summary>Already happened and was observed: <see cref="ScheduledToolChange.ClockUtc"/> is when.</summary>
    Observed,
    /// <summary>Already happened before we were watching.</summary>
    Passed,
}

/// <param name="OffsetSeconds">Time from the start of the job, probing included (measured where available, else estimated).</param>
/// <param name="ClockUtc">Predicted or observed clock time; null for <see cref="ToolChangeTiming.Planned"/> and <see cref="ToolChangeTiming.Passed"/>.</param>
/// <param name="InSeconds">Time left until an upcoming change (feed override applied).</param>
public sealed record ScheduledToolChange(TimelineSegment Operation, double OffsetSeconds, ToolChangeTiming Timing, DateTime? ClockUtc, double? InSeconds);

/// <summary>When each tool change of a job happens, and when the job finishes.</summary>
public sealed record ToolChangeSchedule(IReadOnlyList<ScheduledToolChange> Changes, DateTime? FinishUtc)
{
    /// <summary>The next tool change still ahead, if any.</summary>
    public ScheduledToolChange? Next => Changes.FirstOrDefault(c => c.Timing == ToolChangeTiming.Upcoming);

    /// <param name="position">Where the running job is, or null when no job is running.</param>
    public static ToolChangeSchedule Create(
        JobTimeline timeline, TimelinePosition? position, DateTime nowUtc, IReadOnlyDictionary<int, DateTime> reached)
    {
        var changes = timeline.ToolChanges.Select(op => Schedule(timeline, op, position, nowUtc, reached)).ToList();
        DateTime? finish = position is { } p ? nowUtc.AddSeconds(p.RemainingSeconds) : null;
        return new ToolChangeSchedule(changes, finish);
    }

    private static ScheduledToolChange Schedule(
        JobTimeline timeline, TimelineSegment op, TimelinePosition? position, DateTime nowUtc, IReadOnlyDictionary<int, DateTime> reached)
    {
        if (position is null)
            return new(op, op.StartSeconds, ToolChangeTiming.Planned, null, null);

        // Reached is checked first: during a change the line still points at the previous operation.
        if (reached.TryGetValue(op.Segment.Index, out var at))
            return new(op, op.StartSeconds, ToolChangeTiming.Observed, at, null);

        if (position.Line is not { } line || op.Segment.StartLine > line)
        {
            var until = position.RemainingSeconds - timeline.Times.Remaining(op.Segment.StartLine, position.FeedOverridePercent);
            return new(op, op.StartSeconds, ToolChangeTiming.Upcoming, nowUtc.AddSeconds(until), until);
        }

        return new(op, op.StartSeconds, ToolChangeTiming.Passed, null, null);
    }
}
