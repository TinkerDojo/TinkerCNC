using System.Globalization;
using TinkerCNC.Jobs;

namespace TinkerCNC.Web.Components.Job;

/// <summary>Start options in a few words, for lists of setups.</summary>
public static class StartOptionsText
{
    private static readonly IReadOnlyDictionary<string, string> ModeNames =
        JobModes.All.ToDictionary(m => m.Key, m => m.Name, StringComparer.Ordinal);

    public static IEnumerable<string> Summary(JobStartOptions options)
    {
        if (options.WorkOrigin != WorkOriginChoice.Keep) yield return $"origin: {WorkOriginLooks.For(options.WorkOrigin).Label}";
        if (options.ScanMargin) yield return "scan margin";
        if (options.ZProbe) yield return "Z probe";
        if (options.Leveling) yield return $"leveling {options.LevelingPointsX} × {options.LevelingPointsY}";
        foreach (var (key, on) in options.Modes.Where(m => m.Value))
            yield return ModeNames.GetValueOrDefault(key, key).ToLowerInvariant();
        if (options.Timelapse) yield return "time-lapse";
    }

    public static string Mm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
