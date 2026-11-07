using Microsoft.Data.Sqlite;
using TinkerCNC.Jobs;
using TinkerCNC.Storage;

namespace TinkerCNC.Programs;

/// <summary>
/// The <c>setups</c> table (one column per option, see <see cref="OptionColumns"/>) and the <c>setup_files</c> table
/// of the app's database (<see cref="AppDatabase"/>).
/// </summary>
public sealed class SetupRepository(AppDatabase database)
{
    private const string SetupColumns = "id, name, notes, created_utc, updated_utc, last_started_utc, run_count, deleted_utc";
    private const string FileColumns =
        "id, setup_id, stored_file, original_file_name, md5, created_utc, updated_utc, last_started_utc, run_count, deleted_utc";

    private static readonly string OptionNames = string.Join(", ", OptionColumns.All.Select(c => c.Name));
    private static readonly string OptionParameters = string.Join(", ", OptionColumns.All.Select(c => "$" + c.Name));
    private static readonly string OptionAssignments = string.Join(", ", OptionColumns.All.Select(c => $"{c.Name} = ${c.Name}"));

    /// <summary>Newest change first; deleted ones only when asked for.</summary>
    public IReadOnlyList<Setup> ListSetups(bool includeDeleted) =>
        database.Query($"SELECT {SetupColumns}, {OptionNames} FROM setups" +
                       (includeDeleted ? "" : " WHERE deleted_utc IS NULL") + " ORDER BY updated_utc DESC, id DESC", ReadSetup);

    public Setup? GetSetup(long id) =>
        database.Query($"SELECT {SetupColumns}, {OptionNames} FROM setups WHERE id = $id", ReadSetup, ("$id", id)).FirstOrDefault();

    /// <returns>The new setup's id.</returns>
    public long InsertSetup(string name, string notes, JobStartOptions options, DateTime utc) =>
        database.Insert($"""
            INSERT INTO setups (name, notes, created_utc, updated_utc, {OptionNames})
            VALUES ($name, $notes, $utc, $utc, {OptionParameters});
            """, [("$name", name), ("$notes", notes), ("$utc", AppDatabase.Time(utc)), .. Options(options)]);

    public void UpdateOptions(long id, JobStartOptions options, DateTime utc) =>
        database.Execute($"UPDATE setups SET {OptionAssignments}, updated_utc = $utc WHERE id = $id",
            [("$id", id), ("$utc", AppDatabase.Time(utc)), .. Options(options)]);

    public void UpdateDetails(long id, string name, string notes, DateTime utc) =>
        database.Execute("UPDATE setups SET name = $name, notes = $notes, updated_utc = $utc WHERE id = $id",
            ("$id", id), ("$name", name), ("$notes", notes), ("$utc", AppDatabase.Time(utc)));

    /// <summary>Marks it deleted, or with a null time restores it under <paramref name="name"/>.</summary>
    public void SetSetupDeleted(long id, string name, DateTime? deletedUtc) =>
        database.Execute("UPDATE setups SET name = $name, deleted_utc = $deleted WHERE id = $id",
            ("$id", id), ("$name", name), ("$deleted", AppDatabase.Nullable(deletedUtc is { } t ? AppDatabase.Time(t) : null)));

    public void RecordSetupStart(long id, DateTime utc) =>
        database.Execute("UPDATE setups SET last_started_utc = $utc, run_count = run_count + 1 WHERE id = $id",
            ("$id", id), ("$utc", AppDatabase.Time(utc)));

    /// <summary>Removes the setup and the rows of its files.</summary>
    public void RemoveSetup(long id) =>
        database.Execute("DELETE FROM setup_files WHERE setup_id = $id; DELETE FROM setups WHERE id = $id", ("$id", id));

    /// <summary>Every file of every setup, by name; deleted ones only when asked for.</summary>
    public IReadOnlyList<SetupFile> ListFiles(bool includeDeleted) =>
        database.Query($"SELECT {FileColumns} FROM setup_files" + (includeDeleted ? "" : " WHERE deleted_utc IS NULL") +
                       " ORDER BY original_file_name COLLATE NOCASE, id", ReadFile);

    public SetupFile? GetFile(long id) =>
        database.Query($"SELECT {FileColumns} FROM setup_files WHERE id = $id", ReadFile, ("$id", id)).FirstOrDefault();

    /// <returns>The new file's id.</returns>
    public long InsertFile(long setupId, string storedFile, string originalFileName, string md5, DateTime utc) =>
        database.Insert("""
            INSERT INTO setup_files (setup_id, stored_file, original_file_name, md5, created_utc, updated_utc)
            VALUES ($setup, $stored, $original, $md5, $utc, $utc);
            """, ("$setup", setupId), ("$stored", storedFile), ("$original", originalFileName), ("$md5", md5), ("$utc", AppDatabase.Time(utc)));

    public void UpdateFile(long id, string storedFile, string originalFileName, string md5, DateTime utc) =>
        database.Execute("UPDATE setup_files SET stored_file = $stored, original_file_name = $original, md5 = $md5, updated_utc = $utc WHERE id = $id",
            ("$id", id), ("$stored", storedFile), ("$original", originalFileName), ("$md5", md5), ("$utc", AppDatabase.Time(utc)));

    public void SetFileDeleted(long id, DateTime? deletedUtc) =>
        database.Execute("UPDATE setup_files SET deleted_utc = $deleted WHERE id = $id",
            ("$id", id), ("$deleted", AppDatabase.Nullable(deletedUtc is { } t ? AppDatabase.Time(t) : null)));

    public void RecordFileStart(long id, DateTime utc) =>
        database.Execute("UPDATE setup_files SET last_started_utc = $utc, run_count = run_count + 1 WHERE id = $id",
            ("$id", id), ("$utc", AppDatabase.Time(utc)));

    public void RemoveFile(long id) => database.Execute("DELETE FROM setup_files WHERE id = $id", ("$id", id));

    private static Setup ReadSetup(SqliteDataReader r)
    {
        var options = new JobStartOptions();
        foreach (var column in OptionColumns.All) column.Set(options, r.GetValue(r.GetOrdinal(column.Name)));
        return new Setup(
            r.GetInt64(0), r.GetString(1), r.GetString(2), options, AppDatabase.ParseTime(r.GetString(3)),
            AppDatabase.ParseTime(r.GetString(4)), AppDatabase.OptionalTime(r, 5), r.GetInt32(6), AppDatabase.OptionalTime(r, 7));
    }

    private static SetupFile ReadFile(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), AppDatabase.ParseTime(r.GetString(5)),
        AppDatabase.ParseTime(r.GetString(6)), AppDatabase.OptionalTime(r, 7), r.GetInt32(8), AppDatabase.OptionalTime(r, 9));

    private static IEnumerable<(string Name, object Value)> Options(JobStartOptions options) =>
        OptionColumns.All.Select(c => ("$" + c.Name, c.Get(options)));
}
