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
        new("Confirm", "check", "btn-primary", s => s.SendAsync(ManualToolChange.ConfirmCommand), "Confirmed"),
    ];

    public override string Icon => "tool";
    public override string Kind => "action";

    public override string Title => targetTool is { } tool ? $"Insert {ManualToolChange.NameOf(tool)}" : "Tool change waiting";

    public override string Message => $"The machine is waiting for {Target}. Insert it, then confirm to continue the job.";

    public override IReadOnlyList<NotificationAction> Actions => ConfirmOnly;

    private string Target =>
        targetTool is not { } tool ? "the next tool"
        : toolNames(tool) is { Length: > 0 } name ? $"{ManualToolChange.NameOf(tool)} ({name})"
        : ManualToolChange.NameOf(tool);

    /// <summary>Confirmed on the machine itself or from another client.</summary>
    protected override string? ResolvedBy(StatusReport? status) =>
        ManualToolChange.IsWaiting(status) ? null : "Done on the machine";
}
