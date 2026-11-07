namespace TinkerCNC.Host.Notifications;

/// <summary>
/// A queued job waits for the go (paused right after its start, see <see cref="JobCommands.Pause"/>): Resume sends
/// <c>resume</c>; holding the machine's button does the same on the machine.
/// </summary>
public sealed class QueueGateNotification(string job) : MachineNotification
{
    private static readonly IReadOnlyList<NotificationAction> ResumeOnly =
    [
        new("Resume", "play", "btn-primary", s => s.SendAsync(JobSuspension.ResumeCommand), "Resumed"),
    ];

    public override string Icon => "play";
    public override string Kind => "action";
    public override string Title => "Ready for the next job";

    public override string Message =>
        $"{job} is on the machine, paused before anything moves. Put the piece in place, then hold the machine's button " +
        "until it continues, or press Resume.";

    public override IReadOnlyList<NotificationAction> Actions => ResumeOnly;

    /// <summary>Resumed on the machine, or the job ended meanwhile.</summary>
    protected override string? ResolvedBy(StatusReport? status) =>
        JobSuspension.IsSuspended(status) ? null : status?.IsPlaying == true ? "Continued" : "Over";
}

/// <summary>Something about the queue to read (it stopped, it finished); goes away when dismissed.</summary>
public sealed class QueueNotification(string title, string message, string kind, string icon) : MachineNotification
{
    public override string Icon => icon;
    public override string Kind => kind;
    public override string Title => title;
    public override string Message => message;
    public override IReadOnlyList<NotificationAction> Actions => [];

    protected override string? ResolvedBy(StatusReport? status) => null;
}
