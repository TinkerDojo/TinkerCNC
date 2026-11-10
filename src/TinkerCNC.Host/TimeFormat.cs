namespace TinkerCNC.Host;

/// <summary>Human-readable relative times for the UI.</summary>
public static class TimeFormat
{
    private static readonly (TimeSpan Below, Func<TimeSpan, Text> Format)[] Ranges =
    {
        (TimeSpan.FromSeconds(2), _ => HostTexts.Of("Time.JustNow")),
        (TimeSpan.FromMinutes(1), d => HostTexts.Of("Time.SecondsAgo", (int)d.TotalSeconds)),
        (TimeSpan.FromHours(1), d => HostTexts.Of("Time.MinutesAgo", (int)d.TotalMinutes)),
        (TimeSpan.MaxValue, d => HostTexts.Of("Time.HoursAgo", (int)d.TotalHours)),
    };

    private static readonly (TimeSpan Below, Func<TimeSpan, string> Format)[] Durations =
    {
        (TimeSpan.FromMinutes(1), t => $"{t.Seconds}s"),
        (TimeSpan.FromHours(1), t => $"{t.Minutes}m {t.Seconds:00}s"),
        (TimeSpan.MaxValue, t => $"{(int)t.TotalHours}h {t.Minutes:00}m"),
    };

    /// <summary>Compact duration: "45s", "12m 05s", "1h 02m" (the same in every language; also used in logs).</summary>
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return Durations.First(r => t < r.Below).Format(t);
    }

    /// <summary>Local clock time; the weekday is added when it is not today. (Server time zone.)</summary>
    /// <param name="language">The language of the weekday's name; the current culture when null.</param>
    public static string Clock(DateTime utc, IFormatProvider? language = null)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm", language) : local.ToString("ddd HH:mm", language);
    }

    /// <summary>How long ago: "just now", "12s ago", "5m ago", "3h ago", or "never".</summary>
    public static Text Ago(DateTime? utc)
    {
        if (utc is null) return HostTexts.Of("Time.Never");
        var elapsed = DateTime.UtcNow - utc.Value;
        return Ranges.First(r => elapsed < r.Below).Format(elapsed);
    }
}
