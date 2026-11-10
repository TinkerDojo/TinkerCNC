using System.Globalization;

namespace TinkerCNC.Localization;

/// <summary>
/// Words for people, put into a language only where they are shown. Host services are shared by every open dashboard
/// and each dashboard may show another language, so services hand out texts, not finished strings. Arguments that are
/// texts themselves are put into the same language.
/// </summary>
public sealed class Text
{
    private readonly TextCatalog? _catalog;
    private readonly object?[] _args;
    private readonly long? _count;

    internal Text(TextCatalog? catalog, string key, object?[] args, long? count = null)
    {
        _catalog = catalog;
        Key = key;
        _args = args;
        _count = count;
    }

    /// <summary>The resource key; for a <see cref="Plain"/> text, the words themselves.</summary>
    public string Key { get; }

    public IReadOnlyList<object?> Arguments => _args;

    /// <summary>Words shown as they are, in whatever language they came: what the machine reported, a file name.</summary>
    public static Text Plain(string words) => new(null, words, []);

    /// <summary>The text an error carries for people, or its message when it has none.</summary>
    public static Text Of(Exception error) => error is ILocalizedException localized ? localized.Text : Plain(error.Message);

    /// <summary>The words in <paramref name="language"/> (numbers keep the current culture's format).</summary>
    public string Resolve(CultureInfo language)
    {
        if (_catalog is null) return Key;
        var key = _count is { } count ? PluralRules.KeyFor(Key, count, language) : Key;
        var template = _catalog.Template(key, language);
        if (_args.Length == 0) return template;
        var args = _args.Select(a => a is Text text ? text.Resolve(language) : a).ToArray();
        return string.Format(CultureInfo.CurrentCulture, template, args);
    }

    /// <summary>In English, for logs and exception messages.</summary>
    public override string ToString() => Resolve(TextCatalog.English);
}
