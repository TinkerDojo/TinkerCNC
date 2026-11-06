namespace TinkerCNC.Transfer;

/// <summary>Paths on the machine's SD card.</summary>
public static class RemotePath
{
    /// <summary>Where programs are kept.</summary>
    public const string GCodeDirectory = "/sd/gcodes";

    /// <summary>
    /// The firmware splits a command line at spaces and treats some characters specially, so inside a path these
    /// travel as control bytes, which it turns back when it reads the path (firmware, utils shift_parameter).
    /// Directory listings return names the same way.
    /// </summary>
    private static readonly IReadOnlyDictionary<char, char> Escapes = new Dictionary<char, char>
    {
        [' '] = '\x01',
        ['?'] = '\x02',
        ['*'] = '\x03',
        ['!'] = '\x04',
        ['~'] = '\x05',
    };

    private static readonly IReadOnlyDictionary<char, char> Unescapes = Escapes.ToDictionary(e => e.Value, e => e.Key);

    public static string Encode(string path) => Translate(path, Escapes);

    public static string Decode(string text) => Translate(text, Unescapes);

    public static string Combine(string directory, string name) => $"{directory.TrimEnd('/')}/{name.TrimStart('/')}";

    /// <summary>Where a program called <paramref name="fileName"/> (a local file name, no directories) is stored.</summary>
    public static string ForProgram(string fileName) => Combine(GCodeDirectory, Path.GetFileName(fileName));

    public static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Translate(string text, IReadOnlyDictionary<char, char> map) =>
        string.Create(text.Length, (text, map), static (span, state) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = state.map.GetValueOrDefault(state.text[i], state.text[i]);
        });
}
