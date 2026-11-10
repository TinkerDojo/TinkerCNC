using System.Globalization;

namespace TinkerCNC.Web.Localization;

/// <summary>A language the dashboard is translated into, named in itself ("Deutsch").</summary>
public sealed record UiLanguage(string Code, string NativeName)
{
    public CultureInfo Culture { get; } = CultureInfo.GetCultureInfo(Code);
}

/// <summary>The dashboard's languages. A new one needs an entry here and a translated <c>&lt;code&gt;.json</c> next to every <c>en.json</c>.</summary>
public static class UiLanguages
{
    public static readonly UiLanguage English = new("en", "English");

    public static IReadOnlyList<UiLanguage> All { get; } =
    [
        English,
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("it", "Italiano"),
    ];

    private static readonly IReadOnlyDictionary<string, UiLanguage> ByCode =
        All.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>The language for a tag like <c>de</c> or <c>de-AT</c> (by its first part); null when not translated.</summary>
    public static UiLanguage? Find(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var code = tag.Split('-', '_')[0].Trim();
        return ByCode.GetValueOrDefault(code);
    }
}
