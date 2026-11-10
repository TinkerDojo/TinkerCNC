using System.Globalization;
using TinkerCNC.Localization;

namespace TinkerCNC;

/// <summary>One value of a status field: what it is and its unit; switches are shown as on/off.</summary>
/// <param name="NoneValue">A value that means "nothing" (e.g. −1 for no tool), shown as "none".</param>
public sealed record StatusFieldPart(Text Label, string Unit = "", bool IsSwitch = false, string? NoneValue = null)
{
    private static readonly IReadOnlyDictionary<string, Text> Switches = new Dictionary<string, Text>
    {
        ["0"] = CoreTexts.Of("Status.Off"),
        ["1"] = CoreTexts.Of("Status.On"),
    };

    public Text Format(string raw)
    {
        var value = raw.Trim();
        if (value == NoneValue) return CoreTexts.Of("Status.None");
        if (IsSwitch) return Switches.GetValueOrDefault(value) ?? Text.Plain(value);
        return Text.Plain(Unit.Length == 0 ? value : $"{value} {Unit}");
    }
}

/// <param name="Parts">The comma-separated values in order; values beyond the list are shown as "value n".</param>
public sealed record StatusFieldInfo(Text Name, IReadOnlyList<StatusFieldPart> Parts);

/// <param name="Label">What the value is; null for a value of a field that is not known.</param>
public sealed record DescribedValue(Text? Label, Text Text);

/// <summary>A status field with readable names, e.g. Feed: current 0 mm/min, programmed 3000 mm/min, override 100 %.</summary>
public sealed record DescribedField(string Key, Text Name, IReadOnlyList<DescribedValue> Values, bool IsKnown);

/// <summary>
/// Names and units of the status report's fields, in the order the firmware writes them (Kernel.cpp,
/// get_query_string). Machines may append values the firmware source does not explain; those are listed as
/// "value n" rather than guessed.
/// </summary>
public static class StatusFields
{
    private static readonly IReadOnlyDictionary<string, StatusFieldInfo> Known = new Dictionary<string, StatusFieldInfo>
    {
        ["F"] = new(CoreTexts.Of("Status.Feed"),
        [
            new(CoreTexts.Of("Status.Current"), "mm/min"), new(CoreTexts.Of("Status.Programmed"), "mm/min"),
            new(CoreTexts.Of("Status.Override"), "%"),
        ]),
        ["S"] = new(CoreTexts.Of("Status.Spindle"),
        [
            new(CoreTexts.Of("Status.Current"), "rpm"), new(CoreTexts.Of("Status.Target"), "rpm"), new(CoreTexts.Of("Status.Override"), "%"),
            new(CoreTexts.Of("Status.VacuumMode"), IsSwitch: true), new(CoreTexts.Of("Status.SpindleTemperature"), "°C"),
            new(CoreTexts.Of("Status.SupplyTemperature"), "°C"), new(CoreTexts.Of("Status.Reserved")), new(CoreTexts.Of("Status.Reserved")),
            new(CoreTexts.Of("Status.ExternalOutputMode")),
        ]),
        ["T"] = new(CoreTexts.Of("Status.Tool"),
        [
            new(CoreTexts.Of("Status.ActiveTool"), NoneValue: "-1"), new(CoreTexts.Of("Status.LengthOffset"), "mm"),
            new(CoreTexts.Of("Status.WaitingForTool"), NoneValue: "-1"),
        ]),
        ["W"] = new(CoreTexts.Of("Status.WirelessProbe"), [new(CoreTexts.Of("Status.Battery"), "V")]),
        ["L"] = new(CoreTexts.Of("Status.Laser"),
        [
            new(CoreTexts.Of("Status.LaserMode"), IsSwitch: true), new(CoreTexts.Of("Status.IsOn"), IsSwitch: true),
            new(CoreTexts.Of("Status.TestMode"), IsSwitch: true), new(CoreTexts.Of("Status.Power")), new(CoreTexts.Of("Status.Scale"), "%"),
        ]),
        ["P"] = new(CoreTexts.Of("Status.JobProgress"),
            [new(CoreTexts.Of("Status.Line")), new(CoreTexts.Of("Status.Done"), "%"), new(CoreTexts.Of("Status.Elapsed"), "s")]),
        ["A"] = new(CoreTexts.Of("Status.ToolChanger"), [new(CoreTexts.Of("Status.State"))]),
        ["O"] = new(CoreTexts.Of("Status.AutoLevelling"), [new(CoreTexts.Of("Status.LargestDeviation"), "mm")]),
        ["H"] = new(CoreTexts.Of("Status.Halted"), [new(CoreTexts.Of("Status.ReasonCode"))]),
        ["V"] = new(CoreTexts.Of("Status.Vacuum"),
            [new(CoreTexts.Of("Status.IsOn"), IsSwitch: true), new(CoreTexts.Of("Status.Power"), "%")]), // as in the diagnostics report
        ["C"] = new(CoreTexts.Of("Status.Machine"),
        [
            new(CoreTexts.Of("Status.ModelCode")), new(CoreTexts.Of("Status.FeatureFlags")), new(CoreTexts.Of("Status.InchMode"), IsSwitch: true),
            new(CoreTexts.Of("Status.AbsoluteMode"), IsSwitch: true),
        ]),
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
            return new DescribedField(key, Text.Plain(key), raw.Length == 0 ? [] : [new DescribedValue(null, Text.Plain(value))], IsKnown: false);
        var values = raw.Select((v, i) => i < info.Parts.Count
                ? new DescribedValue(info.Parts[i].Label, info.Parts[i].Format(v))
                : new DescribedValue(CoreTexts.Of("Status.Value", (i + 1).ToString(CultureInfo.InvariantCulture)), Text.Plain(v.Trim())))
            .ToList();
        return new DescribedField(key, info.Name, values, IsKnown: true);
    }
}
