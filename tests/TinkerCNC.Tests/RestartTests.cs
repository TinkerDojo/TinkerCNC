using System.Text;
using TinkerCNC.GCode;
using TinkerCNC.Jobs;
using TinkerCNC.Motion;

namespace TinkerCNC.Tests;

public class ProgramRestartTests
{
    private const string Program = """
        ;@MKR|TIME|value=100
        G21 G90 G17 G54
        M7
        M6 T1
        M3 S12000
        G0 X0 Y0 Z5
        G1 Z-1 F600
        G1 X10
        M5
        M6 T2
        M4 S8000
        G55
        G1 Z-2 F300
        G1 X20
        M9
        M6 T3
        G0 Z5
        G1 X0 F800
        M30
        """;

    private static readonly GCodeAnalysis Analysis = GCodeEstimator.Analyze(Program);

    [Fact]
    public void Each_operation_knows_the_modal_state_the_program_was_in_when_it_began()
    {
        Assert.Equal(3, Analysis.Segments.Count);
        var second = Analysis.Segments[1].ModalAtStart;
        Assert.Equal((1.0, true, 17, 54, 600.0, 1, (int?)null, 12000.0, true, false),
            (second.UnitScale, second.Absolute, second.Plane, second.WorkOffset, second.FeedMmPerMin, second.MotionCode, second.Spindle,
                second.SpindleSpeed, second.Mist, second.Flood));
        var third = Analysis.Segments[2].ModalAtStart;
        Assert.Equal((55, 300.0, 4, 8000.0, false), (third.WorkOffset, third.FeedMmPerMin, third.Spindle, third.SpindleSpeed, third.Mist));
    }

    [Fact]
    public void The_trimmed_copy_restates_the_modal_state_and_keeps_the_lines_from_the_tool_change()
    {
        var restart = ProgramRestart.From(Analysis, "part.nc", Analysis.Segments[2]);

        Assert.Equal("part_from_T3.nc", restart.FileName);
        Assert.Equal("part.nc from T3", restart.Description.ToString());
        var lines = Encoding.UTF8.GetString(restart.Content).TrimEnd('\n').Split('\n');
        Assert.Equal("; TinkerCNC: part.nc from T3 (line 16 of 19)", lines[0]);
        Assert.Equal(["G21", "G90", "G17", "G55", "M4 S8000", "G1 F300", "M6 T3", "G0 Z5", "G1 X0 F800", "M30"], lines[1..]);
    }

    [Fact]
    public void A_spindle_that_is_off_and_inches_are_restated_as_such()
    {
        var analysis = GCodeEstimator.Analyze("G20 G91\nM6 T1\nG1 X1 F10\nG0 X0\nM5\nM6 T2\nG1 X2");
        var lines = Encoding.UTF8.GetString(ProgramRestart.From(analysis, "a.gcode", analysis.Segments[1]).Content).Split('\n');
        Assert.Equal(["G20", "G91", "G17", "G54", "G0 F10"], lines[1..6]); // the feed in the program's own unit, the motion mode in force
        Assert.Equal("a_from_T2.gcode", ProgramRestart.NameFor("a.gcode", analysis.Segments[1]));
    }

    [Fact]
    public void A_dwell_does_not_change_the_spindle_speed()
    {
        var analysis = GCodeEstimator.Analyze("M3 S5000\nG4 S2\nM6 T1\nG1 X1 F100\nM6 T2\nG1 X2");
        Assert.Equal(5000, analysis.Segments[1].ModalAtStart.SpindleSpeed);
    }
}

public class WaitForGoPlanTests
{
    private static readonly MachineTravel CompactTravel = new(
        new AxisRange(-207, 0), new AxisRange(-206, 0), new AxisRange(-102, 0), -190.3, -194.114, -3);

    [Fact]
    public void A_queued_start_holds_first_and_lifts_the_head_before_everything_else_that_is_buffered()
    {
        var context = new JobStartContext(CompactTravel, new ToolpathBounds(0, 0, -1, 10, 10, 1), null, 1, [], ProgramSelectsTool: false);
        var plan = JobStartPlan.Create(new JobStartOptions { ZProbe = true }, context, "/sd/gcodes/a.nc", waitForGo: true);

        Assert.True(plan.HoldFirst);
        Assert.Equal(["buffer G21", "buffer G53 G0 Z-4", "buffer G53 G0 Z-3"], plan.Commands.Take(3));
        Assert.StartsWith("buffer M495", plan.Commands[3]);
        Assert.Equal("play /sd/gcodes/a.nc", plan.Commands[^1]);
    }

    [Fact]
    public void A_start_by_hand_has_no_gate_and_both_share_the_measured_preparation()
    {
        var context = new JobStartContext(CompactTravel, new ToolpathBounds(0, 0, -1, 10, 10, 1), null, 1, [], ProgramSelectsTool: false);
        var options = new JobStartOptions { ZProbe = true };
        var byHand = JobStartPlan.Create(options, context, "/sd/gcodes/a.nc");
        var queued = JobStartPlan.Create(options, context, "/sd/gcodes/a.nc", waitForGo: true);

        Assert.False(byHand.HoldFirst);
        Assert.DoesNotContain(byHand.Commands, c => c.Contains("G53"));
        Assert.Equal(byHand.Preparation!.Fingerprint, queued.Preparation!.Fingerprint);
    }

    [Fact]
    public void The_gate_lifts_to_two_heights_so_one_of_them_always_moves()
    {
        var lifts = JobGate.Commands(CompactTravel).Where(c => c.StartsWith("G53")).ToList();

        Assert.Equal(2, lifts.Distinct().Count());
        Assert.True(JobGate.IsHeld(StatusReport.Parse("<Hold|MPos:0,0,0,0|WPos:0,0,0,0>")));
        Assert.False(JobGate.IsHeld(StatusReport.Parse("<Pause|MPos:0,0,0,0|WPos:0,0,0,0>")));
    }
}

public class ProgramResumeTests
{
    private const string Program = """
        G21 G90 G17 G54
        M6 T2
        M3 S12000
        M7
        G0 X0 Y0 Z5
        G1 Z-1 F600
        G1 X10
        G1 Y10
        G2 X20 Y20 I5 J5 F400
        G1 X30
        M30
        """;

    private static readonly GCodeAnalysis Analysis = GCodeEstimator.Analyze(Program);

    private static string[] Resume(int line, double[]? workZero = null) =>
        Encoding.UTF8.GetString(ProgramResume.From(Analysis, "part.nc", line, new ResumeContext(-3, workZero)).Content)
            .TrimEnd('\n').Split('\n');

    [Fact]
    public void Brings_the_machine_back_to_where_the_line_starts_then_goes_on_from_it()
    {
        var lines = Resume(8, [-120.5, -80, -60.25]);
        Assert.Equal(
        [
            "; TinkerCNC: part.nc resumed at line 8 of 11",
            "G21", "G90", "G17", "G54",
            "G10 L2 P0 X-120.5 Y-80 Z-60.25",
            "M6 T2",
            "G53 G0 Z-3",
            "G0 X10 Y0",          // where line 7 ended
            "M3 S12000", "G4 P4000",
            "M7",
            "G1 Z-1 F600",        // down at the feed in force
            "G21", "G90", "G1 F600",
            "G1 Y10", "G2 X20 Y20 I5 J5 F400", "G1 X30", "M30",
        ], lines);
    }

    [Fact]
    public void Keeps_the_work_origin_when_it_is_not_known() =>
        Assert.DoesNotContain(Resume(8), l => l.StartsWith("G10", StringComparison.Ordinal));

    [Fact]
    public void An_arc_resumes_from_its_start_point()
    {
        var lines = Resume(9);
        Assert.Contains("G0 X10 Y10", lines);
        Assert.Equal("G2 X20 Y20 I5 J5 F400", lines[lines.Length - 3]);
        Assert.Equal("part_resume_L9.nc", ProgramResume.From(Analysis, "part.nc", 9, new ResumeContext(-3, null)).FileName);
    }

    [Fact]
    public void Approaches_in_millimetres_and_absolute_then_restores_inches_and_incremental_moves()
    {
        var inches = GCodeEstimator.Analyze("G20 G91\nM3 S8000\nG1 X1 Y1 F10\nG1 X1\nG1 Y1\n");
        var text = Encoding.UTF8.GetString(ProgramResume.From(inches, "inch.nc", 5, new ResumeContext(-3, null)).Content);
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Contains("G0 X50.8 Y25.4", lines);                 // 2 in, 1 in, absolute
        Assert.Contains("G1 Z0 F254", lines);                     // 10 in/min
        var restore = Array.IndexOf(lines, "G20");
        Assert.True(restore > Array.IndexOf(lines, "G1 Z0 F254"));
        Assert.Equal(["G20", "G91", "G1 F10", "G1 Y1"], lines[restore..]);
    }
}
