using System.Buffers.Binary;
using System.Text;

namespace TinkerCNC.Protocol;

/// <summary>
/// Packets of the XMODEM-like file transfer (types 0xB1–0xB6). They have the same layout in both
/// directions, so each record is both an <see cref="IMachineMessage"/> and an <see cref="IHostMessage"/>.
/// An empty payload is a request ("send me the MD5 / the view / packet n").
/// <para>
/// Download sequence:
/// host sends 0xB0 "download &lt;path&gt;" → machine B1 (MD5) → host B2 (empty, request view)
/// → machine B2 (total_packets:u32, packet_size:u16) → host B3 (seq:u32, starting at 1)
/// → machine B3 (seq:u32 + data) → … → host B4 after the last packet.
/// B6 from the machine asks to resend the last request, B5 cancels. All integers are big-endian.
/// </para>
/// </summary>
public static class FileTransferPayload
{
    public static byte[] UInt32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    public static int ReadUInt32(ReadOnlySpan<byte> data) => data.Length >= 4 ? BinaryPrimitives.ReadInt32BigEndian(data) : 0;

    public static int ReadUInt16(ReadOnlySpan<byte> data) => data.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(data) : 0;
}

/// <summary>0xB1: MD5 of the file (hex text). Empty = request.</summary>
public sealed record FileMd5(string Md5) : IMachineMessage, IHostMessage
{
    public static readonly FileMd5 Request = new(string.Empty);

    public static FileMd5 Parse(Packet packet) => new(Encoding.ASCII.GetString(packet.Data).Trim('\0', ' ', '\r', '\n'));

    public Packet Packet => ToPacket();
    public string Text => Md5;
    public Packet ToPacket() => Packet.FromText(PacketType.FileMd5, Md5);
    public void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
    public Task AcceptAsync(IHostMessageVisitor visitor) => visitor.VisitAsync(this);
}

/// <summary>0xB2: file "view": number of data packets and the packet size. Empty = request.</summary>
public sealed record FileView(int TotalPackets, int PacketSize) : IMachineMessage, IHostMessage
{
    private const int EncodedLength = 6;

    public static readonly FileView Request = new(0, 0);

    public bool IsRequest => TotalPackets == 0 && PacketSize == 0;

    public static FileView Parse(Packet packet) =>
        packet.Data.Length < EncodedLength
            ? Request
            : new(FileTransferPayload.ReadUInt32(packet.Data), FileTransferPayload.ReadUInt16(packet.Data.AsSpan(4)));

    public Packet Packet => ToPacket();
    public string Text => $"{TotalPackets} packets × {PacketSize} bytes";

    public Packet ToPacket()
    {
        if (IsRequest) return new Packet(PacketType.FileView, Array.Empty<byte>());
        var data = new byte[EncodedLength];
        BinaryPrimitives.WriteInt32BigEndian(data, TotalPackets);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)PacketSize);
        return new Packet(PacketType.FileView, data);
    }

    public void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
    public Task AcceptAsync(IHostMessageVisitor visitor) => visitor.VisitAsync(this);
}

/// <summary>0xB3: data packet <paramref name="Sequence"/> (1-based). Without data it is a request for that packet.</summary>
public sealed record FileData(int Sequence, byte[] Data) : IMachineMessage, IHostMessage
{
    public static FileData RequestFor(int sequence) => new(sequence, Array.Empty<byte>());

    public static FileData Parse(Packet packet) =>
        new(FileTransferPayload.ReadUInt32(packet.Data), packet.Data.Length > 4 ? packet.Data[4..] : Array.Empty<byte>());

    public Packet Packet => ToPacket();
    public string Text => $"#{Sequence} ({Data.Length} bytes)";
    public Packet ToPacket() => new(PacketType.FileData, [.. FileTransferPayload.UInt32(Sequence), .. Data]);
    public void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
    public Task AcceptAsync(IHostMessageVisitor visitor) => visitor.VisitAsync(this);
}

/// <summary>0xB4: transfer finished.</summary>
public sealed record FileEnd : IMachineMessage, IHostMessage
{
    public Packet Packet => ToPacket();
    public string Text => "end";
    public Packet ToPacket() => new(PacketType.FileEnd, Array.Empty<byte>());
    public void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
    public Task AcceptAsync(IHostMessageVisitor visitor) => visitor.VisitAsync(this);
}

/// <summary>0xB5: transfer cancelled.</summary>
public sealed record FileCancel : IMachineMessage, IHostMessage
{
    public Packet Packet => ToPacket();
    public string Text => "cancel";
    public Packet ToPacket() => new(PacketType.FileCancel, Array.Empty<byte>());
    public void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
    public Task AcceptAsync(IHostMessageVisitor visitor) => visitor.VisitAsync(this);
}

/// <summary>0xB6: the other side asks to resend the last request.</summary>
public sealed record FileRetry : IMachineMessage, IHostMessage
{
    public Packet Packet => ToPacket();
    public string Text => "retry";
    public Packet ToPacket() => new(PacketType.FileRetry, Array.Empty<byte>());
    public void Accept(IMachineMessageVisitor visitor) => visitor.Visit(this);
    public Task AcceptAsync(IHostMessageVisitor visitor) => visitor.VisitAsync(this);
}

/// <summary>
/// The job the machine is running, from the 0xB7 file-query reply <c>"&lt;path&gt;|&lt;md5&gt;"</c>. The MD5 lets a
/// client skip downloading a file it already has.
/// </summary>
public sealed record RunningFile(string Path, string? Md5)
{
    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];

    public static RunningFile? Parse(string reply)
    {
        var parts = reply.Trim().Split('|');
        return parts[0].Length == 0 ? null : new RunningFile(parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }
}
