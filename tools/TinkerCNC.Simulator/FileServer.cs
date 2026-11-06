using TinkerCNC.Protocol;

namespace TinkerCNC.Simulator;

/// <summary>
/// The machine side of one file transfer; packets B1–B5 from the host go to the active one. Like the firmware, the
/// machine reads nothing else while a transfer runs.
/// </summary>
public interface IFileTransfer
{
    /// <summary>Whether it is over, so the machine reads commands again.</summary>
    bool IsOver => false;

    Task OnMd5Async(FileMd5 message) => Task.CompletedTask;
    Task OnViewAsync(FileView message) => Task.CompletedTask;
    Task OnDataAsync(FileData message) => Task.CompletedTask;
    void OnEnd() { }
    void OnCancel() { }
}

/// <summary>No transfer running: transfer packets are ignored.</summary>
public sealed class NoTransfer : IFileTransfer
{
    public static readonly NoTransfer Instance = new();

    public bool IsOver => true;
}

/// <summary>
/// Machine side of a download: answers the host's B2/B3 requests after "download &lt;path&gt;".
/// </summary>
public sealed class FileServer(byte[] content, IReplyChannel reply) : IFileTransfer
{
    private const int PacketSize = 4096;

    private int TotalPackets => Math.Max(1, (content.Length + PacketSize - 1) / PacketSize);

    public static Task<IFileTransfer> StartAsync(SimFileSystem files, string path, IReplyChannel reply)
    {
        var content = files.Read(path);
        Console.WriteLine($"[file] download {path} ({content?.Length ?? 0} bytes)");
        return content is null
            ? Refuse(reply)
            : Begin(new FileServer(content, reply), reply.SendAsync(new FileMd5(SimFileSystem.Md5Of(content)).ToPacket()));
    }

    public Task OnViewAsync(FileView message) => reply.SendAsync(new FileView(TotalPackets, PacketSize).ToPacket());

    public Task OnDataAsync(FileData message)
    {
        if (message.Sequence < 1 || message.Sequence > TotalPackets) return reply.SendAsync(new FileRetry().ToPacket());
        var chunk = content.Skip((message.Sequence - 1) * PacketSize).Take(PacketSize).ToArray();
        return reply.SendAsync(new FileData(message.Sequence, chunk).ToPacket());
    }

    public void OnEnd() => Console.WriteLine("[file] download finished");

    internal static async Task<IFileTransfer> Refuse(IReplyChannel reply)
    {
        await reply.SendAsync(new FileCancel().ToPacket());
        return NoTransfer.Instance;
    }

    internal static async Task<IFileTransfer> Begin(IFileTransfer transfer, Task firstReply)
    {
        await firstReply;
        return transfer;
    }
}

/// <summary>
/// Machine side of an upload after "upload &lt;path&gt;", like the firmware: wait for the MD5, ask for the view (empty
/// B2), then for each packet in turn (B3 with its number), store the file and answer B4. Refused (B5) while a program
/// plays.
/// </summary>
public sealed class FileReceiver(SimFileSystem files, string path, IReplyChannel reply) : IFileTransfer
{
    private readonly MemoryStream _content = new();
    private int _totalPackets;
    private int _next = 1;

    public bool IsOver { get; private set; }

    public static Task<IFileTransfer> StartAsync(SimContext context, string path, IReplyChannel reply)
    {
        Console.WriteLine($"[file] upload {path}");
        return context.Job.IsPlaying
            ? FileServer.Refuse(reply)
            : Task.FromResult<IFileTransfer>(new FileReceiver(context.Files, path, reply));
    }

    public Task OnMd5Async(FileMd5 message) => reply.SendAsync(FileView.Request.ToPacket());

    public Task OnViewAsync(FileView message)
    {
        _totalPackets = message.TotalPackets;
        return reply.SendAsync(FileData.RequestFor(_next).ToPacket());
    }

    public async Task OnDataAsync(FileData message)
    {
        if (message.Sequence != _next)
        {
            await reply.SendAsync(FileData.RequestFor(_next).ToPacket());
            return;
        }
        _content.Write(message.Data);
        if (_next++ < _totalPackets)
        {
            await reply.SendAsync(FileData.RequestFor(_next).ToPacket());
            return;
        }
        files.Write(path, _content.ToArray());
        IsOver = true;
        Console.WriteLine($"[file] stored {path} ({_content.Length} bytes)");
        await reply.SendAsync(new FileEnd().ToPacket());
    }

    public void OnCancel() => Console.WriteLine($"[file] upload of {path} cancelled");
}
