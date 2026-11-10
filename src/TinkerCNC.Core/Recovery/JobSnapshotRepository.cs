using Microsoft.Data.Sqlite;
using TinkerCNC.Storage;

namespace TinkerCNC.Recovery;

/// <summary>The <c>job_snapshots</c> table of the app's database (<see cref="AppDatabase"/>): one row per machine.</summary>
public sealed class JobSnapshotRepository(AppDatabase database)
{
    private const string Columns =
        "machine, file_name, md5, line, line_count, work_zero_x, work_zero_y, work_zero_z, leveled, recorded_utc";

    public JobSnapshot? Get(string machine) =>
        database.Query($"SELECT {Columns} FROM job_snapshots WHERE machine = $machine", Read, ("$machine", machine)).FirstOrDefault();

    /// <summary>Replaces the machine's snapshot.</summary>
    public void Save(JobSnapshot snapshot) =>
        database.Execute($"""
            INSERT INTO job_snapshots ({Columns})
            VALUES ($machine, $file, $md5, $line, $count, $x, $y, $z, $leveled, $utc)
            ON CONFLICT (machine) DO UPDATE SET
                file_name = excluded.file_name, md5 = excluded.md5, line = excluded.line, line_count = excluded.line_count,
                work_zero_x = excluded.work_zero_x, work_zero_y = excluded.work_zero_y, work_zero_z = excluded.work_zero_z,
                leveled = excluded.leveled, recorded_utc = excluded.recorded_utc;
            """,
            ("$machine", snapshot.Machine), ("$file", snapshot.FileName), ("$md5", snapshot.Md5), ("$line", snapshot.Line),
            ("$count", snapshot.LineCount), ("$x", Zero(snapshot, 0)), ("$y", Zero(snapshot, 1)), ("$z", Zero(snapshot, 2)),
            ("$leveled", snapshot.Leveled ? 1L : 0L), ("$utc", AppDatabase.Time(snapshot.RecordedUtc)));

    public void Delete(string machine) =>
        database.Execute("DELETE FROM job_snapshots WHERE machine = $machine", ("$machine", machine));

    private static object Zero(JobSnapshot snapshot, int axis) => AppDatabase.Nullable(snapshot.WorkZero?[axis]);

    private static JobSnapshot Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4),
        r.IsDBNull(5) || r.IsDBNull(6) || r.IsDBNull(7) ? null : [r.GetDouble(5), r.GetDouble(6), r.GetDouble(7)],
        r.GetInt64(8) == 1, AppDatabase.ParseTime(r.GetString(9)));
}
