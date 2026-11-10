using System.Globalization;
using TinkerCNC.Jobs;
using TinkerCNC.Localization;
using TinkerCNC.Web.Localization;

namespace TinkerCNC.Web.Components.Job;

/// <summary>Start options in a few words, for lists of setups; each part is a text, shown with <c>T[part]</c>.</summary>
public static class StartOptionsText
{
    private static readonly IReadOnlyDictionary<string, JobMode> Modes =
        JobModes.All.ToDictionary(m => m.Key, StringComparer.Ordinal);

    public static IEnumerable<Text> Summary(JobStartOptions options)
    {
        if (options.WorkOrigin != WorkOriginChoice.Keep) yield return Texts.Of("Job.Summary.Origin", WorkOriginLooks.For(options.WorkOrigin).Label);
        if (options.ScanMargin) yield return Texts.Of("Job.Summary.ScanMargin");
        if (options.ZProbe) yield return Texts.Of("Job.Summary.ZProbe");
        if (options.Leveling) yield return Texts.Of("Job.Summary.Leveling", options.LevelingPointsX, options.LevelingPointsY);
        foreach (var (key, _) in options.Modes.Where(m => m.Value))
            yield return Modes.TryGetValue(key, out var mode) ? mode.Name : Text.Plain(key);
        if (options.Timelapse) yield return Texts.Of("Job.Summary.Timelapse");
    }

    public static string Mm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
