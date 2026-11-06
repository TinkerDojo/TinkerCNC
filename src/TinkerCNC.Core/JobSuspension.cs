namespace TinkerCNC;

/// <summary>
/// Pausing a running program so the machine accepts other commands in between (firmware, Player
/// suspend/resume). <c>suspend</c> lets the moves already queued finish (state <c>Wait</c>), stores the position and
/// reports state <c>Pause</c>; commands such as <c>M7</c> or jogs then run normally. <c>resume</c> moves back to the
/// stored position and continues the program. Unlike a feed hold (<c>!</c>), which freezes motion but keeps later
/// commands queued behind the program, a suspended program lets them run.
/// </summary>
public static class JobSuspension
{
    public const string SuspendCommand = "suspend";
    public const string ResumeCommand = "resume";
    public const string SuspendedState = "Pause";

    public static bool IsSuspended(StatusReport? status) => HasState(status, SuspendedState);

    public static bool HasState(StatusReport? status, string state) =>
        status?.State?.StartsWith(state, StringComparison.OrdinalIgnoreCase) == true;
}
