namespace TinkerCNC.Video;

/// <summary>
/// A picture in planar 4:2:0 YCbCr (limited range), padded to whole 16×16 macroblocks: the visible area is
/// <see cref="Width"/>×<see cref="Height"/>, the planes are <see cref="PaddedWidth"/>×<see cref="PaddedHeight"/>
/// (the chroma planes half of that each way). <see cref="ReplicateEdges"/> fills the padding from the last visible
/// row and column, so an encoder sees no hard edge there.
/// </summary>
public sealed class YuvPicture
{
    public const int MacroblockSize = 16;

    public YuvPicture(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        PaddedWidth = Pad(width);
        PaddedHeight = Pad(height);
        Y = new byte[PaddedWidth * PaddedHeight];
        Cb = new byte[ChromaWidth * ChromaHeight];
        Cr = new byte[ChromaWidth * ChromaHeight];
    }

    public int Width { get; }
    public int Height { get; }
    public int PaddedWidth { get; }
    public int PaddedHeight { get; }
    public int ChromaWidth => PaddedWidth / 2;
    public int ChromaHeight => PaddedHeight / 2;
    public int MacroblocksWide => PaddedWidth / MacroblockSize;
    public int MacroblocksHigh => PaddedHeight / MacroblockSize;

    /// <summary>Luma plane, <see cref="PaddedWidth"/> bytes per row.</summary>
    public byte[] Y { get; }

    /// <summary>Blue-difference chroma plane, <see cref="ChromaWidth"/> bytes per row.</summary>
    public byte[] Cb { get; }

    /// <summary>Red-difference chroma plane, <see cref="ChromaWidth"/> bytes per row.</summary>
    public byte[] Cr { get; }

    public bool SameSizeAs(YuvPicture other) => other.Width == Width && other.Height == Height;

    /// <summary>Copies the planes of a picture of the same size.</summary>
    public void CopyFrom(YuvPicture other)
    {
        if (!SameSizeAs(other)) throw new ArgumentException("The pictures differ in size.", nameof(other));
        other.Y.CopyTo(Y, 0);
        other.Cb.CopyTo(Cb, 0);
        other.Cr.CopyTo(Cr, 0);
    }

    /// <summary>Fills the padding to the right of and below the visible area with the nearest visible samples.</summary>
    public void ReplicateEdges()
    {
        Replicate(Y, PaddedWidth, PaddedHeight, Width, Height);
        var chromaVisibleWidth = (Width + 1) / 2;
        var chromaVisibleHeight = (Height + 1) / 2;
        Replicate(Cb, ChromaWidth, ChromaHeight, chromaVisibleWidth, chromaVisibleHeight);
        Replicate(Cr, ChromaWidth, ChromaHeight, chromaVisibleWidth, chromaVisibleHeight);
    }

    private static void Replicate(byte[] plane, int stride, int rows, int visibleWidth, int visibleHeight)
    {
        for (var y = 0; y < visibleHeight; y++)
        {
            var row = plane.AsSpan(y * stride, stride);
            row[visibleWidth..].Fill(row[visibleWidth - 1]);
        }
        var last = plane.AsSpan((visibleHeight - 1) * stride, stride);
        for (var y = visibleHeight; y < rows; y++) last.CopyTo(plane.AsSpan(y * stride, stride));
    }

    private static int Pad(int size) => (size + MacroblockSize - 1) / MacroblockSize * MacroblockSize;
}
