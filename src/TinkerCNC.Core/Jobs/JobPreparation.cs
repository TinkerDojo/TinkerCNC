using System.Security.Cryptography;
using System.Text;
using TinkerCNC.GCode;
using TinkerCNC.Localization;

namespace TinkerCNC.Jobs;

/// <summary>One thing the machine does before the program, with a rough estimate of its time.</summary>
/// <param name="Name">The step in English: part of the preparation's <see cref="JobPreparation.Fingerprint"/>, so it
/// stays the same whatever language is shown.</param>
public sealed record PreparationStep(string Name, double EstimatedSeconds)
{
    /// <summary>The step for people (<see cref="Name"/> unless made with <see cref="Of"/>).</summary>
    public Text Label { get; init; } = Text.Plain(Name);

    /// <summary>A step named by <paramref name="label"/>; its <see cref="Name"/> is the English wording.</summary>
    public static PreparationStep Of(Text label, double estimatedSeconds) => new(label.ToString(), estimatedSeconds) { Label = label };
}

/// <summary>
/// What the machine does after <c>play</c> and before the program's first line when a <see cref="JobStartPlan"/> probes:
/// the queued probing and the tool changes around it. The job's timeline shows it before the program, with a rough
/// estimate until a run with the same commands has been measured (<see cref="PreparationRecorder"/>).
/// </summary>
/// <param name="Fingerprint">The same for the same commands and steps, so a measured time can be reused; stored with the
/// operation times (<see cref="History.OperationHistory"/>) under <see cref="FingerprintPrefix"/>.</param>
public sealed record JobPreparation(string Fingerprint, IReadOnlyList<PreparationStep> Steps)
{
    public const string FingerprintPrefix = "prep:";

    /// <summary>What the preparation is called on the job's timeline.</summary>
    public static Text Label { get; } = CoreTexts.Of("Preparation.Label");

    // Rough guesses, only until the preparation has been measured once.
    private const double XyzCornerSeconds = 60;
    private const double ZProbeSeconds = 20;
    private const double LevelingSecondsPerPoint = 10;
    private const double ScanMmPerMinute = 1500;

    public double EstimatedSeconds => Steps.Sum(s => s.EstimatedSeconds);

    /// <summary>The preparation of a start that probes; null when nothing is probed.</summary>
    /// <param name="queued">The commands queued with <c>buffer</c> (<see cref="JobStartPlan"/>).</param>
    public static JobPreparation? For(JobStartOptions options, JobStartContext context, IReadOnlyList<string> queued)
    {
        var toolChange = EstimatorSettings.Default.ToolChangeSeconds;
        var program = context.Program;
        var cuttingToolIn = context.ActiveTool is { } tool && ManualToolChange.IsCuttingTool(tool);
        (bool Applies, Func<PreparationStep> Step)[] candidates =
        [
            (options.WorkOrigin == WorkOriginChoice.XyzProbe, () => PreparationStep.Of(CoreTexts.Of("Preparation.XyzCorner"), XyzCornerSeconds)),
            (options.UsesProbe && cuttingToolIn, () => PreparationStep.Of(CoreTexts.Of("Preparation.ToProbe"), toolChange)),
            (options.ScanMargin && program is not null,
                () => PreparationStep.Of(CoreTexts.Of("Preparation.ScanMargin"), 2 * (program!.Width + program.Depth) / ScanMmPerMinute * 60)),
            (options.ZProbe, () => PreparationStep.Of(CoreTexts.Of("Preparation.ZProbe"), ZProbeSeconds)),
            (options.Leveling, () => PreparationStep.Of(CoreTexts.Of("Preparation.Leveling", options.LevelingPointsX, options.LevelingPointsY),
                options.LevelingPointsX * options.LevelingPointsY * LevelingSecondsPerPoint)),
            (queued.Any(c => c.StartsWith("M6 ", StringComparison.OrdinalIgnoreCase)),
                () => PreparationStep.Of(CoreTexts.Of("Preparation.ToolBack"), toolChange)),
        ];
        var steps = candidates.Where(c => c.Applies).Select(c => c.Step()).ToList();
        if (steps.Count == 0) return null;
        var identity = string.Join('\n', [.. queued, .. steps.Select(s => s.Name)]);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        return new JobPreparation(FingerprintPrefix + hash, steps);
    }
}

/// <summary>A preparation that was observed from <c>play</c> to the program's start.</summary>
public sealed record PreparationRun(JobPreparation Preparation, double ActiveSeconds, double PausedSeconds, double AverageFeedOverridePercent);

/// <summary>
/// Measures how long the <see cref="JobPreparation"/> of a job takes, from the stream of status samples (with lines of
/// the file, see <see cref="MachineSample.InFileLines"/>). It starts when the machine reports playing and ends when it
/// reports a line of the program: while the queued commands run, the line is 0. Like <see cref="OperationRecorder"/>,
/// the estimate of the program's lines before the first reported one (its first tool change and approach) is taken off.
/// Time waiting for the user (paused, or a manual change to the probe) does not count. Only a job seen from its start
/// and without a gap or an alarm is measured.
/// </summary>
public sealed class PreparationRecorder(JobPreparation preparation, GCodeAnalysis analysis)
{
    private static readonly string[] WaitingStates = [.. OperationRecorder.PausedStates, "Tool"];

    private MachineSample? _previous;
    private bool _started;
    private bool _over;
    private double _active;
    private double _paused;
    private double _overrideWeighted;

    /// <summary>Feeds the next sample; returns the measurement once the program has started.</summary>
    public PreparationRun? Observe(MachineSample sample)
    {
        var previous = _previous;
        _previous = sample;
        if (_over) return null;
        if (previous is null)
        {
            _over = sample.Playing; // the start was not seen
            return null;
        }

        var gap = sample.TimestampUtc - previous.TimestampUtc;
        if (!_started)
        {
            if (!sample.Playing) return null;
            _started = true;
            _over = gap > OperationRecorder.MaxBoundaryGap;
        }
        else if (!sample.Playing || gap > OperationRecorder.MaxSampleGap || OperationRecorder.HasState(sample, OperationRecorder.AbortStates))
        {
            _over = true;
        }
        else
        {
            Accumulate(previous, gap.TotalSeconds);
        }
        if (_over || sample.Line <= 0) return null;

        _over = true;
        if (gap > OperationRecorder.MaxBoundaryGap || _active <= 0) return null; // the boundary or the probing itself was missed
        var programSoFar = analysis.Times.SecondsBefore(sample.Line) * 100 / Math.Max(sample.FeedOverridePercent, 1);
        var active = Math.Max(0, _active - programSoFar);
        return new PreparationRun(preparation, active, _paused, _active > 0 ? _overrideWeighted / _active : 100);
    }

    private void Accumulate(MachineSample sample, double seconds)
    {
        if (OperationRecorder.HasState(sample, WaitingStates))
        {
            _paused += seconds;
            return;
        }
        _active += seconds;
        _overrideWeighted += sample.FeedOverridePercent * seconds;
    }
}
