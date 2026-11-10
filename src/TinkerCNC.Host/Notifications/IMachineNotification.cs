namespace TinkerCNC.Host.Notifications;

/// <summary>A button on a notification: does something on the machine and then resolves the notification.</summary>
/// <param name="Resolution">Shown once the action succeeded, e.g. "Confirmed".</param>
public sealed record NotificationAction(Text Label, string Icon, string Css, Func<MachineSession, Task> Execute, Text Resolution);

/// <summary>Something the machine reported that the user should see or answer.</summary>
public interface IMachineNotification
{
    Guid Id { get; }
    DateTime RaisedUtc { get; }
    string Icon { get; }

    /// <summary>CSS modifier for the notification's look (e.g. "action", "alarm").</summary>
    string Kind { get; }

    Text Title { get; }
    Text Message { get; }
    IReadOnlyList<NotificationAction> Actions { get; }

    /// <summary>How it was resolved ("Confirmed", "Done on the machine"…); null while it is still open.</summary>
    Text? Resolution { get; }

    DateTime? ResolvedUtc { get; }

    bool IsOpen => Resolution is null;

    void Resolve(Text resolution);

    /// <summary>Resolves the notification when the machine's state shows it no longer applies.</summary>
    void Observe(StatusReport? status);
}

/// <summary>Identity and resolution shared by every notification.</summary>
public abstract class MachineNotification : IMachineNotification
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime RaisedUtc { get; } = DateTime.UtcNow;
    public Text? Resolution { get; private set; }
    public DateTime? ResolvedUtc { get; private set; }

    public abstract string Icon { get; }
    public abstract string Kind { get; }
    public abstract Text Title { get; }
    public abstract Text Message { get; }
    public abstract IReadOnlyList<NotificationAction> Actions { get; }

    public void Resolve(Text resolution)
    {
        if (Resolution is not null) return;
        Resolution = resolution;
        ResolvedUtc = DateTime.UtcNow;
    }

    public void Observe(StatusReport? status)
    {
        if (Resolution is null && ResolvedBy(status) is { } resolution) Resolve(resolution);
    }

    /// <summary>The resolution the machine's state implies, or null while the notification still applies.</summary>
    protected abstract Text? ResolvedBy(StatusReport? status);
}
