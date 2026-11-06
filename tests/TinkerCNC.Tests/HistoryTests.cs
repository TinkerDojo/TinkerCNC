using TinkerCNC.GCode;
using TinkerCNC.History;
using TinkerCNC.Jobs;

namespace TinkerCNC.Tests;

public class OperationFingerprintTests
{
    [Fact]
    public void Formatting_comments_and_line_numbers_do_not_change_the_fingerprint()
    {
        var a = OperationFingerprint.Compute(new[] { "M6 T1", "G1 X10.000 Y5 F600 ; cut", ";@MKR|TOOLPATH_START|toolpath_number=3" });
        var b = OperationFingerprint.Compute(new[] { "N10 m6t1", "", "g1x10y5.0f600 (cut)" });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_moves_give_different_fingerprints()
    {
        Assert.NotEqual(OperationFingerprint.Compute(new[] { "G1 X10" }), OperationFingerprint.Compute(new[] { "G1 X11" }));
    }

    [Fact]
    public void Segments_start_cutting_after_the_tool_change()
    {
        var analysis = GCodeEstimator.Analyze("M6 T1\nG1 X60 F600\nM6 T2\nG1 X0 F600", new EstimatorSettings(ToolChangeSeconds: 20));
        var first = analysis.Segments[0];
        Assert.Equal((1, 2, 2), (first.StartLine, first.CuttingStartLine, first.EndLine));
        Assert.Equal(20, first.ToolChangeSeconds, 3);
        Assert.Equal(6, first.CuttingSeconds, 3);
    }
}

public class OperationRecorderTests
{
    // Line 1: M6 T1 (20 s), 2-3: cutting T1 (2 × 6 s), 4: M6 T2, 5: cutting T2 (6 s)
    private static readonly GCodeAnalysis Program = GCodeEstimator.Analyze(
        "M6 T1\nG1 X60 F600\nG1 X0\nM6 T2\nG1 X60",
        new EstimatorSettings(ToolChangeSeconds: 20));

    /// <summary>One phase of a simulated run: <paramref name="Seconds"/> of status samples every 0.5 s.</summary>
    private sealed record Phase(
        double Seconds, int Line, string State = "Run", double Override = 100, bool Playing = true, bool FirmwareMove = false);

    private static List<OperationRun> Run(params Phase[] phases) => Run(Program, phases);

    private static List<OperationRun> Run(GCodeAnalysis program, params Phase[] phases)
    {
        var recorder = new OperationRecorder(program);
        var clock = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var runs = new List<OperationRun>();
        foreach (var phase in phases)
        {
            for (var t = 0.0; t < phase.Seconds; t += 0.5)
            {
                runs.AddRange(recorder.Observe(new MachineSample(clock, phase.Playing, phase.Line, phase.State, phase.Override, phase.FirmwareMove)));
                clock = clock.AddSeconds(0.5);
            }
        }
        return runs;
    }

    private static Phase Stopped(double seconds = 1) => new(seconds, 0, "Idle", Playing: false);

    [Fact]
    public void Line_stuck_on_the_last_feed_move_during_the_tool_change_ends_the_operation_after_its_estimate()
    {
        // Current firmware: line 0 during the first tool change, then the executing feed move; line 3 stays
        // reported through the next tool change until T2's first feed move.
        var runs = Run(Stopped(), new Phase(20, 0), new Phase(7, 2), new Phase(60, 3), new Phase(6, 5), Stopped());

        Assert.Equal(new int?[] { 1, 2 }, runs.Select(r => r.Segment.ToolNumber));
        Assert.Equal(7 + 6, runs[0].ActiveSeconds, 1); // line 3's estimate, not the 60 s it stayed reported
        Assert.Equal(6, runs[1].ActiveSeconds, 1);
    }

    [Fact]
    public void Nothing_is_measured_during_the_tool_change()
    {
        var recorder = new OperationRecorder(Program);
        var clock = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        void Feed(double seconds, int line)
        {
            for (var t = 0.0; t < seconds; t += 0.5, clock = clock.AddSeconds(0.5))
                recorder.Observe(new MachineSample(clock, true, line, "Run", 100));
        }

        Feed(1, 0);
        Feed(7, 2);
        Feed(3, 3);
        Assert.Equal(1, recorder.Measuring?.ToolNumber);
        Feed(20, 3);
        Assert.Null(recorder.Measuring);
    }

    [Fact]
    public void Older_firmware_ends_the_operation_when_the_line_leaves_it()
    {
        var run = Assert.Single(Run(Stopped(), new Phase(20, 1), new Phase(9, 2), new Phase(4, 3), new Phase(5, 4)));
        Assert.Equal(1, run.Segment.ToolNumber);
        Assert.Equal(13, run.ActiveSeconds, 1);
    }

    [Fact]
    public void Waiting_for_a_manual_tool_change_ends_the_operation_at_once()
    {
        var run = Run(new Phase(5, 1), new Phase(7, 2), new Phase(2, 3), new Phase(30, 3, "Tool"), new Phase(6, 5))[0];
        Assert.Equal(9, run.ActiveSeconds, 1);
    }

    [Fact]
    public void The_automatic_tool_change_ends_an_operation_whose_last_feed_move_was_not_sampled()
    {
        // Line 3 (0.2 s) ran between two samples; the next sample already shows the ATC's firmware moves (line 0,
        // reported with the previous line 2 kept).
        var program = GCodeEstimator.Analyze("M6 T1\nG1 X60 F600\nG1 X58\nM6 T2\nG1 X60", new EstimatorSettings(ToolChangeSeconds: 20));
        var run = Run(program, Stopped(), new Phase(20, 0), new Phase(6, 2), new Phase(30, 2, FirmwareMove: true), new Phase(6, 5))[0];
        Assert.Equal(1, run.Segment.ToolNumber);
        Assert.Equal(6, run.ActiveSeconds, 1);
    }

    [Fact]
    public void A_job_started_in_the_middle_of_an_operation_does_not_record_it()
    {
        var program = GCodeEstimator.Analyze("M6 T1\nG1 X60 F600\nG1 X0\nG1 X60\nM6 T2\nG1 X0", new EstimatorSettings(ToolChangeSeconds: 20));
        Assert.Empty(Run(program, Stopped(), new Phase(5, 3), new Phase(6, 4), new Phase(20, 4, FirmwareMove: true)));
    }

    [Fact]
    public void Moves_before_the_first_reported_feed_move_are_added_from_the_estimate()
    {
        // Line 2: G0 300 mm at 3000 mm/min (6 s), line 3: G1 300 mm at 600 mm/min (30 s).
        var program = GCodeEstimator.Analyze("M6 T1\nG0 X300\nG1 X0 F600\nM6 T2\nG1 X60", new EstimatorSettings(ToolChangeSeconds: 20));
        var run = Run(program, Stopped(), new Phase(26, 0), new Phase(40, 3), new Phase(6, 5))[0];
        Assert.Equal(6 + 30, run.ActiveSeconds, 1);
    }

    [Fact]
    public void Pauses_are_excluded_and_feed_override_is_averaged()
    {
        var run = Assert.Single(Run(new Phase(5, 1), new Phase(5, 2, Override: 200), new Phase(60, 2, "Hold"), new Phase(5, 3), new Phase(2, 4)));
        Assert.Equal(60, run.PausedSeconds, 1);
        Assert.Equal(10, run.ActiveSeconds, 1);
        Assert.Equal(150, run.AverageFeedOverridePercent, 1);
    }

    [Fact]
    public void Operations_joined_in_the_middle_are_not_recorded()
    {
        Assert.Empty(Run(new Phase(5, 2), new Phase(5, 3), new Phase(5, 4)));
    }

    [Fact]
    public void Alarms_and_gaps_discard_the_measurement()
    {
        Assert.Empty(Run(new Phase(2, 1), new Phase(5, 2), new Phase(1, 2, "Alarm"), new Phase(5, 4)));

        var recorder = new OperationRecorder(Program);
        var t = DateTime.UtcNow;
        var samples = new[] { (0.0, 1), (0.5, 2), (1.0, 2), (40.0, 3), (40.5, 4) };
        Assert.Empty(samples.SelectMany(s => recorder.Observe(new MachineSample(t.AddSeconds(s.Item1), true, s.Item2, "Run", 100))));
    }

    [Fact]
    public void Last_operation_counts_when_playback_ends_near_its_end()
    {
        Assert.Equal(2, Assert.Single(Run(new Phase(20, 4), new Phase(6, 5), Stopped())).Segment.ToolNumber);
        Assert.Empty(Run(new Phase(20, 1), new Phase(2, 2), Stopped())); // aborted early
    }
}

public class MachineLineTests
{
    [Fact]
    public void Machine_line_numbers_skip_empty_and_overlong_lines_like_the_firmware()
    {
        var text = "G0 X0\n\nG1 X1\r\n\r\n; " + new string('-', MachineLineMap.MaxLineBytes) + "\nG1 X2";
        var map = GCodeEstimator.Analyze(text).MachineLines;

        Assert.Equal(new[] { 0, 1, 3, 4, 6 }, new[] { 0, 1, 2, 3, 4 }.Select(map.ToFileLine));
        Assert.Equal(3, map.ToMachineLine(5)); // a skipped line reports the line before it
        Assert.Equal(4, map.ToMachineLine(6));
    }

    [Fact]
    public void Line_zero_during_the_automatic_tool_change_keeps_the_last_line()
    {
        var now = DateTime.UtcNow;
        var cutting = MachineSample.From(StatusReport.Parse("<Run|MPos:0,0,0|P:42,10,5>"), now);
        var changing = MachineSample.From(StatusReport.Parse("<Run|MPos:0,0,0|P:0,10,6>"), now.AddSeconds(1), cutting);
        var started = MachineSample.From(StatusReport.Parse("<Run|MPos:0,0,0|P:0,0,1>"), now, previous: null);

        Assert.Equal((42, true), (changing.Line, changing.FirmwareMove));
        Assert.Equal((0, false), (started.Line, started.FirmwareMove));
    }

    [Fact]
    public void Operations_know_their_last_feed_move()
    {
        var analysis = GCodeEstimator.Analyze("M6 T1\nG1 X60 F600\nG1 X0\nG0 Z10\nM5\nM6 T2\nG1 X60");
        Assert.Equal((3, 5), (analysis.Segments[0].LastFeedLine, analysis.Segments[0].EndLine));
    }
}

public class OperationHistoryTests
{
    [Fact]
    public void Records_runs_and_reports_the_median_preferring_this_machine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tinkercnc_{Guid.NewGuid():N}.db");
        try
        {
            var history = new OperationHistory(path);
            OperationRunRecord Run(string machine, double active, double over = 100) =>
                new("abc", machine, "part.nc", 1, 10, 50, active, 0, over, DateTime.UtcNow);

            history.Record(Run("Workshop", 40));
            history.Record(Run("Workshop", 30, over: 200)); // 60 s at 100 %
            history.Record(Run("Workshop", 44));
            history.Record(Run("Garage", 90));

            var local = history.GetStatistics(new[] { "abc", "unknown" }, "Workshop");
            Assert.Equal(3, local["abc"].Runs);
            Assert.Equal(44, local["abc"].MedianSeconds, 3);
            Assert.True(local["abc"].FromThisMachine);
            Assert.False(local.ContainsKey("unknown"));

            var other = new OperationHistory(path).GetStatistics(new[] { "abc" }, "Air"); // reopening re-runs migrations safely
            Assert.False(other["abc"].FromThisMachine);
            Assert.Equal(4, other["abc"].Runs);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void Measured_operations_replace_the_estimate_on_the_timeline()
    {
        var analysis = GCodeEstimator.Analyze("M6 T1\nG1 X60 F600\nG1 X0\nM6 T2\nG1 X60", new EstimatorSettings(ToolChangeSeconds: 20));
        var first = analysis.Segments[0];
        var timeline = JobTimeline.Create(analysis, new Dictionary<string, OperationStatistics>
        {
            [first.Fingerprint] = new(first.Fingerprint, 2, 30, DateTime.UtcNow, true),
        });

        Assert.True(timeline.Segments[0].IsMeasured);
        Assert.Equal(20 + 30, timeline.Segments[0].Seconds, 3);     // estimated tool change + measured cutting
        Assert.Equal(26, timeline.Segments[1].Seconds, 3);          // still estimated
        Assert.Equal(15 + 20, timeline.Times.SecondsBefore(3), 3);  // first cutting line scaled 6 → 15 s
    }
}

public class ToolChangeScheduleTests
{
    // Line 1: M6 T1 (20 s), 2-3: cutting T1 (2 × 6 s), 4: M6 T2 (20 s), 5: cutting T2 (6 s)
    private static readonly JobTimeline Timeline = JobTimeline.Estimated(GCodeEstimator.Analyze(
        "M6 T1\nG1 X60 F600\nG1 X0\nM6 T2\nG1 X60", new EstimatorSettings(ToolChangeSeconds: 20)));

    private static readonly DateTime Now = new(2026, 1, 1, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Upcoming_tool_changes_get_a_clock_time_scaled_by_feed_override()
    {
        var schedule = ToolChangeSchedule.Create(Timeline, Timeline.At(2, 200), Now, new Dictionary<int, DateTime>());

        var t2 = schedule.Changes[1];
        Assert.Equal(ToolChangeTiming.Upcoming, t2.Timing);
        Assert.Equal(6, t2.InSeconds!.Value, 3);              // lines 2-3 = 12 s at 200 %
        Assert.Equal(Now.AddSeconds(6), t2.ClockUtc);
        Assert.Equal(32, t2.OffsetSeconds, 3);
        Assert.Equal(Now.AddSeconds((12 + 26) / 2.0), schedule.FinishUtc);
    }

    [Fact]
    public void Past_changes_show_when_they_were_observed_and_idle_jobs_show_offsets()
    {
        var log = new ToolChangeLog(Timeline);
        log.Observe(new MachineSample(Now, false, 0, "Idle", 100));
        log.Observe(new MachineSample(Now.AddSeconds(0.5), true, 1, "Run", 100));

        var running = ToolChangeSchedule.Create(Timeline, Timeline.At(3, 100), Now.AddSeconds(30), log.Reached);
        Assert.Equal(ToolChangeTiming.Observed, running.Changes[0].Timing);
        Assert.Equal(Now.AddSeconds(0.5), running.Changes[0].ClockUtc);

        log.Observe(new MachineSample(Now.AddSeconds(26), true, 2, "Run", 100));
        log.Observe(new MachineSample(Now.AddSeconds(26.5), true, 3, "Run", 100)); // T1's last feed move: T2 is next
        var changing = ToolChangeSchedule.Create(Timeline, Timeline.At(3, 100), Now.AddSeconds(40), log.Reached);
        Assert.Equal(ToolChangeTiming.Observed, changing.Changes[1].Timing);
        Assert.Equal(Now.AddSeconds(26.5 + 6), changing.Changes[1].ClockUtc);

        var idle = ToolChangeSchedule.Create(Timeline, null, Now, log.Reached);
        Assert.All(idle.Changes, c => Assert.Equal(ToolChangeTiming.Planned, c.Timing));
        Assert.Null(idle.FinishUtc);
    }
}
