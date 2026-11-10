using System.Collections.Concurrent;
using TinkerCNC.Video;

namespace TinkerCNC.Host;

/// <summary>Live camera state for one machine host.</summary>
public sealed class CameraFeed
{
    internal CameraFeed(string host) => Host = host;

    public string Host { get; }
    public string SourceId => CameraService.SourceIdFor(Host);
    public bool Running { get; internal set; }
    public bool Connected { get; internal set; }
    public Text? LastError { get; internal set; }
    public long Frames { get; internal set; }
    public double Fps => Meter.Fps;
    public DateTime? LastFrameUtc { get; internal set; }

    /// <summary>The file the recording ends up in, while one is running.</summary>
    public string? RecordingPath { get; internal set; }

    public long RecordedFrames => Recorder?.FramesWritten ?? 0;
    public bool IsRecording => RecordingPath is not null;
    public bool IsTimelapse => Recorder?.Plan.IsTimelapse ?? false;

    public DateTime? RecordingStartedUtc { get; internal set; }

    /// <summary>Time between recorded frames; null records every frame.</summary>


    internal CancellationTokenSource? Cts;
    internal VideoRecorder? Recorder;
    internal readonly object Gate = new();
    internal readonly FrameRateMeter Meter = new(TimeSpan.FromSeconds(2));
}

/// <summary>
/// One upstream camera WebSocket per machine, shared by every browser viewer, with optional recording through
/// <see cref="RecordingStore"/>: MP4 files (H.264) that any player and browser opens, encoded here on a
/// background task (<see cref="VideoRecorder"/>).
/// </summary>
public sealed class CameraService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CameraFeed> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly FrameHub _hub;
    private readonly RecordingStore _store;
    private readonly ILogger<CameraService> _log;

    public CameraService(FrameHub hub, RecordingStore store, ILogger<CameraService> log)
    {
        _hub = hub;
        _store = store;
        _log = log;
    }

    public event Action<CameraFeed>? Changed;

    public static string SourceIdFor(string host) => "live-" + host;

    public CameraFeed Get(string host) => _feeds.GetOrAdd(host, h => new CameraFeed(h));

    public void Start(string host)
    {
        var feed = Get(host);
        lock (feed.Gate)
        {
            if (feed.Running) return;
            feed.Running = true;
            feed.LastError = null;
            feed.Cts = new CancellationTokenSource();
        }

        var client = new LiveCameraClient(host);
        client.ConnectionChanged += (connected, error) =>
        {
            feed.Connected = connected;
            if (error is not null && feed.Running) feed.LastError = error;
            if (connected) feed.LastError = null;
            Changed?.Invoke(feed);
        };
        client.TextReceived += text => _log.LogInformation("[camera {Host}] {Text}", host, text);
        client.FrameReceived += frame => OnFrame(feed, frame);

        var ct = feed.Cts!.Token;
        _ = Task.Run(async () =>
        {
            try { await client.RunAsync(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "camera loop for {Host} ended", host); }
        });
        Changed?.Invoke(feed);
    }

    /// <summary>Stops the camera; a running recording is completed in the background.</summary>
    public void Stop(string host)
    {
        if (!_feeds.TryGetValue(host, out var feed)) return;
        FinishInBackground(StopRecordingAsync(host));
        lock (feed.Gate)
        {
            feed.Running = false;
            feed.Connected = false;
            feed.Cts?.Cancel();
            feed.Cts = null;
            feed.Meter.Reset();
        }
        _hub.Clear(feed.SourceId);
        Changed?.Invoke(feed);
    }

    /// <summary>Starts recording (and the camera, if needed); a recording already running is kept as it is.</summary>
    /// <param name="plan">Which frames to keep and how to time them; <see cref="RecordingPlan.RealTime"/> when omitted.</param>
    /// <returns>The file the recording ends up in.</returns>
    public string StartRecording(string host, string? label = null, RecordingPlan? plan = null)
    {
        plan ??= RecordingPlan.RealTime;
        var feed = Get(host);
        if (!feed.Running) Start(host);
        lock (feed.Gate)
        {
            if (feed.Recorder is not null) return feed.RecordingPath!;
            feed.Recorder = _store.CreateRecording(label ?? host, plan);
            feed.RecordingPath = feed.Recorder.Path;
            feed.RecordingStartedUtc = DateTime.UtcNow;
        }
        Changed?.Invoke(feed);
        return feed.RecordingPath!;
    }

    /// <summary>Stops the recording and completes its file. Null when there was none or nothing was recorded.</summary>
    public async Task<string?> StopRecordingAsync(string host)
    {
        if (!_feeds.TryGetValue(host, out var feed)) return null;
        VideoRecorder? recorder;
        lock (feed.Gate)
        {
            recorder = feed.Recorder;
            feed.Recorder = null;
            feed.RecordingPath = null;
            feed.RecordingStartedUtc = null;
        }
        if (recorder is null) return null;
        Changed?.Invoke(feed);
        var path = await recorder.FinishAsync().ConfigureAwait(false);
        if (recorder.Error is { } error)
        {
            feed.LastError = HostTexts.Of("Camera.RecordingFailed", error);
            Changed?.Invoke(feed);
        }
        return path;
    }

    /// <summary>Saves the latest frame as a still image and returns its path.</summary>
    public string? SaveSnapshot(string host, string? label = null)
    {
        var frame = _hub.Latest(SourceIdFor(host));
        return frame is null ? null : _store.SaveSnapshot(label ?? host, frame);
    }

    private void OnFrame(CameraFeed feed, byte[] frame)
    {
        var now = DateTime.UtcNow;
        var failed = false;
        lock (feed.Gate)
        {
            feed.Frames++;
            feed.LastFrameUtc = now;
            feed.Meter.Tick(now);

            if (feed.Recorder is { } recorder)
            {
                if (recorder.Error is not null) failed = true;
                else recorder.Add(frame, now);
            }
        }
        if (failed) FinishInBackground(StopRecordingAsync(feed.Host));
        _hub.Publish(feed.SourceId, frame);
    }

    private void FinishInBackground(Task<string?> finishing) =>
        _ = finishing.ContinueWith(t => _log.LogWarning(t.Exception, "finishing a recording failed"),
            TaskContinuationOptions.OnlyOnFaulted);

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _feeds.Keys)
        {
            await StopRecordingAsync(host).ConfigureAwait(false);
            Stop(host);
        }
    }
}
