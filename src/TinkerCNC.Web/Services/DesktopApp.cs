using System.Diagnostics;
using Velopack;
using Velopack.Locators;

namespace TinkerCNC.Web.Services;

/// <summary>
/// How the app behaves when it was installed with Velopack (Setup.exe on Windows, .pkg on macOS) rather than run from
/// a build: it starts from its own folder and shows the dashboard in its own window (see <see cref="AppShells"/>).
/// </summary>
public static class DesktopApp
{
    /// <summary>Handles Velopack's install/update/uninstall hooks; must run before anything else.</summary>
    public static void HandleInstallerHooks() =>
        VelopackApp.Build()
            .OnRestarted(_ => RestartedAfterUpdate = true)
            .Run();

    /// <summary>Version of the installed release, or null when running from a build (dotnet run, tests).</summary>
    public static string? InstalledVersion =>
        VelopackLocator.IsCurrentSet ? VelopackLocator.Current.CurrentlyInstalledVersion?.ToString() : null;

    public static bool IsInstalled => InstalledVersion is not null;

    /// <summary>Set when Velopack restarted the app after an update: the browser tab is still open then.</summary>
    public static bool RestartedAfterUpdate { get; private set; }

    /// <summary>
    /// Installed apps are started from shortcuts whose working directory is not guaranteed, so they use their own
    /// folder (wwwroot, appsettings.json) as content root.
    /// </summary>
    public static WebApplicationOptions Options(string[] args) => IsInstalled
        ? new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory }
        : new WebApplicationOptions { Args = args };

    /// <summary>
    /// Whether the dashboard opens in the app's own window (<c>TinkerCNC:Window</c>); by default installed copies do
    /// and builds started from source (<c>dotnet run</c>, servers) do not.
    /// </summary>
    public static bool UsesWindow(IConfiguration config) => config.GetValue<bool?>("TinkerCNC:Window") ?? IsInstalled;

    /// <summary>The first configured URL with a wildcard host replaced by localhost.</summary>
    public static string? DashboardUrl(IConfiguration config) =>
        config["Urls"]?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } url
            ? Browsable(url)
            : null;

    public static string Browsable(string address) =>
        address.Replace("://0.0.0.0", "://localhost").Replace("://[::]", "://localhost").Replace("://+", "://localhost");

    /// <summary>Opens a URL in the default browser (the shell's "open" on Windows and macOS alike).</summary>
    public static void OpenBrowser(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
