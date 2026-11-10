namespace TinkerCNC.Recovery;

/// <summary>
/// Where a machine's job was when it was last seen running: enough to resume it at that line after an emergency stop,
/// an alarm or a machine switched off (<see cref="GCode.ProgramResume"/>), also after the app was closed.
/// </summary>
/// <param name="Machine">The machine's address.</param>
/// <param name="Md5">The program's content in the program cache (the copy of the program that ran).</param>
/// <param name="Line">The line it was running (1-based, the file's own numbering).</param>
/// <param name="LineCount">The program's length, for "line n of m".</param>
/// <param name="WorkZero">Work zero then, in machine coordinates as the tool's tip sees them; null when unknown.</param>
/// <param name="Leveled">Started with auto leveling, whose height map a resume cannot bring back.</param>
/// <param name="RecordedUtc">When the line was seen.</param>
public sealed record JobSnapshot(
    string Machine, string FileName, string Md5, int Line, int LineCount, double[]? WorkZero, bool Leveled, DateTime RecordedUtc)
{
    public double Fraction => LineCount > 0 ? (double)Line / LineCount : 0;
}
