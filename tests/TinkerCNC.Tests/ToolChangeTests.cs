namespace TinkerCNC.Tests;

public class ToolChangeTests
{
    [Fact]
    public void Tool_field_carries_the_target_of_a_manual_change()
    {
        var status = StatusReport.Parse("<Tool|MPos:0,0,0,0|WPos:0,0,0,0|T:1,12.345,3>");

        Assert.True(ManualToolChange.IsWaiting(status));
        Assert.Equal(new ToolStatus(1, 12.345, 3), status.Tool);
    }

    [Fact]
    public void Missing_or_negative_target_means_no_change_pending()
    {
        Assert.Null(StatusReport.Parse("<Run|T:2,0.000,-1>").Tool!.Target);
        Assert.Equal(new ToolStatus(2, null, null), StatusReport.Parse("<Idle|T:2>").Tool);
        Assert.Null(StatusReport.Parse("<Idle|MPos:0,0,0>").Tool);
        Assert.False(ManualToolChange.IsWaiting(StatusReport.Parse("<Run|T:2,0.000,-1>")));
    }

    [Theory]
    [InlineData(0, "the probe")]
    [InlineData(8888, "the laser module")]
    [InlineData(9999, "the 3D probe")]
    [InlineData(4, "T4")]
    public void Reserved_tool_numbers_have_names(int tool, string name) =>
        Assert.Equal(name, ManualToolChange.NameOf(tool).ToString());
}

public class MachineConfigTests
{
    [Fact]
    public void Config_get_replies_are_recognised_and_parsed()
    {
        const string key = MachineConfig.AfterSuspendGCode;
        Assert.Equal("config-get sd after_suspend_gcode", MachineConfig.GetCommand(key));

        Assert.True(MachineConfig.IsReplyFor("sd: after_suspend_gcode is set to G91_G0Z5_G90\r\n", key));
        Assert.Equal("G91_G0Z5_G90", MachineConfig.ValueIn("sd: after_suspend_gcode is set to G91_G0Z5_G90\r\n", key));

        Assert.True(MachineConfig.IsReplyFor("sd: after_suspend_gcode is not in config", key));
        Assert.Null(MachineConfig.ValueIn("sd: after_suspend_gcode is not in config", key));

        Assert.False(MachineConfig.IsReplyFor("sd: before_resume_gcode is not in config", key));
        Assert.False(MachineConfig.IsReplyFor("ok", key));
    }
}

public class StatusFieldTests
{
    [Fact]
    public void Fields_get_names_and_units()
    {
        var feed = StatusFields.Describe("F", "0.0,3000.0,100.0");
        Assert.Equal("Feed", feed.Name.ToString());
        Assert.Equal([("current", "0.0 mm/min"), ("programmed", "3000.0 mm/min"), ("override", "100.0 %")], feed.Values.Select(English));

        var laser = StatusFields.Describe("L", "0, 0, 0, 0.0,100.0");
        Assert.Equal("off", laser.Values[0].Text.ToString());
        Assert.Equal("100.0 %", laser.Values[4].Text.ToString());
    }

    [Fact]
    public void Values_the_firmware_does_not_explain_are_not_guessed()
    {
        var spindle = StatusFields.Describe("S", "0.0,10000.0,100.0,0,38.3,25.7,0,0,0,0");
        Assert.Equal(("spindle temperature", "38.3 °C"), English(spindle.Values[4]));
        Assert.Equal(("value 10", "0"), English(spindle.Values[9]));

        var unknown = StatusFields.Describe("Q", "5");
        Assert.False(unknown.IsKnown);
        Assert.Equal("Q", unknown.Name.ToString());
        Assert.Equal((null, "5"), (unknown.Values[0].Label, unknown.Values[0].Text.ToString()));
    }

    [Fact]
    public void Positions_are_left_out_and_known_fields_come_first()
    {
        var status = StatusReport.Parse("<Idle|MPos:0,0,0|WPos:0,0,0|Q:1|T:6,0.335,-1|F:0,3000,100>");
        Assert.Equal(["F", "T", "Q"], StatusFields.Describe(status).Select(f => f.Key));
    }

    [Fact]
    public void Names_and_switches_follow_the_language()
    {
        var german = System.Globalization.CultureInfo.GetCultureInfo("de");
        var laser = StatusFields.Describe("L", "1,0");
        Assert.Equal("Lasermodus", laser.Values[0].Label!.Resolve(german));
        Assert.Equal("an", laser.Values[0].Text.Resolve(german));
        Assert.Equal("aus", laser.Values[1].Text.Resolve(german));
    }

    private static (string? Label, string Text) English(DescribedValue value) => (value.Label?.ToString(), value.Text.ToString());
}
