using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace TinkerCNC.Localization;

/// <summary>
/// The texts of one project, one JSON file per language (<c>Localization/en.json</c>, <c>de.json</c>, …, embedded in
/// the assembly as <c>&lt;Assembly&gt;.Localization.&lt;code&gt;.json</c>). A file holds categories of named texts:
/// <c>{ "Queue": { "Finished.Title": "Queue finished" } }</c>; the key <c>Queue.Finished.Title</c> names the category
/// before its first dot. A text missing in a language falls back to English, a key missing everywhere shows itself.
/// Categories starting with <c>$</c> (<c>$notes</c>: hints for translators) are not texts.
/// </summary>
public sealed class TextCatalog
{
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NoTexts =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    private readonly Func<string, Stream?> _open;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> _languages =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="open">Opens the file of a language (its two-letter code); null when there is none.</param>
    public TextCatalog(Func<string, Stream?> open) => _open = open;

    /// <summary>The catalog embedded in <paramref name="assembly"/> (see the class summary for the resource names).</summary>
    public static TextCatalog Embedded(Assembly assembly) =>
        new(code => assembly.GetManifestResourceStream($"{assembly.GetName().Name}.Localization.{code}.json"));

    public Text Of(string key, params object?[] args) => new(this, key, args);

    /// <summary>
    /// A text that depends on a count: <c>key.One</c> or <c>key.Other</c>, chosen by the language's plural rule.
    /// The count is argument <c>{0}</c>, <paramref name="args"/> follow it.
    /// </summary>
    public Text Count(string key, long count, params object?[] args) => new(this, key, [count, .. args], count);

    public string Template(string key, CultureInfo language) =>
        Find(key, language.TwoLetterISOLanguageName) ?? Find(key, English.TwoLetterISOLanguageName) ?? key;

    private string? Find(string key, string language)
    {
        var dot = key.IndexOf('.');
        if (dot <= 0) return null;
        return Texts(language).TryGetValue(key[..dot], out var category) && category.TryGetValue(key[(dot + 1)..], out var text)
            ? text
            : null;
    }

    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Texts(string language) =>
        _languages.GetOrAdd(language, Load);

    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load(string language)
    {
        using var stream = _open(language);
        if (stream is null) return NoTexts;
        var categories = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream) ?? [];
        return categories
            .Where(c => !c.Key.StartsWith('$'))
            .ToDictionary(c => c.Key, c => (IReadOnlyDictionary<string, string>)c.Value, StringComparer.Ordinal);
    }
}
