namespace TinkerCNC.Host.Notifications;

/// <summary>Turns a change of the machine's status into a notification (or none).</summary>
public interface INotificationSource
{
    IMachineNotification? Observe(StatusReport? previous, StatusReport current, MachineSession session, JobTracker job);
}

/// <summary>Raised once when the state turns <c>Tool</c>, not again while it stays there.</summary>
public sealed class ToolChangeSource : INotificationSource
{
    public IMachineNotification? Observe(StatusReport? previous, StatusReport current, MachineSession session, JobTracker job)
    {
        if (!ManualToolChange.IsWaiting(current) || ManualToolChange.IsWaiting(previous)) return null;
        return new ToolChangeNotification(current.Tool?.Target,
            tool => job.Timeline?.Analysis.Metadata.Tools.GetValueOrDefault(tool)?.Name);
    }
}

/// <summary>Raised when the state turns <c>Alarm</c>.</summary>
public sealed class AlarmSource : INotificationSource
{
    public IMachineNotification? Observe(StatusReport? previous, StatusReport current, MachineSession session, JobTracker job) =>
        IsAlarm(current) && !IsAlarm(previous) ? new AlarmNotification(session.LastAlarm) : null;

    private static bool IsAlarm(StatusReport? status) =>
        status?.State?.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase) == true;
}
