using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using TinkerCNC.Localization;

namespace TinkerCNC.Web.Localization;

/// <summary>
/// The dashboard's words in the language of this browser tab (one instance per circuit, injected as <c>T</c>): the
/// language service. Keys name a category and a text, <c>T["Job.Queue.Title"]</c>, looked up in
/// <c>Localization/&lt;language&gt;.json</c> (see <see cref="TextCatalog"/>); <c>T[text]</c> puts a <see cref="Text"/>
/// from the host services into the same language. The language is fixed for the circuit; a new choice reloads the
/// page (<see cref="LanguageEndpoints"/>).
/// </summary>
public sealed class Texts
{
    private static readonly TextCatalog Catalog = TextCatalog.Embedded(typeof(Texts).Assembly);

    public LanguagePreference Preference { get; private set; } = new(null, UiLanguages.English);

    public UiLanguage Language => Preference.Effective;

    public CultureInfo Culture => Language.Culture;

    public void Use(LanguagePreference preference) => Preference = preference;

    /// <summary>The text for <paramref name="key"/> (<c>Category.Name</c>), with <paramref name="args"/> filled in.</summary>
    public string this[string key, params object?[] args] => Of(key, args).Resolve(Culture);

    // The indexer above takes the key; this one a finished text from the host.
    public string this[Text? text] => text?.Resolve(Culture) ?? "";

    /// <summary><c>Category.Name.One</c> or <c>Category.Name.Other</c> for <paramref name="count"/> (argument <c>{0}</c>).</summary>
    public string Count(string key, long count, params object?[] args) => Catalog.Count(key, count, args).Resolve(Culture);

    /// <summary>
    /// A text whose translation carries markup (<c>&lt;b&gt;</c>, <c>&lt;a href&gt;</c>); the arguments are encoded,
    /// so only the translations themselves can add markup.
    /// </summary>
    public MarkupString Html(string key, params object?[] args) =>
        new(Of(key, args.Select(Encode).ToArray()).Resolve(Culture));

    private object? Encode(object? arg) =>
        WebUtility.HtmlEncode(arg is Text text ? this[text] : Convert.ToString(arg, CultureInfo.CurrentCulture));

    /// <summary>A text of the dashboard for code that hands texts on (to a dialog, a list of options).</summary>
    public static Text Of(string key, params object?[] args) => Catalog.Of(key, args);
}
