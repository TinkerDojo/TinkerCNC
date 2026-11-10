namespace TinkerCNC.Localization;

/// <summary>The texts of the core library (<c>Localization/en.json</c>, <c>de.json</c>, …).</summary>
public static class CoreTexts
{
    public static TextCatalog Catalog { get; } = TextCatalog.Embedded(typeof(CoreTexts).Assembly);

    public static Text Of(string key, params object?[] args) => Catalog.Of(key, args);

    public static Text Count(string key, long count, params object?[] args) => Catalog.Count(key, count, args);
}
