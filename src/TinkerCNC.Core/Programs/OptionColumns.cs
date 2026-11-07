using System.Globalization;
using TinkerCNC.Jobs;

namespace TinkerCNC.Programs;

/// <summary>
/// One start option as a database column. <see cref="Get"/> gives the value as SQLite stores it (INTEGER as long, REAL
/// as double, TEXT as string); <see cref="Set"/> takes what SQLite reads back.
/// </summary>
public sealed record OptionColumn(string Name, Func<JobStartOptions, object> Get, Action<JobStartOptions, object> Set);

/// <summary>
/// Every <see cref="JobStartOptions"/> value a setup keeps, one column each. The single list of the options: the table
/// (<see cref="SetupRepository"/>) and the check for unsaved changes (<see cref="Same"/>) both use it.
/// A new option needs a column here and a migration in <see cref="Storage.AppDatabase"/>.
/// </summary>
public static class OptionColumns
{
    public static readonly IReadOnlyList<OptionColumn> All =
    [
        Choice("work_origin", o => o.WorkOrigin, (o, v) => o.WorkOrigin = v),
        Number("origin_offset_x", o => o.OriginOffsetX, (o, v) => o.OriginOffsetX = v),
        Number("origin_offset_y", o => o.OriginOffsetY, (o, v) => o.OriginOffsetY = v),
        Number("xyz_probe_height", o => o.XyzProbeHeight, (o, v) => o.XyzProbeHeight = v),
        Number("xyz_probe_tool_diameter", o => o.XyzProbeToolDiameter, (o, v) => o.XyzProbeToolDiameter = v),
        Flag("scan_margin", o => o.ScanMargin, (o, v) => o.ScanMargin = v),
        Flag("z_probe", o => o.ZProbe, (o, v) => o.ZProbe = v),
        Flag("z_probe_from_work_origin", o => o.ZProbeFromWorkOrigin, (o, v) => o.ZProbeFromWorkOrigin = v),
        Number("z_probe_offset_x", o => o.ZProbeOffsetX, (o, v) => o.ZProbeOffsetX = v),
        Number("z_probe_offset_y", o => o.ZProbeOffsetY, (o, v) => o.ZProbeOffsetY = v),
        Flag("leveling", o => o.Leveling, (o, v) => o.Leveling = v),
        Count("leveling_points_x", o => o.LevelingPointsX, (o, v) => o.LevelingPointsX = v),
        Count("leveling_points_y", o => o.LevelingPointsY, (o, v) => o.LevelingPointsY = v),
        Number("leveling_height", o => o.LevelingHeight, (o, v) => o.LevelingHeight = v),
        Mode("mode_vacuum", JobModes.Vacuum),
        Mode("mode_external_output", JobModes.ExternalOutput),
        Mode("mode_blow", JobModes.Blow),
        Mode("mode_bed_clean", JobModes.BedClean),
        Mode("mode_anti_static", JobModes.AntiStatic),
        Flag("timelapse", o => o.Timelapse, (o, v) => o.Timelapse = v),
    ];

    /// <summary>Whether two sets of options start a program the same way (job options missing count as off).</summary>
    public static bool Same(JobStartOptions a, JobStartOptions b) => All.All(c => Equals(c.Get(a), c.Get(b)));

    private static OptionColumn Flag(string name, Func<JobStartOptions, bool> get, Action<JobStartOptions, bool> set) =>
        new(name, o => get(o) ? 1L : 0L, (o, v) => set(o, Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0));

    private static OptionColumn Number(string name, Func<JobStartOptions, double> get, Action<JobStartOptions, double> set) =>
        new(name, o => get(o), (o, v) => set(o, Convert.ToDouble(v, CultureInfo.InvariantCulture)));

    private static OptionColumn Count(string name, Func<JobStartOptions, int> get, Action<JobStartOptions, int> set) =>
        new(name, o => (long)get(o), (o, v) => set(o, Convert.ToInt32(v, CultureInfo.InvariantCulture)));

    /// <summary>Stored by name; an unknown name reads as the default choice.</summary>
    private static OptionColumn Choice<T>(string name, Func<JobStartOptions, T> get, Action<JobStartOptions, T> set) where T : struct, Enum =>
        new(name, o => get(o).ToString(), (o, v) => set(o, Enum.TryParse<T>(Convert.ToString(v, CultureInfo.InvariantCulture), out var e) ? e : default));

    private static OptionColumn Mode(string name, JobMode mode) =>
        Flag(name, o => o.IsOn(mode), (o, on) => o.Modes[mode.Key] = on);
}
