using TinkerCNC.Host;
using TinkerCNC.Web.Components;
using TinkerCNC.Web.Localization;
using TinkerCNC.Web.Services;

namespace TinkerCNC.Web;

public static class Program
{
    [STAThread] // the window's web view (WebView2) needs a single-threaded apartment on Windows
    public static void Main(string[] args)
    {
        DesktopApp.HandleInstallerHooks();
        var app = Build(args);
        AppShells.For(app).Run(app);
    }

    private static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(DesktopApp.Options(args));

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddTinkerCNCHost();
        builder.Services.AddScoped<UiPreferences>();
        builder.Services.AddScoped<Texts>();
        builder.Services.AddScoped<SystemNotifications>();
        builder.Services.AddSingleton<AppUpdates>();
        builder.Services.AddSingleton<AppInfo>();
        builder.Services.AddSingleton<DesktopWindow>();
        builder.Services.AddHostedService<BrowserLauncher>();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
        }
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapMediaEndpoints();
        app.MapToolpathEndpoints();
        app.MapLegalEndpoints();
        app.MapDesktopEndpoints();
        app.MapLanguageEndpoints();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        return app;
    }
}
