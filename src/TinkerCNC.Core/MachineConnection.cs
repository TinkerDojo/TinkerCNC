using System.Text;
using TinkerCNC.Protocol;
using TinkerCNC.Transport;
using TinkerCNC.Localization;

namespace TinkerCNC;

/// <summary>
/// Connection to one machine: opens the transport, frames outgoing <see cref="IHostMessage"/>s,
/// reassembles incoming packets into <see cref="IMachineMessage"/>s and polls status.
/// </summary>
public sealed class MachineConnection : IAsyncDisposable
{
    private readonly IMachineTransport _transport;
    private readonly PacketReader _reader = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly EventRaiser _raiser;
    private Stream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private Task? _pollLoop;
    private int _pollSuspensions;

    public MachineConnection(IMachineTransport transport)
    {
        _transport = transport;
        _raiser = new EventRaiser(this);
    }

    /// <summary>Interval for '?' status polls; set to null to disable.</summary>
    public TimeSpan? StatusPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public bool IsConnected => _stream is not null && _cts is { IsCancellationRequested: false };
    public DateTime LastDataUtc { get; private set; }
    public StatusReport? LastStatus { get; private set; }
    public string Description => _transport.Description;

    public event Action<Packet>? PacketReceived;
    /// <summary>Every decoded message; use <see cref="IMachineMessage.Accept"/> to handle specific kinds.</summary>
    public event Action<IMachineMessage>? MessageReceived;
    public event Action<StatusReport>? StatusReceived;
    /// <summary>NormalInfo (0x90) text: command replies, config values, etc.</summary>
    public event Action<string>? InfoReceived;
    /// <summary>AlarmInfo (0x91) text.</summary>
    public event Action<string>? AlarmReceived;
    public event Action<Exception?>? Disconnected;

    public static MachineConnection ForAddress(string address) => new(TcpTransport.Parse(address));
    public static MachineConnection ForSerialPort(string portName) => new(new SerialTransport(portName));

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected) throw new InvalidOperationException("Already connected.");
        _stream = await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        _reader.Reset();
        _cts = new CancellationTokenSource();
        LastDataUtc = DateTime.UtcNow;
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        if (StatusPollInterval is { } interval)
            _pollLoop = Task.Run(() => PollLoopAsync(interval, _cts.Token));
    }

    /// <summary>Sends a text command line (0xA2), e.g. "G0 X10", "config-get sd wifi.machine_name".</summary>
    public Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
        => SendAsync(new TextCommand(command), cancellationToken);

    /// <summary>Sends one real-time character (0xA1).</summary>
    public Task SendRealtimeAsync(RealtimeCommand command, CancellationToken cancellationToken = default)
        => SendAsync(command, cancellationToken);

    public Task RequestStatusAsync(CancellationToken cancellationToken = default)
        => SendAsync(RealtimeCommand.StatusQuery, cancellationToken);

    /// <summary>Sends a command and returns the next NormalInfo text (best effort; replies are not correlated by id).</summary>
    public async Task<string> QueryAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
        => (await RequestAsync<InfoMessage>(new TextCommand(command), timeout, cancellationToken).ConfigureAwait(false)).Text;

    /// <summary>Asks the machine which file it is running (0xB7). Null when it reports none.</summary>
    public async Task<RunningFile?> QueryRunningFileAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => (await RequestAsync<FileQueryReply>(new FileQueryRequest(), timeout, cancellationToken).ConfigureAwait(false)).RunningFile;

    /// <summary>Sends <paramref name="request"/> and returns the next message of type <typeparamref name="TReply"/>.</summary>
    public async Task<TReply> RequestAsync<TReply>(IHostMessage request, TimeSpan timeout, CancellationToken cancellationToken = default)
        where TReply : IMachineMessage
    {
        var tcs = new TaskCompletionSource<TReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(IMachineMessage message)
        {
            if (message is TReply reply) tcs.TrySetResult(reply);
        }

        MessageReceived += Handler;
        try
        {
            await SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await using var _ = cts.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            MessageReceived -= Handler;
        }
    }

    /// <summary>
    /// Pauses the '?' status polls until the returned handle is disposed, so they do not interleave with a file
    /// transfer.
    /// </summary>
    public IDisposable SuspendStatusPolling()
    {
        Interlocked.Increment(ref _pollSuspensions);
        return new PollingSuspension(this);
    }

    public async Task SendAsync(IHostMessage message, CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new LocalizedException(CoreTexts.Of("Connection.NotConnected"));
        var frame = message.ToPacket().Encode();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        Exception? error = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await _stream!.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break; // remote closed
                LastDataUtc = DateTime.UtcNow;
                foreach (var packet in _reader.Feed(buffer.AsSpan(0, n)))
                {
                    PacketReceived?.Invoke(packet);
                    var message = IMachineMessage.From(packet);
                    message.Accept(_raiser);
                    MessageReceived?.Invoke(message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (!ct.IsCancellationRequested)
        {
            _cts?.Cancel();
            Disconnected?.Invoke(error);
        }
    }

    private async Task PollLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            await RequestStatusAsync(ct).ConfigureAwait(false); // first status right away, not after one interval
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _pollSuspensions) == 0)
                    await RequestStatusAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Write failures surface through the read loop / Disconnected.
        }
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_readLoop is not null) await SafeWait(_readLoop).ConfigureAwait(false);
        if (_pollLoop is not null) await SafeWait(_pollLoop).ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _stream = null;
        _cts?.Dispose();
        _cts = null;
    }

    private static async Task SafeWait(Task t)
    {
        try { await t.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* ignored on shutdown */ }
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());

    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes) sb.Append(b.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    private sealed class PollingSuspension(MachineConnection machine) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref machine._pollSuspensions);
        }
    }

    /// <summary>Maps typed messages onto the convenience events.</summary>
    private sealed class EventRaiser(MachineConnection machine) : MachineMessageVisitor
    {
        public override void Visit(StatusMessage message)
        {
            machine.LastStatus = message.Report;
            machine.StatusReceived?.Invoke(message.Report);
        }

        public override void Visit(InfoMessage message) => machine.InfoReceived?.Invoke(message.Text);

        public override void Visit(AlarmMessage message) => machine.AlarmReceived?.Invoke(message.Text);
    }
}
