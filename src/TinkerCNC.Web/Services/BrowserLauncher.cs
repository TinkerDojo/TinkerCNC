using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace TinkerCNC.Web.Services;

/// <summary>
/// Opens the dashboard in the default browser once the server listens, for installed copies that were set to use the
/// browser instead of their own window (<c>TinkerCNC:Window = false</c>). Not after an update restart, when the old
/// tab is still open.
/// </summary>
public sealed class BrowserLauncher(
    IHostApplicationLifetime lifetime, IServer server, IConfiguration config, ILogger<BrowserLauncher> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (DesktopApp.IsInstalled && !DesktopApp.UsesWindow(config) && !DesktopApp.RestartedAfterUpdate)
            lifetime.ApplicationStarted.Register(Open);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Open()
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (address is null) return;
        try
        {
            DesktopApp.OpenBrowser(DesktopApp.Browsable(address));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not open the browser; the dashboard is at {Address}", address);
        }
    }
}
