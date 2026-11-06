using Microsoft.Data.Sqlite;
using TinkerCNC.Jobs;
using TinkerCNC.Storage;

namespace TinkerCNC.Programs;

/// <summary>
/// A program saved under a name of its own, with the options to start it and its own copy of the G-code file.
/// </summary>
/// <param name="StoredFile">The copy's file name in the saved programs folder (not a path, so the data folder can move).</param>
/// <param name="OriginalFileName">The name it had when it was saved; it is uploaded to the machine under this name.</param>
/// <param name="DeletedUtc">When it was deleted; deleted programs keep their file and can be restored.</param>
public sealed record SavedProgram(
    long Id, string Name, string Notes, string StoredFile, string OriginalFileName, string Md5, JobStartOptions Options,
    DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? LastStartedUtc, int RunCount, DateTime? DeletedUtc)
{
    public bool IsDeleted => DeletedUtc is not null;
}

/// <summary>The <c>saved_programs</c> table of the app's database (<see cref="AppDatabase"/>), one column per option.</summary>
public sealed class SavedProgramRepository(AppDatabase database)
{
    private const string Columns =
        "id, name, notes, stored_file, original_file_name, md5, created_utc, updated_utc, last_started_utc, run_count, deleted_utc";

    private static readonly string OptionNames = string.Join(", ", OptionColumns.All.Select(c => c.Name));
    private static readonly string OptionParameters = string.Join(", ", OptionColumns.All.Select(c => "$" + c.Name));
    private static readonly string OptionAssignments = string.Join(", ", OptionColumns.All.Select(c => $"{c.Name} = ${c.Name}"));

    /// <summary>Newest change first; deleted ones only when asked for.</summary>
    public IReadOnlyList<SavedProgram> List(bool includeDeleted) =>
        Query($"SELECT {Columns}, {OptionNames} FROM saved_programs" +
              (includeDeleted ? "" : " WHERE deleted_utc IS NULL") + " ORDER BY updated_utc DESC, id DESC");

    public SavedProgram? Get(long id) =>
        Query($"SELECT {Columns}, {OptionNames} FROM saved_programs WHERE id = $id", ("$id", id)).FirstOrDefault();

    /// <returns>The new program's id.</returns>
    public long Insert(string name, string notes, string storedFile, string originalFileName, string md5, JobStartOptions options, DateTime utc)
    {
        using var conn = database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO saved_programs (name, notes, stored_file, original_file_name, md5, created_utc, updated_utc, {OptionNames})
            VALUES ($name, $notes, $stored, $original, $md5, $utc, $utc, {OptionParameters});
            SELECT last_insert_rowid();
            """;
        Add(cmd, ("$name", name), ("$notes", notes), ("$stored", storedFile), ("$original", originalFileName), ("$md5", md5),
            ("$utc", AppDatabase.Time(utc)));
        AddOptions(cmd, options);
        return (long)cmd.ExecuteScalar()!;
    }

    public void UpdateOptions(long id, JobStartOptions options, DateTime utc)
    {
        using var conn = database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE saved_programs SET {OptionAssignments}, updated_utc = $utc WHERE id = $id";
        Add(cmd, ("$id", id), ("$utc", AppDatabase.Time(utc)));
        AddOptions(cmd, options);
        cmd.ExecuteNonQuery();
    }

    public void UpdateDetails(long id, string name, string notes, DateTime utc) =>
        Execute("UPDATE saved_programs SET name = $name, notes = $notes, updated_utc = $utc WHERE id = $id",
            ("$id", id), ("$name", name), ("$notes", notes), ("$utc", AppDatabase.Time(utc)));

    public void UpdateFile(long id, string storedFile, string originalFileName, string md5, DateTime utc) =>
        Execute("UPDATE saved_programs SET stored_file = $stored, original_file_name = $original, md5 = $md5, updated_utc = $utc WHERE id = $id",
            ("$id", id), ("$stored", storedFile), ("$original", originalFileName), ("$md5", md5), ("$utc", AppDatabase.Time(utc)));

    /// <summary>Marks it deleted, or with a null time restores it under <paramref name="name"/>.</summary>
    public void SetDeleted(long id, string name, DateTime? deletedUtc) =>
        Execute("UPDATE saved_programs SET name = $name, deleted_utc = $deleted WHERE id = $id",
            ("$id", id), ("$name", name), ("$deleted", deletedUtc is { } t ? AppDatabase.Time(t) : DBNull.Value));

    public void RecordStart(long id, DateTime utc) =>
        Execute("UPDATE saved_programs SET last_started_utc = $utc, run_count = run_count + 1 WHERE id = $id",
            ("$id", id), ("$utc", AppDatabase.Time(utc)));

    public void Remove(long id) => Execute("DELETE FROM saved_programs WHERE id = $id", ("$id", id));

    private IReadOnlyList<SavedProgram> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Add(cmd, parameters);
        using var reader = cmd.ExecuteReader();
        var programs = new List<SavedProgram>();
        while (reader.Read()) programs.Add(Read(reader));
        return programs;
    }

    private static SavedProgram Read(SqliteDataReader r)
    {
        var options = new JobStartOptions();
        foreach (var column in OptionColumns.All) column.Set(options, r.GetValue(r.GetOrdinal(column.Name)));
        return new SavedProgram(
            r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), options,
            AppDatabase.ParseTime(r.GetString(6)), AppDatabase.ParseTime(r.GetString(7)), OptionalTime(r, 8), r.GetInt32(9),
            OptionalTime(r, 10));
    }

    private static DateTime? OptionalTime(SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : AppDatabase.ParseTime(r.GetString(ordinal));

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Add(cmd, parameters);
        cmd.ExecuteNonQuery();
    }

    private static void AddOptions(SqliteCommand cmd, JobStartOptions options)
    {
        foreach (var column in OptionColumns.All) cmd.Parameters.AddWithValue("$" + column.Name, column.Get(options));
    }

    private static void Add(SqliteCommand cmd, params (string Name, object Value)[] parameters)
    {
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
    }
}
