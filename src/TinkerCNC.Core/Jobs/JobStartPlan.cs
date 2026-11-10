using System.Globalization;
using TinkerCNC.GCode;
using TinkerCNC.Localization;
using TinkerCNC.Motion;
using TinkerCNC.Transfer;

namespace TinkerCNC.Jobs;

/// <summary>
/// Commands that run a program on the machine's SD card (firmware, Player). <c>play &lt;path&gt;</c> starts it
/// (only when the machine is homed and nothing else is playing); <c>buffer &lt;command&gt;</c> queues a command that runs
/// once the next program has started, before its first line, and <c>abort</c> stops the program (spindle off,
/// remaining moves dropped). Pausing is <see cref="JobSuspension"/>.
/// </summary>
public static class JobCommands
{
    public const string Abort = "abort";

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
public sealed class JobStartException(Text text) : LocalizedException(text);

/// <summary>
/// The commands that start a program with the chosen <see cref="JobStartOptions"/>, in the order they are sent:
/// <list type="number">
/// <item>the machine's job options (<see cref="JobMode"/>), each switched on or off;</item>
/// <item>the work origin, when it is set from the bed corner or the head position (<c>G10 L2 P0 X Y</c>: the active
/// work offset becomes that machine position);</item>
/// <item>queued with <c>buffer</c>, so they run after the program has started: for a queued job the lift of the
/// gate first (<see cref="JobGate"/>: it waits in the feed hold sent before the start, see <see cref="HoldFirst"/>),
/// then probing
/// the stock's corner with the XYZ probe block (<c>M495.3 H D</c>), then the automation before the program
/// (<see cref="Automation"/>), then, when the program does not change to a tool of its own, the tool that was in the
/// spindle before probing replaced it;</item>
/// <item><c>play</c>.</item>
/// </list>
/// The firmware runs the automation itself and asks for the probe first if another tool is in the spindle.
/// </summary>
/// <param name="Preparation">What runs before the program when it probes: on the job's timeline before the program.</param>
/// <param name="HoldFirst">Send the feed hold of the gate (<see cref="JobGate.Hold"/>) before <see cref="Commands"/>.</param>
public sealed record JobStartPlan(IReadOnlyList<string> Commands, JobPreparation? Preparation = null, bool HoldFirst = false)
{
    private const double MinLevelingHeight = 0.5;
    private const double MaxLevelingHeight = 50;

    /// <param name="waitForGo">Wait for the go before anything moves (<see cref="JobGate"/>; queued jobs).</param>
    public static JobStartPlan Create(JobStartOptions options, JobStartContext context, string remotePath, bool waitForGo = false)
    {
        Validate(options, context);
        var origin = WorkOrigins.For(options.WorkOrigin);
        List<string> preparation =
        [
            .. origin.BeforeProgram(options),
            .. context.Program is { } program ? [Automation(options, program)] : Array.Empty<string>(),
            .. ToolBackAfterProbing(options, context),
        ];
        IReadOnlyList<string> gate = waitForGo ? JobGate.Commands(context.Travel) : [];
        return new JobStartPlan(
        [
            .. context.Modes.Select(m => m.CommandFor(options.IsOn(m))),
            .. origin.SetNow(options, context),
            .. gate.Concat(preparation).Select(JobCommands.Buffer),
            JobCommands.Play(remotePath),
        ], JobPreparation.For(options, context, preparation), waitForGo);
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
            throw new JobStartException(CoreTexts.Of("Start.NothingToProbe"));
        if (options.Leveling && !(InRange(options.LevelingPointsX) && InRange(options.LevelingPointsY)))
            throw new JobStartException(
                CoreTexts.Of("Start.LevelingPoints", JobStartOptions.MinLevelingPoints, JobStartOptions.MaxLevelingPoints));
        if (options.Leveling && options.LevelingHeight is < MinLevelingHeight or > MaxLevelingHeight)
            throw new JobStartException(CoreTexts.Of("Start.LevelingHeight", MinLevelingHeight, MaxLevelingHeight));
        if (options.Leveling && context.Program is { } p && (p.Width <= 0 || p.Depth <= 0))
            throw new JobStartException(CoreTexts.Of("Start.LevelingArea"));
    }

    private static bool InRange(int points) => points is >= JobStartOptions.MinLevelingPoints and <= JobStartOptions.MaxLevelingPoints;

    internal static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
