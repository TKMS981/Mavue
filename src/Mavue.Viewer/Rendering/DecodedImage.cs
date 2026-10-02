using System.Diagnostics;
using Windows.Graphics.Imaging;

namespace Mavue.Viewer.Rendering;

/// <summary>A decoded image or rendered PDF page, sized for display.</summary>
/// <param name="Bitmap">BGRA8 premultiplied pixels; null when <paramref name="Reason"/> is set.</param>
/// <param name="SourceWidth">Width of the source image (PDF: page width in points).</param>
/// <param name="SourceHeight">Height of the source image (PDF: page height in points).</param>
/// <param name="DecodeMilliseconds">Time spent decoding or rendering.</param>
/// <param name="Decoder">Which path produced the pixels (diagnostics).</param>
/// <param name="PageCount">Pages in the PDF; 0 for images.</param>
/// <param name="Reason">Set when nothing was decoded: "no-codec", "too-large", "password".</param>
/// <param name="PageIndex">Zero-based page that was rendered.</param>
public sealed record DecodedImage(SoftwareBitmap? Bitmap, uint SourceWidth, uint SourceHeight, double DecodeMilliseconds, string Decoder, int PageCount, string? Reason, int PageIndex = 0)
{
    public static DecodedImage Decoded(SoftwareBitmap bitmap, uint sourceWidth, uint sourceHeight, long elapsedTicks, string decoder, int pageCount = 0, int pageIndex = 0) =>
        new(bitmap, sourceWidth, sourceHeight, elapsedTicks * 1000.0 / Stopwatch.Frequency, decoder, pageCount, null, pageIndex);

    public static DecodedImage Skipped(string reason) => new(null, 0, 0, 0, "none", 0, reason);

    /// <summary>Bytes held by the pixels (for cache budgets).</summary>
    public long ByteCount => Bitmap is { } b ? (long)b.PixelWidth * b.PixelHeight * 4 : 0;
}
