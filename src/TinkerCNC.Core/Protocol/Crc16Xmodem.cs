namespace TinkerCNC.Protocol;

/// <summary>
/// CRC-16/XMODEM (poly 0x1021, init 0x0000, no reflection, no final xor), computed over the type byte and the
/// payload of a packet. Standard table-driven implementation.
/// </summary>
public static class Crc16Xmodem
{
    private static readonly ushort[] Table = BuildTable();

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
            crc = (ushort)((crc << 8) ^ Table[((crc >> 8) ^ b) & 0xFF]);
        return crc;
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var c = (ushort)(i << 8);
            for (var bit = 0; bit < 8; bit++)
                c = (c & 0x8000) != 0 ? (ushort)((c << 1) ^ 0x1021) : (ushort)(c << 1);
            table[i] = c;
        }
        return table;
    }
}
