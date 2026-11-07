using System.Globalization;
using TinkerCNC.GCode;
using TinkerCNC.Motion;
using TinkerCNC.Transfer;

namespace TinkerCNC.Jobs;

/// <summary>
/// Commands that run a program on the machine's SD card (firmware, Player). <c>play &lt;path&gt;</c> starts it
/// (only when the machine is homed and nothing else is playing); <c>buffer &lt;command&gt;</c> queues a command that runs
/// once the next program has started, before its first line, and <c>abort</c> stops the program (spindle off,
/// remaining moves dropped). Pausing is <see cref="JobSuspension"/>; <see cref="Pause"/> does the same from inside
/// the program.
/// </summary>
public static class JobCommands
{
    public const string Abort = "abort";

    /// <summary>
    /// <c>M600</c>: the program pauses where it is (firmware, Player: the same as <c>suspend</c>: the queued moves
    /// finish, the position is saved, the spindle stops and the pause routine runs, if one is configured). Queued
    /// first with <c>buffer</c>, the machine waits right after <c>play</c>, before any probing and before the first
    /// line, until <c>resume</c> arrives or the machine's own button is held (its resume while paused).
    /// </summary>
    public const string Pause = "M600";

    public static string Play(string remotePath) => $"play {RemotePath.Encode(remotePath)}";

    public static string Buffer(string command) => $"buffer {command}";
}

/// <summary>What the start of a program depends on besides the chosen options.</summary>
/// <param name="Program">The extent of the program's cutting moves in its own (work) coordinates; null when it has none.</param>
/// <param name="MachinePosition">Current head position (machine coordinates, X/Y/Z), if reported.</param>
/// <param name="ActiveTool">Tool in the spindle (status field T), if reported.</param>
/// <param name="ProgramSelectsTool">Whether the program changes to a tool of its own (M6 T…).</param>
public sealed record JobStartContext(
    MachineTravel Travel, ToolpathBounds? Program, double[]? MachinePosition, int? ActiveTool, IReadOnlyList<JobMode> Modes,
    bool ProgramSelectsTool = false);

/// <summary>The options and the program can't be started together.</summary>
public sealed class JobStartException(string message) : InvalidOperationException(message);

/// <summary>
/// The commands that start a program with the chosen <see cref="JobStartOptions"/>, in the order they are sent:
/// <list type="number">
/// <item>the machine's job options (<see cref="JobMode"/>), each switched on or off;</item>
/// <item>the work origin, when it is set from the bed corner or the head position (<c>G10 L2 P0 X Y</c>: the active
/// work offset becomes that machine position);</item>
/// <item>queued with <c>buffer</c>, so they run after the program has started: for a queued job a
/// pause first (<see cref="JobCommands.Pause"/>: the machine waits for the go before anything moves), then probing
/// the stock's corner with the XYZ probe block (<c>M495.3 H D</c>), then the automation before the program
/// (<see cref="Automation"/>), then, when the program does not change to a tool of its own, the tool that was in the
/// spindle before probing replaced it;</item>
/// <item><c>play</c>.</item>
/// </list>
/// The firmware runs the automation itself and asks for the probe first if another tool is in the spindle.
/// </summary>
/// <param name="Preparation">What runs before the program when it probes: on the job's timeline before the program.</param>
public sealed record JobStartPlan(IReadOnlyList<string> Commands, JobPreparation? Preparation = null)
{
    private const double MinLevelingHeight = 0.5;
    private const double MaxLevelingHeight = 50;

    /// <param name="waitForGo">Pause right after the start, until the user resumes (queued jobs).</param>
    public static JobStartPlan Create(JobStartOptions options, JobStartContext context, string remotePath, bool waitForGo = false)
    {
        Validate(options, context);
        var origin = WorkOrigins.For(options.WorkOrigin);
        List<string> queued =
        [
            .. waitForGo ? [JobCommands.Pause] : Array.Empty<string>(),
            .. origin.BeforeProgram(options),
            .. context.Program is { } program ? [Automation(options, program)] : Array.Empty<string>(),
            .. ToolBackAfterProbing(options, context),
        ];
        return new JobStartPlan(
        [
            .. context.Modes.Select(m => m.CommandFor(options.IsOn(m))),
            .. origin.SetNow(options, context),
            .. queued.Select(JobCommands.Buffer),
            JobCommands.Play(remotePath),
        ], JobPreparation.For(options, context, queued));
    }

    /// <summary>
    /// <c>M495 X Y</c> with the program's corner (work coordinates), followed by what the firmware does before the
    /// program, in this order (firmware, ATCHandler M495):
    /// <list type="bullet">
    /// <item><c>C D</c>: trace the outline up to that far corner at the clearance height (scan margin);</item>
    /// <item><c>O F</c>: probe the stock's top at that offset from the program's corner and make it Z zero;</item>
    /// <item><c>A B I J H</c>: probe a grid of I × J points over A × B mm, moving at H mm above the surface, and
    /// follow the measured surface (auto leveling);</item>
    /// <item>without any of these, <c>P1</c>: go to the program's corner at the clearance height first.</item>
    /// </list>
    /// </summary>
    public static string Automation(JobStartOptions options, ToolpathBounds program)
    {
        var words = new List<string> { "M495", $"X{N(program.MinX)}", $"Y{N(program.MinY)}" };
        if (options.ScanMargin) words.AddRange([$"C{N(program.MaxX)}", $"D{N(program.MaxY)}"]);
        if (options.ZProbe)
        {
            var (x, y) = options.ZProbeFromCorner(program);
            words.AddRange([$"O{N(x)}", $"F{N(y)}"]);
        }
        if (options.Leveling)
            words.AddRange([
                $"A{N(program.Width)}", $"B{N(program.Depth)}", $"I{options.LevelingPointsX}", $"J{options.LevelingPointsY}",
                $"H{N(options.LevelingHeight)}",
            ]);
        if (!options.UsesProbe) words.Add("P1");
        return string.Join(' ', words);
    }

    /// <summary>
    /// After probing the probe is in the spindle. A program that changes to its own tool replaces it anyway; for any
    /// other the tool that was there before is changed back in.
    /// </summary>
    private static IEnumerable<string> ToolBackAfterProbing(JobStartOptions options, JobStartContext context) =>
        options.UsesProbe && !context.ProgramSelectsTool && context.ActiveTool is { } tool && ManualToolChange.IsCuttingTool(tool)
            ? [$"M6 T{tool}"]
            : [];

    private static void Validate(JobStartOptions options, JobStartContext context)
    {
        if (options.UsesProbe && context.Program is null)
            throw new JobStartException("The program has no cutting moves, so there is no area to probe.");
        if (options.Leveling && !(InRange(options.LevelingPointsX) && InRange(options.LevelingPointsY)))
            throw new JobStartException(
                $"Auto leveling needs {JobStartOptions.MinLevelingPoints} to {JobStartOptions.MaxLevelingPoints} points in X and Y.");
        if (options.Leveling && options.LevelingHeight is < MinLevelingHeight or > MaxLevelingHeight)
            throw new JobStartException($"The leveling height must be between {MinLevelingHeight} and {MaxLevelingHeight} mm.");
        if (options.Leveling && context.Program is { } p && (p.Width <= 0 || p.Depth <= 0))
            throw new JobStartException("Auto leveling needs a program that spans an area in X and Y.");
    }

    private static bool InRange(int points) => points is >= JobStartOptions.MinLevelingPoints and <= JobStartOptions.MaxLevelingPoints;

    internal static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
