namespace TinkerCNC.Protocol;

/// <summary>
/// A packet received from the machine, typed by its <see cref="PacketType"/>.
/// Consumers handle the kinds they care about by implementing <see cref="IMachineMessageVisitor"/>
/// (or deriving from <see cref="MachineMessageVisitor"/>).
/// </summary>
public interface IMachineMessage
{
    private static readonly IReadOnlyDictionary<PacketType, Func<Packet, IMachineMessage>> Factories =
        new Dictionary<PacketType, Func<Packet, IMachineMessage>>
        {
            [PacketType.Status] = p => new StatusMessage(p, StatusReport.Parse(p.Text)),
            [PacketType.Diagnose] = p => new DiagnoseMessage(p),
            [PacketType.LoadInfo] = p => new LoadInfoMessage(p),
            [PacketType.LoadFinished] = p => new LoadFinishedMessage(p),
            [PacketType.LoadError] = p => new LoadErrorMessage(p),
            [PacketType.NormalInfo] = p => new InfoMessage(p),
            [PacketType.AlarmInfo] = p => new AlarmMessage(p),
            [PacketType.FileQuery] = p => new FileQueryReply(p),
            [PacketType.FileMd5] = FileMd5.Parse,
            [PacketType.FileView] = FileView.Parse,
            [PacketType.FileData] = FileData.Parse,
            [PacketType.FileEnd] = _ => new FileEnd(),
            [PacketType.FileCancel] = _ => new FileCancel(),
            [PacketType.FileRetry] = _ => new FileRetry(),
        };

    Packet Packet { get; }

    string Text { get; }

    void Accept(IMachineMessageVisitor visitor);

    static IMachineMessage From(Packet packet) =>
        Factories.TryGetValue(packet.Type, out var create) ? create(packet) : new UnknownMachineMessage(packet);
}

/// <summary>State shared by every machine message: the packet and its text payload.</summary>
public abstract record MachineMessage(Packet Packet) : IMachineMessage
{
    public string Text => Packet.Text;

    public abstract void Accept(IMachineMessageVisitor visitor);
}

/// <summary>0x81: status report.</summary>
public sealed record StatusMessage(Packet Packet, StatusReport Report) : MachineMessage(Packet)
{
    public override void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
}

/// <summary>0x82: reply to the "diagnose" poll.</summary>
public sealed record DiagnoseMessage(Packet Packet) : MachineMessage(Packet)
{
    public override void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
}

/// <summary>0x83: load progress info.</summary>
public sealed record LoadInfoMessage(Packet Packet) : MachineMessage(Packet)
{
    public override void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
}

/// <summary>0x84: load finished.</summary>
public sealed record LoadFinishedMessage(Packet Packet) : MachineMessage(Packet)
{
    public override void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
}

/// <summary>0x85: load error.</summary>
public sealed record LoadErrorMessage(Packet Packet) : MachineMessage(Packet)
{
    public override void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
}

/// <summary>0x90: normal text output (command replies, config values, …).</summary>
public sealed record InfoMessage(Packet Packet) : MachineMessage(Packet)
{
    public override void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
}

/// <summary>0x91: alarm text (also sent when the machine refuses a connection because too many clients are connected).</summary>
public sealed record AlarmMessage(Packet Packet) : MachineMessage(Packet)
{
    public override void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
}

/// <summary>0xB7: reply to a file query: the file the machine is running, if any.</summary>
public sealed record FileQueryReply(Packet Packet) : MachineMessage(Packet)
{
    public RunningFile? RunningFile => RunningFile.Parse(Text);

    public override void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
}

/// <summary>Any other packet type (file-transfer packets, unknown types).</summary>
public sealed record UnknownMachineMessage(Packet Packet) : MachineMessage(Packet)
{
    public override void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
}

public interface IMachineMessageVisitor
{
    void Visit(StatusMessage message);
    void Visit(DiagnoseMessage message);
    void Visit(LoadInfoMessage message);
    void Visit(LoadFinishedMessage message);
    void Visit(LoadErrorMessage message);
    void Visit(InfoMessage message);
    void Visit(AlarmMessage message);
    void Visit(FileQueryReply message);
    void Visit(FileMd5 message);
    void Visit(FileView message);
    void Visit(FileData message);
    void Visit(FileEnd message);
    void Visit(FileCancel message);
    void Visit(FileRetry message);
    void Visit(UnknownMachineMessage message);
}

/// <summary>Visitor with no-op defaults; override only the messages you handle.</summary>
public abstract class MachineMessageVisitor : IMachineMessageVisitor
{
    public virtual void Visit(StatusMessage message) { }
    public virtual void Visit(DiagnoseMessage message) { }
    public virtual void Visit(LoadInfoMessage message) { }
    public virtual void Visit(LoadFinishedMessage message) { }
    public virtual void Visit(LoadErrorMessage message) { }
    public virtual void Visit(InfoMessage message) { }
    public virtual void Visit(AlarmMessage message) { }
    public virtual void Visit(FileQueryReply message) { }
    public virtual void Visit(FileMd5 message) { }
    public virtual void Visit(FileView message) { }
    public virtual void Visit(FileData message) { }
    public virtual void Visit(FileEnd message) { }
    public virtual void Visit(FileCancel message) { }
    public virtual void Visit(FileRetry message) { }
    public virtual void Visit(UnknownMachineMessage message) { }
}
