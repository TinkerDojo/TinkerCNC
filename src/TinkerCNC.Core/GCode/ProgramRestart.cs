using System.Globalization;
using System.Text;
using TinkerCNC.Localization;

namespace TinkerCNC.GCode;

/// <summary>
/// The modal state of a program at some line: what earlier lines have set and later lines rely on without repeating
/// it. A program that starts at that line needs it restated first.
/// </summary>
/// <param name="UnitScale">1 for millimetres (<c>G21</c>), 25.4 for inches (<c>G20</c>).</param>
/// <param name="Absolute"><c>G90</c> (true) or <c>G91</c>.</param>
/// <param name="Plane"><c>G17</c>, <c>G18</c> or <c>G19</c>.</param>
/// <param name="WorkOffset"><c>G54</c> to <c>G59</c>.</param>
/// <param name="FeedMmPerMin">The feed rate last set (0 when none yet).</param>
/// <param name="MotionCode">The motion mode in force: 0 to 3 for <c>G0</c> to <c>G3</c>.</param>
/// <param name="Spindle">3 or 4 while the spindle turns (<c>M3</c>/<c>M4</c>), null when it is off.</param>
/// <param name="SpindleSpeed">The <c>S</c> word last set.</param>
/// <param name="Mist"><c>M7</c> on (the machine's air or mist output).</param>
/// <param name="Flood"><c>M8</c> on.</param>
public sealed record ModalState(
    double UnitScale, bool Absolute, int Plane, int WorkOffset, double FeedMmPerMin, int MotionCode, int? Spindle, double SpindleSpeed,
    bool Mist, bool Flood)
{
    /// <summary>What the firmware assumes when a program begins.</summary>
    public static readonly ModalState Default = new(1, true, 17, 54, 0, 0, null, 0, false, false);

    /// <summary>The lines that put a program into this state, in the order they are needed.</summary>
    public IEnumerable<string> ToLines()
    {
        yield return UnitScale == 1 ? "G21" : "G20";
        yield return Absolute ? "G90" : "G91";
        yield return $"G{Plane}";
        yield return $"G{WorkOffset}";
        if (Mist) yield return "M7";
        if (Flood) yield return "M8";
        if (Spindle is { } spindle) yield return $"M{spindle} S{N(SpindleSpeed)}";
        foreach (var line in FeedLines()) yield return line;
    }

    /// <summary>
    /// The feed and the motion mode in force, in the program's own unit. The feed always goes on a <c>G1</c>: on the
    /// firmware an <c>F</c> on a <c>G0</c> line becomes the speed of every later rapid move, so <c>G0 F300</c> would
    /// slow all rapids to 300 mm/min. A program left in <c>G0</c> gets a bare <c>G0</c> after it, which changes nothing
    /// but the motion mode.
    /// </summary>
    public IEnumerable<string> FeedLines()
    {
        if (FeedMmPerMin <= 0) yield break;
        yield return $"G1 F{N(FeedMmPerMin / UnitScale)}";
        if (MotionCode == 0) yield return "G0";
    }

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>A copy of a program that begins later than its first line, ready to upload.</summary>
/// <param name="Description">Where it comes from, for people.</param>
/// <param name="Preamble">The lines put before the program's own (to show what the copy does first).</param>
public sealed record RestartProgram(string FileName, byte[] Content, Text Description, IReadOnlyList<string>? Preamble = null);

/// <summary>
/// Restarts a program at a tool operation after it ended early: the firmware plays a file from its first line only
/// (and jumps inside a paused program without restating anything), so a trimmed copy is made: a comment, the modal
/// state the program was in at that point (<see cref="ModalState.ToLines"/>), the operation's tool number when its
/// tool change does not name it, then the lines from the operation's tool change to the end. The machine asks for the
/// tool as usual, or carries on if it is in the spindle already.
/// </summary>
public static class ProgramRestart
{
    /// <summary>The copy of <paramref name="fileName"/> that starts at <paramref name="segment"/>.</summary>
    public static RestartProgram From(GCodeAnalysis analysis, string fileName, ToolSegment segment)
    {
        var lines = new List<string>
        {
            $"; TinkerCNC: {fileName} from {segment.Label} (line {segment.StartLine} of {analysis.LineCount})",
        };
        lines.AddRange(segment.ModalAtStart.ToLines());
        // The tool change names its tool on an earlier line ("T4", then "M6"): without it the machine would change to
        // whatever tool it last heard of.
        if (segment.ToolNumber is { } tool && !GCodeLine.Parse(analysis.Lines[segment.StartLine - 1]).Has('T')) lines.Add($"T{tool}");
        lines.AddRange(analysis.Lines.Skip(segment.StartLine - 1));
        var text = string.Join('\n', lines) + "\n";
        return new RestartProgram(NameFor(fileName, segment), Encoding.UTF8.GetBytes(text),
            CoreTexts.Of("Restart.Description", fileName, segment.DisplayLabel));
    }

    /// <summary><c>part_from_T6.nc</c>; an operation without a tool number is named by its position.</summary>
    public static string NameFor(string fileName, ToolSegment segment)
    {
        var dot = fileName.LastIndexOf('.');
        var (stem, extension) = dot <= 0 ? (fileName, "") : (fileName[..dot], fileName[dot..]);
        var at = segment.ToolNumber is { } tool ? $"T{tool}" : $"op{segment.Index + 1}";
        return $"{stem}_from_{at}{extension}";
    }
}
