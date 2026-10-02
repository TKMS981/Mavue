namespace Mavue.Image.Wic;

/// <summary>A decoded image: premultiplied BGRA, top-down rows, stride = Width * 4.</summary>
/// <param name="Width">Decoded width (display orientation).</param>
/// <param name="Height">Decoded height (display orientation).</param>
/// <param name="Pixels">Premultiplied BGRA pixels.</param>
/// <param name="SourceWidth">Width of the full image in display orientation.</param>
/// <param name="SourceHeight">Height of the full image in display orientation.</param>
/// <param name="Orientation">EXIF orientation that was applied (1 = none).</param>
public sealed record DecodedPixels(uint Width, uint Height, byte[] Pixels, uint SourceWidth, uint SourceHeight, int Orientation);

/// <summary>
/// An opened WIC pipeline (decoder → scaler → flip/rotate → premultiplied BGRA) that has not produced
/// pixels yet. The caller chooses where the pixels go (<see cref="CopyPixels(byte*, uint, uint)"/>), so a
/// large image can be written straight into its final buffer without an intermediate copy.
/// Use from one thread and dispose it to release the decoder.
/// </summary>
public sealed unsafe class WicPreparedImage : IDisposable
{
    private nint _factory, _decoder, _frame, _scaler, _flipRotator, _converter;

    internal WicPreparedImage(nint factory, nint decoder, nint frame, nint scaler, nint flipRotator, nint converter, uint width, uint height, uint sourceWidth, uint sourceHeight, int orientation)
    {
        (_factory, _decoder, _frame, _scaler, _flipRotator, _converter) = (factory, decoder, frame, scaler, flipRotator, converter);
        (Width, Height, SourceWidth, SourceHeight, Orientation) = (width, height, sourceWidth, sourceHeight, orientation);
    }

    /// <summary>Output width (display orientation).</summary>
    public uint Width { get; }

    /// <summary>Output height (display orientation).</summary>
    public uint Height { get; }

    /// <summary>Width of the full image in display orientation.</summary>
    public uint SourceWidth { get; }

    /// <summary>Height of the full image in display orientation.</summary>
    public uint SourceHeight { get; }

    /// <summary>EXIF orientation that is applied (1 = none).</summary>
    public int Orientation { get; }

    /// <summary>Decodes into caller-owned memory: top-down rows of premultiplied BGRA.</summary>
    /// <param name="destination">First byte of the first row.</param>
    /// <param name="stride">Bytes per row (at least Width × 4).</param>
    /// <param name="bufferSize">Writable bytes at <paramref name="destination"/> (at least stride × (Height − 1) + Width × 4).</param>
    public void CopyPixels(byte* destination, uint stride, uint bufferSize)
    {
        ObjectDisposedException.ThrowIf(_converter == 0, this);
        ArgumentNullException.ThrowIfNull(destination);
        if (stride < (ulong)Width * 4 || bufferSize < ((ulong)stride * (Height - 1)) + ((ulong)Width * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize), "Destination is too small for the image.");
        }

        WicNative.CopyPixels(_converter, stride, bufferSize, destination);
    }

    /// <summary>Decodes into a new array (stride = Width × 4).</summary>
    public byte[] CopyPixels()
    {
        ObjectDisposedException.ThrowIf(_converter == 0, this);
        byte[] pixels = new byte[checked((long)Width * Height * 4)];
        WicNative.CopyPixels(_converter, Width * 4, pixels);
        return pixels;
    }

    public void Dispose()
    {
        WicNative.Release(_converter);
        WicNative.Release(_flipRotator);
        WicNative.Release(_scaler);
        WicNative.Release(_frame);
        WicNative.Release(_decoder);
        WicNative.Release(_factory);
        (_factory, _decoder, _frame, _scaler, _flipRotator, _converter) = (0, 0, 0, 0, 0, 0);
    }
}

/// <summary>Thrown before decoding when the image exceeds the caller's pixel budget.</summary>
public sealed class ImageTooLargeException : Exception
{
    public ImageTooLargeException()
    {
    }

    public ImageTooLargeException(string message)
        : base(message)
    {
    }

    public ImageTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ImageTooLargeException(uint width, uint height)
        : base($"Image of {width}x{height} pixels exceeds the decode budget.")
    {
    }
}

/// <summary>
/// Fast "fit to window" decode through Windows Imaging Component directly: decoder → scaler (Fant,
/// which uses the codec's native downscaling such as JPEG DCT scaling) → EXIF flip/rotate →
/// premultiplied BGRA. Measured on a 192 MP JPEG: ~240 ms vs ~430 ms through WinRT BitmapDecoder.
/// <para>
/// Color: this path does no color management. It declines (returns null) for images that carry an
/// ICC profile or a non-sRGB EXIF color space, so the caller can use a color-managed path instead.
/// </para>
/// </summary>
public static class WicPreviewDecoder
{
    // WICBitmapTransformOptions
    private const int Rotate0 = 0;
    private const int Rotate90 = 1;
    private const int Rotate180 = 2;
    private const int Rotate270 = 3;
    private const int FlipHorizontal = 8;
    private const int FlipVertical = 16;
    private const uint ExifColorSpaceSrgb = 1;

    /// <summary>
    /// Decodes the first frame of <paramref name="path"/> scaled to fit within the given size (never upscaled).
    /// </summary>
    /// <param name="path">Image file.</param>
    /// <param name="maxWidth">Box width in pixels.</param>
    /// <param name="maxHeight">Box height in pixels.</param>
    /// <param name="maxSourcePixels">Refuse (before any pixel decoding) images with more source pixels than this.</param>
    /// <param name="cancellationToken">Checked between pipeline steps.</param>
    /// <returns>The pixels, or null if the image needs color management (caller should use another path).</returns>
    /// <exception cref="ImageTooLargeException">The image exceeds <paramref name="maxSourcePixels"/>.</exception>
    public static DecodedPixels? DecodeToFit(string path, uint maxWidth, uint maxHeight, long maxSourcePixels = long.MaxValue, CancellationToken cancellationToken = default)
    {
        using WicPreparedImage? prepared = Prepare(path, maxWidth, maxHeight, maxSourcePixels, cancellationToken);
        return prepared is null
            ? null
            : new DecodedPixels(prepared.Width, prepared.Height, prepared.CopyPixels(), prepared.SourceWidth, prepared.SourceHeight, prepared.Orientation);
    }

    /// <summary>
    /// Opens the same pipeline as <see cref="DecodeToFit"/> without decoding pixels yet, so the caller can
    /// decode straight into its own buffer with <see cref="WicPreparedImage.CopyPixels(byte*, uint, uint)"/>.
    /// </summary>
    /// <returns>The prepared pipeline (dispose it), or null if the image needs color management.</returns>
    /// <exception cref="ImageTooLargeException">The image exceeds <paramref name="maxSourcePixels"/>.</exception>
    public static WicPreparedImage? Prepare(string path, uint maxWidth, uint maxHeight, long maxSourcePixels = long.MaxValue, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        nint factory = 0, decoder = 0, frame = 0, scaler = 0, flipRotator = 0, converter = 0;
        bool handedOver = false;
        try
        {
            factory = WicNative.CreateFactory();
            WicNative.Check(WicNative.CreateDecoderFromFilename(factory, path, out decoder));
            frame = WicNative.GetFrame(decoder, 0);
            if (!IsSrgbOrUntagged(factory, frame))
            {
                return null;
            }

            int orientation = ReadOrientation(frame);
            bool swapsAxes = orientation is >= 5 and <= 8;
            (uint storedWidth, uint storedHeight) = WicNative.GetSize(frame);
            if ((long)storedWidth * storedHeight > maxSourcePixels)
            {
                throw new ImageTooLargeException(storedWidth, storedHeight);
            }

            (uint sourceWidth, uint sourceHeight) = swapsAxes ? (storedHeight, storedWidth) : (storedWidth, storedHeight);
            (uint width, uint height) = FitWithin(sourceWidth, sourceHeight, maxWidth, maxHeight);
            cancellationToken.ThrowIfCancellationRequested();

            // Scale in stored orientation, then flip/rotate the (small) result.
            nint source = frame;
            (uint scaledWidth, uint scaledHeight) = swapsAxes ? (height, width) : (width, height);
            if (scaledWidth != storedWidth || scaledHeight != storedHeight)
            {
                scaler = WicNative.CreateBitmapScaler(factory);
                WicNative.InitializeScaler(scaler, source, scaledWidth, scaledHeight);
                source = scaler;
            }

            int transform = TransformFor(orientation);
            if (transform != Rotate0)
            {
                flipRotator = WicNative.CreateBitmapFlipRotator(factory);
                WicNative.InitializeFlipRotator(flipRotator, source, transform);
                source = flipRotator;
            }

            converter = WicNative.CreateFormatConverter(factory);
            WicNative.InitializeConverter(converter, source, WicNative.PixelFormat32bppPBGRA);
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = new WicPreparedImage(factory, decoder, frame, scaler, flipRotator, converter, width, height, sourceWidth, sourceHeight, orientation);
            handedOver = true;
            return prepared;
        }
        finally
        {
            if (!handedOver)
            {
                ReleaseAll(factory, decoder, frame, scaler, flipRotator, converter);
            }
        }
    }

    private static void ReleaseAll(nint factory, nint decoder, nint frame, nint scaler, nint flipRotator, nint converter)
    {
        WicNative.Release(converter);
        WicNative.Release(flipRotator);
        WicNative.Release(scaler);
        WicNative.Release(frame);
        WicNative.Release(decoder);
        WicNative.Release(factory);
    }

    /// <summary>
    /// Reads the displayed size (EXIF orientation applied) from the header without decoding pixels.
    /// Returns null if no installed codec can open the file.
    /// </summary>
    public static (uint Width, uint Height)? TryReadDimensions(string path)
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
            return ReadOrientation(frame) is >= 5 and <= 8 ? (height, width) : (width, height);
        }
        catch (System.Runtime.InteropServices.COMException)
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

    /// <summary>
    /// Maps an EXIF orientation (1–8) to the WIC flip/rotate that displays the image upright.
    /// For the mirrored cases WIC combines the flip and the rotation in the opposite order to the
    /// EXIF definition, so 5 (transpose) needs Rotate270+Flip and 7 (transverse) Rotate90+Flip;
    /// verified pixel by pixel against Windows' own decoder (WicPreviewDecoderTests).
    /// </summary>
    public static int TransformFor(int exifOrientation) => exifOrientation switch
    {
        2 => FlipHorizontal,
        3 => Rotate180,
        4 => FlipVertical,
        5 => Rotate270 | FlipHorizontal,
        6 => Rotate90,
        7 => Rotate90 | FlipHorizontal,
        8 => Rotate270,
        _ => Rotate0,
    };

    /// <summary>Fit inside the box, preserving aspect ratio, never upscaling; each side at least 1.</summary>
    public static (uint Width, uint Height) FitWithin(uint width, uint height, uint maxWidth, uint maxHeight)
    {
        if (width == 0 || height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        if (maxWidth == 0 || maxHeight == 0)
        {
            return (width, height);
        }

        double scale = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        return ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
    }

    private static unsafe int ReadOrientation(nint frame)
    {
        if (WicNative.GetMetadataQueryReader(frame, out nint reader) < 0 || reader == 0)
        {
            return 1; // format without metadata (e.g. BMP)
        }

        try
        {
            foreach (string query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}" })
            {
                WicNative.PropVariant value = default;
                if (WicNative.GetMetadataByName(reader, query, &value) >= 0)
                {
                    int orientation = value.Type == WicNative.VtUi2 ? value.UInt16Value : 1;
                    _ = WicNative.PropVariantClear(&value); // VT_UI2 owns no memory; clearing is a formality
                    return orientation is >= 1 and <= 8 ? orientation : 1;
                }
            }

            return 1;
        }
        finally
        {
            WicNative.Release(reader);
        }
    }

    /// <summary>True if the frame has no color context, or only an EXIF "sRGB" color space.</summary>
    private static unsafe bool IsSrgbOrUntagged(nint factory, nint frame)
    {
        if (WicNative.GetColorContexts(frame, 0, null, out uint count) < 0 || count == 0)
        {
            return true;
        }

        nint* contexts = stackalloc nint[(int)Math.Min(count, 16)];
        uint created = Math.Min(count, 16);
        for (uint i = 0; i < created; i++)
        {
            contexts[i] = WicNative.CreateColorContext(factory);
        }

        try
        {
            if (WicNative.GetColorContexts(frame, created, contexts, out uint actual) < 0)
            {
                return false;
            }

            for (uint i = 0; i < Math.Min(actual, created); i++)
            {
                int type = WicNative.GetColorContextType(contexts[i]);
                if (type == WicNative.ColorContextProfile ||
                    (type == WicNative.ColorContextExifColorSpace && WicNative.GetExifColorSpace(contexts[i]) != ExifColorSpaceSrgb))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            for (uint i = 0; i < created; i++)
            {
                WicNative.Release(contexts[i]);
            }
        }
    }
}
