using System.Runtime.InteropServices;
using Mavue.Core.Viewing;

namespace Mavue.Image;

/// <summary>
/// Turns and mirrors 32-bit pixels (BGRA) for display (<see cref="ViewOrientation"/>). Works on decoded,
/// display-sized pixels, so the cost is proportional to what is on screen, not to the file.
/// </summary>
public static class PixelOrientation
{
    /// <summary>
    /// Writes <paramref name="source"/> (<paramref name="width"/> × <paramref name="height"/>) turned by
    /// <paramref name="orientation"/> into <paramref name="destination"/>, whose size is the oriented size
    /// (<see cref="ViewOrientation.Oriented{T}"/>).
    /// </summary>
    /// <param name="sourceStride">Bytes per source row (at least width × 4).</param>
    /// <param name="destinationStride">Bytes per destination row (at least oriented width × 4).</param>
    public static void Apply(
        ReadOnlySpan<byte> source, int width, int height, int sourceStride,
        Span<byte> destination, int destinationStride, ViewOrientation orientation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        (int outWidth, int outHeight) = orientation.Oriented(width, height);
        if (sourceStride < width * 4 || source.Length < ((long)sourceStride * (height - 1)) + (width * 4))
        {
            throw new ArgumentException("The source is smaller than the image.", nameof(source));
        }

        if (destinationStride % 4 != 0 || destinationStride < outWidth * 4 || destination.Length < ((long)destinationStride * (outHeight - 1)) + (outWidth * 4))
        {
            throw new ArgumentException("The destination is smaller than the oriented image.", nameof(destination));
        }

        if (orientation.IsIdentity)
        {
            for (int y = 0; y < height; y++)
            {
                source.Slice(y * sourceStride, width * 4).CopyTo(destination.Slice(y * destinationStride, width * 4));
            }

            return;
        }

        // Every source row maps to a straight line of destination pixels: find its start and step once per row.
        int destinationPixelsPerRow = destinationStride / 4;
        Span<uint> target = MemoryMarshal.Cast<byte, uint>(destination[..(destination.Length & ~3)]);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<uint> row = MemoryMarshal.Cast<byte, uint>(source.Slice(y * sourceStride, width * 4));
            (int x0, int y0) = orientation.MapPixel(0, y, width, height);
            int start = (y0 * destinationPixelsPerRow) + x0;
            int step = 0;
            if (width > 1)
            {
                (int x1, int y1) = orientation.MapPixel(1, y, width, height);
                step = ((y1 - y0) * destinationPixelsPerRow) + (x1 - x0);
            }

            int index = start;
            for (int x = 0; x < row.Length; x++, index += step)
            {
                target[index] = row[x];
            }
        }
    }
}
