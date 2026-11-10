namespace TinkerCNC.Host.Notifications;

/// <summary>The machine went into alarm; Unlock sends <c>$X</c> like the alarm banner.</summary>
/// <param name="message">What the machine reported, shown as it is.</param>
public sealed class AlarmNotification(string? message) : MachineNotification
{
    private static readonly IReadOnlyList<NotificationAction> UnlockOnly =
    [
        new(HostTexts.Of("Notice.Unlock"), "unlock", "btn-light", s => s.UnlockAsync(), HostTexts.Of("Notice.Unlocked")),
    ];

    public override string Icon => "alert";
    public override string Kind => "alarm";
    public override Text Title => HostTexts.Of("Notice.Alarm.Title");
    public override Text Message => string.IsNullOrWhiteSpace(message) ? HostTexts.Of("Notice.Alarm.Message") : Text.Plain(message);
    public override IReadOnlyList<NotificationAction> Actions => UnlockOnly;

    protected override Text? ResolvedBy(StatusReport? status) =>
        status?.State?.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase) == true ? null : HostTexts.Of("Notice.Cleared");
}
