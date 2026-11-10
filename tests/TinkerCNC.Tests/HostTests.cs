using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using TinkerCNC.Host;
using TinkerCNC.Host.Localization;
using TinkerCNC.Host.Notifications;
using TinkerCNC.Localization;

namespace TinkerCNC.Tests;

public class NotificationTests
{
    private static StatusReport Status(string text) => StatusReport.Parse(text);

    [Fact]
    public void Tool_change_names_the_tool_from_the_program_once_known()
    {
        string? name = null;
        var note = new ToolChangeNotification(2, _ => name);
        Assert.Equal("The machine is waiting for T2. Insert it, then confirm to continue the job.", note.Message.ToString());

        name = "Ball Nose 2.0"; // the program finished downloading
        Assert.Equal("The machine is waiting for T2 (Ball Nose 2.0). Insert it, then confirm to continue the job.", note.Message.ToString());
        Assert.Equal("Insert T2", note.Title.ToString());
    }

    [Fact]
    public void Tool_change_resolves_when_the_machine_stops_waiting()
    {
        var note = new ToolChangeNotification(0, _ => null);
        note.Observe(Status("<Tool|T:1,0.000,0>"));
        Assert.True(((IMachineNotification)note).IsOpen);

        note.Observe(Status("<Run|T:0,0.000,-1>"));
        Assert.Equal("Done on the machine", note.Resolution?.ToString());
    }

    [Fact]
    public void An_answer_is_kept_when_the_state_changes_afterwards()
    {
        var note = new ToolChangeNotification(3, _ => null);
        note.Resolve(Text.Plain("Confirmed"));
        note.Observe(Status("<Run|T:3,0.000,-1>"));
        Assert.Equal("Confirmed", note.Resolution?.ToString());
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

/// <summary>Host and core texts are put into the language of the dashboard that shows them.</summary>
public class HostTextTests
{
    private static readonly System.Globalization.CultureInfo German = System.Globalization.CultureInfo.GetCultureInfo("de");
    private static readonly System.Globalization.CultureInfo French = System.Globalization.CultureInfo.GetCultureInfo("fr");

    [Fact]
    public void Host_texts_come_in_every_language()
    {
        var text = HostTexts.Of("Machine.NotConnected");
        Assert.Equal("The machine is not connected.", text.ToString());
        Assert.Equal("Die Maschine ist nicht verbunden.", text.Resolve(German));
        Assert.Equal("La machine n'est pas connectée.", text.Resolve(French));
    }

    [Fact]
    public void Core_texts_come_in_every_language() =>
        Assert.Equal(("Automatische Absaugung", "Aspiration automatique"),
            (Jobs.JobModes.Vacuum.Name.Resolve(German), Jobs.JobModes.Vacuum.Name.Resolve(French)));

    [Fact]
    public void Texts_inside_texts_take_the_same_language()
    {
        var note = new ToolChangeNotification(0, _ => null);
        Assert.Equal("Insert the probe", note.Title.ToString());
        Assert.Equal("den Taster einsetzen", note.Title.Resolve(German));
        Assert.Equal("Insérer le palpeur", note.Title.Resolve(French));
    }

    [Fact]
    public void Errors_keep_their_text_and_an_english_message()
    {
        var error = new QueueException(HostTexts.Of("Queue.EntryGone"));
        Assert.Equal("That entry is gone.", error.Message);
        Assert.Equal("Dieser Eintrag ist nicht mehr vorhanden.", Text.Of(error).Resolve(German));
        Assert.IsAssignableFrom<InvalidOperationException>(error);
    }

    [Fact]
    public void Relative_times_follow_the_language()
    {
        var ago = TimeFormat.Ago(DateTime.UtcNow.AddMinutes(-5).AddSeconds(-10));
        Assert.Equal("5m ago", ago.ToString());
        Assert.Equal("vor 5 min", ago.Resolve(German));
        Assert.Equal("il y a 5 min", ago.Resolve(French));
        Assert.Equal(("never", "nie", "jamais"),
            (TimeFormat.Ago(null).ToString(), TimeFormat.Ago(null).Resolve(German), TimeFormat.Ago(null).Resolve(French)));
    }

    [Fact]
    public void Clock_names_the_weekday_in_the_language()
    {
        var utc = DateTime.UtcNow.AddDays(-3);
        var day = German.DateTimeFormat.GetAbbreviatedDayName(utc.ToLocalTime().DayOfWeek);
        Assert.StartsWith(day + " ", TimeFormat.Clock(utc, German));
    }
}
