using TinkerCNC.Motion;
using TinkerCNC.Protocol;

namespace TinkerCNC.Jobs;

/// <summary>
/// How a queued job waits for the go before anything moves, so the piece can be swapped and the start confirmed with
/// the machine's button (firmware, MainButton: any press in state <c>Hold</c> releases the feed hold; in state
/// <c>Pause</c>, after <c>M600</c>/<c>suspend</c>, the button does nothing).
/// <list type="number">
/// <item>Before the start a feed hold (<see cref="Hold"/>, <c>!</c>) is sent while the machine is idle; it reports
/// <see cref="HeldState"/>. Nothing moves yet, so nothing is frozen halfway.</item>
/// <item>The first commands queued for the start (<see cref="Commands"/>) lift the head to the clearance height. The
/// firmware waits before planning each new move while the feed hold is on (Robot), so the first lift waits there,
/// before the probing, the tool and the program's first line: no spindle, no move.</item>
/// <item>A press of the machine's button, or <see cref="Release"/> (<c>~</c>), lets it go on.</item>
/// </list>
/// The firmware skips a move that goes nowhere without waiting, so the head is sent to two different heights: whichever
/// it is at, one of them is a real move. Text commands sent while a move waits are only read once it goes on, so a
/// job stopped at the gate is aborted and then released (the head may still lift).
/// </summary>
public static class JobGate
{
    public const string HeldState = "Hold";

    /// <summary>How far below the clearance height the first lift goes, so the second one always moves.</summary>
    private const double StepBelowClearance = 1;

    public static RealtimeCommand Hold => RealtimeCommand.FeedHold;

    public static RealtimeCommand Release => RealtimeCommand.CycleStart;

    public static bool IsHeld(StatusReport? status) => JobSuspension.HasState(status, HeldState);

    /// <summary>Queued first with <c>buffer</c>: millimetres, then the two lifts in machine coordinates.</summary>
    public static IReadOnlyList<string> Commands(MachineTravel travel) =>
    [
        "G21",
        $"G53 G0 Z{JobStartPlan.N(travel.ClearanceZ - StepBelowClearance)}",
        $"G53 G0 Z{JobStartPlan.N(travel.ClearanceZ)}",
    ];
}
