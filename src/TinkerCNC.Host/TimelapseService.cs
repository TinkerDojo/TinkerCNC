using System.Collections.Concurrent;
using TinkerCNC.Video;

namespace TinkerCNC.Host;

/// <summary>
/// One time-lapse player per machine host. The machine only allows one preview
/// viewer at a time, so the server holds the channel and all browser tabs share it through
/// the MJPEG endpoint.
/// </summary>
public sealed class TimelapseService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TimelapsePlayer> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly FrameHub _hub;

    public TimelapseService(FrameHub hub) => _hub = hub;

    public static string SourceIdFor(string host) => "timelapse-" + host;

    public TimelapsePlayer Get(string host) => _players.GetOrAdd(host, h =>
    {
        var p = new TimelapsePlayer(h);
        p.FrameReceived += frame => _hub.Publish(SourceIdFor(h), frame);
        return p;
    });

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _players.Values) await p.DisposeAsync();
    }
}
