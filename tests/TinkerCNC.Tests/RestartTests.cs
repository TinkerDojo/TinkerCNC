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
        Assert.Equal("part.nc from T3", restart.Description);
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
    public void A_queued_start_pauses_before_everything_else_that_is_buffered()
    {
        var context = new JobStartContext(CompactTravel, new ToolpathBounds(0, 0, -1, 10, 10, 1), null, 1, [], ProgramSelectsTool: false);
        var plan = JobStartPlan.Create(new JobStartOptions { ZProbe = true }, context, "/sd/gcodes/a.nc", waitForGo: true);

        Assert.Equal("buffer M600", plan.Commands[0]);
        Assert.StartsWith("buffer M495", plan.Commands[1]);
        Assert.Equal("play /sd/gcodes/a.nc", plan.Commands[^1]);
        Assert.DoesNotContain("buffer M600", JobStartPlan.Create(new JobStartOptions(), context, "/sd/gcodes/a.nc").Commands);
    }
}
