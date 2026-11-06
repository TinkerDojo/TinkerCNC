using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using TinkerCNC.Host;
using TinkerCNC.Jobs;
using TinkerCNC.Programs;
using TinkerCNC.Storage;

namespace TinkerCNC.Tests;

/// <summary>A data folder of its own, removed afterwards.</summary>
public sealed class TempDataFolder : IDisposable
{
    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public TempDataFolder()
    {
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TinkerCNC:DataDirectory"] = Root }).Build(),
            new Env(Root));
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tinkercnc-test-" + Guid.NewGuid().ToString("N"));
    public AppPaths Paths { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }
}

public sealed class AppDatabaseTests : IDisposable
{
    private readonly TempDataFolder _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void A_database_with_only_the_history_gets_the_saved_programs_and_keeps_its_runs()
    {
        var path = _data.Paths.HistoryDatabase;
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE operation_runs (id INTEGER PRIMARY KEY AUTOINCREMENT, fingerprint TEXT NOT NULL, machine TEXT NOT NULL,
                    file_name TEXT, tool_number INTEGER, line_count INTEGER NOT NULL, estimated_seconds REAL NOT NULL,
                    active_seconds REAL NOT NULL, paused_seconds REAL NOT NULL, average_feed_override REAL NOT NULL,
                    seconds_at_100_percent REAL NOT NULL, recorded_utc TEXT NOT NULL);
                INSERT INTO operation_runs VALUES (1, 'abc', 'Workshop', 'a.nc', 1, 10, 50, 40, 0, 100, 40, '2026-01-01T00:00:00.0000000Z');
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        var db = new AppDatabase(path);

        Assert.Equal(1, new History.OperationHistory(db).GetStatistics(["abc"], "Workshop")["abc"].Runs);
        Assert.Empty(new SavedProgramRepository(db).List(includeDeleted: true));
    }

    [Fact]
    public void Every_option_column_exists_in_the_table()
    {
        var db = new AppDatabase(_data.Paths.HistoryDatabase);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('saved_programs')";
        using var reader = cmd.ExecuteReader();
        var columns = new HashSet<string>();
        while (reader.Read()) columns.Add(reader.GetString(0));

        Assert.All(OptionColumns.All, c => Assert.Contains(c.Name, columns));
    }
}

public class OptionColumnsTests
{
    [Fact]
    public void Every_start_option_and_job_mode_has_a_column()
    {
        // Each settable value must change at least one column, or saving would silently drop it.
        var baseline = new JobStartOptions();
        var properties = typeof(JobStartOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.Name != nameof(JobStartOptions.Modes));
        foreach (var property in properties)
        {
            var changed = baseline.Clone();
            property.SetValue(changed, Different(property.GetValue(baseline)!));
            Assert.False(OptionColumns.Same(baseline, changed), $"{property.Name} has no column");
        }

        foreach (var mode in typeof(JobModes).GetFields().Select(f => (JobMode)f.GetValue(null)!))
        {
            var changed = baseline.Clone();
            changed.Modes[mode.Key] = true;
            Assert.False(OptionColumns.Same(baseline, changed), $"job mode {mode.Key} has no column");
        }
    }

    [Fact]
    public void A_job_mode_that_is_missing_counts_as_off()
    {
        var a = new JobStartOptions { Modes = { ["blow"] = false } };
        Assert.True(OptionColumns.Same(a, new JobStartOptions()));
    }

    private static readonly IReadOnlyDictionary<Type, Func<object, object>> Changes = new Dictionary<Type, Func<object, object>>
    {
        [typeof(bool)] = v => !(bool)v,
        [typeof(int)] = v => (int)v + 1,
        [typeof(double)] = v => (double)v + 1.5,
        [typeof(WorkOriginChoice)] = v => (WorkOriginChoice)v == WorkOriginChoice.Keep ? WorkOriginChoice.XyzProbe : WorkOriginChoice.Keep,
    };

    private static object Different(object value) => Changes[value.GetType()](value);
}

public sealed class SavedProgramLibraryTests : IDisposable
{
    private static readonly byte[] Part = Encoding.ASCII.GetBytes("G0 X0 Y0\nG1 X10 F100\nM30\n");
    private static readonly byte[] PartV2 = Encoding.ASCII.GetBytes("G0 X0 Y0\nG1 X20 F100\nM30\n");

    private readonly TempDataFolder _data = new();
    private readonly SavedProgramLibrary _library;

    public SavedProgramLibraryTests()
    {
        _library = new SavedProgramLibrary(new SavedProgramRepository(new AppDatabase(_data.Paths.HistoryDatabase)), _data.Paths);
    }

    public void Dispose() => _data.Dispose();

    private static JobStartOptions Options() => new()
    {
        WorkOrigin = WorkOriginChoice.BedCorner, OriginOffsetX = 12.5, OriginOffsetY = -3, XyzProbeHeight = 10, XyzProbeToolDiameter = 6,
        ScanMargin = true, ZProbe = true, ZProbeFromWorkOrigin = true, ZProbeOffsetX = 59, ZProbeOffsetY = 39.25,
        Leveling = true, LevelingPointsX = 7, LevelingPointsY = 3, LevelingHeight = 2.5,
        Modes = { ["blow"] = true, ["bedclean"] = true, ["vacuum"] = false },
    };

    [Fact]
    public void A_saved_program_keeps_every_option_and_its_own_copy_of_the_file()
    {
        var saved = _library.Save("  Coaster  ", "oak, 3 mm bit", "my part.nc", Part, Options(), replace: false);

        Assert.Equal("Coaster", saved.Name);
        Assert.Equal("oak, 3 mm bit", saved.Notes);
        Assert.Equal("my part.nc", saved.OriginalFileName);
        Assert.Matches("^my part_[0-9a-f]{32}\\.nc$", saved.StoredFile);
        Assert.Equal(Path.Combine(_data.Paths.SavedPrograms, saved.StoredFile), _library.PathOf(saved));
        Assert.Equal(Part, _library.ReadFile(saved));
        Assert.Equal(GCodeCache.Md5Of(Part), saved.Md5);
        Assert.True(OptionColumns.Same(Options(), saved.Options));
        Assert.Equal(WorkOriginChoice.BedCorner, saved.Options.WorkOrigin);
        Assert.Equal(39.25, saved.Options.ZProbeOffsetY);
        Assert.Equal(0, saved.RunCount);
        Assert.Null(saved.LastStartedUtc);
    }

    [Fact]
    public void Files_of_the_same_name_are_stored_separately_for_different_programs()
    {
        var a = _library.Save("A", "", "part.nc", Part, new JobStartOptions(), replace: false);
        var b = _library.Save("B", "", "part.nc", PartV2, new JobStartOptions(), replace: false);

        Assert.NotEqual(a.StoredFile, b.StoredFile);
        Assert.Equal(Part, _library.ReadFile(a));
        Assert.Equal(PartV2, _library.ReadFile(b));
    }

    [Fact]
    public void A_taken_name_is_refused_unless_replacing_which_moves_the_old_one_to_the_deleted()
    {
        var first = _library.Save("Coaster", "", "a.nc", Part, new JobStartOptions(), replace: false);

        var taken = Assert.Throws<SavedProgramNameTakenException>(() =>
            _library.Save("COASTER", "", "b.nc", PartV2, new JobStartOptions(), replace: false));
        Assert.Equal("Coaster", taken.Name);

        var second = _library.Save("COASTER", "", "b.nc", PartV2, new JobStartOptions(), replace: true);
        Assert.Equal([second.Id], _library.List(includeDeleted: false).Select(e => e.Program.Id));
        Assert.True(_library.Get(first.Id).IsDeleted);
        Assert.Equal(Part, _library.ReadFile(_library.Get(first.Id))); // kept so it can be restored
    }

    [Fact]
    public void Updating_options_and_details()
    {
        var saved = _library.Save("Coaster", "", "a.nc", Part, new JobStartOptions(), replace: false);
        _library.Save("Sign", "", "b.nc", Part, new JobStartOptions(), replace: false);

        var updated = _library.UpdateOptions(saved.Id, Options());
        Assert.True(OptionColumns.Same(Options(), updated.Options));

        Assert.Throws<SavedProgramNameTakenException>(() => _library.Edit(saved.Id, "sign", ""));
        var renamed = _library.Edit(saved.Id, "Coaster round", " for the café ");
        Assert.Equal(("Coaster round", "for the café"), (renamed.Name, renamed.Notes));
        Assert.Equal("coaster ROUND", _library.Edit(saved.Id, "coaster ROUND", "").Name); // its own name in other case
        Assert.Throws<SavedProgramException>(() => _library.Edit(saved.Id, "   ", ""));
    }

    [Fact]
    public void Replacing_the_file_keeps_the_options_and_deletes_the_old_copy()
    {
        var saved = _library.Save("Coaster", "", "a.nc", Part, Options(), replace: false);
        var oldPath = _library.PathOf(saved);

        var replaced = _library.ReplaceFile(saved.Id, "a v2.gcode", PartV2);

        Assert.Equal("a v2.gcode", replaced.OriginalFileName);
        Assert.EndsWith(".gcode", replaced.StoredFile);
        Assert.Equal(PartV2, _library.ReadFile(replaced));
        Assert.Equal(GCodeCache.Md5Of(PartV2), replaced.Md5);
        Assert.True(OptionColumns.Same(Options(), replaced.Options));
        Assert.False(File.Exists(oldPath));
    }

    [Fact]
    public void Deleting_frees_the_name_and_restoring_needs_a_free_one()
    {
        var saved = _library.Save("Coaster", "", "a.nc", Part, new JobStartOptions(), replace: false);
        _library.Delete(saved.Id);

        Assert.Empty(_library.List(includeDeleted: false));
        Assert.Single(_library.List(includeDeleted: true));
        Assert.Throws<SavedProgramException>(() => _library.UpdateOptions(saved.Id, Options()));

        var newer = _library.Save("Coaster", "", "b.nc", PartV2, new JobStartOptions(), replace: false);
        Assert.Throws<SavedProgramNameTakenException>(() => _library.Restore(saved.Id));

        var restored = _library.Restore(saved.Id, "Coaster (old)");
        Assert.False(restored.IsDeleted);
        Assert.Equal("Coaster (old)", restored.Name);
        Assert.Equal(2, _library.List(includeDeleted: false).Count);
        Assert.NotEqual(newer.Id, restored.Id);
    }

    [Fact]
    public void Only_deleted_programs_are_deleted_for_good_with_their_file()
    {
        var saved = _library.Save("Coaster", "", "a.nc", Part, new JobStartOptions(), replace: false);
        Assert.Throws<SavedProgramException>(() => _library.DeleteForever(saved.Id));

        _library.Delete(saved.Id);
        _library.DeleteForever(saved.Id);

        Assert.Null(_library.Find(saved.Id));
        Assert.False(File.Exists(_library.PathOf(saved)));
        Assert.Empty(_library.List(includeDeleted: true));
    }

    [Fact]
    public void A_missing_file_is_reported()
    {
        var saved = _library.Save("Coaster", "", "a.nc", Part, new JobStartOptions(), replace: false);
        File.Delete(_library.PathOf(saved));

        Assert.True(_library.List(includeDeleted: false).Single().FileMissing);
        var error = Assert.Throws<SavedProgramException>(() => _library.ReadFile(saved));
        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public void Starts_are_counted()
    {
        var saved = _library.Save("Coaster", "", "a.nc", Part, new JobStartOptions(), replace: false);
        _library.RecordStart(saved.Id);
        _library.RecordStart(saved.Id);

        var counted = _library.Get(saved.Id);
        Assert.Equal(2, counted.RunCount);
        Assert.NotNull(counted.LastStartedUtc);
        Assert.Equal(DateTimeKind.Utc, counted.LastStartedUtc!.Value.Kind);
    }

    [Fact]
    public void Changes_are_announced()
    {
        var changes = 0;
        _library.Changed += () => changes++;
        var saved = _library.Save("Coaster", "", "a.nc", Part, new JobStartOptions(), replace: false);
        _library.Delete(saved.Id);
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData("part?.nc", "^part__[0-9a-f]{32}\\.nc$")]
    [InlineData("a:b*c.tap", "^a_b_c_[0-9a-f]{32}\\.tap$")]
    [InlineData("noext", "^noext_[0-9a-f]{32}$")]
    [InlineData(".nc", "^program_[0-9a-f]{32}\\.nc$")]
    public void Stored_file_names_are_safe_on_every_system(string original, string pattern)
    {
        Assert.Matches(pattern, SavedProgramLibrary.StoredFileName(original));
    }
}
