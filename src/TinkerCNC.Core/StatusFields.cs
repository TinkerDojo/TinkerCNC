using System.Globalization;

namespace TinkerCNC;

/// <summary>One value of a status field: what it is and its unit; switches are shown as on/off.</summary>
/// <param name="NoneValue">A value that means "nothing" (e.g. −1 for no tool), shown as "none".</param>
public sealed record StatusFieldPart(string Label, string Unit = "", bool IsSwitch = false, string? NoneValue = null)
{
    public string Format(string raw)
    {
        var value = raw.Trim();
        if (value == NoneValue) return "none";
        if (IsSwitch) return value == "0" ? "off" : value == "1" ? "on" : value;
        return Unit.Length == 0 ? value : $"{value} {Unit}";
    }
}

/// <param name="Parts">The comma-separated values in order; values beyond the list are shown as "value n".</param>
public sealed record StatusFieldInfo(string Name, IReadOnlyList<StatusFieldPart> Parts);

public sealed record DescribedValue(string Label, string Text);

/// <summary>A status field with readable names, e.g. Feed: current 0 mm/min, programmed 3000 mm/min, override 100 %.</summary>
public sealed record DescribedField(string Key, string Name, IReadOnlyList<DescribedValue> Values, bool IsKnown);

/// <summary>
/// Names and units of the status report's fields, in the order the firmware writes them (Kernel.cpp,
/// get_query_string). Machines may append values the firmware source does not explain; those are listed as
/// "value n" rather than guessed.
/// </summary>
public static class StatusFields
{
    private static readonly IReadOnlyDictionary<string, StatusFieldInfo> Known = new Dictionary<string, StatusFieldInfo>
    {
        ["F"] = new("Feed", [new("current", "mm/min"), new("programmed", "mm/min"), new("override", "%")]),
        ["S"] = new("Spindle",
        [
            new("current", "rpm"), new("target", "rpm"), new("override", "%"), new("vacuum mode", IsSwitch: true),
            new("spindle temperature", "°C"), new("power supply temperature", "°C"), new("reserved"), new("reserved"),
            new("external output mode"),
        ]),
        ["T"] = new("Tool", [new("active tool", NoneValue: "-1"), new("length offset", "mm"), new("waiting for tool", NoneValue: "-1")]),
        ["W"] = new("Wireless probe", [new("battery", "V")]),
        ["L"] = new("Laser", [new("laser mode", IsSwitch: true), new("on", IsSwitch: true), new("test mode", IsSwitch: true), new("power"), new("scale", "%")]),
        ["P"] = new("Job progress", [new("line"), new("done", "%"), new("elapsed", "s")]),
        ["A"] = new("Tool changer", [new("state")]),
        ["O"] = new("Auto-levelling", [new("largest deviation", "mm")]),
        ["H"] = new("Halted", [new("reason code")]),
        ["V"] = new("Vacuum", [new("on", IsSwitch: true), new("power", "%")]), // as in the diagnostics report
        ["C"] = new("Machine", [new("model code"), new("feature flags"), new("inch mode", IsSwitch: true), new("absolute mode", IsSwitch: true)]),
    };

    private static readonly string[] Order = [.. Known.Keys];

    /// <summary>Every field except the positions, the known ones first (in firmware order).</summary>
    public static IReadOnlyList<DescribedField> Describe(StatusReport status) => status.Fields
        .Where(f => f.Key is not ("MPos" or "WPos"))
        .OrderBy(f => Array.IndexOf(Order, f.Key) is var i and >= 0 ? i : Order.Length)
        .Select(f => Describe(f.Key, f.Value))
        .ToList();

    public static DescribedField Describe(string key, string value)
    {
        var raw = value.Length == 0 ? [] : value.Split(',');
        if (!Known.TryGetValue(key, out var info))
            return new DescribedField(key, key, raw.Length == 0 ? [] : [new DescribedValue("", value)], IsKnown: false);
        var values = raw.Select((v, i) => i < info.Parts.Count
                ? new DescribedValue(info.Parts[i].Label, info.Parts[i].Format(v))
                : new DescribedValue(string.Create(CultureInfo.InvariantCulture, $"value {i + 1}"), v.Trim()))
            .ToList();
        return new DescribedField(key, info.Name, values, IsKnown: true);
    }
}
