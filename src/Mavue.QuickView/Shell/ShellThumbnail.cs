using System.Runtime.InteropServices;
using Mavue.QuickView.Interop;

namespace Mavue.QuickView.Shell;

/// <summary>A decoded 32-bit BGRA image (top-down rows, stride = Width * 4, premultiplied alpha).</summary>
public sealed record BgraImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// Reads thumbnails that Windows already has in its thumbnail cache (thumbcache_*.db) via
/// IShellItemImageFactory with SIIGBF_INCACHEONLY, so Quick View can show *something* within
/// milliseconds while the full-quality decode runs (docs/ARCHITECTURE.md §4.3 step 4b).
/// Never triggers thumbnail extraction (which can be slow or hydrate cloud files).
/// <para>
/// Works from STA and MTA threads. The first call in a process costs ~60 ms (shell initialization,
/// measured); later calls take ~4–7 ms, so the resident host warms it up at startup.
/// </para>
/// </summary>
public static partial class ShellThumbnail
{
    /// <summary>Returns the cached thumbnail closest to <paramref name="maxSize"/>, or null if none is cached.</summary>
    public static BgraImage? TryGetCached(string path, int maxSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (ShellNative.SHCreateItemFromParsingName(path, 0, ShellNative.IidOf<IShellItemImageFactory>(), out nint factoryPointer) < 0)
        {
            return null;
        }

        object factoryObject = ShellNative.Wrap<object>(factoryPointer);
        try
        {
            var size = new NativeSize { Width = maxSize, Height = maxSize };
            int flags = ShellNative.SIIGBF_THUMBNAILONLY | ShellNative.SIIGBF_INCACHEONLY | ShellNative.SIIGBF_BIGGERSIZEOK;
            if (((IShellItemImageFactory)factoryObject).GetImage(size, flags, out nint bitmap) < 0 || bitmap == 0)
            {
                return null;
            }

            try
            {
                return ToBgra(bitmap);
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            ShellNative.Release(factoryObject);
        }
    }

    private static unsafe BgraImage? ToBgra(nint bitmap)
    {
        Bitmap info;
        if (GetObjectW(bitmap, sizeof(Bitmap), &info) == 0 || info.Width <= 0 || info.Height == 0)
        {
            return null;
        }

        int width = info.Width;
        int height = Math.Abs(info.Height);
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = width,
            Height = -height, // top-down
            Planes = 1,
            BitCount = 32,
        };

        byte[] pixels = new byte[checked(width * height * 4)];
        nint dc = CreateCompatibleDC(0);
        try
        {
            fixed (byte* p = pixels)
            {
                if (GetDIBits(dc, bitmap, 0, (uint)height, p, &header, 0) == 0)
                {
                    return null;
                }
            }
        }
        finally
        {
            DeleteDC(dc);
        }

        // Opaque shell thumbnails often come back with alpha = 0 everywhere; treat them as opaque.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                anyAlpha = true;
                break;
            }
        }

        if (!anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }
        }

        return new BgraImage(width, height, pixels);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;

        // BITMAPINFO.bmiColors[1] placeholder (unused for 32bpp BI_RGB).
        public uint Colors;
    }

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetObjectW(nint h, int c, void* pv);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetDIBits(nint hdc, nint hbm, uint start, uint lines, void* bits, BitmapInfoHeader* bmi, uint usage);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint ho);
}
