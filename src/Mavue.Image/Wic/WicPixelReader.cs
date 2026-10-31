using Mavue.Core.Viewing;

namespace Mavue.Image.Wic;

/// <summary>How an image file is stored: its first frame's size and EXIF orientation.</summary>
public readonly record struct StoredImageLayout(int StoredWidth, int StoredHeight, int Orientation)
{
    /// <summary>Size of the upright image (EXIF orientation applied).</summary>
    public (int Width, int Height) Upright => PixelInspection.UprightSize(StoredWidth, StoredHeight, Orientation);
}

/// <summary>Full-resolution pixels of a rectangle of an image file (32-bit BGRA, straight alpha, top-down).</summary>
public sealed record PixelTile(PixelRect Rect, byte[] Bgra)
{
    /// <summary>The pixel at (<paramref name="x"/>, <paramref name="y"/>) in file coordinates, or null outside the tile.</summary>
    public PixelColor? At(int x, int y)
    {
        if (!Rect.Contains(x, y))
        {
            return null;
        }

        int offset = (((y - Rect.Y) * Rect.Width) + (x - Rect.X)) * 4;
        return PixelColor.FromBgra(Bgra.AsSpan(offset, 4));
    }
}

/// <summary>
/// Reads pixels for the pixel inspector (docs/ARCHITECTURE.md §4.7.8): one rectangle of the first frame at full
/// resolution, as stored in the file (no scaling, no EXIF rotation — the caller maps coordinates with
/// <see cref="PixelInspection.StoredPixel"/>), converted to 8-bit BGRA with straight alpha. Values are the file's own
/// (no color management). The file is opened for each call and closed again, so it is never kept locked.
/// </summary>
public static class WicPixelReader
{
    /// <summary>Largest rectangle read at once (a 1024 × 1024 tile is 4 MiB).</summary>
    public const int MaxTilePixels = 1024 * 1024;

    /// <summary>The first frame's stored size and EXIF orientation, or null if no installed codec reads the file.</summary>
    public static StoredImageLayout? ReadLayout(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        nint factory = 0, decoder = 0, frame = 0;
        try
        {
            factory = WicNative.CreateFactory();
            if (WicNative.CreateDecoderFromFilename(factory, path, out decoder) < 0)
            {
                return null;
            }

            frame = WicNative.GetFrame(decoder, 0);
            (uint width, uint height) = WicNative.GetSize(frame);
            return new StoredImageLayout((int)width, (int)height, WicPreviewDecoder.ReadOrientation(frame));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            WicNative.Release(frame);
            WicNative.Release(decoder);
            WicNative.Release(factory);
        }
    }

    /// <summary>Reads <paramref name="rect"/> (file coordinates, clipped to the frame) at full resolution.</summary>
    /// <returns>The tile, or null when the file can no longer be read or the rectangle is outside the frame.</returns>
    public static unsafe PixelTile? ReadTile(string path, PixelRect rect, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (rect.IsEmpty || rect.X < 0 || rect.Y < 0 || (long)rect.Width * rect.Height > MaxTilePixels)
        {
            throw new ArgumentOutOfRangeException(nameof(rect));
        }

        nint factory = 0, decoder = 0, frame = 0, converter = 0;
        try
        {
            factory = WicNative.CreateFactory();
            if (WicNative.CreateDecoderFromFilename(factory, path, out decoder) < 0)
            {
                return null;
            }

            frame = WicNative.GetFrame(decoder, 0);
            (uint width, uint height) = WicNative.GetSize(frame);
            if (rect.X >= width || rect.Y >= height)
            {
                return null;
            }

            var clipped = new PixelRect(rect.X, rect.Y, (int)Math.Min(rect.Width, width - (uint)rect.X), (int)Math.Min(rect.Height, height - (uint)rect.Y));
            cancellation.ThrowIfCancellationRequested();
            converter = WicNative.CreateFormatConverter(factory);
            WicNative.InitializeConverter(converter, frame, WicNative.PixelFormat32bppBGRA);
            uint stride = (uint)clipped.Width * 4;
            byte[] pixels = new byte[stride * (uint)clipped.Height];
            fixed (byte* p = pixels)
            {
                WicNative.CopyPixels(converter, clipped.X, clipped.Y, clipped.Width, clipped.Height, stride, (uint)pixels.Length, p);
            }

            return new PixelTile(clipped, pixels);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            return null; // replaced or damaged while shown
        }
        finally
        {
            WicNative.Release(converter);
            WicNative.Release(frame);
            WicNative.Release(decoder);
            WicNative.Release(factory);
        }
    }
}
