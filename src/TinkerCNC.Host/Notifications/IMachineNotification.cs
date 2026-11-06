namespace TinkerCNC.Host.Notifications;

/// <summary>A button on a notification: does something on the machine and then resolves the notification.</summary>
/// <param name="Resolution">Shown once the action succeeded, e.g. "Confirmed".</param>
public sealed record NotificationAction(string Label, string Icon, string Css, Func<MachineSession, Task> Execute, string Resolution);

/// <summary>Something the machine reported that the user should see or answer.</summary>
public interface IMachineNotification
{
    Guid Id { get; }
    DateTime RaisedUtc { get; }
    string Icon { get; }

    /// <summary>CSS modifier for the notification's look (e.g. "action", "alarm").</summary>
    string Kind { get; }

    string Title { get; }
    string Message { get; }
    IReadOnlyList<NotificationAction> Actions { get; }

    /// <summary>How it was resolved ("Confirmed", "Done on the machine"…); null while it is still open.</summary>
    string? Resolution { get; }

    DateTime? ResolvedUtc { get; }

    bool IsOpen => Resolution is null;

    void Resolve(string resolution);

    /// <summary>Resolves the notification when the machine's state shows it no longer applies.</summary>
    void Observe(StatusReport? status);
}

/// <summary>Identity and resolution shared by every notification.</summary>
public abstract class MachineNotification : IMachineNotification
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime RaisedUtc { get; } = DateTime.UtcNow;
    public string? Resolution { get; private set; }
    public DateTime? ResolvedUtc { get; private set; }

    public abstract string Icon { get; }
    public abstract string Kind { get; }
    public abstract string Title { get; }
    public abstract string Message { get; }
    public abstract IReadOnlyList<NotificationAction> Actions { get; }

    public void Resolve(string resolution)
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
    protected abstract string? ResolvedBy(StatusReport? status);
}
