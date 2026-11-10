using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using TinkerCNC.Host.Localization;
using TinkerCNC.Localization;

namespace TinkerCNC.Tests;

public class TextTests
{
    [Fact]
    public void PlainTextsShowAsTheyAre() =>
        Assert.Equal("Alarm: Hard limit {X}", Text.Plain("Alarm: Hard limit {X}").Resolve(CultureInfo.GetCultureInfo("de")));

    [Fact]
    public void ErrorsCarryTheirText()
    {
        var text = Text.Plain("not now");
        Assert.Same(text, Text.Of(new LocalizedException(text)));
        Assert.Equal("boom", Text.Of(new InvalidOperationException("boom")).Key);
        Assert.Equal("not now", new LocalizedException(text).Message);
    }

    [Theory]
    [InlineData("en", 0, false)]
    [InlineData("en", 1, true)]
    [InlineData("de", 1, true)]
    [InlineData("de", 2, false)]
    [InlineData("it", 0, false)]
    [InlineData("fr", 0, true)]
    [InlineData("fr", 1, true)]
    [InlineData("fr", 2, false)]
    public void PluralFormsFollowTheLanguage(string language, long count, bool one) =>
        Assert.Equal(one, PluralRules.IsOne(count, CultureInfo.GetCultureInfo(language)));

    [Fact]
    public void MissingKeysShowThemselves() =>
        Assert.Equal("No.Such.Key", HostTexts.Of("No.Such.Key").Resolve(CultureInfo.GetCultureInfo("it")));
}

/// <summary>
/// The language files (<c>Localization/&lt;code&gt;.json</c> of each project) and the code that uses them agree: every
/// language has every text with the same placeholders, every key the code names exists, and no text is left unused.
/// </summary>
public class TranslationTests
{
    private static readonly string[] Languages = ["de", "fr", "it"];

    /// <summary>The texts of one project, and the pattern by which its code names their keys.</summary>
    public sealed record Catalog(string Name, string Folder, string SourceFolder, Regex KeyUse)
    {
        public override string ToString() => Name;

        public string File(string language) => Path.Combine(Folder, language + ".json");
    }

    private static readonly string Root = RepositoryRoot();

    private static Catalog Project(string name, string project, string keyUse) =>
        new(name, Path.Combine(Root, "src", project, "Localization"), Path.Combine(Root, "src", project), new Regex(keyUse));

    // The dashboard names its keys T["Job.Queue.Title"], T.Count("…"), T.Html("…"), and Texts.Of("…") for texts handed
    // around (settings pages, option lists) before they are shown; the host and the core library through their catalogs.
    public static TheoryData<Catalog> Catalogs() => new()
    {
        Project("Core", "TinkerCNC.Core", @"CoreTexts\.(?:Of|Count)\(\s*""([^""]+)"""),
        Project("Host", "TinkerCNC.Host", @"HostTexts\.(?:Of|Count)\(\s*""([^""]+)"""),
        Project("Web", "TinkerCNC.Web", @"(?:\bT\[|\bT\.(?:Count|Html)\(|\bTexts\.Of\()\s*""([A-Za-z0-9_.]+)"""),
    };

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void EveryLanguageHasEveryText(Catalog catalog)
    {
        var english = Load(catalog.File("en"));
        foreach (var language in Languages)
        {
            var translated = Load(catalog.File(language));
            Assert.Equal(english.Keys.Order(), translated.Keys.Order());
            foreach (var (key, text) in english)
            {
                Assert.True(Placeholders(text).SetEquals(Placeholders(translated[key])),
                    $"{catalog}: {key} ({language}) has other placeholders than the English \"{text}\": \"{translated[key]}\"");
                Assert.False(string.IsNullOrWhiteSpace(translated[key]), $"{catalog}: {key} ({language}) is empty");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void EveryKeyTheCodeNamesExists(Catalog catalog)
    {
        var keys = Load(catalog.File("en")).Keys.ToHashSet(StringComparer.Ordinal);
        var missing = UsedKeys(catalog)
            .Where(k => !keys.Contains(k) && !(keys.Contains(k + PluralRules.OneSuffix) && keys.Contains(k + PluralRules.OtherSuffix)))
            .ToList();
        Assert.True(missing.Count == 0, $"{catalog}: missing texts: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void EveryTextIsUsed(Catalog catalog)
    {
        var used = UsedKeys(catalog).ToHashSet(StringComparer.Ordinal);
        var unused = Load(catalog.File("en")).Keys
            .Where(k => !used.Contains(k) && !used.Contains(WithoutPluralSuffix(k)))
            .ToList();
        Assert.True(unused.Count == 0, $"{catalog}: unused texts: " + string.Join(", ", unused));
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void TranslatorNotesBelongToTexts(Catalog catalog)
    {
        var keys = Load(catalog.File("en")).Keys.ToHashSet(StringComparer.Ordinal);
        var notes = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(catalog.File("en")))!
            .GetValueOrDefault("$notes") ?? [];
        Assert.DoesNotContain(notes.Keys, k => !keys.Contains(k));
    }

    private static string WithoutPluralSuffix(string key) =>
        key.EndsWith(PluralRules.OneSuffix, StringComparison.Ordinal) ? key[..^PluralRules.OneSuffix.Length]
        : key.EndsWith(PluralRules.OtherSuffix, StringComparison.Ordinal) ? key[..^PluralRules.OtherSuffix.Length]
        : key;

    private static IEnumerable<string> UsedKeys(Catalog catalog) =>
        SourceFiles(catalog.SourceFolder)
            .SelectMany(file => catalog.KeyUse.Matches(File.ReadAllText(file)))
            .Select(m => m.Groups[1].Value)
            .Distinct();

    private static IEnumerable<string> SourceFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)(?:[,:][^}]*)?\}").Select(m => m.Groups[1].Value).ToHashSet();

    /// <summary>Every text of a language file by its key (<c>Category.Name</c>); <c>$</c> categories are not texts.</summary>
    private static Dictionary<string, string> Load(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(file))!
            .Where(c => !c.Key.StartsWith('$'))
            .SelectMany(c => c.Value.Select(t => (Key: $"{c.Key}.{t.Key}", t.Value)))
            .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);

    private static string RepositoryRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
