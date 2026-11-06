using System.Globalization;
using System.Text.RegularExpressions;

namespace TinkerCNC.GCode;

/// <summary>One G-code line split into address words (comments removed).</summary>
public sealed partial class GCodeLine
{
    private readonly Dictionary<char, double> _values = new();
    private readonly List<(char Letter, double Value)> _words = new();

    private GCodeLine(string text)
    {
        Text = text;
        var code = Comment().Replace(text, " ");
        foreach (Match m in Word().Matches(code))
        {
            var letter = char.ToUpperInvariant(m.Groups[1].Value[0]);
            var value = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            _words.Add((letter, value));
            _values[letter] = value;
        }
    }

    public string Text { get; }

    public IReadOnlyList<(char Letter, double Value)> Words => _words;

    public bool IsEmpty => _words.Count == 0;

    public static GCodeLine Parse(string text) => new(text);

    public double? this[char letter] => _values.TryGetValue(letter, out var v) ? v : null;

    public bool Has(char letter) => _values.ContainsKey(letter);

    /// <summary>All values of a letter that can repeat on one line (G and M words).</summary>
    public IEnumerable<int> Codes(char letter) =>
        _words.Where(w => w.Letter == letter).Select(w => (int)Math.Round(w.Value));

    [GeneratedRegex(@"\(.*?\)|;.*$")]
    private static partial Regex Comment();

    [GeneratedRegex(@"([A-Za-z])\s*([+-]?(?:\d+\.?\d*|\.\d+))")]
    private static partial Regex Word();
}
