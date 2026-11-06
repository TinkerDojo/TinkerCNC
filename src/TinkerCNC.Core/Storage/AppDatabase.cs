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
