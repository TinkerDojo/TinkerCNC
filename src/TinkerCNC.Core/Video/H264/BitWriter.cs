namespace TinkerCNC.Video.H264;

/// <summary>Writes bits most significant first, with the Exp-Golomb codes of H.264 (ue(v) and se(v)).</summary>
public sealed class BitWriter
{
    private byte[] _data;
    private int _length;
    private int _current;
    private int _filled;

    public BitWriter(int capacity = 4096) => _data = new byte[Math.Max(16, capacity)];

    /// <summary>Bits written so far.</summary>
    public long BitCount => (long)_length * 8 + _filled;

    public bool IsByteAligned => _filled == 0;

    public void WriteBit(int bit)
    {
        _current = (_current << 1) | (bit & 1);
        if (++_filled == 8) Flush();
    }

    /// <summary>Writes the low <paramref name="bits"/> bits of <paramref name="value"/>, most significant first.</summary>
    public void Write(uint value, int bits)
    {
        if (bits is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(bits));
        while (bits > 0)
        {
            var take = Math.Min(8 - _filled, bits);
            var chunk = (int)((value >> (bits - take)) & ((1u << take) - 1));
            _current = (_current << take) | chunk;
            _filled += take;
            bits -= take;
            if (_filled == 8) Flush();
        }
    }

    /// <summary>Unsigned Exp-Golomb code: leading zeros, then value + 1 in binary.</summary>
    public void WriteUe(uint value)
    {
        var codeNum = (ulong)value + 1;
        var length = 64 - System.Numerics.BitOperations.LeadingZeroCount(codeNum);
        Write(0, length - 1);
        Write((uint)codeNum, length);
    }

    /// <summary>Signed Exp-Golomb code: positive values take the odd code numbers, negative the even ones.</summary>
    public void WriteSe(int value) => WriteUe(value > 0 ? (uint)(2L * value - 1) : (uint)(-2L * value));

    /// <summary>A stop bit and zero bits up to the next byte boundary.</summary>
    public void WriteTrailingBits()
    {
        WriteBit(1);
        while (!IsByteAligned) WriteBit(0);
    }

    public byte[] ToArray()
    {
        if (!IsByteAligned) throw new InvalidOperationException("The bit stream is not byte-aligned.");
        return _data.AsSpan(0, _length).ToArray();
    }

    public void Reset()
    {
        _length = 0;
        _current = 0;
        _filled = 0;
    }

    private void Flush()
    {
        if (_length == _data.Length) Array.Resize(ref _data, _data.Length * 2);
        _data[_length++] = (byte)_current;
        _current = 0;
        _filled = 0;
    }
}
