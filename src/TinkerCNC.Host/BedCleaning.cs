using TinkerCNC.Motion;

namespace TinkerCNC.Host;

/// <summary>What a bed cleaning is doing right now.</summary>
public enum BedCleaningPhase { None, PausingJob, Sweeping, Returning, ResumingJob }

/// <summary>
/// Cleans the whole bed with compressed air: the head sweeps the travel at the clearance height with the air on
/// (<see cref="BedSweepPlanner"/>), one to <see cref="MaxPasses"/> times, and then goes back to where it was. During a
/// running program the program is paused first (<see cref="JobPause"/>) and resumed once the head is back exactly
/// where it stopped; if it can't be brought back, the program stays paused. Stop ends the sweep early, still returning
/// and resuming. The sequence runs here rather than in the browser, so it completes even when the page is closed.
/// </summary>
public sealed class BedCleaning(MachineSession session)
{
    public const int MaxPasses = 3;

    /// <summary>Added to a move's expected duration before it counts as stuck.</summary>
    private static readonly TimeSpan MoveMargin = TimeSpan.FromSeconds(20);

    /// <summary>Allows for a lowered feed override.</summary>
    private const double MoveSlack = 3;

    /// <summary>Longest wait for a cleaning to come back when disconnecting.</summary>
    private static readonly TimeSpan DisconnectWait = TimeSpan.FromSeconds(30);

    // Machine states a cleaning may start in, and whether the running program has to be paused for it.
    private static readonly IReadOnlyDictionary<string, bool> StartStates =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["Idle"] = false,
            [JobSuspension.SuspendedState] = false, // paused by the user: clean, then stay paused
            ["Run"] = true,
        };

    private readonly JobPause _pause = new(session);
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task _running = Task.CompletedTask;

    public BedSweepSettings Settings { get; init; } = BedSweepSettings.Default;

    public BedCleaningPhase Phase { get; private set; }
    public bool IsRunning => Phase != BedCleaningPhase.None;

    /// <summary>The pass being swept (1-based) and how many were asked for.</summary>
    public int Pass { get; private set; }
    public int Passes { get; private set; }

    /// <summary>Why the last cleaning did not complete, or null.</summary>
    public string? LastError { get; private set; }

    public static bool CanStartIn(StatusReport? status) => StartStates.Keys.Any(s => JobSuspension.HasState(status, s));

    /// <summary>Whether a cleaning in this state pauses and resumes the running program.</summary>
    public static bool PausesJobIn(StatusReport? status) =>
        StartStates.FirstOrDefault(s => JobSuspension.HasState(status, s.Key)).Value;

    /// <summary>Starts cleaning the bed <paramref name="passes"/> times in a row.</summary>
    public void Start(int passes)
    {
        if (passes is < 1 or > MaxPasses)
            throw new ArgumentOutOfRangeException(nameof(passes), $"Between 1 and {MaxPasses} passes.");
        if (session.Limits.Travel is not { } travel)
            throw new InvalidOperationException(session.Limits.Error ?? "The machine's travel limits are not known yet.");
        var status = session.Status;
        if (!CanStartIn(status))
            throw new InvalidOperationException($"Can't clean the bed while the machine is {status?.State ?? "not reporting"}.");
        var stop = new CancellationTokenSource();
        lock (_gate)
        {
            if (IsRunning) throw new InvalidOperationException("The bed is being cleaned already.");
            _stop = stop;
            LastError = null;
            Passes = passes;
            Pass = 0;
            var pauseJob = PausesJobIn(status);
            SetPhase(pauseJob ? BedCleaningPhase.PausingJob : BedCleaningPhase.Sweeping);
            _running = RunAsync(travel, pauseJob, stop);
        }
    }

    /// <summary>Ends the sweep early; the head still returns and a paused program is resumed.</summary>
    public void Stop()
    {
        lock (_gate) _stop?.Cancel();
    }

    /// <summary>Before disconnecting: stops and gives the head a moment to get back.</summary>
    internal async Task StopForDisconnectAsync()
    {
        Stop();
        try { await _running.WaitAsync(DisconnectWait); }
        catch (TimeoutException) { session.Note("Disconnecting before the bed cleaning returned the head."); }
    }

    private async Task RunAsync(MachineTravel travel, bool pauseJob, CancellationTokenSource stop)
    {
        await Task.Yield(); // let Start return before the first command is sent
        var paused = false;
        double[]? origin = null;
        var height = travel.ClearanceZ;
        try
        {
            if (pauseJob)
            {
                // Not stoppable: a suspend that was sent pauses the program sooner or later, and it must be resumed.
                await _pause.PauseAsync(CancellationToken.None);
                paused = true;
                await _pause.EnsureHeadStayedAsync(CancellationToken.None);
            }
            origin = session.Status?.MachinePosition is { Length: >= 3 } position
                ? position[..3]
                : throw new InvalidOperationException("The machine has not reported its position.");
            height = Math.Max(origin[2], travel.ClearanceZ);
            await SweepAsync(origin, travel, height, stop.Token);
        }
        catch (OperationCanceledException)
        {
            session.Note("Bed cleaning stopped.");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            session.Note($"Bed cleaning failed: {ex.Message}");
        }
        finally
        {
            await FinishAsync(origin, height, paused);
            lock (_gate) _stop = null;
            stop.Dispose();
        }
    }

    private async Task SweepAsync(double[] origin, MachineTravel travel, double height, CancellationToken ct)
    {
        session.Note($"Cleaning the bed ({Passes}×) from {JobPause.Describe(origin)} (machine), at Z{height:0.###}");
        SetPhase(BedCleaningPhase.Sweeping);
        var pass = BedSweepPlanner.Pass(travel, height, Settings);
        await MoveAllAsync(BedSweepPlanner.Lift(origin, travel), ct);
        await MoveAsync(pass[0], ct); // to the start corner before the air goes on
        await session.Air.OnAsync();
        for (var i = 1; i <= Passes; i++)
        {
            Pass = i;
            session.NotifyChanged();
            await MoveAllAsync(i == 1 ? pass.Skip(1) : pass, ct);
        }
    }

    /// <summary>Air off, the head back where it started, then the program resumed; runs however the cleaning ended.</summary>
    private async Task FinishAsync(double[]? origin, double height, bool paused)
    {
        try
        {
            await session.Air.OffIfOnAsync();
            var back = origin is null || await ReturnAsync(origin, height);
            if (!paused) return;
            if (!back)
            {
                LastError = $"The head is not back where the program stopped ({JobPause.Describe(origin)}, machine), so the job stays paused. " +
                            "Bring the head back there, at that height, before resuming: resuming moves straight to that point.";
                session.Note(LastError);
                return;
            }
            SetPhase(BedCleaningPhase.ResumingJob);
            await session.SendAsync(JobSuspension.ResumeCommand);
        }
        catch (Exception ex)
        {
            LastError = $"Could not finish the bed cleaning: {ex.Message}";
            session.Note(LastError);
        }
        finally
        {
            Pass = 0;
            SetPhase(BedCleaningPhase.None);
        }
    }

    /// <returns>Whether the head is back at <paramref name="origin"/>.</returns>
    private async Task<bool> ReturnAsync(double[] origin, double height)
    {
        if (Arrived(session.Status, origin)) return true;
        if (JobSuspension.HasState(session.Status, "Alarm")) return false;
        SetPhase(BedCleaningPhase.Returning);
        try
        {
            await MoveAllAsync(BedSweepPlanner.Return(origin, height), CancellationToken.None);
        }
        catch (Exception ex)
        {
            session.Note($"Could not bring the head back: {ex.Message}");
        }
        return Arrived(session.Status, origin);
    }

    private async Task MoveAllAsync(IEnumerable<MachineMove> moves, CancellationToken ct)
    {
        foreach (var move in moves) await MoveAsync(move, ct);
    }

    /// <summary>
    /// Sends one move and waits until the head arrives. One move at a time, so Stop never has to flush moves that are
    /// already queued on the machine.
    /// </summary>
    private async Task MoveAsync(MachineMove move, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var from = session.Status?.MachinePosition ?? move.Target;
        await session.SendAsync(move.Command);
        var status = await session.WaitForStatusAsync(
            s => Arrived(s, move.Target) || JobSuspension.HasState(s, "Alarm"),
            move.DurationFrom(from) * MoveSlack + MoveMargin, ct);
        if (JobSuspension.HasState(status, "Alarm"))
            throw new InvalidOperationException($"The machine raised an alarm while moving ({move.Command}).");
    }

    private static bool Arrived(StatusReport? status, double[] target) => JobPause.SamePlace(status?.MachinePosition, target);

    private void SetPhase(BedCleaningPhase phase)
    {
        Phase = phase;
        session.NotifyChanged();
    }
}
