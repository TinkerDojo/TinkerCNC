using System.Buffers.Binary;

namespace TinkerCNC.Protocol;

/// <summary>
/// Reassembles packets from an arbitrary byte stream:
/// <list type="bullet">
/// <item>discard bytes until the 0x8668 header,</item>
/// <item>wait for LEN + 6 bytes,</item>
/// <item>invalid footer: drop the 2 header bytes,</item>
/// <item>bad CRC/format: drop the header byte and resync.</item>
/// </list>
/// </summary>
public sealed class PacketReader
{
    private readonly List<byte> _buffer = new();

    public int DroppedFrames { get; private set; }

    public IEnumerable<Packet> Feed(ReadOnlySpan<byte> bytes)
    {
        _buffer.AddRange(bytes.ToArray());
        var result = new List<Packet>();

        while (true)
        {
            // Resync on header.
            var start = 0;
            while (start + 1 < _buffer.Count && !(_buffer[start] == 0x86 && _buffer[start + 1] == 0x68))
                start++;
            if (start > 0) _buffer.RemoveRange(0, start);
            if (_buffer.Count < 4) break;

            int len = (_buffer[2] << 8) | _buffer[3];
            var total = len + 6;
            if (len < 3 || len > Packet.MaxLength)
            {
                DroppedFrames++;
                _buffer.RemoveAt(0);
                continue;
            }
            if (_buffer.Count < total) break;

            var frame = _buffer.GetRange(0, total).ToArray();
            if (BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(total - 2)) != Packet.Footer)
            {
                DroppedFrames++;
                _buffer.RemoveRange(0, 2);
                continue;
            }

            if (Packet.TryDecode(frame, out var packet))
            {
                result.Add(packet!);
                _buffer.RemoveRange(0, total);
            }
            else
            {
                DroppedFrames++;
                _buffer.RemoveAt(0);
            }
        }

        return result;
    }

    public void Reset() => _buffer.Clear();
}
