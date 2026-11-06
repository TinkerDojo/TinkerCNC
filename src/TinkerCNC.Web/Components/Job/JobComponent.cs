using Microsoft.AspNetCore.Components;
using TinkerCNC.Web.Components.Machine;
using TinkerCNC.Host;

namespace TinkerCNC.Web.Components.Job;

/// <summary>A panel showing the job of the session's machine; re-renders when the job or the session changes.</summary>
public abstract class JobComponent : SessionComponent
{
    [Inject] private JobTrackers Trackers { get; set; } = default!;

    protected JobTracker Job { get; private set; } = default!;

    protected override void OnParametersSet()
    {
        var job = Trackers.Get(Session);
        if (!ReferenceEquals(job, Job))
        {
            if (Job is not null) Job.Changed -= RequestRender;
            Job = job;
            Job.Changed += RequestRender;
        }
        base.OnParametersSet();
    }

    protected override void OnDispose()
    {
        if (Job is not null) Job.Changed -= RequestRender;
        base.OnDispose();
    }
}
