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
    public string? LastError { get; internal set; }
    public long Frames { get; internal set; }
    public double Fps => Meter.Fps;
    public DateTime? LastFrameUtc { get; internal set; }
    public string? RecordingPath { get; internal set; }
    public long RecordedFrames { get; internal set; }
    public bool IsRecording => RecordingPath is not null;

    internal CancellationTokenSource? Cts;
    internal FileStream? Recording;
    internal readonly object Gate = new();
    internal readonly FrameRateMeter Meter = new(TimeSpan.FromSeconds(2));
}

/// <summary>
/// One upstream camera WebSocket per machine, shared by every browser viewer, with optional
/// recording through <see cref="RecordingStore"/>. Recordings are raw concatenated JPEG frames (.mjpeg),
/// which VLC/ffmpeg play directly (e.g. <c>ffmpeg -f mjpeg -framerate 10 -i rec.mjpeg out.mp4</c>).
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

    public void Stop(string host)
    {
        if (!_feeds.TryGetValue(host, out var feed)) return;
        StopRecording(host);
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

    public string StartRecording(string host, string? label = null)
    {
        var feed = Get(host);
        if (!feed.Running) Start(host);
        lock (feed.Gate)
        {
            if (feed.Recording is not null) return feed.RecordingPath!;
            feed.Recording = _store.CreateRecording(label ?? host);
            feed.RecordingPath = feed.Recording.Name;
            feed.RecordedFrames = 0;
        }
        Changed?.Invoke(feed);
        return feed.RecordingPath!;
    }

    public string? StopRecording(string host)
    {
        if (!_feeds.TryGetValue(host, out var feed)) return null;
        string? path;
        lock (feed.Gate)
        {
            path = feed.RecordingPath;
            feed.Recording?.Dispose();
            feed.Recording = null;
            feed.RecordingPath = null;
        }
        if (path is not null) Changed?.Invoke(feed);
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
        lock (feed.Gate)
        {
            feed.Frames++;
            feed.LastFrameUtc = now;
            feed.Meter.Tick(now);

            if (feed.Recording is { } rec)
            {
                try
                {
                    rec.Write(frame);
                    feed.RecordedFrames++;
                }
                catch (Exception ex)
                {
                    feed.LastError = "Recording failed: " + ex.Message;
                    rec.Dispose();
                    feed.Recording = null;
                    feed.RecordingPath = null;
                }
            }
        }
        _hub.Publish(feed.SourceId, frame);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var host in _feeds.Keys) Stop(host);
        return ValueTask.CompletedTask;
    }
}
