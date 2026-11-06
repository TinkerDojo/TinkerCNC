using System.Globalization;

namespace TinkerCNC.GCode;

/// <summary>A tool declared in the file header.</summary>
public sealed record ToolInfo(int Number, string? Name, string? Type, double? Diameter);

/// <summary>A toolpath declared in the file header.</summary>
public sealed record ToolpathInfo(int Number, int ToolNumber, string? Name);

/// <summary>
/// Metadata comments that some CAM exporters write at the top of G-code files:
/// <c>;@MKR|KIND|key=value|key=value…</c> with kinds such as TOOL (number, name, type, diameter…),
/// TOOLPATH (number, tool_number, name), TOOLPATH_START (toolpath_number), TIME (seconds) and
/// MAXFEEDRATE (value). Unknown kinds are kept in <see cref="Records"/>.
/// </summary>
public sealed class HeaderMetadata
{
    private const string Prefix = ";@MKR|";

    private readonly List<HeaderRecord> _records = new();

    public IReadOnlyList<HeaderRecord> Records => _records;

    public IReadOnlyDictionary<int, ToolInfo> Tools => _records
        .Where(r => r.Kind == "TOOL" && r.Int("number") is not null)
        .GroupBy(r => r.Int("number")!.Value)
        .ToDictionary(g => g.Key, g => new ToolInfo(g.Key, g.Last().Text("name"), g.Last().Text("type"), g.Last().Number("diameter")));

    public IReadOnlyList<ToolpathInfo> Toolpaths => _records
        .Where(r => r.Kind == "TOOLPATH" && r.Int("number") is not null)
        .Select(r => new ToolpathInfo(r.Int("number")!.Value, r.Int("tool_number") ?? 0, r.Text("name")))
        .ToList();

    /// <summary>Sum of all TIME records (the exporter's own estimate), or null if the file has none.</summary>
    public double? EstimatedSeconds =>
        _records.Where(r => r.Kind == "TIME").Select(r => r.Number("seconds")).Where(s => s is not null).Sum() is { } sum && sum > 0
            ? sum
            : null;

    public double? MaxFeedRate => _records.LastOrDefault(r => r.Kind == "MAXFEEDRATE")?.Number("value");

    /// <summary>Adds the line if it is a metadata comment; returns false otherwise.</summary>
    public bool TryAdd(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        _records.Add(HeaderRecord.Parse(trimmed[Prefix.Length..]));
        return true;
    }
}

public sealed record HeaderRecord(string Kind, IReadOnlyDictionary<string, string> Values)
{
    public static HeaderRecord Parse(string body)
    {
        var parts = body.Split('|');
        var values = parts.Skip(1)
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
        return new HeaderRecord(parts[0].Trim().ToUpperInvariant(), values);
    }

    public string? Text(string key) => Values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public double? Number(string key) =>
        Text(key) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public int? Int(string key) => Number(key) is { } d ? (int)Math.Round(d) : null;
}
