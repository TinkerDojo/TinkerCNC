namespace TinkerCNC.Video.H264;

/// <summary>
/// One intra prediction mode of a square block, predicted from the reconstructed samples above (p[x, −1]), to the
/// left (p[−1, y]) and in the corner (p[−1, −1]) of the block.
/// </summary>
internal interface IIntraPredictor
{
    /// <summary>Whether the mode can be used when the neighbours above and to the left are (not) available.</summary>
    bool CanPredict(bool above, bool left);

    /// <summary>Fills <paramref name="prediction"/> (size × size, raster order) for the block at (x, y) of the plane.</summary>
    void Predict(ReadOnlySpan<byte> plane, int stride, int x, int y, bool above, bool left, Span<byte> prediction);
}

/// <summary>The modes of a plane, in the order of their mode numbers in the bit stream.</summary>
internal static class IntraModes
{
    /// <summary>Intra_16x16: vertical, horizontal, DC, plane (clause 8.3.3).</summary>
    public static readonly IIntraPredictor[] Luma16x16 =
    [
        new VerticalPredictor(16), new HorizontalPredictor(16), new LumaDcPredictor(), new PlanePredictor(16, 5),
    ];

    /// <summary>Chroma of 4:2:0 macroblocks: DC, horizontal, vertical, plane (clause 8.3.4).</summary>
    public static readonly IIntraPredictor[] Chroma8x8 =
    [
        new ChromaDcPredictor(), new HorizontalPredictor(8), new VerticalPredictor(8), new PlanePredictor(8, 34),
    ];
}

internal sealed class VerticalPredictor(int size) : IIntraPredictor
{
    public bool CanPredict(bool above, bool left) => above;

    public void Predict(ReadOnlySpan<byte> plane, int stride, int x, int y, bool above, bool left, Span<byte> prediction)
    {
        var row = plane.Slice((y - 1) * stride + x, size);
        for (var j = 0; j < size; j++) row.CopyTo(prediction.Slice(j * size, size));
    }
}

internal sealed class HorizontalPredictor(int size) : IIntraPredictor
{
    public bool CanPredict(bool above, bool left) => left;

    public void Predict(ReadOnlySpan<byte> plane, int stride, int x, int y, bool above, bool left, Span<byte> prediction)
    {
        for (var j = 0; j < size; j++) prediction.Slice(j * size, size).Fill(plane[(y + j) * stride + x - 1]);
    }
}

/// <summary>The mean of the 16 samples above and the 16 to the left, whichever are available.</summary>
internal sealed class LumaDcPredictor : IIntraPredictor
{
    public bool CanPredict(bool above, bool left) => true;

    public void Predict(ReadOnlySpan<byte> plane, int stride, int x, int y, bool above, bool left, Span<byte> prediction)
    {
        var sum = 0;
        if (above) for (var i = 0; i < 16; i++) sum += plane[(y - 1) * stride + x + i];
        if (left) for (var j = 0; j < 16; j++) sum += plane[(y + j) * stride + x - 1];
        var dc = above && left ? (sum + 16) >> 5 : above || left ? (sum + 8) >> 4 : 128;
        prediction.Fill((byte)dc);
    }
}

/// <summary>DC prediction of each 4×4 chroma block: the blocks on the diagonal use both neighbours, the others
/// prefer the one along their own edge of the macroblock.</summary>
internal sealed class ChromaDcPredictor : IIntraPredictor
{
    public bool CanPredict(bool above, bool left) => true;

    public void Predict(ReadOnlySpan<byte> plane, int stride, int x, int y, bool above, bool left, Span<byte> prediction)
    {
        for (var by = 0; by < 2; by++)
            for (var bx = 0; bx < 2; bx++)
            {
                var sumAbove = 0;
                var sumLeft = 0;
                if (above) for (var i = 0; i < 4; i++) sumAbove += plane[(y - 1) * stride + x + bx * 4 + i];
                if (left) for (var j = 0; j < 4; j++) sumLeft += plane[(y + by * 4 + j) * stride + x - 1];
                var dc = DcOf(bx, by, above, left, sumAbove, sumLeft);
                for (var j = 0; j < 4; j++) prediction.Slice((by * 4 + j) * 8 + bx * 4, 4).Fill((byte)dc);
            }
    }

    private static int DcOf(int bx, int by, bool above, bool left, int sumAbove, int sumLeft)
    {
        var preferAbove = bx == 1 && by == 0;
        var preferLeft = bx == 0 && by == 1;
        if (preferAbove && above) return (sumAbove + 2) >> 2;
        if (preferLeft && left) return (sumLeft + 2) >> 2;
        if (above && left && !preferAbove && !preferLeft) return (sumAbove + sumLeft + 4) >> 3;
        if (left) return (sumLeft + 2) >> 2;
        if (above) return (sumAbove + 2) >> 2;
        return 128;
    }
}

/// <summary>A plane fitted through the neighbours; <paramref name="gradientScale"/> is 5 for 16×16 luma and 34 for 8×8 chroma.</summary>
internal sealed class PlanePredictor(int size, int gradientScale) : IIntraPredictor
{
    public bool CanPredict(bool above, bool left) => above && left;

    public void Predict(ReadOnlySpan<byte> plane, int stride, int x, int y, bool above, bool left, Span<byte> prediction)
    {
        var half = size / 2;
        var h = 0;
        var v = 0;
        for (var i = 0; i < half; i++)
        {
            h += (i + 1) * (plane[(y - 1) * stride + x + half + i] - plane[(y - 1) * stride + x + half - 2 - i]);
            v += (i + 1) * (plane[(y + half + i) * stride + x - 1] - plane[(y + half - 2 - i) * stride + x - 1]);
        }
        var a = 16 * (plane[(y + size - 1) * stride + x - 1] + plane[(y - 1) * stride + x + size - 1]);
        var b = (gradientScale * h + 32) >> 6;
        var c = (gradientScale * v + 32) >> 6;
        for (var j = 0; j < size; j++)
            for (var i = 0; i < size; i++)
                prediction[j * size + i] = (byte)Math.Clamp((a + b * (i - half + 1) + c * (j - half + 1) + 16) >> 5, 0, 255);
    }
}
