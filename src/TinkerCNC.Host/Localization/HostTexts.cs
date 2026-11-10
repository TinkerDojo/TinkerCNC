using TinkerCNC.Localization;

namespace TinkerCNC.Host.Localization;

/// <summary>The texts of the host services (<c>Localization/en.json</c>, <c>de.json</c>, …).</summary>
public static class HostTexts
{
    public static TextCatalog Catalog { get; } = TextCatalog.Embedded(typeof(HostTexts).Assembly);

    public static Text Of(string key, params object?[] args) => Catalog.Of(key, args);

    public static Text Count(string key, long count, params object?[] args) => Catalog.Count(key, count, args);
}
