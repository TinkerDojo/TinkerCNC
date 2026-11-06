using TinkerCNC.Host;
using Microsoft.AspNetCore.Components;

namespace TinkerCNC.Web.Components.Machine;

/// <summary>A dashboard panel bound to one <see cref="MachineSession"/>; re-renders when the session changes.</summary>
public abstract class SessionComponent : LiveComponent
{
    private MachineSession? _subscribed;

    [Parameter, EditorRequired] public MachineSession Session { get; set; } = default!;

    protected bool Connected => Session.State == SessionState.Connected;

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(_subscribed, Session)) return;
        Unsubscribe();
        _subscribed = Session;
        Session.Changed += RequestRender;
    }

    protected override void OnDispose() => Unsubscribe();

    private void Unsubscribe()
    {
        if (_subscribed is not null) _subscribed.Changed -= RequestRender;
        _subscribed = null;
    }
}
