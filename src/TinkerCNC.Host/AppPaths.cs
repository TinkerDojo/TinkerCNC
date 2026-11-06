using TinkerCNC.Storage;

namespace TinkerCNC.Host;

/// <summary>
/// Where the app keeps its data. Everything lives in the per-user data folder (<see cref="UserDataFolders"/>), never
/// next to the app: an installed app's folder is replaced by every update. Each location can be overridden in the
/// <c>TinkerCNC</c> configuration section.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(IConfiguration config, IHostEnvironment env)
    {
        string Setting(string key, string fallback) => config[$"TinkerCNC:{key}"] is { Length: > 0 } value ? value : fallback;

        DataDirectory = Setting("DataDirectory", UserDataFolders.TinkerCNC);
        HistoryDatabase = Setting("HistoryDatabasePath", Path.Combine(DataDirectory, "tinkercnc.db"));
        GCodeCache = Setting("GCodeCacheDirectory", Path.Combine(DataDirectory, "gcode"));
        SavedPrograms = Setting("SavedProgramsDirectory", Path.Combine(DataDirectory, "programs"));
        Recordings = Setting("RecordingsDirectory", Path.Combine(DataDirectory, "recordings"));
        ManualMachines = Setting("ManualMachinesPath", Path.Combine(DataDirectory, "machines.json"));
        JobOptions = Setting("JobOptionsPath", Path.Combine(DataDirectory, "job-options.json"));
        LegacyManualMachines = Path.Combine(env.ContentRootPath, "App_Data", "machines.json");
    }

    public string DataDirectory { get; }

    /// <summary>The app's SQLite database: measured operation times and saved programs.</summary>
    public string HistoryDatabase { get; }

    /// <summary>Programs downloaded from machines, by MD5.</summary>
    public string GCodeCache { get; }

    /// <summary>The G-code files of saved programs; the database refers to them by file name only.</summary>
    public string SavedPrograms { get; }

    /// <summary>Camera recordings (.mjpeg) and snapshots.</summary>
    public string Recordings { get; }

    /// <summary>Machines added by hand (JSON).</summary>
    public string ManualMachines { get; }

    /// <summary>The options last used to start a program, per machine (JSON).</summary>
    public string JobOptions { get; }

    /// <summary>Where earlier versions kept the manual machines (next to the app); copied over once.</summary>
    public string LegacyManualMachines { get; }
}
