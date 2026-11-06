using System.Net;
using System.Net.Sockets;
using TinkerCNC.Protocol;
using TinkerCNC.Transport;

namespace TinkerCNC.Simulator;

/// <summary>TCP 2222 control port: decodes framed packets and handles them in a <see cref="ControlSession"/>.</summary>
public sealed class ControlServer(SimContext context)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, TcpTransport.DefaultPort);
        listener.Start();
        while (!ct.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(ct);
            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        var machine = context.Machine;
        machine.ClientConnected();
        Console.WriteLine($"[tcp] client connected {client.Client.RemoteEndPoint}");
        using var _ = client;
        var session = new ControlSession(context, client.GetStream());
        var reader = new PacketReader();
        var buffer = new byte[4096];
        try
        {
            int n;
            while ((n = await client.GetStream().ReadAsync(buffer, ct)) > 0)
                foreach (var packet in reader.Feed(buffer.AsSpan(0, n)))
                    await IHostMessage.From(packet).AcceptAsync(session);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
        }
        finally
        {
            machine.ClientDisconnected();
            Console.WriteLine("[tcp] client disconnected");
        }
    }
}

/// <summary>Handles the messages of one connected client.</summary>
public sealed class ControlSession : IHostMessageVisitor, IReplyChannel
{
    private readonly SimContext _context;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private IFileTransfer _transfer = NoTransfer.Instance;

    private static readonly IReadOnlyDictionary<char, Action<SimContext>> Realtime = new Dictionary<char, Action<SimContext>>
    {
        [RealtimeCommand.FeedHold.Character] = c => c.Machine.Transition("Run", "Hold"),
        [RealtimeCommand.CycleStart.Character] = c => c.Machine.Transition("Hold", c.Job.IsPlaying ? "Run" : "Idle"),
        [RealtimeCommand.Reset.Character] = c => { c.Job.Stop(); c.Machine.Reset(); },
    };

    // 0xB0 commands by their first word; the rest is the path (spaces sent as 0x01).
    private static readonly IReadOnlyDictionary<string, Func<SimContext, string, IReplyChannel, Task<IFileTransfer>>> Transfers =
        new Dictionary<string, Func<SimContext, string, IReplyChannel, Task<IFileTransfer>>>
        {
            ["download"] = (c, path, reply) => FileServer.StartAsync(c.Files, path, reply),
            ["upload"] = FileReceiver.StartAsync,
        };

    public ControlSession(SimContext context, Stream stream)
    {
        _context = context;
        _stream = stream;
    }

    private SimMachine Machine => _context.Machine;

    private bool Transferring => !_transfer.IsOver;

    private Task DroppedAsync(string what)
    {
        Console.WriteLine($"[tcp] {what} dropped: a file transfer is running");
        return Task.CompletedTask;
    }

    public Task VisitAsync(RealtimeCommand message)
    {
        if (Transferring) return DroppedAsync($"realtime 0x{(int)message.Character:X2}");
        if (message == RealtimeCommand.StatusQuery)
            return this.SendAsync(PacketType.Status, Machine.StatusReport(_context.Job.StatusField));

        Console.WriteLine($"[tcp] realtime 0x{(int)message.Character:X2}");
        Realtime.GetValueOrDefault(message.Character)?.Invoke(_context);
        return Task.CompletedTask;
    }

    public Task VisitAsync(TextCommand message)
    {
        var command = message.Text.Trim();
        if (Transferring) return DroppedAsync($"command '{TinkerCNC.Transfer.RemotePath.Decode(command)}'");
        Console.WriteLine($"[tcp] command '{TinkerCNC.Transfer.RemotePath.Decode(command)}'");
        return TextCommandHandlers.All.First(h => h.Matches(command, _context)).HandleAsync(command, _context, this);
    }

    public async Task VisitAsync(FileTransferCommand message)
    {
        var words = message.Text.Trim().Split(' ', 2);
        var path = words.Length > 1 ? TinkerCNC.Transfer.RemotePath.Decode(words[1].Trim()) : "";
        _transfer = Transfers.TryGetValue(words[0], out var start)
            ? await start(_context, path, this)
            : await FileServer.Refuse(this);
    }

    /// <summary>0xB7: "path|md5" of the running job, empty when idle.</summary>
    public Task VisitAsync(FileQueryRequest message)
    {
        if (Transferring) return DroppedAsync("running-file query");
        var job = _context.Job;
        var reply = _context.Options.QueryReportsMd5 ? $"{job.Path}|{job.Md5}" : job.Path!;
        return this.SendAsync(PacketType.FileQuery, job.IsPlaying ? reply : "");
    }

    public Task VisitAsync(FileMd5 message) => _transfer.OnMd5Async(message);
    public Task VisitAsync(FileView message) => _transfer.OnViewAsync(message);
    public async Task VisitAsync(FileData message)
    {
        await _transfer.OnDataAsync(message);
        if (_transfer.IsOver) _transfer = NoTransfer.Instance;
    }

    public Task VisitAsync(FileEnd message)
    {
        _transfer.OnEnd();
        _transfer = NoTransfer.Instance;
        return Task.CompletedTask;
    }

    public Task VisitAsync(FileCancel message)
    {
        _transfer.OnCancel();
        _transfer = NoTransfer.Instance;
        return Task.CompletedTask;
    }

    public Task VisitAsync(FileRetry message) => Task.CompletedTask;

    public Task VisitAsync(UnknownHostMessage message)
    {
        Console.WriteLine($"[tcp] unhandled {message.Packet}");
        return Task.CompletedTask;
    }

    public async Task SendAsync(Packet packet)
    {
        await _writeLock.WaitAsync();
        try { await _stream.WriteAsync(packet.Encode()); }
        finally { _writeLock.Release(); }
    }
}

public interface IReplyChannel
{
    Task SendAsync(Packet packet);
}

public static class ReplyChannelExtensions
{
    public static Task SendAsync(this IReplyChannel channel, PacketType type, string text) =>
        channel.SendAsync(Packet.FromText(type, text));
}
