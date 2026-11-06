namespace TinkerCNC.Host;

/// <summary>Frames per second over a sliding window.</summary>
public sealed class FrameRateMeter(TimeSpan window)
{
    private readonly Queue<DateTime> _ticks = new();

    public double Fps => _ticks.Count / window.TotalSeconds;

    public void Tick(DateTime utcNow)
    {
        _ticks.Enqueue(utcNow);
        while (_ticks.Count > 0 && utcNow - _ticks.Peek() > window)
            _ticks.Dequeue();
    }

    public void Reset() => _ticks.Clear();
}
