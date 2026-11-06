using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace TinkerCNC.Host;

/// <summary>
/// Fans out encoded video frames from one producer (camera or time-lapse) to any number
/// of HTTP MJPEG viewers. Each subscriber gets a 1-slot channel, so slow clients skip frames
/// instead of building a backlog.
/// </summary>
public sealed class FrameHub
{
    private readonly ConcurrentDictionary<string, Source> _sources = new();

    /// <summary>Raised when the number of viewers of a source changes: (sourceId, viewers).</summary>
    public event Action<string, int>? ViewersChanged;

    public void Publish(string sourceId, byte[] frame)
    {
        var source = _sources.GetOrAdd(sourceId, _ => new Source());
        source.Latest = frame;
        source.LatestAt = DateTime.UtcNow;
        foreach (var ch in source.Subscribers.Values)
            ch.Writer.TryWrite(frame);
    }

    public byte[]? Latest(string sourceId) => _sources.TryGetValue(sourceId, out var s) ? s.Latest : null;

    public int Viewers(string sourceId) => _sources.TryGetValue(sourceId, out var s) ? s.Subscribers.Count : 0;

    public void Clear(string sourceId)
    {
        if (_sources.TryGetValue(sourceId, out var s)) s.Latest = null;
    }

    public async IAsyncEnumerable<byte[]> SubscribeAsync(string sourceId, [EnumeratorCancellation] CancellationToken ct)
    {
        var source = _sources.GetOrAdd(sourceId, _ => new Source());
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        source.Subscribers[id] = channel;
        ViewersChanged?.Invoke(sourceId, source.Subscribers.Count);
        try
        {
            if (source.Latest is { } latest) yield return latest;
            await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return frame;
        }
        finally
        {
            source.Subscribers.TryRemove(id, out _);
            ViewersChanged?.Invoke(sourceId, source.Subscribers.Count);
        }
    }

    // Frames are encoded images, JPEG in practice; the format is recognised by its magic bytes.
    private sealed record ImageFormat(byte[] Magic, string ContentType, string Extension);

    private static readonly ImageFormat Jpeg = new(new byte[] { 0xFF, 0xD8 }, "image/jpeg", "jpg");

    private static readonly ImageFormat[] Formats =
    {
        Jpeg,
        new(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "image/png", "png"),
        new(new byte[] { 0x42, 0x4D }, "image/bmp", "bmp"),
    };

    private static ImageFormat FormatOf(byte[] frame) =>
        Formats.FirstOrDefault(f => frame.AsSpan().StartsWith(f.Magic)) ?? Jpeg;

    public static string ContentTypeOf(byte[] frame) => FormatOf(frame).ContentType;

    public static string ExtensionOf(byte[] frame) => FormatOf(frame).Extension;

    private sealed class Source
    {
        public readonly ConcurrentDictionary<Guid, Channel<byte[]>> Subscribers = new();
        public volatile byte[]? Latest;
        public DateTime LatestAt;
    }
}
