namespace TinkerCNC.Video;

/// <summary>Converts decoded camera images into the pictures the video encoder takes.</summary>
public static class ColorConversion
{
    /// <summary>
    /// Converts 8-bit RGBA rows (alpha ignored) into limited-range BT.709 YCbCr 4:2:0: every pixel gets a luma
    /// sample, each 2×2 group of pixels shares one chroma sample made from their mean colour.
    /// </summary>
    /// <param name="rgba">The pixels, <paramref name="rowBytes"/> bytes per row, at least the picture's size.</param>
    public static void RgbaToYuv420(ReadOnlySpan<byte> rgba, int rowBytes, YuvPicture picture)
    {
        var width = picture.Width;
        var height = picture.Height;
        if (rowBytes < width * 4 || rgba.Length < (height - 1) * rowBytes + width * 4)
            throw new ArgumentException("The pixel buffer is smaller than the picture.", nameof(rgba));

        for (var y = 0; y < height; y++)
        {
            var row = rgba.Slice(y * rowBytes, width * 4);
            var luma = picture.Y.AsSpan(y * picture.PaddedWidth, width);
            for (var x = 0; x < width; x++)
            {
                var p = x * 4;
                luma[x] = Luma(row[p], row[p + 1], row[p + 2]);
            }
        }

        var chromaRows = (height + 1) / 2;
        var chromaColumns = (width + 1) / 2;
        for (var cy = 0; cy < chromaRows; cy++)
        {
            var y0 = cy * 2;
            var y1 = Math.Min(y0 + 1, height - 1);
            for (var cx = 0; cx < chromaColumns; cx++)
            {
                var x0 = cx * 2;
                var x1 = Math.Min(x0 + 1, width - 1);
                int r = 0, g = 0, b = 0;
                Accumulate(rgba, y0 * rowBytes + x0 * 4, ref r, ref g, ref b);
                Accumulate(rgba, y0 * rowBytes + x1 * 4, ref r, ref g, ref b);
                Accumulate(rgba, y1 * rowBytes + x0 * 4, ref r, ref g, ref b);
                Accumulate(rgba, y1 * rowBytes + x1 * 4, ref r, ref g, ref b);
                var index = cy * picture.ChromaWidth + cx;
                picture.Cb[index] = (byte)Math.Clamp(128 + ((-26 * r - 87 * g + 112 * b + 512) >> 10), 16, 240);
                picture.Cr[index] = (byte)Math.Clamp(128 + ((112 * r - 102 * g - 10 * b + 512) >> 10), 16, 240);
            }
        }
        picture.ReplicateEdges();
    }

    private static byte Luma(int r, int g, int b) => (byte)Math.Clamp(16 + ((47 * r + 157 * g + 16 * b + 128) >> 8), 16, 235);

    private static void Accumulate(ReadOnlySpan<byte> rgba, int offset, ref int r, ref int g, ref int b)
    {
        r += rgba[offset];
        g += rgba[offset + 1];
        b += rgba[offset + 2];
    }
}
