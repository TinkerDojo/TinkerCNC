namespace TinkerCNC.Video;

/// <summary>Which camera frames a recording keeps and how it times them.</summary>
/// <param name="Selection">The frames kept.</param>
/// <param name="Timing">The clock the kept frames are shown by.</param>
/// <param name="IsTimelapse">Whether the recording plays faster than it was captured.</param>
public sealed record RecordingPlan(IFrameSelection Selection, IFrameTiming Timing, bool IsTimelapse)
{
    /// <summary>A recording in real time keeps one camera frame in this many: smooth enough, at a fifth of the data.</summary>
    public const int RealTimeKeepEvery = 5;

    /// <summary>Every <see cref="RealTimeKeepEvery"/>th frame, shown when it was captured.</summary>
    public static readonly RecordingPlan RealTime = RealTimeKeeping(RealTimeKeepEvery);

    /// <summary>Every <paramref name="every"/>th frame, shown when it was captured.</summary>
    public static RecordingPlan RealTimeKeeping(int every) => new(new EveryNthFrame(every), new RealTimeTiming(), false);

    /// <summary>One frame per <paramref name="interval"/>, shown at <paramref name="playbackFramesPerSecond"/>.</summary>
    public static RecordingPlan Timelapse(TimeSpan interval, int playbackFramesPerSecond) =>
        new(new FixedInterval(interval), new FixedRateTiming(playbackFramesPerSecond), true);
}
