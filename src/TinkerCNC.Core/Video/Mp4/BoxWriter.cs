using System.Buffers.Binary;
using System.Text;

namespace TinkerCNC.Video.Mp4;

/// <summary>Writes nested ISO base media file format boxes (big-endian) into memory, patching each size on close.</summary>
internal sealed class BoxWriter
{
    private readonly MemoryStream _stream = new();
    private readonly Stack<long> _open = new();

    public void Begin(string type)
    {
        _open.Push(_stream.Position);
        WriteUInt32(0);
        WriteType(type);
    }

    /// <summary>A box with a version byte and 24 flag bits.</summary>
    public void BeginFull(string type, byte version, uint flags)
    {
        Begin(type);
        WriteUInt32(((uint)version << 24) | (flags & 0xFFFFFF));
    }

    public void End()
    {
        var start = _open.Pop();
        var end = _stream.Position;
        _stream.Position = start;
        WriteUInt32((uint)(end - start));
        _stream.Position = end;
    }

    public void WriteType(string type)
    {
        if (type.Length != 4) throw new ArgumentException("A box type has four characters.", nameof(type));
        WriteBytes(Encoding.ASCII.GetBytes(type));
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    public void WriteZeros(int count)
    {
        for (var i = 0; i < count; i++) _stream.WriteByte(0);
    }

    public void WriteUInt8(byte value) => _stream.WriteByte(value);

    public void WriteUInt16(ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        _stream.Write(b);
    }

    public void WriteInt16(short value) => WriteUInt16((ushort)value);

    public void WriteUInt32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        _stream.Write(b);
    }

    public void WriteUInt64(ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        _stream.Write(b);
    }

    /// <summary>A 16.16 fixed-point number.</summary>
    public void WriteFixed(int integer) => WriteUInt32((uint)integer << 16);

    /// <summary>The identity transformation matrix of movie and track headers.</summary>
    public void WriteIdentityMatrix()
    {
        WriteUInt32(0x00010000); WriteUInt32(0); WriteUInt32(0);
        WriteUInt32(0); WriteUInt32(0x00010000); WriteUInt32(0);
        WriteUInt32(0); WriteUInt32(0); WriteUInt32(0x40000000);
    }

    public byte[] ToArray()
    {
        if (_open.Count > 0) throw new InvalidOperationException("A box is still open.");
        return _stream.ToArray();
    }
}
