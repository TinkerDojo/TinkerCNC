using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TinkerCNC.GCode;
using TinkerCNC.Transfer;

namespace TinkerCNC.Host;

/// <summary>
/// Programs downloaded from machines, keyed by the MD5 of their content (the same hash the machine announces
/// before a download). Files are kept as <c>&lt;md5&gt;_&lt;name&gt;</c> in <see cref="AppPaths.GCodeCache"/>, so a
/// running job is transferred only once, across reconnects and restarts.
/// </summary>
public sealed class GCodeCache : IFileCache
{
    private const int Md5HexLength = 32;

    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);        // md5 → path
    private readonly ConcurrentDictionary<string, GCodeAnalysis> _analyses = new(StringComparer.OrdinalIgnoreCase); // md5 → analysis

    public GCodeCache(AppPaths paths)
    {
        Directory = paths.GCodeCache;
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory))
        {
            var name = Path.GetFileName(path);
            if (name.Length > Md5HexLength && name[Md5HexLength] == '_' && IsMd5(name[..Md5HexLength]))
                _files[name[..Md5HexLength]] = path;
        }
    }

    public string Directory { get; }

    public static bool IsMd5(string? value) => value is { Length: Md5HexLength } && value.All(Uri.IsHexDigit);

    public static string Md5Of(byte[] content) => Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();

    public byte[]? TryGet(string md5) => _files.TryGetValue(md5, out var path) && File.Exists(path) ? File.ReadAllBytes(path) : null;

    /// <summary>The analysis of a cached file, or null when the file is not cached.</summary>
    public GCodeAnalysis? Find(string? md5) =>
        IsMd5(md5) && TryGet(md5!) is { } content ? _analyses.GetOrAdd(md5!, _ => Analyze(content)) : null;

    /// <summary>Stores a file (if new) and returns its analysis.</summary>
    public GCodeAnalysis Add(string fileName, byte[] content)
    {
        var md5 = Md5Of(content);
        if (!_files.ContainsKey(md5))
        {
            var path = Path.Combine(Directory, $"{md5}_{Path.GetFileName(fileName)}");
            File.WriteAllBytes(path, content);
            _files[md5] = path;
        }
        return _analyses.GetOrAdd(md5, _ => Analyze(content));
    }

    /// <summary>Analyses a file that did not come from the machine (e.g. uploaded in the browser).</summary>
    public static GCodeAnalysis Analyze(string content) => GCodeEstimator.Analyze(content);

    private static GCodeAnalysis Analyze(byte[] content) => Analyze(Encoding.UTF8.GetString(content));
}
