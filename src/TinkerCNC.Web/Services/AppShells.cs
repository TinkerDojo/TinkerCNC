using System.Net.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TinkerCNC.Host;

namespace TinkerCNC.Web.Services;

/// <summary>How the app presents itself once the server is built.</summary>
public interface IAppShell
{
    void Run(WebApplication app);
}

public static class AppShells
{
    /// <summary>Its own window when <see cref="DesktopApp.UsesWindow"/>, otherwise a plain server.</summary>
    public static IAppShell For(WebApplication app) =>
        DesktopApp.UsesWindow(app.Configuration) ? new WindowShell() : new ServerShell();

    /// <summary>Asks a running instance to show its window; false when none answers.</summary>
    public static bool ActivateRunningInstance(string dashboardUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return http.PostAsync(new Uri(new Uri(dashboardUrl), DesktopEndpoints.ActivatePath), null)
                .GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Server only: logs to the console, the dashboard is opened in a browser. An installed copy started a second time
/// finds the port taken and shows the running one instead.
/// </summary>
public sealed class ServerShell : IAppShell
{
    public void Run(WebApplication app)
    {
        try
        {
            app.Run();
        }
        catch (IOException ex) when (DesktopApp.IsInstalled && ex.InnerException is AddressInUseException)
        {
            if (DesktopApp.DashboardUrl(app.Configuration) is { } url) DesktopApp.OpenBrowser(url);
        }
    }
}

/// <summary>
/// The app's own window: starts the server in the background, shows the dashboard in a <see cref="DesktopWindow"/>
/// and stops the server when the window closes. A second start brings the running window to the front instead.
/// There is no console, so the log goes to <c>logs/tinkercnc.log</c> in the data folder.
/// </summary>
public sealed class WindowShell : IAppShell
{
    public void Run(WebApplication app)
    {
        if (DesktopApp.DashboardUrl(app.Configuration) is { } configured && AppShells.ActivateRunningInstance(configured)) return;

        var paths = app.Services.GetRequiredService<AppPaths>();
        DesktopLog.RedirectConsole(Path.Combine(paths.DataDirectory, "logs"));
        app.StartAsync().GetAwaiter().GetResult();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            app.Services.GetRequiredService<DesktopWindow>().Run(DesktopApp.Browsable(address), paths.DataDirectory);
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
    }
}

/// <summary>Where console output goes when there is no console: a log file that is replaced on every start.</summary>
public static class DesktopLog
{
    public const string FileName = "tinkercnc.log";

    public static void RedirectConsole(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path)) File.Copy(path, path + ".previous", overwrite: true);
        var writer = new StreamWriter(path, append: false) { AutoFlush = true };
        Console.SetOut(writer);
        Console.SetError(writer);
    }
}
