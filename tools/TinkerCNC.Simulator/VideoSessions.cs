using System.Net.WebSockets;
using System.Text;
using TinkerCNC.Video;

namespace TinkerCNC.Simulator;

public static class WebSocketEndpoint
{
    public static async Task RunAsync(HttpContext ctx, Func<WebSocket, Task> session)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        try { await session(ws); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    }
}

/// <summary>Sends JPEG frames on ws_video at 10 fps once the client sends "start_stream".</summary>
public sealed class LiveVideoSession(WebSocket ws, SimMachine machine, FrameRenderer renderer)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var first = await WebSocketMessages.ReadAsync(ws, ct).FirstOrDefaultAsync(ct);
        if (first is not TextWebSocketMessage { Text: LiveCameraClient.StartMessage })
        {
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, $"expected {LiveCameraClient.StartMessage}", ct);
            return;
        }

        Console.WriteLine("[video] start_stream");
        while (ws.State == WebSocketState.Open)
        {
            await ws.SendAsync(renderer.Live(machine), WebSocketMessageType.Binary, true, ct);
            await Task.Delay(100, ct);
        }
    }
}

/// <summary>Serves one recording over ws_preview using the vpreview request/reply contracts.</summary>
public sealed class PreviewSession(WebSocket ws, FrameRenderer renderer) : IWebSocketMessageVisitor, IPreviewRequestVisitor
{
    private const int TotalFrames = 150;
    private const double FramePeriodUs = 100_000;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly string _sessionId = "sim-" + Guid.NewGuid().ToString("N")[..8];
    private CancellationTokenSource? _playback;
    private int _frame;

    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var message in WebSocketMessages.ReadAsync(ws, ct))
            await message.AcceptAsync(this);
        _playback?.Cancel();
    }

    public Task VisitAsync(BinaryWebSocketMessage message) => Task.CompletedTask;

    public Task VisitAsync(TextWebSocketMessage message)
    {
        Console.WriteLine($"[preview] {message.Text}");
        return IPreviewRequest.Parse(message.Text)?.AcceptAsync(this) ?? Task.CompletedTask;
    }

    public async Task VisitAsync(OpenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            await ReplyAsync(new ErrorReply(2, "file not found", null));
            return;
        }
        await ReplyAsync(new OpenReply(_sessionId, TotalFrames, 640, 360));
        await ReplyAsync(new MetaReply(_sessionId, TotalFrames, FramePeriodUs, TotalFrames * FramePeriodUs / 1000, 0));
    }

    public async Task VisitAsync(PlayRequest request)
    {
        _frame = request.FromFrame;
        await ReplyAsync(new PlayReply(_sessionId, _frame));
        StartPlayback();
    }

    public async Task VisitAsync(PauseRequest request)
    {
        _playback?.Cancel();
        await ReplyAsync(new PauseReply(_sessionId));
    }

    public async Task VisitAsync(ResumeRequest request)
    {
        await ReplyAsync(new PlayReply(_sessionId, _frame));
        StartPlayback();
    }

    public async Task VisitAsync(StopRequest request)
    {
        _playback?.Cancel();
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stopped", CancellationToken.None);
    }

    private void StartPlayback()
    {
        _playback?.Cancel();
        _playback = new CancellationTokenSource();
        _ = PlayAsync(_playback.Token);
    }

    private async Task PlayAsync(CancellationToken ct)
    {
        try
        {
            while (_frame < TotalFrames)
            {
                await SendAsync(renderer.Timelapse(_frame++, TotalFrames), WebSocketMessageType.Binary, ct);
                await Task.Delay(TimeSpan.FromMicroseconds(FramePeriodUs), ct);
            }
            await ReplyAsync(new EosReply(_sessionId));
        }
        catch (OperationCanceledException) { }
    }

    private Task ReplyAsync(IPreviewReply reply) =>
        SendAsync(Encoding.UTF8.GetBytes(PreviewJson.Serialize(reply)), WebSocketMessageType.Text, CancellationToken.None);

    private async Task SendAsync(byte[] payload, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(payload, type, true, ct); }
        finally { _sendLock.Release(); }
    }
}
