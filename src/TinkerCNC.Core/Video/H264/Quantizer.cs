namespace TinkerCNC.Video.H264;

/// <summary>
/// Quantization of transform coefficients for one quantization parameter and the scaling the decoder applies
/// (H.264 clause 8.5.12.1 with flat weights, 8.5.10 for luma DC, 8.5.11.2 for chroma DC). Reconstruction in the
/// encoder goes through the same scaling, so it matches the decoder exactly.
/// </summary>
internal sealed class Quantizer
{
    public const int MaxQp = 51;

    // Multipliers of the forward quantization and the decoder's scaling factors, per QP % 6 and coefficient class.
    private static readonly int[,] Multiplier =
    {
        { 13107, 5243, 8066 }, { 11916, 4660, 7490 }, { 10082, 4194, 6554 },
        { 9362, 3647, 5825 }, { 8192, 3355, 5243 }, { 7282, 2893, 4559 },
    };

    private static readonly int[,] Scale =
    {
        { 10, 16, 13 }, { 11, 18, 14 }, { 13, 20, 16 }, { 14, 23, 18 }, { 16, 25, 20 }, { 18, 29, 23 },
    };

    // Class of each coefficient position (raster): both indices even 0, both odd 1, mixed 2.
    private static readonly int[] PositionClass = [0, 2, 0, 2, 2, 1, 2, 1, 0, 2, 0, 2, 2, 1, 2, 1];

    // Chroma QP for a luma QP (Table 8-15 with chroma_qp_index_offset 0).
    private static readonly int[] ChromaQp =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        29, 30, 31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38, 39, 39, 39, 39,
    ];

    private readonly int _rem;
    private readonly int _per;
    private readonly int _bits;
    private readonly int[] _multiplierAt = new int[16];
    private readonly int[] _scaleAt = new int[16];

    public Quantizer(int qp)
    {
        if (qp is < 0 or > MaxQp) throw new ArgumentOutOfRangeException(nameof(qp));
        Qp = qp;
        _rem = qp % 6;
        _per = qp / 6;
        _bits = 15 + _per;
        IntraRounding = (1 << _bits) / 3;
        InterRounding = (1 << _bits) / 6;
        for (var position = 0; position < 16; position++)
        {
            _multiplierAt[position] = Multiplier[_rem, PositionClass[position]];
            _scaleAt[position] = Scale[_rem, PositionClass[position]] << _per;
        }
    }

    public int Qp { get; }

    /// <summary>Rounding offset of intra blocks (a dead zone of one third).</summary>
    public int IntraRounding { get; }

    /// <summary>Rounding offset of inter blocks (a dead zone of one sixth).</summary>
    public int InterRounding { get; }

    public static int ChromaQpFor(int lumaQp) => ChromaQp[lumaQp];

    /// <summary>Quantizes one coefficient of a 4×4 block.</summary>
    public int Quantize(int coefficient, int position, int rounding) =>
        Level(coefficient, _multiplierAt[position], rounding, _bits);

    /// <summary>Quantizes a Hadamard-transformed luma DC coefficient of an Intra_16x16 macroblock.</summary>
    public int QuantizeLumaDc(int coefficient, int rounding) =>
        Level(coefficient, Multiplier[_rem, 0], 4 * rounding, _bits + 2);

    /// <summary>Quantizes a Hadamard-transformed chroma DC coefficient.</summary>
    public int QuantizeChromaDc(int coefficient, int rounding) =>
        Level(coefficient, Multiplier[_rem, 0], 2 * rounding, _bits + 1);

    /// <summary>The decoder's scaling of a 4×4 block coefficient.</summary>
    public int Scaled(int level, int position) => level * _scaleAt[position];

    /// <summary>The decoder's scaling of an inverse-transformed luma DC coefficient.</summary>
    public int ScaledLumaDc(int value)
    {
        var scaled = value * 16 * Scale[_rem, 0];
        return _per >= 6 ? scaled << (_per - 6) : (scaled + (1 << (5 - _per))) >> (6 - _per);
    }

    /// <summary>The decoder's scaling of an inverse-transformed chroma DC coefficient.</summary>
    public int ScaledChromaDc(int value) => ((value * 16 * Scale[_rem, 0]) << _per) >> 5;

    private static int Level(int coefficient, int multiplier, int rounding, int bits)
    {
        var magnitude = Math.Min(CavlcWriter.MaxLevel, (int)(((long)Math.Abs(coefficient) * multiplier + rounding) >> bits));
        return coefficient < 0 ? -magnitude : magnitude;
    }
}
