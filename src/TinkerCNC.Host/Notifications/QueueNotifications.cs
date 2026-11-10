using TinkerCNC.Jobs;

namespace TinkerCNC.Host.Notifications;

/// <summary>
/// A queued job waits for the go (held right after its start, see <see cref="JobGate"/>): Resume releases the feed
/// hold (<c>~</c>); a press of the machine's button does the same on the machine.
/// </summary>
/// <param name="job">Which job it is, and its place in the queue.</param>
public sealed class QueueGateNotification(Text job) : MachineNotification
{
    private static readonly IReadOnlyList<NotificationAction> ResumeOnly =
    [
        new(HostTexts.Of("Notice.Resume"), "play", "btn-primary", s => s.RealtimeAsync(JobGate.Release, "~ (cycle start / resume)"),
            HostTexts.Of("Notice.Resumed")),
    ];

    public override string Icon => "play";
    public override string Kind => "action";
    public override Text Title => HostTexts.Of("Notice.NextJob.Title");

    public override Text Message => HostTexts.Of("Notice.NextJob.Message", job);

    public override IReadOnlyList<NotificationAction> Actions => ResumeOnly;

    /// <summary>Resumed on the machine, or the job ended meanwhile.</summary>
    protected override Text? ResolvedBy(StatusReport? status) =>
        JobGate.IsHeld(status) ? null : status?.IsPlaying == true ? HostTexts.Of("Notice.Continued") : HostTexts.Of("Notice.Over");
}

/// <summary>Something about the queue to read (it stopped, it finished); goes away when dismissed.</summary>
public sealed class QueueNotification(Text title, Text message, string kind, string icon) : MachineNotification
{
    public override string Icon => icon;
    public override string Kind => kind;
    public override Text Title => title;
    public override Text Message => message;
    public override IReadOnlyList<NotificationAction> Actions => [];

    protected override Text? ResolvedBy(StatusReport? status) => null;
}
