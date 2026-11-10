namespace TinkerCNC.Video.H264;

/// <summary>
/// The integer transforms of H.264: the 4×4 core transform and its inverse (clause 8.5.12), the 4×4 Hadamard of
/// the luma DC coefficients of an Intra_16x16 macroblock (8.5.10) and the 2×2 Hadamard of the chroma DC
/// coefficients (8.5.11). Blocks are 16 (or 4) values in raster order.
/// </summary>
internal static class Transform
{
    /// <summary>Forward core transform of a residual block.</summary>
    public static void Forward4x4(ReadOnlySpan<int> residual, Span<int> coefficients)
    {
        Span<int> rows = stackalloc int[16];
        for (var y = 0; y < 4; y++) ForwardRow(residual.Slice(y * 4, 4), rows.Slice(y * 4, 4));
        for (var x = 0; x < 4; x++)
        {
            var s03 = rows[x] + rows[12 + x];
            var d03 = rows[x] - rows[12 + x];
            var s12 = rows[4 + x] + rows[8 + x];
            var d12 = rows[4 + x] - rows[8 + x];
            coefficients[x] = s03 + s12;
            coefficients[4 + x] = 2 * d03 + d12;
            coefficients[8 + x] = s03 - s12;
            coefficients[12 + x] = d03 - 2 * d12;
        }
    }

    private static void ForwardRow(ReadOnlySpan<int> x, Span<int> y)
    {
        var s03 = x[0] + x[3];
        var d03 = x[0] - x[3];
        var s12 = x[1] + x[2];
        var d12 = x[1] - x[2];
        y[0] = s03 + s12;
        y[1] = 2 * d03 + d12;
        y[2] = s03 - s12;
        y[3] = d03 - 2 * d12;
    }

    /// <summary>Inverse core transform of scaled coefficients into a residual block, in place.</summary>
    public static void Inverse4x4(Span<int> block)
    {
        for (var y = 0; y < 4; y++) InverseFour(block, y * 4, 1);
        for (var x = 0; x < 4; x++) InverseFour(block, x, 4);
        for (var i = 0; i < 16; i++) block[i] = (block[i] + 32) >> 6;
    }

    private static void InverseFour(Span<int> d, int start, int step)
    {
        int d0 = d[start], d1 = d[start + step], d2 = d[start + 2 * step], d3 = d[start + 3 * step];
        var e0 = d0 + d2;
        var e1 = d0 - d2;
        var e2 = (d1 >> 1) - d3;
        var e3 = d1 + (d3 >> 1);
        d[start] = e0 + e3;
        d[start + step] = e1 + e2;
        d[start + 2 * step] = e1 - e2;
        d[start + 3 * step] = e0 - e3;
    }

    /// <summary>The 4×4 Hadamard transform, its own inverse up to scale; in place.</summary>
    public static void Hadamard4x4(Span<int> block)
    {
        for (var y = 0; y < 4; y++) HadamardFour(block, y * 4, 1);
        for (var x = 0; x < 4; x++) HadamardFour(block, x, 4);
    }

    private static void HadamardFour(Span<int> d, int start, int step)
    {
        int d0 = d[start], d1 = d[start + step], d2 = d[start + 2 * step], d3 = d[start + 3 * step];
        var s01 = d0 + d1;
        var d01 = d0 - d1;
        var s23 = d2 + d3;
        var d23 = d2 - d3;
        d[start] = s01 + s23;
        d[start + step] = s01 - s23;
        d[start + 2 * step] = d01 - d23;
        d[start + 3 * step] = d01 + d23;
    }

    /// <summary>The 2×2 Hadamard transform of the four chroma DC coefficients (raster order), in place.</summary>
    public static void Hadamard2x2(Span<int> dc)
    {
        int c0 = dc[0], c1 = dc[1], c2 = dc[2], c3 = dc[3];
        dc[0] = c0 + c1 + c2 + c3;
        dc[1] = c0 - c1 + c2 - c3;
        dc[2] = c0 + c1 - c2 - c3;
        dc[3] = c0 - c1 - c2 + c3;
    }
}
