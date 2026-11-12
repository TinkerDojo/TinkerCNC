using System.Globalization;
using System.Text;
using TinkerCNC.Localization;

namespace TinkerCNC.GCode;

/// <summary>What a resume needs to know about the machine.</summary>
/// <param name="ClearanceZ">Safe height in machine coordinates (<c>coordinate.clearance_z</c>), lifted to first.</param>
/// <param name="WorkZero">
/// Work zero as it was when the job stopped (machine coordinates as the tool's tip sees them,
/// <see cref="StatusReport.WorkZero"/>); restored with <c>G10 L2 P0</c>, since a machine switched off may have lost it.
/// Null: the work origin is kept as it is.
/// </param>
/// <param name="SpinUpSeconds">Wait after switching the spindle on, before the tool goes down.</param>
public sealed record ResumeContext(double ClearanceZ, double[]? WorkZero, double SpinUpSeconds = 4);

/// <summary>
/// Resumes a program at any line after it stopped (an emergency stop, an alarm, a machine switched off), as a copy
/// that begins there. The firmware plays a file from its first line only, so the copy first brings the machine back
/// into the program's state at that line, then carries on with it:
/// <list type="number">
/// <item>the work origin as it was (<see cref="ResumeContext.WorkZero"/>) and the plane and work offset;</item>
/// <item>the tool of that line (<c>M6 T</c>): the machine asks for it or keeps it, and measures its length again,
/// which a switched-off machine has forgotten;</item>
/// <item>up to the clearance height (<c>G53 G0 Z</c>), across to where the tool was before that line, spindle and
/// air as they were, a pause for the spindle to reach speed, then down to that point at feed: the tool was there, so
/// the way down is cut free (it stays up when the program had lifted in machine coordinates, <c>G53</c>);</item>
/// <item>units, absolute or incremental moves, the motion mode and feed of that line, then the line and the rest.</item>
/// </list>
/// The approach is written in millimetres and absolute coordinates whatever the program uses.
/// </summary>
public static class ProgramResume
{
    /// <summary>Feed for the last bit down when the program has set none yet.</summary>
    public const double DefaultPlungeMmPerMin = 300;

    public static RestartProgram From(GCodeAnalysis analysis, string fileName, int line, ResumeContext context)
    {
        var at = Math.Clamp(line, 1, analysis.LineCount);
        var point = analysis.StateBefore(at);
        var modal = point.Modal;
        var preamble = new List<string> { $"; TinkerCNC: {fileName} resumed at line {at} of {analysis.LineCount}" };
        preamble.AddRange(Approach(point, context, at));
        preamble.Add(modal.UnitScale == 1 ? "G21" : "G20");
        preamble.Add(modal.Absolute ? "G90" : "G91");
        preamble.AddRange(modal.FeedLines());
        var text = string.Join('\n', preamble.Concat(analysis.Lines.Skip(at - 1))) + "\n";
        return new RestartProgram(NameFor(fileName, at), Encoding.UTF8.GetBytes(text),
            CoreTexts.Of("Resume.Description", fileName, at, analysis.LineCount), preamble);
    }

    /// <summary><c>part_resume_L1234.nc</c>.</summary>
    public static string NameFor(string fileName, int line)
    {
        var dot = fileName.LastIndexOf('.');
        var (stem, extension) = dot <= 0 ? (fileName, "") : (fileName[..dot], fileName[dot..]);
        return $"{stem}_resume_L{line}{extension}";
    }

    /// <summary>
    /// From wherever the machine is to the tool's position before the line, in mm and absolute coordinates. When the
    /// program last moved Z in machine coordinates (a <c>G53</c> retract), the tool stays at the clearance height
    /// instead of going down to a height that is not known; an unknown X or Y can't be approached at all.
    /// </summary>
    /// <exception cref="LocalizedException">The tool's X or Y before the line is not known.</exception>
    private static IEnumerable<string> Approach(ProgramPoint point, ResumeContext context, int line)
    {
        var (modal, at, unknown) = (point.Modal, point.Position, point.UnknownAxes);
        if (unknown.Contains('X') || unknown.Contains('Y')) throw new LocalizedException(CoreTexts.Of("Resume.PositionUnknown", line));
        yield return "G21";
        yield return "G90";
        yield return $"G{modal.Plane}";
        yield return $"G{modal.WorkOffset}";
        if (context.WorkZero is [var x, var y, var z, ..]) yield return $"G10 L2 P0 X{N(x)} Y{N(y)} Z{N(z)}";
        if (point.Tool is { } tool) yield return $"M6 T{tool}";
        yield return $"G53 G0 Z{N(context.ClearanceZ)}";
        yield return at.A == 0 || unknown.Contains('A') ? $"G0 X{N(at.X)} Y{N(at.Y)}" : $"G0 X{N(at.X)} Y{N(at.Y)} A{N(at.A)}";
        if (modal.Spindle is { } spindle)
        {
            yield return $"M{spindle} S{N(modal.SpindleSpeed)}";
            yield return $"G4 P{N(context.SpinUpSeconds * 1000)}"; // Smoothieware: P in milliseconds
        }
        if (modal.Mist) yield return "M7";
        if (modal.Flood) yield return "M8";
        if (!unknown.Contains('Z'))
            yield return $"G1 Z{N(at.Z)} F{N(modal.FeedMmPerMin > 0 ? modal.FeedMmPerMin : DefaultPlungeMmPerMin)}";
    }

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
