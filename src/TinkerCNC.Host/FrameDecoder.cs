using SkiaSharp;
using TinkerCNC.Video;

namespace TinkerCNC.Host;

/// <summary>Turns a camera frame (an encoded image, JPEG in practice) into a picture the video encoder takes.</summary>
public interface IFrameDecoder
{
    /// <summary>The decoded picture, or null when the bytes are not an image.</summary>
    YuvPicture? Decode(byte[] frame);
}

/// <summary>Decodes frames with SkiaSharp and converts them to limited-range BT.709 4:2:0.</summary>
public sealed class SkiaFrameDecoder : IFrameDecoder
{
    public YuvPicture? Decode(byte[] frame)
    {
        var bounds = SKBitmap.DecodeBounds(frame);
        if (bounds.IsEmpty) return null;
        var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = SKBitmap.Decode(frame, info);
        if (bitmap is null) return null;
        var picture = new YuvPicture(info.Width, info.Height);
        ColorConversion.RgbaToYuv420(bitmap.GetPixelSpan(), bitmap.RowBytes, picture);
        return picture;
    }
}
