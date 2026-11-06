using System.Buffers.Binary;
using System.Text;

namespace TinkerCNC.Protocol;

/// <summary>
/// One framed packet. Wire format (all multi-byte fields big-endian):
/// <code>
///   86 68 | LEN:u16 | TYPE:u8 | DATA[LEN-3] | CRC:u16 | 55 AA
/// </code>
/// LEN = 1 (type) + DATA.Length + 2 (crc). CRC-16/XMODEM covers LEN, TYPE and DATA.
/// Total frame size is LEN + 6.
/// </summary>
public sealed record Packet(PacketType Type, byte[] Data)
{
    public const ushort Header = 0x8668;
    public const ushort Footer = 0x55AA;

    /// <summary>Largest LEN accepted (1..0x2008).</summary>
    public const int MaxLength = 0x2008;

    public string Text => Encoding.UTF8.GetString(Data);

    public static Packet FromText(PacketType type, string text) => new(type, Encoding.UTF8.GetBytes(text));

    public byte[] Encode()
    {
        var len = Data.Length + 3;
        if (len > MaxLength)
            throw new ArgumentException($"Payload too large ({Data.Length} bytes).");

        var frame = new byte[len + 6];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), Header);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)len);
        frame[4] = (byte)Type;
        Data.CopyTo(frame, 5);
        var crc = Crc16Xmodem.Compute(frame.AsSpan(2, len)); // LEN(2) + TYPE(1) + DATA
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2 + len), crc);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4 + len), Footer);
        return frame;
    }

    /// <summary>Validates and decodes one complete frame (header, length, CRC and footer).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out Packet? packet)
    {
        packet = null;
        if (frame.Length < 9) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(frame) != Header) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(frame[^2..]) != Footer) return false;

        int len = BinaryPrimitives.ReadUInt16BigEndian(frame[2..]);
        if (len < 3 || frame.Length != len + 6) return false;

        var expected = BinaryPrimitives.ReadUInt16BigEndian(frame[(2 + len)..]);
        if (Crc16Xmodem.Compute(frame.Slice(2, len)) != expected) return false;

        packet = new Packet((PacketType)frame[4], frame.Slice(5, len - 3).ToArray());
        return true;
    }

    public override string ToString() => $"{Type} (0x{(byte)Type:X2}) [{Data.Length} bytes]";
}
