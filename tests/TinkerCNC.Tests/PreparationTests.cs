using TinkerCNC.GCode;
using TinkerCNC.History;
using TinkerCNC.Jobs;
using TinkerCNC.Motion;

namespace TinkerCNC.Tests;

public class JobPreparationTests
{
    private static readonly MachineTravel CompactTravel = new(
        new AxisRange(-207, 0), new AxisRange(-206, 0), new AxisRange(-102, 0), -190.3, -194.114, -3);

    private static readonly ToolpathBounds Program = new(10, 20, -3, 110, 80, 5);

    private static JobStartContext Context(int? tool) =>
        new(CompactTravel, Program, [-100, -120, -10], tool, MachineTypes.JobModesFor(CompactTravel));

    private static JobStartPlan Plan(JobStartOptions options, int? tool = 3) => JobStartPlan.Create(options, Context(tool), "/sd/gcodes/a.nc");

    [Fact]
    public void A_start_without_probing_has_no_preparation()
    {
        Assert.Null(Plan(new JobStartOptions()).Preparation);
        Assert.Null(Plan(new JobStartOptions { WorkOrigin = WorkOriginChoice.BedCorner }).Preparation);
    }

    [Fact]
    public void Probing_lists_its_steps_in_order_with_the_tool_changes_around_it()
    {
        var preparation = Plan(new JobStartOptions { ZProbe = true, Leveling = true, LevelingPointsX = 5, LevelingPointsY = 4 })
            .Preparation!;

        Assert.Equal(["Change to the probe", "Z probe", "Auto leveling 5 × 4", "Change back to the tool"], preparation.Steps.Select(s => s.Name));
        Assert.Equal(preparation.Steps.Sum(s => s.EstimatedSeconds), preparation.EstimatedSeconds);
        Assert.StartsWith(JobPreparation.FingerprintPrefix, preparation.Fingerprint);
    }

    [Fact]
    public void The_same_probing_has_the_same_fingerprint_and_other_probing_another()
    {
        var options = new JobStartOptions { ZProbe = true, ZProbeOffsetX = 5, ZProbeOffsetY = 6 };
        var first = Plan(options).Preparation!.Fingerprint;

        Assert.Equal(first, Plan(options).Preparation!.Fingerprint);
        options.ZProbeOffsetX = 7;
        Assert.NotEqual(first, Plan(options).Preparation!.Fingerprint);
        Assert.NotEqual(first, Plan(new JobStartOptions { ZProbe = true, ZProbeOffsetX = 5, ZProbeOffsetY = 6 }, tool: null).Preparation!.Fingerprint);
    }
}

public class PreparationTimelineTests
{
    // Line 1: M6 T1 (20 s), 2-3: cutting T1 (2 × 6 s), 4: M6 T2 (20 s), 5: cutting T2 (6 s)
    private static readonly GCodeAnalysis Analysis = GCodeEstimator.Analyze(
        "M6 T1\nG1 X60 F600\nG1 X0\nM6 T2\nG1 X60", new EstimatorSettings(ToolChangeSeconds: 20));

    private static readonly JobPreparation Probing = new("prep:test", [new PreparationStep("Z probe", 30)]);

    private static readonly DateTime Now = new(2026, 1, 1, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Probing_comes_before_the_program_on_the_timeline()
    {
        var timeline = JobTimeline.Estimated(Analysis).WithPreparation(Probing, null);

        Assert.Equal(30, timeline.PreparationSeconds);
        Assert.Equal(30 + 58, timeline.TotalSeconds, 3);
        Assert.Equal([30d, 62d], timeline.Segments.Select(s => Math.Round(s.StartSeconds, 3)));
        Assert.Equal(30 + 26, timeline.SecondsBefore(3), 3);
        Assert.Equal(58, timeline.Times.Total, 3); // the program's own times stay as they are

        var again = timeline.WithPreparation(null, null);
        Assert.Equal([0d, 32d], again.Segments.Select(s => Math.Round(s.StartSeconds, 3)));
    }

    [Fact]
    public void A_measured_preparation_replaces_its_estimate()
    {
        var measured = new OperationStatistics(Probing.Fingerprint, 2, 45, Now, true);
        var timeline = JobTimeline.Estimated(Analysis).WithPreparation(Probing, measured);

        Assert.True(timeline.Preparation!.IsMeasured);
        Assert.Equal(45 + 58, timeline.TotalSeconds, 3);
    }

    [Fact]
    public void While_probing_the_job_is_before_the_program_and_every_tool_change_is_ahead()
    {
        var timeline = JobTimeline.Estimated(Analysis).WithPreparation(Probing, null);

        var probing = timeline.WhilePreparing(10, 100);
        Assert.True(probing.Preparing);
        Assert.Equal(10, probing.DoneSeconds);
        Assert.Equal(20 + 58, probing.RemainingSeconds, 3);

        var late = timeline.WhilePreparing(50, 100); // takes longer than expected: waits at the program's start
        Assert.Equal(30, late.DoneSeconds);
        Assert.Equal(58, late.RemainingSeconds, 3);

        var schedule = ToolChangeSchedule.Create(timeline, probing, Now, new Dictionary<int, DateTime>());
        Assert.All(schedule.Changes, c => Assert.Equal(ToolChangeTiming.Upcoming, c.Timing));
        Assert.Equal(20, schedule.Changes[0].InSeconds!.Value, 3);
        Assert.Same(schedule.Changes[0], schedule.Next);
        Assert.Equal(20 + 32, schedule.Changes[1].InSeconds!.Value, 3);
        Assert.Equal(Now.AddSeconds(20 + 58), schedule.FinishUtc);

        var cutting = timeline.At(2, 100);
        Assert.Equal(30 + 20, cutting.DoneSeconds, 3);
        Assert.Equal(38, cutting.RemainingSeconds, 3);
    }
}

public class PreparationRecorderTests
{
    private static readonly GCodeAnalysis Analysis = GCodeEstimator.Analyze(
        "M6 T1\nG1 X60 F600\nG1 X0\nM6 T2\nG1 X60", new EstimatorSettings(ToolChangeSeconds: 20));

    private static readonly JobPreparation Probing = new("prep:test", [new PreparationStep("Z probe", 30)]);

    private static readonly DateTime T0 = new(2026, 1, 1, 14, 0, 0, DateTimeKind.Utc);

    private static MachineSample At(double seconds, bool playing, int line, string state = "Run") =>
        new(T0.AddSeconds(seconds), playing, line, state, 100);

    /// <summary>Idle, then playing with line 0 for 40 s (10 s of it waiting for the probe), then the program's line 2.</summary>
    private static IEnumerable<MachineSample> Job() =>
    [
        At(0, false, 0, "Idle"),
        .. Enumerable.Range(1, 40).Select(t => At(t, true, 0, t is >= 10 and < 20 ? "Tool" : "Run")),
        At(41, true, 2),
        At(42, true, 3),
    ];

    [Fact]
    public void Probing_is_timed_from_play_to_the_program_without_its_first_lines_and_waits()
    {
        var recorder = new PreparationRecorder(Probing, Analysis);
        var runs = Job().Select(recorder.Observe).OfType<PreparationRun>().ToList();

        var run = Assert.Single(runs);
        Assert.Same(Probing, run.Preparation);
        Assert.Equal(40 - 10 - 20, run.ActiveSeconds, 3); // the program's M6 (20 s estimate) ran before line 2
        Assert.Equal(10, run.PausedSeconds, 3);
        Assert.Equal(100, run.AverageFeedOverridePercent, 3);
    }

    [Fact]
    public void A_job_seen_from_the_middle_or_interrupted_is_not_measured()
    {
        var fromMiddle = new PreparationRecorder(Probing, Analysis);
        Assert.Empty(Job().Skip(5).Select(fromMiddle.Observe).OfType<PreparationRun>());

        var alarm = new PreparationRecorder(Probing, Analysis);
        Assert.Empty(Job().Select(s => s.TimestampUtc == T0.AddSeconds(30) ? s with { State = "Alarm" } : s)
            .Select(alarm.Observe).OfType<PreparationRun>());

        var gap = new PreparationRecorder(Probing, Analysis);
        Assert.Empty(Job().Where(s => s.TimestampUtc < T0.AddSeconds(20) || s.TimestampUtc > T0.AddSeconds(35))
            .Select(gap.Observe).OfType<PreparationRun>());
    }
}
