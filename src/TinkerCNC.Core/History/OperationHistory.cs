using System.Globalization;
using Microsoft.Data.Sqlite;
using TinkerCNC.Storage;

namespace TinkerCNC.History;

/// <summary>One measured execution of a tool operation.</summary>
/// <param name="Fingerprint">See <see cref="GCode.OperationFingerprint"/>.</param>
/// <param name="Machine">Machine name; timings are preferred from the same machine.</param>
/// <param name="EstimatedSeconds">Estimated cutting time, kept to compare estimate and reality.</param>
/// <param name="ActiveSeconds">Measured cutting time without pauses (feed hold).</param>
/// <param name="AverageFeedOverridePercent">Time-weighted feed override while cutting.</param>
public sealed record OperationRunRecord(
    string Fingerprint, string Machine, string? FileName, int? ToolNumber, int LineCount,
    double EstimatedSeconds, double ActiveSeconds, double PausedSeconds, double AverageFeedOverridePercent,
    DateTime RecordedUtc)
{
    /// <summary>The cutting time this run would have taken at 100 % feed override.</summary>
    public double SecondsAt100Percent => ActiveSeconds * AverageFeedOverridePercent / 100;
}

/// <summary>Aggregated measurements of one operation.</summary>
/// <param name="MedianSeconds">Median cutting time at 100 % feed override.</param>
/// <param name="FromThisMachine">False when only other machines have recorded it.</param>
public sealed record OperationStatistics(string Fingerprint, int Runs, double MedianSeconds, DateTime LastRecordedUtc, bool FromThisMachine);

/// <summary>Measured tool-operation times, in the app's database (<see cref="AppDatabase"/>).</summary>
public sealed class OperationHistory
{
    private readonly AppDatabase _db;

    public OperationHistory(string? databasePath = null) : this(new AppDatabase(databasePath ?? AppDatabase.DefaultPath))
    {
    }

    public OperationHistory(AppDatabase database) => _db = database;

    public string DatabasePath => _db.Path;

    public void Record(OperationRunRecord run)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO operation_runs (fingerprint, machine, file_name, tool_number, line_count, estimated_seconds,
                active_seconds, paused_seconds, average_feed_override, seconds_at_100_percent, recorded_utc)
            VALUES ($fingerprint, $machine, $file, $tool, $lines, $estimated, $active, $paused, $override, $at100, $utc)
            """;
        cmd.Parameters.AddWithValue("$fingerprint", run.Fingerprint);
        cmd.Parameters.AddWithValue("$machine", run.Machine);
        cmd.Parameters.AddWithValue("$file", (object?)run.FileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tool", (object?)run.ToolNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lines", run.LineCount);
        cmd.Parameters.AddWithValue("$estimated", run.EstimatedSeconds);
        cmd.Parameters.AddWithValue("$active", run.ActiveSeconds);
        cmd.Parameters.AddWithValue("$paused", run.PausedSeconds);
        cmd.Parameters.AddWithValue("$override", run.AverageFeedOverridePercent);
        cmd.Parameters.AddWithValue("$at100", run.SecondsAt100Percent);
        cmd.Parameters.AddWithValue("$utc", AppDatabase.Time(run.RecordedUtc));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Statistics for the given operations. Runs on <paramref name="machine"/> are used when there are any;
    /// otherwise runs from other machines are used (and flagged).
    /// </summary>
    public IReadOnlyDictionary<string, OperationStatistics> GetStatistics(IEnumerable<string> fingerprints, string machine)
    {
        var wanted = fingerprints.Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<string, OperationStatistics>();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var names = wanted.Select((_, i) => $"$f{i}").ToList();
        cmd.CommandText = $"SELECT fingerprint, machine, seconds_at_100_percent, recorded_utc FROM operation_runs WHERE fingerprint IN ({string.Join(',', names)})";
        for (var i = 0; i < wanted.Count; i++) cmd.Parameters.AddWithValue(names[i], wanted[i]);

        var rows = new List<(string Fingerprint, string Machine, double Seconds, DateTime Utc)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2),
                    AppDatabase.ParseTime(reader.GetString(3))));
        }

        return rows.GroupBy(r => r.Fingerprint).ToDictionary(g => g.Key, g =>
        {
            var local = g.Where(r => string.Equals(r.Machine, machine, StringComparison.OrdinalIgnoreCase)).ToList();
            var used = local.Count > 0 ? local : g.ToList();
            return new OperationStatistics(g.Key, used.Count, Median(used.Select(r => r.Seconds)), used.Max(r => r.Utc), local.Count > 0);
        });
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private SqliteConnection Open() => _db.Open();
}
