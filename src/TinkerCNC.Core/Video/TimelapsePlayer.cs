using System.Net.WebSockets;
using System.Text;
using TinkerCNC.Localization;

namespace TinkerCNC.Video;

public enum TimelapseState { Idle, Connecting, Opening, Playing, Paused, Ended, Preempted, Error }

/// <summary>Metadata of an opened recording (from the "open" and "meta" replies).</summary>
public sealed record TimelapseInfo(
    string SessionId, int TotalFrames, int Width, int Height,
    double FramePeriodUs, double DurationMs, int FirstFrameIndex);

/// <summary>
/// Plays a time-lapse recording stored on the machine.
/// <para>
/// Endpoint <c>ws://&lt;ip&gt;:82/ws_preview</c>.
/// The client sends <see cref="OpenRequest"/>, the machine answers <see cref="OpenReply"/> and
/// <see cref="MetaReply"/>, and the client then sends <see cref="PlayRequest"/> from the first frame.
/// Frames arrive as binary messages, one encoded
/// image each. Only one viewer can use the channel at a time; a second one gets
/// <see cref="PreemptedReply"/>.
/// </para>
/// </summary>
public sealed class TimelapsePlayer : IAsyncDisposable
{
    public const int Port = 82;
    public const string Path = "/ws_preview";

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ReplyHandler _replies;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private int _seq;

    public TimelapsePlayer(string host)
    {
        Host = host;
        _replies = new ReplyHandler(this);
    }

    public string Host { get; }
    public Uri Uri => new($"ws://{Host}:{Port}{Path}");
    public string? FilePath { get; private set; }
    public TimelapseState State { get; private set; } = TimelapseState.Idle;
    public TimelapseInfo? Info { get; private set; }
    public Text? LastError { get; private set; }
    public int FramesReceived { get; private set; }
    /// <summary>Index of the last displayed frame (start frame + frames received since play).</summary>
    public int CurrentFrame { get; private set; }

    public double Progress => Info is { TotalFrames: > 0 } i ? Math.Clamp((double)(CurrentFrame - i.FirstFrameIndex) / i.TotalFrames, 0, 1) : 0;

    public event Action<byte[]>? FrameReceived;
    public event Action? Changed;
    /// <summary>Every parsed reply, for diagnostics.</summary>
    public event Action<IPreviewReply>? ReplyReceived;

    public async Task OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await CloseSocketAsync().ConfigureAwait(false);
        FilePath = filePath;
        Info = null;
        LastError = null;
        FramesReceived = 0;
        SetState(TimelapseState.Connecting);

        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        _ws.Options.Proxy = null;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            await _ws.ConnectAsync(Uri, connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(Localization.Text.Of(ex));
            throw;
        }

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_ws, _cts.Token));
        SetState(TimelapseState.Opening);
        await SendAsync(new OpenRequest(filePath) { Seq = NextSeq() }, cancellationToken).ConfigureAwait(false);
    }

    public Task PlayAsync(int? fromFrame = null, CancellationToken cancellationToken = default)
    {
        var info = RequireInfo();
        var from = fromFrame ?? info.FirstFrameIndex;
        CurrentFrame = from;
        return SendAsync(new PlayRequest(info.SessionId, from) { Seq = NextSeq() }, cancellationToken);
    }

    /// <summary>Seeks by restarting playback at <paramref name="fraction"/> of the recording.</summary>
    public Task SeekAsync(double fraction, CancellationToken cancellationToken = default)
    {
        var info = RequireInfo();
        var frame = info.FirstFrameIndex + (int)Math.Round(Math.Clamp(fraction, 0, 1) * Math.Max(0, info.TotalFrames - 1));
        return PlayAsync(frame, cancellationToken);
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(new PauseRequest(RequireInfo().SessionId) { Seq = NextSeq() }, cancellationToken).ConfigureAwait(false);
        SetState(TimelapseState.Paused);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(new ResumeRequest(RequireInfo().SessionId) { Seq = NextSeq() }, cancellationToken).ConfigureAwait(false);
        SetState(TimelapseState.Playing);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Info is { } info && _ws?.State == WebSocketState.Open)
        {
            try { await SendAsync(new StopRequest(info.SessionId) { Seq = NextSeq() }, cancellationToken).ConfigureAwait(false); }
            catch { /* closing anyway */ }
        }
        await CloseSocketAsync().ConfigureAwait(false);
        SetState(TimelapseState.Idle);
    }

    private TimelapseInfo RequireInfo() => Info ?? throw new LocalizedException(CoreTexts.Of("Timelapse.NotOpen"));

    private int NextSeq() => Interlocked.Increment(ref _seq);

    private async Task SendAsync(IPreviewRequest request, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) throw new LocalizedException(CoreTexts.Of("Timelapse.NotConnected"));
        var bytes = Encoding.UTF8.GetBytes(PreviewJson.Serialize(request));

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            await foreach (var message in WebSocketMessages.ReadAsync(ws, ct).ConfigureAwait(false))
                await message.AcceptAsync(_replies).ConfigureAwait(false);

            if (State is TimelapseState.Playing or TimelapseState.Opening) SetState(TimelapseState.Ended);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fail(Localization.Text.Of(ex));
        }
    }

    private void Fail(Text message)
    {
        LastError = message;
        SetState(TimelapseState.Error);
    }

    private void SetState(TimelapseState state)
    {
        State = state;
        Changed?.Invoke();
    }

    private async Task CloseSocketAsync()
    {
        _cts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, t.Token).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* ignore */ }
        }
        _ws?.Dispose();
        _ws = null;
        _cts?.Dispose();
        _cts = null;
        _receiveLoop = null;
        Info = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Handles frames and JSON replies arriving on the channel.</summary>
    private sealed class ReplyHandler(TimelapsePlayer player) : IWebSocketMessageVisitor, IPreviewReplyVisitor
    {
        public Task VisitAsync(BinaryWebSocketMessage message)
        {
            player.FramesReceived++;
            player.CurrentFrame++;
            player.FrameReceived?.Invoke(message.Payload);
            return Task.CompletedTask;
        }

        public Task VisitAsync(TextWebSocketMessage message)
        {
            var reply = IPreviewReply.Parse(message.Text);
            player.ReplyReceived?.Invoke(reply);
            return reply.AcceptAsync(this);
        }

        public Task VisitAsync(OpenReply reply)
        {
            player.Info = new TimelapseInfo(reply.SessionId, reply.TotalFrames, reply.Width, reply.Height,
                player.Info?.FramePeriodUs ?? 0, player.Info?.DurationMs ?? 0, player.Info?.FirstFrameIndex ?? 0);
            player.Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task VisitAsync(MetaReply reply)
        {
            var current = player.Info ?? new TimelapseInfo(reply.SessionId ?? "", 0, 0, 0, 0, 0, 0);
            player.Info = current with
            {
                SessionId = reply.SessionId ?? current.SessionId,
                TotalFrames = reply.TotalFrames > 0 ? reply.TotalFrames : current.TotalFrames,
                FramePeriodUs = reply.FramePeriodUs,
                DurationMs = reply.DurationMs,
                FirstFrameIndex = reply.FirstFrameIndex,
            };
            // Start playing as soon as the metadata is known.
            return player.PlayAsync();
        }

        public Task VisitAsync(PlayReply reply)
        {
            player.CurrentFrame = reply.FromFrame ?? player.CurrentFrame;
            player.SetState(TimelapseState.Playing);
            return Task.CompletedTask;
        }

        public Task VisitAsync(PauseReply reply)
        {
            player.SetState(TimelapseState.Paused);
            return Task.CompletedTask;
        }

        public Task VisitAsync(EosReply reply)
        {
            player.SetState(TimelapseState.Ended);
            return Task.CompletedTask;
        }

        public Task VisitAsync(PreemptedReply reply)
        {
            player.LastError = CoreTexts.Of("Timelapse.Preempted");
            player.SetState(TimelapseState.Preempted);
            return Task.CompletedTask;
        }

        public Task VisitAsync(ErrorReply reply)
        {
            player.Fail(reply.Text);
            return Task.CompletedTask;
        }

        public Task VisitAsync(UnknownReply reply) => Task.CompletedTask;
    }
}
