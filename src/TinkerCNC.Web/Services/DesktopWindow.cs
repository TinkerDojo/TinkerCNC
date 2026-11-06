using Photino.NET;

namespace TinkerCNC.Web.Services;

/// <summary>
/// The app's own window: a native window with an embedded web view (WebView2 on Windows, WebKit on macOS) showing
/// the dashboard served by this process.
/// </summary>
public sealed class DesktopWindow
{
    private const string Title = "TinkerCNC";
    private PhotinoWindow? _window;

    /// <summary>Shows the window and blocks until it is closed; must run on the main thread.</summary>
    /// <param name="dataDirectory">The web view keeps its storage here (not in the app folder, which updates replace).</param>
    public void Run(string url, string dataDirectory)
    {
        var window = new PhotinoWindow()
            .SetTitle(Title)
            .SetSize(1440, 900)
            .SetMinSize(1024, 680)
            .Center()            // where it goes when restored from maximized
            .SetMaximized(true)
            .SetTemporaryFilesPath(Path.Combine(dataDirectory, "webview"))
            .SetDevToolsEnabled(false)
            .SetMediaAutoplayEnabled(true);
        var icon = Path.Combine(AppContext.BaseDirectory, "icons", "TinkerCNC.ico");
        if (OperatingSystem.IsWindows() && File.Exists(icon)) window.SetIconFile(icon);
        _window = window.Load(new Uri(url));
        _window.WaitForClose();
        _window = null;
    }

    /// <summary>Brings the window to the front (another start of the app asks for it).</summary>
    public void Activate()
    {
        var window = _window;
        window?.Invoke(() =>
        {
            window.SetMinimized(false);
            window.SetTopMost(true);  // raise above other windows…
            window.SetTopMost(false); // …without pinning it there
        });
    }
}
