using Microsoft.AspNetCore.Components;

namespace TinkerCNC.Web.Components;

/// <summary>
/// Base for components that re-render on background events (status reports, discovery, frames).
/// Coalesces bursts into at most one render per <see cref="MinRenderInterval"/>.
/// </summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    private readonly object _gate = new();
    private bool _pending;
    private DateTime _lastRender;
    private bool _disposed;
    private Timer? _ticker;

    protected TimeSpan MinRenderInterval { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Call from any thread when the underlying data changed.</summary>
    protected void RequestRender()
    {
        lock (_gate)
        {
            if (_pending || _disposed) return;
            _pending = true;
        }
        var wait = MinRenderInterval - (DateTime.UtcNow - _lastRender);
        _ = RenderLaterAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
    }

    /// <summary>Re-render periodically (for "x seconds ago" labels and similar).</summary>
    protected void StartTicker(TimeSpan interval) =>
        _ticker ??= new Timer(_ => RequestRender(), null, interval, interval);

    private async Task RenderLaterAsync(TimeSpan wait)
    {
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        lock (_gate)
        {
            _pending = false;
            if (_disposed) return;
        }
        _lastRender = DateTime.UtcNow;
        try { await InvokeAsync(StateHasChanged); }
        catch (ObjectDisposedException) { }
    }

    protected virtual void OnDispose() { }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _ticker?.Dispose();
        OnDispose();
        GC.SuppressFinalize(this);
    }
}
