using System.Globalization;

namespace TinkerCNC.Localization;

/// <summary>Which form a counted text takes: <c>.One</c> or <c>.Other</c>.</summary>
public static class PluralRules
{
    public const string OneSuffix = ".One";
    public const string OtherSuffix = ".Other";

    /// <summary>Languages that use the singular for more than exactly one (French: 0 and 1).</summary>
    private static readonly IReadOnlyDictionary<string, Func<long, bool>> Singular =
        new Dictionary<string, Func<long, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["fr"] = n => n is 0 or 1,
        };

    public static bool IsOne(long count, CultureInfo language) =>
        Singular.TryGetValue(language.TwoLetterISOLanguageName, out var rule) ? rule(count) : count == 1;

    public static string KeyFor(string key, long count, CultureInfo language) =>
        key + (IsOne(count, language) ? OneSuffix : OtherSuffix);
}
