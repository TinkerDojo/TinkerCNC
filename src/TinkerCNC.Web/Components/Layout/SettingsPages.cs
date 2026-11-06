namespace TinkerCNC.Web.Components.Layout;

/// <summary>A sub-page listed under "Settings" in the side bar.</summary>
public sealed record SettingsPage(string Title, string Icon, string Href);

/// <summary>The settings sub-pages, in side bar order; a new page only needs an entry here.</summary>
public static class SettingsPages
{
    public static readonly IReadOnlyList<SettingsPage> All =
    [
        new("Time-lapse", "film", "settings/timelapse"),
        new("About", "info", "settings/about"),
    ];
}
