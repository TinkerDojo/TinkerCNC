namespace TinkerCNC.Web.Localization;

/// <summary>
/// <c>GET /language?use=de&amp;return=/settings/language</c> remembers a language for this browser
/// (<c>use=</c> empty: follow the browser again) and goes back to the page, which loads anew in that language.
/// </summary>
public static class LanguageEndpoints
{
    public const string Path = "/language";

    public static string Link(UiLanguage? language, string returnPath) =>
        $"{Path.TrimStart('/')}?use={language?.Code}&return={Uri.EscapeDataString(returnPath)}";

    public static void MapLanguageEndpoints(this WebApplication app) =>
        app.MapGet(Path, (HttpContext http, string? use, string? @return) =>
        {
            LanguagePreference.Remember(http.Response, UiLanguages.Find(use));
            return Results.LocalRedirect(IsLocal(@return) ? @return! : "/");
        });

    private static bool IsLocal(string? path) =>
        path is ['/', not '/' and not '\\', ..] or "/";
}
