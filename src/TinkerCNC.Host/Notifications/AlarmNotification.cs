namespace TinkerCNC.Host.Notifications;

/// <summary>The machine went into alarm; Unlock sends <c>$X</c> like the alarm banner.</summary>
public sealed class AlarmNotification(string? message) : MachineNotification
{
    private static readonly IReadOnlyList<NotificationAction> UnlockOnly =
    [
        new("Unlock ($X)", "unlock", "btn-light", s => s.UnlockAsync(), "Unlocked"),
    ];

    public override string Icon => "alert";
    public override string Kind => "alarm";
    public override string Title => "Alarm";
    public override string Message => string.IsNullOrWhiteSpace(message) ? "The machine reported an alarm." : message;
    public override IReadOnlyList<NotificationAction> Actions => UnlockOnly;

    protected override string? ResolvedBy(StatusReport? status) =>
        status?.State?.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase) == true ? null : "Cleared";
}
