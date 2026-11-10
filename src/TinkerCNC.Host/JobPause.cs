namespace TinkerCNC.Host;

/// <summary>
/// Pauses a running program (<see cref="JobSuspension"/>) so the host can use the machine in between, making sure the
/// head is still where the program stopped. A machine configured with a pause routine (<c>after_suspend_gcode</c>) may
/// move it, so such a machine is not paused at all; as a second safeguard the head must not have moved after pausing.
/// </summary>
public sealed class JobPause(MachineSession session)
{
    /// <summary>Longest wait for a program to reach the pause (it first finishes the moves already queued).</summary>
    public static readonly TimeSpan SuspendTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Longest wait for the head to come to rest after pausing.</summary>
    private static readonly TimeSpan RestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Positions closer than this (mm, per axis) count as the same place.</summary>
    public const double SamePlaceMm = 0.01;

    /// <summary>Pauses the program: it first finishes the moves already queued.</summary>
    /// <exception cref="InvalidOperationException">The program could not be paused (or ended meanwhile).</exception>
    public async Task PauseAsync(CancellationToken ct)
    {
        await EnsureNoPauseRoutineAsync(ct);
        await session.SendAsync(JobSuspension.SuspendCommand);
        var status = await session.WaitForStatusAsync(
            s => JobSuspension.IsSuspended(s) || !s.IsPlaying || JobSuspension.HasState(s, "Alarm"), SuspendTimeout, ct);
        if (!JobSuspension.IsSuspended(status))
            throw new LocalizedException(HostTexts.Of("Pause.NotPaused", MachineSession.StateOf(status)));
    }

    /// <summary>
    /// After <see cref="PauseAsync"/>: waits until the head is at rest and checks it is still where the program
    /// stopped; the machine may run its own pause routine.
    /// </summary>
    /// <exception cref="InvalidOperationException">The head moved; the program is still paused.</exception>
    public async Task EnsureHeadStayedAsync(CancellationToken ct)
    {
        var paused = session.Status;
        var previous = paused;
        var deadline = DateTime.UtcNow + RestTimeout;
        while (true)
        {
            var next = await session.WaitForStatusAsync(s => !ReferenceEquals(s, previous), RestTimeout, ct);
            if (SamePlace(previous?.MachinePosition, next.MachinePosition)) break;
            if (DateTime.UtcNow > deadline) throw new LocalizedTimeoutException(HostTexts.Of("Pause.NoRest"));
            previous = next;
        }
        if (!SamePlace(paused?.MachinePosition, session.Status?.MachinePosition))
            throw new LocalizedException(HostTexts.Of("Pause.HeadMoved", Describe(paused?.WorkPosition), Describe(session.Status?.WorkPosition)));
    }

    public static bool SamePlace(double[]? a, double[]? b) =>
        a is { Length: >= 3 } && b is { Length: >= 3 } && Enumerable.Range(0, 3).All(i => Math.Abs(a[i] - b[i]) < SamePlaceMm);

    /// <summary>"X1 Y2 Z3", or "the current position" when it is not known.</summary>
    public static Text Describe(double[]? position) => position is { Length: >= 3 }
        ? Text.Plain(FormattableString.Invariant($"X{position[0]:0.###} Y{position[1]:0.###} Z{position[2]:0.###}"))
        : HostTexts.Of("Pause.CurrentPosition");

    /// <summary>
    /// Refuses when the machine runs its own G-code after a pause: it might move the head away from where the program
    /// stopped. A machine that does not answer the query is paused anyway; the position check then still applies.
    /// </summary>
    private async Task EnsureNoPauseRoutineAsync(CancellationToken ct)
    {
        string? routine;
        try
        {
            routine = await session.GetConfigAsync(MachineConfig.AfterSuspendGCode, ct);
        }
        catch (TimeoutException)
        {
            session.Note($"The machine did not report its {MachineConfig.AfterSuspendGCode} setting; checking the head position instead.");
            return;
        }
        if (routine is not null)
            throw new LocalizedException(HostTexts.Of("Pause.Routine", routine.Replace('_', ' '), MachineConfig.AfterSuspendGCode));
    }
}
