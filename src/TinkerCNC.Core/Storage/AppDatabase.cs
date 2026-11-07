using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TinkerCNC.Storage;

/// <summary>
/// The app's SQLite database, by default <c>tinkercnc.db</c> in the data folder (<see cref="UserDataFolders"/>).
/// Every store in it shares one ordered list of schema migrations, tracked with <c>PRAGMA user_version</c>: a store
/// with a list of its own would undo the version of the others.
/// </summary>
public sealed class AppDatabase
{
    /// <summary>Applied in order; append new ones, never change applied ones.</summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE operation_runs (
            id                       INTEGER PRIMARY KEY AUTOINCREMENT,
            fingerprint              TEXT    NOT NULL,
            machine                  TEXT    NOT NULL,
            file_name                TEXT,
            tool_number              INTEGER,
            line_count               INTEGER NOT NULL,
            estimated_seconds        REAL    NOT NULL,
            active_seconds           REAL    NOT NULL,
            paused_seconds           REAL    NOT NULL,
            average_feed_override    REAL    NOT NULL,
            seconds_at_100_percent   REAL    NOT NULL,
            recorded_utc             TEXT    NOT NULL
        );
        CREATE INDEX ix_operation_runs_fingerprint ON operation_runs (fingerprint);
        """,
        """
        CREATE TABLE saved_programs (
            id                        INTEGER PRIMARY KEY AUTOINCREMENT,
            name                      TEXT    NOT NULL,
            notes                     TEXT    NOT NULL DEFAULT '',
            stored_file               TEXT    NOT NULL,
            original_file_name        TEXT    NOT NULL,
            md5                       TEXT    NOT NULL,
            work_origin               TEXT    NOT NULL,
            origin_offset_x           REAL    NOT NULL,
            origin_offset_y           REAL    NOT NULL,
            xyz_probe_height          REAL    NOT NULL,
            xyz_probe_tool_diameter   REAL    NOT NULL,
            scan_margin               INTEGER NOT NULL,
            z_probe                   INTEGER NOT NULL,
            z_probe_from_work_origin  INTEGER NOT NULL,
            z_probe_offset_x          REAL    NOT NULL,
            z_probe_offset_y          REAL    NOT NULL,
            leveling                  INTEGER NOT NULL,
            leveling_points_x         INTEGER NOT NULL,
            leveling_points_y         INTEGER NOT NULL,
            leveling_height           REAL    NOT NULL,
            mode_vacuum               INTEGER NOT NULL,
            mode_external_output      INTEGER NOT NULL,
            mode_blow                 INTEGER NOT NULL,
            mode_bed_clean            INTEGER NOT NULL,
            mode_anti_static          INTEGER NOT NULL,
            mode_timelapse            INTEGER NOT NULL,
            created_utc               TEXT    NOT NULL,
            updated_utc               TEXT    NOT NULL,
            last_started_utc          TEXT,
            run_count                 INTEGER NOT NULL DEFAULT 0,
            deleted_utc               TEXT
        );
        CREATE UNIQUE INDEX ux_saved_programs_name ON saved_programs (name COLLATE NOCASE) WHERE deleted_utc IS NULL;
        """,
        // The time-lapse is recorded here from the camera now, not by the machine (its M951 job mode).
        "ALTER TABLE saved_programs RENAME COLUMN mode_timelapse TO timelapse;",
        // Saved programs become setups (the options) with their files (one each): a setup can have several files.
        """
        CREATE TABLE setups (
            id                        INTEGER PRIMARY KEY AUTOINCREMENT,
            name                      TEXT    NOT NULL,
            notes                     TEXT    NOT NULL DEFAULT '',
            work_origin               TEXT    NOT NULL,
            origin_offset_x           REAL    NOT NULL,
            origin_offset_y           REAL    NOT NULL,
            xyz_probe_height          REAL    NOT NULL,
            xyz_probe_tool_diameter   REAL    NOT NULL,
            scan_margin               INTEGER NOT NULL,
            z_probe                   INTEGER NOT NULL,
            z_probe_from_work_origin  INTEGER NOT NULL,
            z_probe_offset_x          REAL    NOT NULL,
            z_probe_offset_y          REAL    NOT NULL,
            leveling                  INTEGER NOT NULL,
            leveling_points_x         INTEGER NOT NULL,
            leveling_points_y         INTEGER NOT NULL,
            leveling_height           REAL    NOT NULL,
            mode_vacuum               INTEGER NOT NULL,
            mode_external_output      INTEGER NOT NULL,
            mode_blow                 INTEGER NOT NULL,
            mode_bed_clean            INTEGER NOT NULL,
            mode_anti_static          INTEGER NOT NULL,
            timelapse                 INTEGER NOT NULL,
            created_utc               TEXT    NOT NULL,
            updated_utc               TEXT    NOT NULL,
            last_started_utc          TEXT,
            run_count                 INTEGER NOT NULL DEFAULT 0,
            deleted_utc               TEXT
        );
        CREATE UNIQUE INDEX ux_setups_name ON setups (name COLLATE NOCASE) WHERE deleted_utc IS NULL;
        CREATE TABLE setup_files (
            id                        INTEGER PRIMARY KEY AUTOINCREMENT,
            setup_id                  INTEGER NOT NULL REFERENCES setups (id),
            stored_file               TEXT    NOT NULL,
            original_file_name        TEXT    NOT NULL,
            md5                       TEXT    NOT NULL,
            created_utc               TEXT    NOT NULL,
            updated_utc               TEXT    NOT NULL,
            last_started_utc          TEXT,
            run_count                 INTEGER NOT NULL DEFAULT 0,
            deleted_utc               TEXT
        );
        CREATE INDEX ix_setup_files_setup ON setup_files (setup_id);
        INSERT INTO setups (id, name, notes, work_origin, origin_offset_x, origin_offset_y, xyz_probe_height, xyz_probe_tool_diameter,
            scan_margin, z_probe, z_probe_from_work_origin, z_probe_offset_x, z_probe_offset_y, leveling, leveling_points_x,
            leveling_points_y, leveling_height, mode_vacuum, mode_external_output, mode_blow, mode_bed_clean, mode_anti_static,
            timelapse, created_utc, updated_utc, last_started_utc, run_count, deleted_utc)
        SELECT id, name, notes, work_origin, origin_offset_x, origin_offset_y, xyz_probe_height, xyz_probe_tool_diameter,
            scan_margin, z_probe, z_probe_from_work_origin, z_probe_offset_x, z_probe_offset_y, leveling, leveling_points_x,
            leveling_points_y, leveling_height, mode_vacuum, mode_external_output, mode_blow, mode_bed_clean, mode_anti_static,
            timelapse, created_utc, updated_utc, last_started_utc, run_count, deleted_utc
        FROM saved_programs;
        INSERT INTO setup_files (setup_id, stored_file, original_file_name, md5, created_utc, updated_utc, last_started_utc, run_count)
        SELECT id, stored_file, original_file_name, md5, created_utc, updated_utc, last_started_utc, run_count FROM saved_programs;
        DROP TABLE saved_programs;
        """,
        // The job queue of each machine: a setup's file (or a file queued for one run) with a repeat count.
        """
        CREATE TABLE queue_entries (
            id                        INTEGER PRIMARY KEY AUTOINCREMENT,
            machine                   TEXT    NOT NULL,
            position                  INTEGER NOT NULL,
            setup_id                  INTEGER NOT NULL REFERENCES setups (id),
            file_id                   INTEGER REFERENCES setup_files (id),
            stored_file               TEXT,
            original_file_name        TEXT,
            md5                       TEXT,
            repeat_count              INTEGER NOT NULL DEFAULT 1,
            done_count                INTEGER NOT NULL DEFAULT 0,
            state                     TEXT    NOT NULL,
            error                     TEXT,
            restart_segment           INTEGER,
            created_utc               TEXT    NOT NULL
        );
        CREATE INDEX ix_queue_entries_machine ON queue_entries (machine, position);
        """,
    ];

    public AppDatabase(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        Migrate();
    }

    public string Path { get; }

    public static string DefaultPath => System.IO.Path.Combine(UserDataFolders.TinkerCNC, "tinkercnc.db");

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>Times are stored as round-trip ISO 8601 text.</summary>
    public static string Time(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);

    public static DateTime ParseTime(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>A stored time that may be NULL.</summary>
    public static DateTime? OptionalTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTime(reader.GetString(ordinal));

    /// <summary>A text column that may be NULL.</summary>
    public static string? OptionalString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>An INTEGER column that may be NULL.</summary>
    public static long? OptionalInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    /// <summary>A parameter value for a nullable field: NULL when it is null.</summary>
    public static object Nullable(object? value) => value ?? DBNull.Value;

    /// <summary>Runs a query and reads every row with <paramref name="read"/>.</summary>
    public IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params IEnumerable<(string Name, object Value)> parameters)
    {
        using var conn = Open();
        using var cmd = Command(conn, sql, parameters);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    public void Execute(string sql, params IEnumerable<(string Name, object Value)> parameters)
    {
        using var conn = Open();
        using var cmd = Command(conn, sql, parameters);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Runs an INSERT and returns the new row's id.</summary>
    public long Insert(string sql, params IEnumerable<(string Name, object Value)> parameters)
    {
        using var conn = Open();
        using var cmd = Command(conn, sql + "\nSELECT last_insert_rowid();", parameters);
        return (long)cmd.ExecuteScalar()!;
    }

    private static SqliteCommand Command(SqliteConnection conn, string sql, IEnumerable<(string Name, object Value)> parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private void Migrate()
    {
        using var conn = Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL"; // readers (UI) and writers do not block each other
            wal.ExecuteNonQuery();
        }

        using var version = conn.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);

        foreach (var (sql, index) in Migrations.Select((sql, i) => (sql, i)).Skip(current))
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"{sql}\nPRAGMA user_version = {index + 1};";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }
}
