using System.Reflection;
using TinkerCNC.Localization;
using TinkerCNC.Web.Localization;

namespace TinkerCNC.Web.Services;

/// <summary>What the About page tells users about this build: copyright, license and where its source is.</summary>
public sealed class AppInfo(IConfiguration config)
{
    public const string License = "Apache-2.0";
    public const string LicenseName = "Apache License 2.0";

    /// <summary>License texts shipped next to the app (see the csproj), served under <see cref="LegalEndpoints"/>.</summary>
    public static readonly IReadOnlyList<(string File, Text Title)> LegalFiles =
    [
        ("LICENSE.txt", Texts.Of("Pages.About.Legal.License")),
        ("NOTICE.txt", Texts.Of("Pages.About.Legal.Notice")),
        ("COPYRIGHT.md", Texts.Of("Pages.About.Legal.Copyright")),
        ("THIRD-PARTY-NOTICES.md", Texts.Of("Pages.About.Legal.ThirdParty")),
    ];

    public string Copyright =>
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright TinkerDojo";

    /// <summary>
    /// Where the source code of this build can be downloaded: <c>TinkerCNC:SourceUrl</c> in the configuration, else
    /// the value the build was given (<c>-p:SourceUrl=…</c>), else null.
    /// </summary>
    public string? SourceUrl => config["TinkerCNC:SourceUrl"] is { Length: > 0 } url ? url : BuildValue("SourceUrl");

    /// <summary>A value baked into the executable when it was built (<c>AssemblyMetadata</c> in the csproj), or null when empty.</summary>
    public static string? BuildValue(string key) =>
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value
            is { Length: > 0 } value ? value : null;
}
