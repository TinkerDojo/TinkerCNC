using TinkerCNC.Motion;

namespace TinkerCNC.Host;

/// <summary>
/// The connected machine's travel, read from its configuration after connecting (<see cref="MachineTravel"/>). Until
/// it is known, nothing may be moved to typed positions: guessed limits are not safe. Every query makes the machine
/// read its config.txt from the SD card, so the queries are spaced out and the result is kept for later connections
/// of the same machine.
/// </summary>
public sealed class MachineLimits(MachineSession session)
{
    /// <summary>Pause between two configuration queries.</summary>
    private static readonly TimeSpan QueryGap = TimeSpan.FromMilliseconds(200);

    public MachineTravel? Travel { get; private set; }

    /// <summary>Why the travel is unknown (machine did not answer, settings missing), or null.</summary>
    public Text? Error { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>Reads the settings one by one (unless known already); a setting the machine lacks counts as not set.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (Travel is not null || Loading) return;
        Loading = true;
        Error = null;
        session.NotifyChanged();
        try
        {
            var values = new Dictionary<string, string?>();
            foreach (var key in MachineTravel.ConfigKeys)
            {
                values[key] = await session.GetConfigAsync(key, ct);
                await Task.Delay(QueryGap, ct);
            }
            Travel = MachineTravel.FromConfig(values);
            Error = Travel is null ? HostTexts.Of("Limits.NoSoftLimits") : null;
            if (Travel is { } t)
                session.Note(FormattableString.Invariant(
                    $"Travel: X {t.X.Min:0.###}…{t.X.Max:0.###}, Y {t.Y.Min:0.###}…{t.Y.Max:0.###}, Z {t.Z.Min:0.###}…{t.Z.Max:0.###} mm (machine coordinates)"));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            Travel = null;
            Error = HostTexts.Of("Limits.ReadFailed", Text.Of(ex));
        }
        finally
        {
            Loading = false;
            session.NotifyChanged();
        }
    }

    /// <summary>Clears a failed attempt so the next connection tries again; known limits stay.</summary>
    internal void ForgetError() => Error = null;
}
