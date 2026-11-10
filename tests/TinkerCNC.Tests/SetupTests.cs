using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using TinkerCNC.Host;
using TinkerCNC.Jobs;
using TinkerCNC.Programs;
using TinkerCNC.Queue;
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
    public void A_database_with_only_the_history_gets_the_setups_and_keeps_its_runs()
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
        Assert.Empty(new SetupRepository(db).ListSetups(includeDeleted: true));
        Assert.Empty(new QueueRepository(db).List("any"));
    }

    [Fact]
    public void Saved_programs_of_an_earlier_version_become_setups_with_one_file_each()
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
                CREATE TABLE saved_programs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, notes TEXT NOT NULL DEFAULT '', stored_file TEXT NOT NULL,
                    original_file_name TEXT NOT NULL, md5 TEXT NOT NULL, work_origin TEXT NOT NULL, origin_offset_x REAL NOT NULL,
                    origin_offset_y REAL NOT NULL, xyz_probe_height REAL NOT NULL, xyz_probe_tool_diameter REAL NOT NULL,
                    scan_margin INTEGER NOT NULL, z_probe INTEGER NOT NULL, z_probe_from_work_origin INTEGER NOT NULL,
                    z_probe_offset_x REAL NOT NULL, z_probe_offset_y REAL NOT NULL, leveling INTEGER NOT NULL,
                    leveling_points_x INTEGER NOT NULL, leveling_points_y INTEGER NOT NULL, leveling_height REAL NOT NULL,
                    mode_vacuum INTEGER NOT NULL, mode_external_output INTEGER NOT NULL, mode_blow INTEGER NOT NULL,
                    mode_bed_clean INTEGER NOT NULL, mode_anti_static INTEGER NOT NULL, timelapse INTEGER NOT NULL,
                    created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, last_started_utc TEXT, run_count INTEGER NOT NULL DEFAULT 0,
                    deleted_utc TEXT);
                INSERT INTO saved_programs VALUES (7, 'Coaster', 'oak', 'coaster_abc.nc', 'coaster.nc', 'd41d8cd9', 'BedCorner', 12.5, -3, 9, 3.175,
                    1, 1, 0, 20, 10, 0, 5, 5, 5, 0, 0, 1, 0, 0, 1, '2026-01-01T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z',
                    '2026-01-03T00:00:00.0000000Z', 4, NULL);
                INSERT INTO saved_programs VALUES (8, 'Gone', '', 'gone_def.nc', 'gone.nc', 'ffff', 'Keep', 0, 0, 9, 3.175,
                    0, 0, 0, 0, 0, 0, 5, 5, 5, 0, 0, 0, 0, 0, 0, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z',
                    NULL, 0, '2026-01-05T00:00:00.0000000Z');
                PRAGMA user_version = 3;
                """;
            cmd.ExecuteNonQuery();
        }

        var repository = new SetupRepository(new AppDatabase(path));

        var setup = Assert.Single(repository.ListSetups(includeDeleted: false));
        Assert.Equal((7L, "Coaster", "oak", 4), (setup.Id, setup.Name, setup.Notes, setup.RunCount));
        Assert.Equal(WorkOriginChoice.BedCorner, setup.Options.WorkOrigin);
        Assert.True(setup.Options.ScanMargin && setup.Options.ZProbe && setup.Options.Timelapse && setup.Options.IsOn(JobModes.Blow));
        Assert.Equal((20.0, 10.0), (setup.Options.ZProbeOffsetX, setup.Options.ZProbeOffsetY));
        Assert.NotNull(setup.LastStartedUtc);
        Assert.True(repository.GetSetup(8)!.IsDeleted);

        var files = repository.ListFiles(includeDeleted: true);
        Assert.Equal(2, files.Count);
        var file = files.Single(f => f.SetupId == 7);
        Assert.Equal(("coaster_abc.nc", "coaster.nc", "d41d8cd9", 4), (file.StoredFile, file.OriginalFileName, file.Md5, file.RunCount));
        Assert.False(file.IsDeleted);
    }

    [Fact]
    public void Every_option_column_exists_in_the_table()
    {
        var db = new AppDatabase(_data.Paths.HistoryDatabase);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('setups')";
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

public sealed class SetupLibraryTests : IDisposable
{
    private static readonly byte[] Part = Encoding.ASCII.GetBytes("G0 X0 Y0\nG1 X10 F100\nM30\n");
    private static readonly byte[] PartV2 = Encoding.ASCII.GetBytes("G0 X0 Y0\nG1 X20 F100\nM30\n");

    private readonly TempDataFolder _data = new();
    private readonly SetupLibrary _library;

    public SetupLibraryTests()
    {
        _library = new SetupLibrary(new SetupRepository(new AppDatabase(_data.Paths.HistoryDatabase)), _data.Paths);
    }

    public void Dispose() => _data.Dispose();

    private static JobStartOptions Options() => new()
    {
        WorkOrigin = WorkOriginChoice.BedCorner, OriginOffsetX = 12.5, OriginOffsetY = -3, XyzProbeHeight = 10, XyzProbeToolDiameter = 6,
        ScanMargin = true, ZProbe = true, ZProbeFromWorkOrigin = true, ZProbeOffsetX = 59, ZProbeOffsetY = 39.25,
        Leveling = true, LevelingPointsX = 7, LevelingPointsY = 3, LevelingHeight = 2.5,
        Modes = { ["blow"] = true, ["bedclean"] = true, ["vacuum"] = false },
    };

    private Setup Coaster(string name = "Coaster") => _library.Create(name, "", new JobStartOptions(), replace: false);

    [Fact]
    public void A_setup_keeps_every_option_and_its_files_keep_their_own_copies()
    {
        var setup = _library.Create("  Coaster  ", "oak, 3 mm bit", Options(), replace: false);
        var file = _library.AddFile(setup.Id, "my part.nc", Part);

        Assert.Equal("Coaster", setup.Name);
        Assert.Equal("oak, 3 mm bit", setup.Notes);
        Assert.True(OptionColumns.Same(Options(), setup.Options));
        Assert.Equal(WorkOriginChoice.BedCorner, setup.Options.WorkOrigin);
        Assert.Equal(39.25, setup.Options.ZProbeOffsetY);
        Assert.Equal(0, setup.RunCount);
        Assert.Null(setup.LastStartedUtc);

        Assert.Equal(setup.Id, file.SetupId);
        Assert.Equal("my part.nc", file.OriginalFileName);
        Assert.Matches("^my part_[0-9a-f]{32}\\.nc$", file.StoredFile);
        Assert.Equal(Path.Combine(_data.Paths.SetupFiles, file.StoredFile), _library.PathOf(file));
        Assert.Equal(Part, _library.ReadFile(file));
        Assert.Equal(GCodeCache.Md5Of(Part), file.Md5);

        var listed = Assert.Single(_library.List(includeDeleted: false));
        Assert.Equal(file.Id, Assert.Single(listed.Files).File.Id);
    }

    [Fact]
    public void Files_of_the_same_name_are_stored_separately_also_within_one_setup()
    {
        var setup = Coaster();
        var a = _library.AddFile(setup.Id, "part.nc", Part);
        var b = _library.AddFile(setup.Id, "part.nc", PartV2);

        Assert.NotEqual(a.StoredFile, b.StoredFile);
        Assert.Equal(Part, _library.ReadFile(a));
        Assert.Equal(PartV2, _library.ReadFile(b));
        Assert.Equal(2, _library.FilesOf(setup.Id, includeDeleted: false).Count);
    }

    [Fact]
    public void A_taken_name_is_refused_unless_replacing_which_moves_the_old_one_to_the_deleted()
    {
        var first = Coaster();
        _library.AddFile(first.Id, "a.nc", Part);

        var taken = Assert.Throws<SetupNameTakenException>(() => _library.Create("COASTER", "", new JobStartOptions(), replace: false));
        Assert.Equal("Coaster", taken.Name);

        var second = _library.Create("COASTER", "", new JobStartOptions(), replace: true);
        Assert.Equal([second.Id], _library.List(includeDeleted: false).Select(e => e.Setup.Id));
        Assert.True(_library.Get(first.Id).IsDeleted);
        Assert.Equal(Part, _library.ReadFile(_library.FilesOf(first.Id, includeDeleted: true).Single().File)); // kept so it can be restored
    }

    [Fact]
    public void Updating_options_and_details()
    {
        var setup = Coaster();
        Coaster("Sign");

        var updated = _library.UpdateOptions(setup.Id, Options());
        Assert.True(OptionColumns.Same(Options(), updated.Options));

        Assert.Throws<SetupNameTakenException>(() => _library.Edit(setup.Id, "sign", ""));
        var renamed = _library.Edit(setup.Id, "Coaster round", " for the café ");
        Assert.Equal(("Coaster round", "for the café"), (renamed.Name, renamed.Notes));
        Assert.Equal("coaster ROUND", _library.Edit(setup.Id, "coaster ROUND", "").Name); // its own name in other case
        Assert.Throws<SetupException>(() => _library.Edit(setup.Id, "   ", ""));
    }

    [Fact]
    public void Replacing_a_file_keeps_the_setup_and_deletes_the_old_copy()
    {
        var setup = _library.Create("Coaster", "", Options(), replace: false);
        var file = _library.AddFile(setup.Id, "a.nc", Part);
        var oldPath = _library.PathOf(file);

        var replaced = _library.ReplaceFile(file.Id, "a v2.gcode", PartV2);

        Assert.Equal("a v2.gcode", replaced.OriginalFileName);
        Assert.EndsWith(".gcode", replaced.StoredFile);
        Assert.Equal(PartV2, _library.ReadFile(replaced));
        Assert.Equal(GCodeCache.Md5Of(PartV2), replaced.Md5);
        Assert.True(OptionColumns.Same(Options(), _library.Get(setup.Id).Options));
        Assert.False(File.Exists(oldPath));
    }

    [Fact]
    public void Deleting_a_setup_frees_the_name_hides_its_files_and_restoring_needs_a_free_one()
    {
        var setup = Coaster();
        var file = _library.AddFile(setup.Id, "a.nc", Part);
        _library.Delete(setup.Id);

        Assert.Empty(_library.List(includeDeleted: false));
        Assert.Single(_library.List(includeDeleted: true));
        Assert.Throws<SetupException>(() => _library.UpdateOptions(setup.Id, Options()));
        Assert.Throws<SetupException>(() => _library.AddFile(setup.Id, "b.nc", Part));
        Assert.Throws<SetupException>(() => _library.ReplaceFile(file.Id, "b.nc", PartV2));

        var newer = Coaster();
        Assert.Throws<SetupNameTakenException>(() => _library.Restore(setup.Id));

        var restored = _library.Restore(setup.Id, "Coaster (old)");
        Assert.False(restored.IsDeleted);
        Assert.Equal("Coaster (old)", restored.Name);
        Assert.Equal(2, _library.List(includeDeleted: false).Count);
        Assert.NotEqual(newer.Id, restored.Id);
        Assert.Equal(file.Id, _library.FilesOf(setup.Id, includeDeleted: false).Single().File.Id);
    }

    [Fact]
    public void Only_deleted_setups_are_deleted_for_good_with_all_their_files()
    {
        var setup = Coaster();
        var file = _library.AddFile(setup.Id, "a.nc", Part);
        var deletedFile = _library.AddFile(setup.Id, "b.nc", PartV2);
        _library.DeleteFile(deletedFile.Id);
        Assert.Throws<SetupException>(() => _library.DeleteForever(setup.Id));

        _library.Delete(setup.Id);
        _library.DeleteForever(setup.Id);

        Assert.Null(_library.Find(setup.Id));
        Assert.Null(_library.FindFile(file.Id));
        Assert.False(File.Exists(_library.PathOf(file)));
        Assert.False(File.Exists(_library.PathOf(deletedFile)));
        Assert.Empty(_library.List(includeDeleted: true));
    }

    [Fact]
    public void A_file_is_deleted_restored_or_deleted_for_good_on_its_own()
    {
        var setup = Coaster();
        var file = _library.AddFile(setup.Id, "a.nc", Part);

        _library.DeleteFile(file.Id);
        Assert.Empty(_library.FilesOf(setup.Id, includeDeleted: false));
        Assert.True(_library.FilesOf(setup.Id, includeDeleted: true).Single().File.IsDeleted);
        Assert.Throws<SetupException>(() => _library.ReplaceFile(file.Id, "b.nc", PartV2));

        Assert.False(_library.RestoreFile(file.Id).IsDeleted);
        Assert.Throws<SetupException>(() => _library.DeleteFileForever(file.Id));

        _library.DeleteFile(file.Id);
        _library.DeleteFileForever(file.Id);
        Assert.Null(_library.FindFile(file.Id));
        Assert.False(File.Exists(_library.PathOf(file)));
        Assert.NotNull(_library.Find(setup.Id)); // the setup stays
    }

    [Fact]
    public void A_missing_copy_is_reported()
    {
        var setup = Coaster();
        var file = _library.AddFile(setup.Id, "a.nc", Part);
        File.Delete(_library.PathOf(file));

        Assert.True(_library.List(includeDeleted: false).Single().Files.Single().FileMissing);
        var error = Assert.Throws<SetupException>(() => _library.ReadFile(file));
        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public void Starts_are_counted_on_the_setup_and_on_its_file()
    {
        var setup = Coaster();
        var file = _library.AddFile(setup.Id, "a.nc", Part);
        _library.RecordStart(setup.Id, file.Id);
        _library.RecordStart(setup.Id, fileId: null); // a one-off file run with the setup

        var counted = _library.Get(setup.Id);
        Assert.Equal(2, counted.RunCount);
        Assert.NotNull(counted.LastStartedUtc);
        Assert.Equal(DateTimeKind.Utc, counted.LastStartedUtc!.Value.Kind);
        Assert.Equal(1, _library.GetFile(file.Id).RunCount);
    }

    [Fact]
    public void Changes_are_announced()
    {
        var changes = 0;
        _library.Changed += () => changes++;
        var setup = Coaster();
        _library.AddFile(setup.Id, "a.nc", Part);
        _library.Delete(setup.Id);
        Assert.Equal(3, changes);
    }

    [Theory]
    [InlineData("part?.nc", "^part__[0-9a-f]{32}\\.nc$")]
    [InlineData("a:b*c.tap", "^a_b_c_[0-9a-f]{32}\\.tap$")]
    [InlineData("noext", "^noext_[0-9a-f]{32}$")]
    [InlineData(".nc", "^program_[0-9a-f]{32}\\.nc$")]
    public void Stored_file_names_are_safe_on_every_system(string original, string pattern)
    {
        Assert.Matches(pattern, StoredFiles.StoredFileName(original));
    }
}

public sealed class QueueRepositoryTests : IDisposable
{
    private readonly TempDataFolder _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Entries_are_kept_per_machine_in_order_with_their_state()
    {
        var db = new AppDatabase(_data.Paths.HistoryDatabase);
        var setups = new SetupRepository(db);
        var setupId = setups.InsertSetup("Debossing", "", new JobStartOptions(), DateTime.UtcNow);
        var fileId = setups.InsertFile(setupId, "a_x.nc", "a.nc", "md5a", DateTime.UtcNow);
        var queue = new QueueRepository(db);

        var first = queue.Insert("10.0.0.5:2222", 1, setupId, fileId, null, null, null, 3, DateTime.UtcNow);
        var second = queue.Insert("10.0.0.5:2222", 2, setupId, null, "b_y.nc", "b.nc", "md5b", 1, DateTime.UtcNow);
        queue.Insert("other", 1, setupId, fileId, null, null, null, 1, DateTime.UtcNow);

        var entries = queue.List("10.0.0.5:2222");
        Assert.Equal([first, second], entries.Select(e => e.Id));
        Assert.False(entries[0].IsQueuedCopy);
        Assert.True(entries[1].IsQueuedCopy);
        Assert.Equal((3, 0, QueueEntryState.Pending, 3), (entries[0].Repeat, entries[0].Done, entries[0].State, entries[0].Remaining));

        queue.UpdateState(first, QueueEntryState.Failed, 1, "Stopped.", 2);
        var failed = queue.Get(first)!;
        Assert.Equal((QueueEntryState.Failed, 1, "Stopped.", 2), (failed.State, failed.Done, failed.Error, failed.RestartSegment));

        queue.UpdatePosition(first, 2);
        queue.UpdatePosition(second, 1);
        Assert.Equal([second, first], queue.List("10.0.0.5:2222").Select(e => e.Id));

        queue.UseSetupFile(second, fileId);
        Assert.False(queue.Get(second)!.IsQueuedCopy);
        queue.Remove(first);
        Assert.Single(queue.List("10.0.0.5:2222"));
    }
}

public sealed class JobSnapshotRepositoryTests : IDisposable
{
    private readonly TempDataFolder _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void One_snapshot_per_machine_is_kept_replaced_and_deleted()
    {
        var snapshots = new Recovery.JobSnapshotRepository(new AppDatabase(_data.Paths.HistoryDatabase));
        var utc = new DateTime(2026, 11, 10, 21, 0, 0, DateTimeKind.Utc);
        Assert.Null(snapshots.Get("10.0.0.5:2222"));

        snapshots.Save(new Recovery.JobSnapshot("10.0.0.5:2222", "part.nc", "abc", 120, 900, [-100.5, -80, -60.25], true, utc));
        snapshots.Save(new Recovery.JobSnapshot("other", "b.nc", "def", 5, 10, null, false, utc));
        snapshots.Save(new Recovery.JobSnapshot("10.0.0.5:2222", "part.nc", "abc", 340, 900, [-100.5, -80, -60.25], true, utc.AddSeconds(10)));

        var kept = snapshots.Get("10.0.0.5:2222")!;
        Assert.Equal((340, 900, true, utc.AddSeconds(10)), (kept.Line, kept.LineCount, kept.Leveled, kept.RecordedUtc));
        Assert.Equal([-100.5, -80, -60.25], kept.WorkZero!);
        Assert.Null(snapshots.Get("other")!.WorkZero);

        snapshots.Delete("10.0.0.5:2222");
        Assert.Null(snapshots.Get("10.0.0.5:2222"));
        Assert.NotNull(snapshots.Get("other"));
    }
}
