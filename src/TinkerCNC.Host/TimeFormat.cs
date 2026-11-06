namespace TinkerCNC.Host;

/// <summary>Human-readable relative times for the UI.</summary>
public static class TimeFormat
{
    private static readonly (TimeSpan Below, Func<TimeSpan, string> Format)[] Ranges =
    {
        (TimeSpan.FromSeconds(2), _ => "just now"),
        (TimeSpan.FromMinutes(1), d => $"{(int)d.TotalSeconds}s ago"),
        (TimeSpan.FromHours(1), d => $"{(int)d.TotalMinutes}m ago"),
        (TimeSpan.MaxValue, d => $"{(int)d.TotalHours}h ago"),
    };

    private static readonly (TimeSpan Below, Func<TimeSpan, string> Format)[] Durations =
    {
        (TimeSpan.FromMinutes(1), t => $"{t.Seconds}s"),
        (TimeSpan.FromHours(1), t => $"{t.Minutes}m {t.Seconds:00}s"),
        (TimeSpan.MaxValue, t => $"{(int)t.TotalHours}h {t.Minutes:00}m"),
    };

    /// <summary>Compact duration: "45s", "12m 05s", "1h 02m".</summary>
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return Durations.First(r => t < r.Below).Format(t);
    }

    /// <summary>Local clock time; the weekday is added when it is not today. (Server time zone.)</summary>
    public static string Clock(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("ddd HH:mm");
    }

    public static string Ago(DateTime? utc)
    {
        if (utc is null) return "never";
        var elapsed = DateTime.UtcNow - utc.Value;
        return Ranges.First(r => elapsed < r.Below).Format(elapsed);
    }
}
