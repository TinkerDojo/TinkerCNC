using System.Net.WebSockets;
using System.Text;

namespace TinkerCNC.Video;

/// <summary>
/// Live camera feed.
/// <list type="number">
/// <item>Connect a WebSocket to <c>ws://&lt;machine-ip&gt;:82/ws_video</c> (no proxy).</item>
/// <item>Once connected, send the text message <c>start_stream</c>.</item>
/// <item>Each binary message is one complete encoded image (JPEG in practice).</item>
/// <item>Text messages are informational and only logged.</item>
/// </list>
/// <see cref="RunAsync"/> reconnects 2 s after an unexpected drop and warns when no frame arrives within 10 s.
/// </summary>
public sealed class LiveCameraClient
{
    public const int Port = 82;
    public const string Path = "/ws_video";
    public const string StartMessage = "start_stream";

    private readonly MessageHandler _handler;

    public LiveCameraClient(string host)
    {
        Host = host;
        _handler = new MessageHandler(this);
    }

    public string Host { get; }
    public Uri Uri => new($"ws://{Host}:{Port}{Path}");
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);
    public long FramesReceived { get; private set; }
    public bool IsStreaming { get; private set; }

    public event Action<byte[]>? FrameReceived;
    public event Action<string>? TextReceived;
    /// <summary>Connection state changes: (connected, error message or null).</summary>
    public event Action<bool, string?>? ConnectionChanged;

    /// <summary>Streams until cancelled, reconnecting on failure.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? error = null;
            try
            {
                await StreamOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            IsStreaming = false;
            ConnectionChanged?.Invoke(false, error ?? "stream closed");
            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        IsStreaming = false;
    }

    private async Task StreamOnceAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.Proxy = null; // the camera is on the local network; never go through a proxy
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(Uri, connectCts.Token).ConfigureAwait(false);
        }

        await ws.SendAsync(Encoding.ASCII.GetBytes(StartMessage), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        IsStreaming = true;
        ConnectionChanged?.Invoke(true, null);

        await foreach (var message in WebSocketMessages.ReadAsync(ws, ct).ConfigureAwait(false))
            await message.AcceptAsync(_handler).ConfigureAwait(false);
    }

    private sealed class MessageHandler(LiveCameraClient client) : IWebSocketMessageVisitor
    {
        public Task VisitAsync(BinaryWebSocketMessage message)
        {
            client.FramesReceived++;
            client.FrameReceived?.Invoke(message.Payload);
            return Task.CompletedTask;
        }

        public Task VisitAsync(TextWebSocketMessage message)
        {
            client.TextReceived?.Invoke(message.Text);
            return Task.CompletedTask;
        }
    }
}
