namespace TinkerCNC.Video;

/// <summary>
/// When a recorded frame is shown: its position on the recording's clock, in <see cref="Timescale"/> ticks per second.
/// </summary>
public interface IFrameTiming
{
    /// <summary>Clock ticks per second of the recording.</summary>
    int Timescale { get; }

    /// <summary>The clock position of a frame.</summary>
    /// <param name="index">How many frames were recorded before it.</param>
    /// <param name="sinceStart">How long after the first recorded frame it was captured.</param>
    long TimestampOf(int index, TimeSpan sinceStart);
}

/// <summary>Frames are shown when they were captured: a recording in real time, to the millisecond.</summary>
public sealed class RealTimeTiming : IFrameTiming
{
    public int Timescale => 1000;

    public long TimestampOf(int index, TimeSpan sinceStart) => (long)Math.Round(sinceStart.TotalMilliseconds);
}

/// <summary>Frames are shown one after the other at a fixed rate, whenever they were captured: a time-lapse.</summary>
public sealed class FixedRateTiming : IFrameTiming
{
    public FixedRateTiming(int framesPerSecond)
    {
        if (framesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        Timescale = framesPerSecond;
    }

    public int Timescale { get; }

    public long TimestampOf(int index, TimeSpan sinceStart) => index;
}
