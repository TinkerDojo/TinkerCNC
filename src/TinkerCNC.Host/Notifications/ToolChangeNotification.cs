namespace TinkerCNC.Host.Notifications;

/// <summary>
/// A manual tool change waits for the user (state <c>Tool</c>); Confirm sends <c>M490.2</c> so the machine carries on
/// (see <see cref="ManualToolChange"/>).
/// </summary>
/// <param name="toolNames">
/// Name of a tool in the running program's header, if it declares one; asked when shown, because the program may
/// still be downloading when the machine starts waiting.
/// </param>
public sealed class ToolChangeNotification(int? targetTool, Func<int, string?> toolNames) : MachineNotification
{
    private static readonly IReadOnlyList<NotificationAction> ConfirmOnly =
    [
        new(HostTexts.Of("Notice.Confirm"), "check", "btn-primary", s => s.SendAsync(ManualToolChange.ConfirmCommand),
            HostTexts.Of("Notice.Confirmed")),
    ];

    public override string Icon => "tool";
    public override string Kind => "action";

    public override Text Title => targetTool is { } tool ? HostTexts.Of("Notice.Insert", ManualToolChange.NameOf(tool)) : HostTexts.Of("Notice.ToolChange.Title");

    public override Text Message => HostTexts.Of("Notice.ToolChange.Message", Target);

    public override IReadOnlyList<NotificationAction> Actions => ConfirmOnly;

    private Text Target =>
        targetTool is not { } tool ? HostTexts.Of("Notice.NextTool")
        : toolNames(tool) is { Length: > 0 } name ? HostTexts.Of("Notice.NamedTool", ManualToolChange.NameOf(tool), name)
        : ManualToolChange.NameOf(tool);

    /// <summary>Confirmed on the machine itself or from another client.</summary>
    protected override Text? ResolvedBy(StatusReport? status) =>
        ManualToolChange.IsWaiting(status) ? null : HostTexts.Of("Notice.DoneOnMachine");
}
