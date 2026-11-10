using TinkerCNC.Localization;
using TinkerCNC.Web.Localization;

namespace TinkerCNC.Web.Components.Layout;

/// <summary>A sub-page listed under "Settings" in the side bar.</summary>
public sealed record SettingsPage(Text Title, string Icon, string Href);

/// <summary>The settings sub-pages, in side bar order; a new page only needs an entry here.</summary>
public static class SettingsPages
{
    public static readonly IReadOnlyList<SettingsPage> All =
    [
        new(Texts.Of("Layout.Settings.Notifications"), "bell", "settings/notifications"),
        new(Texts.Of("Layout.Settings.Timelapse"), "film", "settings/timelapse"),
        new(Texts.Of("Layout.Settings.Language"), "globe", "settings/language"),
        new(Texts.Of("Layout.Settings.About"), "info", "settings/about"),
    ];
}
