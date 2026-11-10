namespace TinkerCNC.Video.H264;

/// <summary>Network abstraction layer units: a header byte and a payload in which byte patterns that could be
/// mistaken for a start code are broken up with emulation prevention bytes (0x03).</summary>
public static class NalUnit
{
    public const int Slice = 1;
    public const int IdrSlice = 5;
    public const int SequenceParameterSet = 7;
    public const int PictureParameterSet = 8;

    /// <summary>Reference pictures and parameter sets carry the highest priority.</summary>
    public const int HighPriority = 3;

    public static byte[] Pack(int referenceIdc, int type, ReadOnlySpan<byte> rbsp)
    {
        var output = new List<byte>(rbsp.Length + 8) { (byte)((referenceIdc << 5) | type) };
        var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3)
            {
                output.Add(0x03);
                zeros = 0;
            }
            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return output.ToArray();
    }
}
