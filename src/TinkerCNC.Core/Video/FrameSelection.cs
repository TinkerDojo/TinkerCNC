namespace TinkerCNC.Video;

/// <summary>Which of the camera's frames a recording keeps.</summary>
public interface IFrameSelection
{
    /// <summary>Whether to keep a frame.</summary>
    /// <param name="index">How many camera frames arrived before it since the recording started.</param>
    /// <param name="sinceLastKept">How long after the last kept frame it arrived; null while none was kept.</param>
    bool Keeps(long index, TimeSpan? sinceLastKept);
}

/// <summary>Keeps the first frame and every <see cref="Every"/>th after it.</summary>
public sealed class EveryNthFrame : IFrameSelection
{
    public EveryNthFrame(int every)
    {
        if (every <= 0) throw new ArgumentOutOfRangeException(nameof(every));
        Every = every;
    }

    public int Every { get; }

    public bool Keeps(long index, TimeSpan? sinceLastKept) => index % Every == 0;
}

/// <summary>Keeps one frame per <see cref="Interval"/>: the first one that arrives once the interval has passed.</summary>
public sealed class FixedInterval(TimeSpan interval) : IFrameSelection
{
    public TimeSpan Interval { get; } = interval;

    public bool Keeps(long index, TimeSpan? sinceLastKept) => sinceLastKept is not { } since || since >= Interval;
}
