using Microsoft.AspNetCore.Components;
using TinkerCNC.Host;
using TinkerCNC.Jobs;

namespace TinkerCNC.Web.Components.Job;

/// <summary>
/// A panel that starts or controls programs on the session's machine (<see cref="JobControl"/>) and its queue
/// (<see cref="JobQueue"/>); it also sees the job the machine runs (<see cref="JobComponent"/>).
/// </summary>
public abstract class JobControlComponent : JobComponent
{
    [Inject] private JobControls Controls { get; set; } = default!;
    [Inject] private JobQueues Queues { get; set; } = default!;

    private JobQueue? _queue;

    protected JobControl Control => Controls.Get(Session);

    protected JobQueue Queue => _queue!;

    /// <summary>The options the next start uses, edited in place.</summary>
    protected JobStartOptions Options => Control.Options;

    protected bool Playing => Job.IsPlaying;

    protected override void OnParametersSet()
    {
        var queue = Queues.Get(Session);
        if (!ReferenceEquals(queue, _queue))
        {
            if (_queue is not null) _queue.Changed -= RequestRender;
            _queue = queue;
            _queue.Changed += RequestRender;
        }
        base.OnParametersSet();
    }

    protected override void OnDispose()
    {
        if (_queue is not null) _queue.Changed -= RequestRender;
        base.OnDispose();
    }
}
