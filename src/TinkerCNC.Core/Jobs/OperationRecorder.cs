using TinkerCNC.GCode;

namespace TinkerCNC.Jobs;

/// <summary>One status observation while a job may be running.</summary>
/// <param name="Line">Line from the status <c>P</c> field (0 when not playing), as the machine numbers it until
/// <see cref="InFileLines"/> converts it.</param>
/// <param name="State">Machine state (Idle, Run, Hold, Tool, Alarm, …).</param>
/// <param name="FirmwareMove">
/// The machine runs moves of its own, not of the program: the automatic tool change. They report line 0
/// (Player::on_get_public_data, block <c>line</c> of internal commands); <see cref="Line"/> keeps the last program line.
/// </param>
public sealed record MachineSample(
    DateTime TimestampUtc, bool Playing, int Line, string State, double FeedOverridePercent, bool FirmwareMove = false)
{
    /// <param name="previous">The sample before, whose line is kept during firmware moves.</param>
    public static MachineSample From(StatusReport? status, DateTime utc, MachineSample? previous = null)
    {
        var playing = status?.IsPlaying == true;
        var line = status?.Job?.Line ?? 0;
        var firmwareMove = playing && line == 0 && previous is { Playing: true, Line: > 0 };
        if (firmwareMove) line = previous!.Line;
        return new(utc, playing, line, status?.State ?? "", status?.FeedOverridePercent ?? 100, firmwareMove);
    }

    /// <summary>The same sample with <see cref="Line"/> converted to a line of the analysed file.</summary>
    public MachineSample InFileLines(MachineLineMap map) => this with { Line = map.ToFileLine(Line) };
}

/// <summary>A tool operation that was observed completely, from its first cutting line to its last line.</summary>
public sealed record OperationRun(ToolSegment Segment, DateTime StartedUtc, double ActiveSeconds, double PausedSeconds, double AverageFeedOverridePercent);

/// <summary>
/// Measures how long each tool operation of a running program actually takes, from the stream of status samples
/// (with lines of the file, see <see cref="MachineSample.InFileLines"/>).
/// <para>
/// The firmware reports the G1/G2/G3 move being executed (since 2026-08-04; before, the last line handed to the
/// planner). Between two operations the line therefore stays on the last feed move of the first one through the
/// retract and the whole tool change, and the next operation is only seen at its first feed move. So:
/// an operation starts when the line enters it, back-dated by the estimate of its lines before the reported one
/// (the approach after the tool change). It ends when the line leaves it (older firmware); when the tool change
/// begins near its end (firmware moves, or waiting for a manual change); or once the lines from its last feed move
/// to its end have run for their estimated time.
/// </para>
/// Pauses are excluded and the feed override is averaged so the result can be normalised to 100 %.
/// Only clean observations are reported: a run seen from the middle, interrupted by an alarm, a gap in the samples
/// or a jump backwards is discarded.
/// </summary>
public sealed class OperationRecorder(GCodeAnalysis analysis)
{
    /// <summary>Longest tolerated gap between two samples (e.g. a disconnect) before the measurement is dropped.</summary>
    public static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Longest gap across which the start or end of an operation is accepted: the boundary happened somewhere in
    /// the gap, so this bounds the error. (Status polling pauses during a file download, for example.)
    /// </summary>
    public static readonly TimeSpan MaxBoundaryGap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// When playback stops inside an operation, it still counts if the lines after the last reported one
    /// add up to at most this much (estimated). The reported line itself was still running.
    /// </summary>
    public static readonly TimeSpan EndTolerance = TimeSpan.FromSeconds(5);

    internal static readonly string[] PausedStates = { "Hold", "Pause", "Door" };
    internal static readonly string[] AbortStates = { "Alarm" };

    /// <summary>Waiting for a manual tool change (M490.1) or for the queue to empty.</summary>
    private static readonly string[] ToolChangeStates = { "Tool", "Wait" };

    private MachineSample? _previous;
    private Measurement? _current;

    /// <summary>The operation being timed right now, if any.</summary>
    public ToolSegment? Measuring => _current?.Segment;

    /// <summary>Feeds the next sample; returns the operations completed by it.</summary>
    public IReadOnlyList<OperationRun> Observe(MachineSample sample)
    {
        var completed = new List<OperationRun>();
        if (_previous is { } previous) Advance(previous, sample, completed);
        _previous = sample;
        return completed;
    }

    private void Advance(MachineSample previous, MachineSample next, List<OperationRun> completed)
    {
        var gap = next.TimestampUtc - previous.TimestampUtc;
        if (gap < TimeSpan.Zero || gap > MaxSampleGap || HasState(next, AbortStates) || (next.Playing && next.Line < previous.Line))
        {
            _current = null;
            return;
        }

        _current?.Accumulate(previous, gap);

        if (!next.Playing)
        {
            if (_current is { } last && gap <= MaxBoundaryGap && (last.InTail || FinishedNearEnd(last.Segment, previous.Line)))
                completed.Add(last.ToRun());
            _current = null;
            return;
        }

        if (gap > MaxBoundaryGap)
        {
            if (_current is { } running && next.Line > running.Segment.EndLine) _current = null; // left it unseen
            return;
        }

        CompleteIfDone(next, completed);
        _current ??= Start(previous, next);
        CompleteIfDone(next, completed); // an operation entered at its last feed move
    }

    /// <summary>
    /// The operation whose cutting lines the line counter just entered, if any. Feed moves before the reported line
    /// would have been reported themselves, so an operation entered after more of them than fit into the sample gap
    /// was not followed from its start (e.g. a job started from a line in the middle).
    /// </summary>
    private Measurement? Start(MachineSample previous, MachineSample next) => analysis.Segments
        .Where(s => previous.Line < s.CuttingStartLine && next.Line >= s.CuttingStartLine && next.Line <= s.EndLine)
        .Where(s => FeedSecondsOf(s.CuttingStartLine, next.Line - 1) <= MaxBoundaryGap.TotalSeconds * next.FeedOverridePercent / 100)
        .Select(s => new Measurement(s, next.TimestampUtc, EstimateOf(s.CuttingStartLine, next.Line - 1), next.FeedOverridePercent))
        .FirstOrDefault();

    private void CompleteIfDone(MachineSample next, List<OperationRun> completed)
    {
        if (_current is not { } running) return;
        var segment = running.Segment;
        if (next.Line >= segment.LastFeedLine && !running.InTail)
            running.EnterTail(EstimateOf(next.Line, segment.EndLine));

        var toolChangeBegun = next.FirmwareMove || HasState(next, ToolChangeStates);
        var done = next.Line > segment.EndLine
                   || running.TailElapsed
                   || (toolChangeBegun && (running.InTail || FinishedNearEnd(segment, next.Line)));
        if (!done) return;
        completed.Add(running.ToRun());
        _current = null;
    }

    private double EstimateOf(int firstLine, int lastLine) =>
        lastLine >= firstLine ? analysis.Times.Between(firstLine, lastLine) : 0;

    private double FeedSecondsOf(int firstLine, int lastLine) => Enumerable.Range(firstLine, Math.Max(0, lastLine - firstLine + 1))
        .Where(analysis.IsFeedMove)
        .Sum(line => analysis.Times.Seconds[line - 1]);

    private bool FinishedNearEnd(ToolSegment segment, int lastLine) =>
        analysis.Times.Between(Math.Max(lastLine + 1, segment.CuttingStartLine), segment.EndLine) <= EndTolerance.TotalSeconds;

    internal static bool HasState(MachineSample sample, IEnumerable<string> states) =>
        states.Any(s => sample.State.StartsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <param name="unseenSeconds">Estimate (at 100 %) of the lines that ran before the operation was seen.</param>
    /// <param name="feedOverridePercent">Override they ran at.</param>
    private sealed class Measurement(ToolSegment segment, DateTime startedUtc, double unseenSeconds, double feedOverridePercent)
    {
        private double _active = unseenSeconds * 100 / Math.Max(feedOverridePercent, 1);
        private double _paused;
        private double _overrideWeighted = unseenSeconds * 100;
        private double _tailStart;   // normalised seconds when the last feed move was reached
        private double _tailSeconds; // estimate of the lines from there to the end

        public ToolSegment Segment { get; } = segment;

        /// <summary>The line counter reached the last feed move; only the tail of the operation is left.</summary>
        public bool InTail { get; private set; }

        /// <summary>The tail ran for its estimated time: the line counter is waiting for the next operation.</summary>
        public bool TailElapsed => InTail && NormalisedSeconds - _tailStart >= _tailSeconds;

        /// <summary>Active time converted to 100 % feed override.</summary>
        private double NormalisedSeconds => _overrideWeighted / 100;

        public void EnterTail(double estimatedSeconds)
        {
            InTail = true;
            _tailStart = NormalisedSeconds;
            _tailSeconds = estimatedSeconds;
        }

        /// <summary>Attributes the time since <paramref name="sample"/> to the state that sample reported.</summary>
        public void Accumulate(MachineSample sample, TimeSpan duration)
        {
            if (HasState(sample, PausedStates))
            {
                _paused += duration.TotalSeconds;
                return;
            }
            _active += duration.TotalSeconds;
            _overrideWeighted += sample.FeedOverridePercent * duration.TotalSeconds;
        }

        public OperationRun ToRun() => new(Segment, startedUtc, _active, _paused, _active > 0 ? _overrideWeighted / _active : 100);
    }
}
