using TinkerCNC.Localization;
using static TinkerCNC.Jobs.JobStartPlan;

namespace TinkerCNC.Jobs;

/// <summary>How a <see cref="WorkOriginChoice"/> sets the work origin when a program is started.</summary>
public interface IWorkOrigin
{
    /// <summary>How the origin is set, for people.</summary>
    Text Describe(JobStartOptions options);

    /// <summary>Commands sent right away, before the program is started.</summary>
    IEnumerable<string> SetNow(JobStartOptions options, JobStartContext context) => [];

    /// <summary>Commands the machine runs once the program has started, before anything else.</summary>
    IEnumerable<string> BeforeProgram(JobStartOptions options) => [];

    /// <summary>
    /// Whether <see cref="BeforeProgram"/> starts from where the head was put by hand, so nothing may move it away
    /// first (<see cref="JobGate"/> then waits with a move that comes back to the same place).
    /// </summary>
    bool StartsFromHead => false;

    /// <summary><c>G10 L2 P0</c>: the active work offset becomes the given machine position (X/Y; Z stays).</summary>
    protected static string SetOrigin(double machineX, double machineY) => $"G10 L2 P0 X{N(machineX)} Y{N(machineY)}";
}

public sealed record KeepWorkOrigin : IWorkOrigin
{
    public Text Describe(JobStartOptions options) => CoreTexts.Of("Origin.Unchanged");
}

/// <summary>The bed's reference corner (<c>coordinate.anchor1_x/_y</c>) plus the offset.</summary>
public sealed record BedCornerWorkOrigin : IWorkOrigin
{
    public Text Describe(JobStartOptions options) => CoreTexts.Of("Origin.BedCorner", N(options.OriginOffsetX), N(options.OriginOffsetY));

    public IEnumerable<string> SetNow(JobStartOptions options, JobStartContext context) =>
        context.Travel is { BedCornerX: { } x, BedCornerY: { } y }
            ? [IWorkOrigin.SetOrigin(x + options.OriginOffsetX, y + options.OriginOffsetY)]
            : throw new JobStartException(CoreTexts.Of("Start.NoBedCorner"));
}

/// <summary>The head's current position plus the offset.</summary>
public sealed record HeadPositionWorkOrigin : IWorkOrigin
{
    public Text Describe(JobStartOptions options) => CoreTexts.Of("Origin.HeadPosition", N(options.OriginOffsetX), N(options.OriginOffsetY));

    public IEnumerable<string> SetNow(JobStartOptions options, JobStartContext context) =>
        context.MachinePosition is { Length: >= 2 } head
            ? [IWorkOrigin.SetOrigin(head[0] + options.OriginOffsetX, head[1] + options.OriginOffsetY)]
            : throw new JobStartException(CoreTexts.Of("Start.NoHeadPosition"));
}

/// <summary>
/// <c>M495.3 H D</c>: with the probe block on the stock's front left corner and the tool above the block's corner,
/// the machine touches the block from above, from the right and from the front, and makes the stock's corner and top
/// the work origin; H is the block's height and D the tool's diameter (firmware, ATCHandler M495.3; defaults
/// 9 mm and 3.175 mm). It starts from where the head is.
/// </summary>
public sealed record XyzProbeWorkOrigin : IWorkOrigin
{
    public Text Describe(JobStartOptions options) =>
        CoreTexts.Of("Origin.XyzProbe", N(options.XyzProbeHeight), N(options.XyzProbeToolDiameter));

    public IEnumerable<string> BeforeProgram(JobStartOptions options) =>
        [$"M495.3 H{N(options.XyzProbeHeight)} D{N(options.XyzProbeToolDiameter)}"];

    public bool StartsFromHead => true;
}

public static class WorkOrigins
{
    private static readonly IReadOnlyDictionary<WorkOriginChoice, IWorkOrigin> All = new Dictionary<WorkOriginChoice, IWorkOrigin>
    {
        [WorkOriginChoice.Keep] = new KeepWorkOrigin(),
        [WorkOriginChoice.BedCorner] = new BedCornerWorkOrigin(),
        [WorkOriginChoice.HeadPosition] = new HeadPositionWorkOrigin(),
        [WorkOriginChoice.XyzProbe] = new XyzProbeWorkOrigin(),
    };

    public static IWorkOrigin For(WorkOriginChoice choice) => All[choice];
}
