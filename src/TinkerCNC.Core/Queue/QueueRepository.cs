using Microsoft.Data.Sqlite;
using TinkerCNC.Storage;

namespace TinkerCNC.Queue;

/// <summary>The <c>queue_entries</c> table of the app's database (<see cref="AppDatabase"/>).</summary>
public sealed class QueueRepository(AppDatabase database)
{
    private const string Columns =
        "id, machine, position, setup_id, file_id, stored_file, original_file_name, md5, repeat_count, done_count, state, error, " +
        "restart_segment, created_utc";

    /// <summary>The entries of one machine in queue order, done ones included.</summary>
    public IReadOnlyList<QueueEntry> List(string machine) =>
        database.Query($"SELECT {Columns} FROM queue_entries WHERE machine = $machine ORDER BY position, id", Read, ("$machine", machine));

    public QueueEntry? Get(long id) =>
        database.Query($"SELECT {Columns} FROM queue_entries WHERE id = $id", Read, ("$id", id)).FirstOrDefault();

    /// <returns>The new entry's id.</returns>
    public long Insert(string machine, int position, long setupId, long? fileId, string? storedFile, string? originalFileName, string? md5,
        int repeat, DateTime utc) =>
        database.Insert($"""
            INSERT INTO queue_entries (machine, position, setup_id, file_id, stored_file, original_file_name, md5, repeat_count, state, created_utc)
            VALUES ($machine, $position, $setup, $file, $stored, $original, $md5, $repeat, $state, $utc);
            """,
            ("$machine", machine), ("$position", position), ("$setup", setupId), ("$file", AppDatabase.Nullable(fileId)),
            ("$stored", AppDatabase.Nullable(storedFile)), ("$original", AppDatabase.Nullable(originalFileName)),
            ("$md5", AppDatabase.Nullable(md5)), ("$repeat", repeat), ("$state", QueueEntryState.Pending.ToString()),
            ("$utc", AppDatabase.Time(utc)));

    public void UpdatePosition(long id, int position) =>
        database.Execute("UPDATE queue_entries SET position = $position WHERE id = $id", ("$id", id), ("$position", position));

    public void UpdateRepeat(long id, int repeat) =>
        database.Execute("UPDATE queue_entries SET repeat_count = $repeat WHERE id = $id", ("$id", id), ("$repeat", repeat));

    /// <summary>The outcome of a run, or a retry: state, runs done, the error and where the next run starts.</summary>
    public void UpdateState(long id, QueueEntryState state, int done, string? error, int? restartSegment) =>
        database.Execute(
            "UPDATE queue_entries SET state = $state, done_count = $done, error = $error, restart_segment = $segment WHERE id = $id",
            ("$id", id), ("$state", state.ToString()), ("$done", done), ("$error", AppDatabase.Nullable(error)),
            ("$segment", AppDatabase.Nullable(restartSegment)));

    /// <summary>The queued copy became one of the setup's files.</summary>
    public void UseSetupFile(long id, long fileId) =>
        database.Execute("UPDATE queue_entries SET file_id = $file, stored_file = NULL, original_file_name = NULL, md5 = NULL WHERE id = $id",
            ("$id", id), ("$file", fileId));

    /// <summary>Forgets the queued copy's file once it was removed.</summary>
    public void ClearStoredFile(long id) =>
        database.Execute("UPDATE queue_entries SET stored_file = NULL WHERE id = $id", ("$id", id));

    public void Remove(long id) => database.Execute("DELETE FROM queue_entries WHERE id = $id", ("$id", id));

    private static QueueEntry Read(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), AppDatabase.OptionalInt64(r, 4), AppDatabase.OptionalString(r, 5),
        AppDatabase.OptionalString(r, 6), AppDatabase.OptionalString(r, 7), r.GetInt32(8), r.GetInt32(9),
        Enum.Parse<QueueEntryState>(r.GetString(10)), AppDatabase.OptionalString(r, 11),
        AppDatabase.OptionalInt64(r, 12) is { } segment ? (int)segment : null, AppDatabase.ParseTime(r.GetString(13)));
}
