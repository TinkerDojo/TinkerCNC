using TinkerCNC.Motion;

namespace TinkerCNC.Tests;

public class GoToTests
{
    private static readonly MachineTravel Travel = MachineTravel.FromConfig(new Dictionary<string, string?>
    {
        ["soft_endstop.x_min"] = "-371.0", ["soft_endstop.y_min"] = "-250.0", ["soft_endstop.z_min"] = "-135.0",
        ["coordinate.anchor1_x"] = "-360.158", ["coordinate.anchor1_y"] = "-234.568", ["coordinate.clearance_z"] = "-3.0",
    })!;

    // Tool at machine (-200, -120, -50); work origin at machine (-320, -220, -60).
    private static readonly StatusReport Status = StatusReport.Parse("<Idle|MPos:-200,-120,-50,0|WPos:120,100,10,0>");

    [Fact]
    public void Travel_comes_from_the_soft_limits_with_home_at_zero()
    {
        Assert.Equal(new AxisRange(-371, 0), Travel.X);
        Assert.Equal(new AxisRange(-250, 0), Travel.Y);
        Assert.Equal(new AxisRange(-135, 0), Travel.Z);
        Assert.Equal(-3, Travel.ClearanceZ);
        Assert.Null(MachineTravel.FromConfig(new Dictionary<string, string?> { ["soft_endstop.x_min"] = "-371" }));
    }

    [Fact]
    public void Work_coordinates_are_converted_and_moved_at_clearance_height()
    {
        var plan = GoToPlanner.Plan(new GoToRequest(10, 20, 5, CoordinateFrame.Work), Status, Travel);

        Assert.True(plan.IsValid);
        Assert.Equal(["G90", "G53 G0 Z-3", "G53 G0 X-310 Y-200", "G53 G0 Z-55"], plan.Commands);
        Assert.Equal([-310.0, -200.0, -55.0], plan.Target);
    }

    [Fact]
    public void Without_z_the_tool_stays_at_clearance_height()
    {
        var plan = GoToPlanner.Plan(new GoToRequest(-100, null, null, CoordinateFrame.Machine), Status, Travel);

        Assert.Equal(["G90", "G53 G0 Z-3", "G53 G0 X-100 Y-120"], plan.Commands);
        Assert.Equal(-3.0, plan.Target[2]);
    }

    [Fact]
    public void Straight_move_when_not_lifting()
    {
        var plan = GoToPlanner.Plan(new GoToRequest(null, null, -60, CoordinateFrame.Machine, LiftFirst: false), Status, Travel);
        Assert.Equal(["G90", "G53 G0 X-200 Y-120 Z-60"], plan.Commands);
    }

    [Fact]
    public void Positions_outside_the_travel_are_refused_per_axis()
    {
        var plan = GoToPlanner.Plan(new GoToRequest(400, -260, 0, CoordinateFrame.Work), Status, Travel);

        Assert.False(plan.IsValid);
        Assert.Empty(plan.Commands);
        Assert.Equal([Axis.X, Axis.Y], plan.Errors.Keys.Order());
        Assert.Equal("X must be between -51 and 320 (work coordinates).", plan.Errors[Axis.X].ToString());
        Assert.Equal(new AxisRange(-30, 220), GoToPlanner.RangeIn(CoordinateFrame.Work, Axis.Y, Travel, Status));
    }

    [Fact]
    public void Limits_themselves_are_reachable()
    {
        var plan = GoToPlanner.Plan(new GoToRequest(-371, 0, -135, CoordinateFrame.Machine, LiftFirst: false), Status, Travel);
        Assert.True(plan.IsValid);
    }
}

public class BedSweepTests
{
    // Compact-machine travel: X -207…0, Y -206…0, Z -102…0, clearance Z -3.
    private static readonly MachineTravel Travel = MachineTravel.FromConfig(new Dictionary<string, string?>
    {
        ["soft_endstop.x_min"] = "-207", ["soft_endstop.y_min"] = "-206", ["soft_endstop.z_min"] = "-102",
        ["coordinate.clearance_z"] = "-3",
    })!;

    [Fact]
    public void A_pass_starts_at_the_back_corner_and_zigzags_to_the_front_within_the_inset_travel()
    {
        var pass = BedSweepPlanner.Pass(Travel, -3, new BedSweepSettings(LaneSpacingMm: 51, EdgeInsetMm: 2, FeedMmPerMin: 3000));

        Assert.Equal("G53 G0 X-2 Y-2 Z-3", pass[0].Command);
        Assert.Equal(
        [
            "G53 G1 X-205 Y-2 Z-3 F3000",
            "G53 G1 X-205 Y-52.5 Z-3 F3000", "G53 G1 X-2 Y-52.5 Z-3 F3000",
            "G53 G1 X-2 Y-103 Z-3 F3000", "G53 G1 X-205 Y-103 Z-3 F3000",
            "G53 G1 X-205 Y-153.5 Z-3 F3000", "G53 G1 X-2 Y-153.5 Z-3 F3000",
            "G53 G1 X-2 Y-204 Z-3 F3000", "G53 G1 X-205 Y-204 Z-3 F3000",
        ], pass.Skip(1).Select(m => m.Command));
        Assert.All(pass, m => Assert.Equal(-3.0, m.Target[2]));
    }

    [Fact]
    public void Lanes_are_never_further_apart_than_the_spacing()
    {
        var pass = BedSweepPlanner.Pass(Travel, -3, BedSweepSettings.Default);
        var lanes = pass.Select(m => m.Target[1]).Distinct().ToList();

        Assert.Equal(-2, lanes[0]);
        Assert.Equal(-204, lanes[^1]);
        Assert.All(lanes.Zip(lanes.Skip(1)), p => Assert.InRange(p.First - p.Second, 0.1, BedSweepSettings.Default.LaneSpacingMm));
    }

    [Fact]
    public void Lifts_only_when_below_the_clearance_height()
    {
        Assert.Equal(["G53 G0 X-100 Y-80 Z-3"], BedSweepPlanner.Lift([-100, -80, -60], Travel).Select(m => m.Command));
        Assert.Empty(BedSweepPlanner.Lift([-100, -80, -1], Travel));
    }

    [Fact]
    public void Returns_across_at_the_sweep_height_then_straight_down()
    {
        var moves = BedSweepPlanner.Return([-100, -80, -60], -3);

        Assert.Equal(["G53 G0 X-100 Y-80 Z-3", "G53 G0 X-100 Y-80 Z-60"], moves.Select(m => m.Command));
        Assert.Equal(["G53 G0 X-100 Y-80 Z-1"], BedSweepPlanner.Return([-100, -80, -1], -1).Select(m => m.Command));
    }

    [Fact]
    public void A_move_takes_its_length_at_its_feed()
    {
        var move = MachineMove.Feed(-100, 0, 0, 3000);
        Assert.Equal(TimeSpan.FromSeconds(2), move.DurationFrom([0, 0, 0]));
    }
}

public class AirOutputTests
{
    private static MachineTravel TravelOf(string xMin, string yMin) => MachineTravel.FromConfig(new Dictionary<string, string?>
    {
        ["soft_endstop.x_min"] = xMin, ["soft_endstop.y_min"] = yMin, ["soft_endstop.z_min"] = "-100",
    })!;

    [Fact]
    public void A_z1_blows_with_the_spindle_blower()
    {
        var air = MachineTypes.AirFor(TravelOf("-207", "-206"));

        Assert.Equal("M811 S100", air.OnCommand);
        Assert.Equal("M812", air.OffCommand);
    }

    [Fact]
    public void Other_machines_use_compressed_air()
    {
        var air = MachineTypes.AirFor(TravelOf("-371", "-250"));

        Assert.Equal("M7", air.OnCommand);
        Assert.Equal("M9", air.OffCommand);
    }
}
