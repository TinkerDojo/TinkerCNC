namespace TinkerCNC.Host;

/// <summary>
/// Switches a machine's air on and off. Which output that is depends on the machine (<see cref="MachineTypes"/>,
/// recognised by its travel): compressed air on most machines, the spindle blower on compact machines. Until the travel is
/// known the air can't be used, as a wrong guess would silently do nothing. The state is what was last sent: the
/// regular status report does not include it. Cleaning the bed with the air on is <see cref="BedCleaning"/>.
/// </summary>
public sealed class AirControl(MachineSession session)
{
    // Continuous air only while nothing moves under program control.
    private static readonly string[] SwitchOnStates = ["Idle", JobSuspension.SuspendedState];

    /// <summary>The output that was switched on, so the same one is switched off.</summary>
    private IAirOutput? _onOutput;

    public bool IsOn => _onOutput is not null;

    /// <summary>This machine's air output, or null while its travel is unknown.</summary>
    public IAirOutput? Output => session.Limits.Travel is { } travel ? MachineTypes.AirFor(travel) : null;

    public static bool CanSwitchOnIn(StatusReport? status) => SwitchOnStates.Any(s => JobSuspension.HasState(status, s));

    public async Task OnAsync()
    {
        var output = Output ?? throw new InvalidOperationException(
            session.Limits.Error ?? "The machine's type (read from its travel) is not known yet, so its air output isn't either.");
        await session.SendAsync(output.OnCommand);
        SetOn(output);
    }

    public async Task OffAsync()
    {
        var output = _onOutput ?? Output ?? MachineTypes.DefaultAir;
        await session.SendAsync(output.OffCommand);
        SetOn(null);
    }

    /// <summary>Switches the air off if it is on; failures are noted, not thrown (cleanup paths).</summary>
    internal async Task OffIfOnAsync()
    {
        if (!IsOn) return;
        try { await OffAsync(); }
        catch (Exception ex) { session.Note($"Could not switch the air off: {ex.Message}"); }
    }

    private void SetOn(IAirOutput? output)
    {
        _onOutput = output;
        session.NotifyChanged();
    }
}
