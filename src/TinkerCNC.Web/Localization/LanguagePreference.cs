namespace TinkerCNC.Web.Localization;

/// <summary>
/// Which language a browser gets: the one chosen in Settings → Language (kept in a cookie, so even the first page the
/// server sends is in it), else the first of the browser's languages that is translated (in the desktop window, the
/// system's), else English.
/// </summary>
/// <param name="Chosen">Picked by the user; null when the browser's language applies.</param>
/// <param name="Browser">What the browser asks for.</param>
public sealed record LanguagePreference(UiLanguage? Chosen, UiLanguage Browser)
{
    public const string CookieName = "TinkerCNC.Language";

    public UiLanguage Effective => Chosen ?? Browser;

    public static LanguagePreference For(HttpContext http) =>
        new(UiLanguages.Find(http.Request.Cookies[CookieName]), FromBrowser(http.Request));

    private static UiLanguage FromBrowser(HttpRequest request) =>
        request.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(l => l.Quality ?? 1)
            .Select(l => UiLanguages.Find(l.Value.Value))
            .FirstOrDefault(l => l is not null)
        ?? UiLanguages.English;

    /// <summary>Remembers <paramref name="language"/> for this browser, or forgets the choice when it is null.</summary>
    public static void Remember(HttpResponse response, UiLanguage? language)
    {
        if (language is null)
        {
            response.Cookies.Delete(CookieName);
            return;
        }
        response.Cookies.Append(CookieName, language.Code, new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(5),
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            HttpOnly = true,
        });
    }
}
