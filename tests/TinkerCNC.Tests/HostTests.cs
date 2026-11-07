using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using TinkerCNC.Host;
using TinkerCNC.Host.Notifications;

namespace TinkerCNC.Tests;

public class NotificationTests
{
    private static StatusReport Status(string text) => StatusReport.Parse(text);

    [Fact]
    public void Tool_change_names_the_tool_from_the_program_once_known()
    {
        string? name = null;
        var note = new ToolChangeNotification(2, _ => name);
        Assert.Equal("The machine is waiting for T2. Insert it, then confirm to continue the job.", note.Message);

        name = "Ball Nose 2.0"; // the program finished downloading
        Assert.Equal("The machine is waiting for T2 (Ball Nose 2.0). Insert it, then confirm to continue the job.", note.Message);
        Assert.Equal("Insert T2", note.Title);
    }

    [Fact]
    public void Tool_change_resolves_when_the_machine_stops_waiting()
    {
        var note = new ToolChangeNotification(0, _ => null);
        note.Observe(Status("<Tool|T:1,0.000,0>"));
        Assert.True(((IMachineNotification)note).IsOpen);

        note.Observe(Status("<Run|T:0,0.000,-1>"));
        Assert.Equal("Done on the machine", note.Resolution);
    }

    [Fact]
    public void An_answer_is_kept_when_the_state_changes_afterwards()
    {
        var note = new ToolChangeNotification(3, _ => null);
        note.Resolve("Confirmed");
        note.Observe(Status("<Run|T:3,0.000,-1>"));
        Assert.Equal("Confirmed", note.Resolution);
    }

    [Fact]
    public void Tool_change_is_raised_only_on_the_transition()
    {
        var source = new ToolChangeSource();
        var waiting = Status("<Tool|T:1,0.000,2>");
        Assert.NotNull(source.Observe(Status("<Run|T:1,0.000,-1>"), waiting, null!, null!));
        Assert.Null(source.Observe(waiting, Status("<Tool|T:1,0.000,2>"), null!, null!));
    }
}

public class AppPathsTests
{
    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static AppPaths Paths(params (string Key, string Value)[] settings) => new(
        new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build(),
        new Env("/app"));

    [Fact]
    public void Everything_lives_in_the_data_folder_by_default()
    {
        var paths = Paths(("TinkerCNC:DataDirectory", "/data"));
        Assert.Equal(Path.Combine("/data", "tinkercnc.db"), paths.HistoryDatabase);
        Assert.Equal(Path.Combine("/data", "gcode"), paths.GCodeCache);
        Assert.Equal(Path.Combine("/data", "programs"), paths.SetupFiles);
        Assert.Equal(Path.Combine("/data", "queue"), paths.QueuedFiles);
        Assert.Equal(Path.Combine("/data", "recordings"), paths.Recordings);
        Assert.Equal(Path.Combine("/data", "machines.json"), paths.ManualMachines);
        Assert.Equal(Path.Combine("/app", "App_Data", "machines.json"), paths.LegacyManualMachines);
    }

    [Fact]
    public void Single_locations_can_be_overridden()
    {
        var paths = Paths(("TinkerCNC:RecordingsDirectory", "/videos"));
        Assert.Equal("/videos", paths.Recordings);
        Assert.EndsWith("TinkerCNC", paths.DataDirectory);
    }
}
