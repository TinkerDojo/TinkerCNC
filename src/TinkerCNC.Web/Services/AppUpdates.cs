using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace TinkerCNC.Web.Services;

/// <summary>
/// Updates of the installed app through Velopack. The feed is <c>TinkerCNC:Updates:Url</c>, or the value the build was
/// given (<c>-p:UpdatesUrl=…</c>): a GitHub repository (releases created with <c>vpk upload github</c>), or any URL or
/// local folder holding the files <c>vpk pack</c> produced.
/// Only installed copies can update; builds started with <c>dotnet run</c> just show their version.
/// </summary>
public sealed class AppUpdates
{
    private readonly UpdateManager? _manager;
    private UpdateInfo? _available;

    public AppUpdates(IConfiguration config)
    {
        Feed = config["TinkerCNC:Updates:Url"] is { Length: > 0 } url ? url : AppInfo.BuildValue("UpdatesUrl");
        if (DesktopApp.IsInstalled && Feed is { } feed) _manager = ManagerFor(feed);
    }

    public event Action? Changed;

    /// <summary>Where updates come from, or null when none is configured.</summary>
    public string? Feed { get; }

    public string CurrentVersion => DesktopApp.InstalledVersion ?? BuildVersion;

    public bool IsInstalled => DesktopApp.IsInstalled;

    /// <summary>Installed and a feed is configured.</summary>
    public bool CanUpdate => _manager is not null;

    public string? AvailableVersion => _available?.TargetFullRelease.Version.ToString();
    public DateTime? CheckedUtc { get; private set; }

    /// <summary>Download progress in percent while an update downloads.</summary>
    public int? DownloadPercent { get; private set; }

    public async Task CheckAsync()
    {
        if (_manager is null) return;
        _available = await _manager.CheckForUpdatesAsync();
        CheckedUtc = DateTime.UtcNow;
        Changed?.Invoke();
    }

    /// <summary>Downloads the available update, then exits, installs it and starts the new version.</summary>
    public async Task InstallAndRestartAsync()
    {
        if (_manager is null || _available is not { } update) return;
        DownloadPercent = 0;
        Changed?.Invoke();
        await _manager.DownloadUpdatesAsync(update, percent =>
        {
            DownloadPercent = percent;
            Changed?.Invoke();
        });
        _manager.ApplyUpdatesAndRestart(update);
    }

    /// <summary>GitHub releases, or Velopack's own handling of an http(s) URL or a local folder.</summary>
    private static UpdateManager ManagerFor(string feed) =>
        feed.Contains("github.com", StringComparison.OrdinalIgnoreCase)
            ? new UpdateManager(new GithubSource(feed, accessToken: null, prerelease: false))
            : new UpdateManager(feed);

    private static string BuildVersion =>
        typeof(AppUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";
}
