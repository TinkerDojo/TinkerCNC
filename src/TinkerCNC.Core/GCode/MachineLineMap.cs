using System.Text;

namespace TinkerCNC.GCode;

/// <summary>
/// Translates between the line numbers the machine reports (status <c>P</c> field) and the lines of the file.
/// The firmware numbers only the lines it executes: it reads the file with <c>fgets</c> into a 130-byte buffer and
/// skips empty lines and lines longer than 128 bytes (firmware Player::on_main_loop, <c>message.line =
/// played_lines + 1</c>), so after such a line the two numberings drift apart.
/// </summary>
public sealed class MachineLineMap
{
    /// <summary>Longest line the firmware executes, in bytes (including a <c>\r</c>, excluding the <c>\n</c>).</summary>
    public const int MaxLineBytes = 128;

    private readonly int[] _fileLines; // machine line k (1-based) is file line _fileLines[k - 1]

    /// <param name="rawLines">The file's lines without <c>\n</c>, a trailing <c>\r</c> included.</param>
    public MachineLineMap(IEnumerable<string> rawLines)
    {
        _fileLines = rawLines
            .Select((text, index) => (Bytes: Encoding.UTF8.GetByteCount(text), FileLine: index + 1))
            .Where(l => l.Bytes is > 0 and <= MaxLineBytes)
            .Select(l => l.FileLine)
            .ToArray();
    }

    /// <summary>File line of a reported line; 0 stays 0 (nothing executed yet).</summary>
    public int ToFileLine(int machineLine) =>
        machineLine <= 0 || _fileLines.Length == 0 ? machineLine : _fileLines[Math.Min(machineLine, _fileLines.Length) - 1];

    /// <summary>The number the machine reports while it executes <paramref name="fileLine"/> (for the simulator).</summary>
    public int ToMachineLine(int fileLine)
    {
        var index = Array.BinarySearch(_fileLines, fileLine);
        return index >= 0 ? index + 1 : ~index; // a skipped line reports the line before it
    }
}
