namespace TinkerCNC.Protocol;

/// <summary>
/// A packet sent from the host to the machine. <see cref="MachineConnection"/> sends these;
/// a machine-side implementation (such as the simulator) decodes them with
/// <see cref="From"/> and handles them through <see cref="IHostMessageVisitor"/>.
/// </summary>
public interface IHostMessage
{
    private static readonly IReadOnlyDictionary<PacketType, Func<Packet, IHostMessage>> Factories =
        new Dictionary<PacketType, Func<Packet, IHostMessage>>
        {
            [PacketType.ControlSingle] = p => new RealtimeCommand(p.Data.Length > 0 ? (char)p.Data[0] : '\0'),
            [PacketType.ControlMulti] = p => new TextCommand(p.Text),
            [PacketType.FileTransferCommand] = p => new FileTransferCommand(p.Text),
            [PacketType.FileQuery] = _ => new FileQueryRequest(),
            [PacketType.FileMd5] = FileMd5.Parse,
            [PacketType.FileView] = FileView.Parse,
            [PacketType.FileData] = FileData.Parse,
            [PacketType.FileEnd] = _ => new FileEnd(),
            [PacketType.FileCancel] = _ => new FileCancel(),
            [PacketType.FileRetry] = _ => new FileRetry(),
        };

    Packet ToPacket();

    Task AcceptAsync(IHostMessageVisitor visitor);

    static IHostMessage From(Packet packet) =>
        Factories.TryGetValue(packet.Type, out var create) ? create(packet) : new UnknownHostMessage(packet);
}

/// <summary>0xA1: one real-time character ('?', '!', '~', Ctrl-X).</summary>
public sealed record RealtimeCommand(char Character) : IHostMessage
{
    public static readonly RealtimeCommand StatusQuery = new('?');
    public static readonly RealtimeCommand FeedHold = new('!');
    public static readonly RealtimeCommand CycleStart = new('~');
    public static readonly RealtimeCommand Reset = new('\x18');

    public Packet ToPacket() => new(PacketType.ControlSingle, new[] { (byte)Character });
    public Task AcceptAsync(IHostMessageVisitor visitor) => visitor.VisitAsync(this);
}

/// <summary>0xA2: a text command line, sent as-is (no newline is appended).</summary>
public sealed record TextCommand(string Text) : IHostMessage
{
    public Packet ToPacket() => Packet.FromText(PacketType.ControlMulti, Text);
    public Task AcceptAsync(IHostMessageVisitor visitor) => visitor.VisitAsync(this);
}

/// <summary>
/// 0xB0: file-transfer command, sent as-is without a newline: "download &lt;path&gt;" or "upload &lt;path&gt;"
/// (spaces in the path encoded, see <see cref="Transfer.RemotePath"/>).
/// </summary>
public sealed record FileTransferCommand(string Text) : IHostMessage
{
    public static FileTransferCommand Download(string remotePath) => new($"download {Transfer.RemotePath.Encode(remotePath)}");

    public static FileTransferCommand Upload(string remotePath) => new($"upload {Transfer.RemotePath.Encode(remotePath)}");

    public Packet ToPacket() => Packet.FromText(PacketType.FileTransferCommand, Text);

    public Task AcceptAsync(IHostMessageVisitor visitor) => visitor.VisitAsync(this);
}

/// <summary>0xB7 with an empty payload.</summary>
public sealed record FileQueryRequest : IHostMessage
{
    public Packet ToPacket() => new(PacketType.FileQuery, Array.Empty<byte>());
    public Task AcceptAsync(IHostMessageVisitor visitor) => visitor.VisitAsync(this);
}

public sealed record UnknownHostMessage(Packet Packet) : IHostMessage
{
    public Packet ToPacket() => Packet;
    public Task AcceptAsync(IHostMessageVisitor visitor) => visitor.VisitAsync(this);
}

public interface IHostMessageVisitor
{
    Task VisitAsync(RealtimeCommand message);
    Task VisitAsync(TextCommand message);
    Task VisitAsync(FileTransferCommand message);
    Task VisitAsync(FileQueryRequest message);
    Task VisitAsync(FileMd5 message);
    Task VisitAsync(FileView message);
    Task VisitAsync(FileData message);
    Task VisitAsync(FileEnd message);
    Task VisitAsync(FileCancel message);
    Task VisitAsync(FileRetry message);
    Task VisitAsync(UnknownHostMessage message);
}
