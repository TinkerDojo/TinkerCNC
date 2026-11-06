using System.Globalization;

namespace TinkerCNC;

/// <summary>
/// Parsed status report (payload of a 0x81 packet), e.g.
/// <c>&lt;Idle|MPos:0.000,0.000,0.000,0.000|WPos:...|F:0,100|S:0,100|...&gt;</c>.
/// Fields are separated by '|'; the first one is the state (Idle, Run, Hold, Home, Tool, Wait, Alarm, Sleep, …).
/// Every key/value pair is kept in <see cref="Fields"/>; the properties below interpret the known ones
/// (field layout as in the open-source firmware, Kernel.cpp).
/// </summary>
public sealed record StatusReport(string Raw, string State, IReadOnlyDictionary<string, string> Fields)
{
    public double[]? MachinePosition => GetNumbers("MPos");
    public double[]? WorkPosition => GetNumbers("WPos");

    /// <summary>
    /// Job progress from the <c>P</c> field (<c>line,percent,elapsed</c>), present while a file is playing.
    /// </summary>
    public JobProgress? Job => GetNumbers("P") is { Length: >= 3 } p ? new JobProgress((int)p[0], p[1], p[2]) : null;

    /// <summary>Feed override in percent: the third value of <c>F</c>.</summary>
    public double? FeedOverridePercent => GetNumbers("F") is { Length: >= 3 } f ? f[2] : null;

    /// <summary>
    /// The <c>T</c> field, <c>T:active,offset[,target]</c> (firmware Kernel.cpp). A missing or negative target
    /// means no tool change is pending.
    /// </summary>
    public ToolStatus? Tool => GetNumbers("T") is { Length: >= 1 } t
        ? new ToolStatus((int)t[0], t.Length > 1 ? t[1] : null, t.Length > 2 && t[2] >= 0 ? (int)t[2] : null)
        : null;

    public double[]? GetNumbers(string key)
    {
        if (!Fields.TryGetValue(key, out var value)) return null;
        var parts = value.Split(',');
        var result = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                return null;
        return result;
    }

    public bool IsPlaying => Job is not null;

    public static StatusReport Parse(string text)
    {
        var body = text.Trim().TrimStart('<').TrimEnd('>');
        var parts = body.Split('|');
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < parts.Length; i++)
        {
            var colon = parts[i].IndexOf(':');
            if (colon > 0) fields[parts[i][..colon]] = parts[i][(colon + 1)..];
            else if (parts[i].Length > 0) fields[parts[i]] = string.Empty;
        }
        return new StatusReport(text, parts.Length > 0 ? parts[0] : string.Empty, fields);
    }

    public override string ToString()
    {
        var m = MachinePosition;
        var w = WorkPosition;
        string Fmt(double[]? v) => v is null ? "-" : string.Join(", ", v.Select(x => x.ToString("0.000", CultureInfo.InvariantCulture)));
        return $"{State}  MPos[{Fmt(m)}]  WPos[{Fmt(w)}]";
    }
}

/// <summary>
/// The <c>P</c> status field: the line being executed, the percentage done and the elapsed seconds.
/// </summary>
public sealed record JobProgress(int Line, double Percent, double ElapsedSeconds);

/// <param name="Active">Tool in the spindle (−1 = none).</param>
/// <param name="Offset">Tool length offset in millimetres.</param>
/// <param name="Target">
/// Tool a manual tool change is waiting for while the state is <c>Tool</c> (null = none): firmware ATCHandler sets it
/// on <c>M6</c> before entering the wait with <c>M490.1</c>.
/// </param>
public sealed record ToolStatus(int Active, double? Offset, int? Target);
