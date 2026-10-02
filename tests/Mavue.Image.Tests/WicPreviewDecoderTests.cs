using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.Image.Wic;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.Image.Tests;

/// <summary>
/// Checks the WIC fast path against Windows' own decoder (WinRT BitmapDecoder with EXIF orientation),
/// using generated images whose four quadrants have distinct colors, so any wrong flip/rotate shows up.
/// </summary>
[Trait("Category", "Image")]
public sealed class WicPreviewDecoderTests : IDisposable
{
    // Quadrant colors (B, G, R) of the stored image: top-left, top-right, bottom-left, bottom-right.
    private static readonly (byte B, byte G, byte R)[] Quadrants = [(0, 0, 230), (0, 200, 0), (220, 0, 0), (0, 220, 220)];
    private readonly string _dir = Directory.CreateTempSubdirectory("mavue-wic-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task ExifOrientation_MatchesWindowsDecoder(int orientation)
    {
        string path = Path.Combine(_dir, $"o{orientation}.jpg");
        await WriteQuadrantJpegAsync(path, 160, 96, orientation);

        DecodedPixels? fast = WicPreviewDecoder.DecodeToFit(path, 4000, 4000, cancellationToken: TestContext.Current.CancellationToken);
        (uint w, uint h, byte[] reference) = await DecodeWithWinRtAsync(path);

        Assert.NotNull(fast);
        Assert.Equal(orientation, fast.Orientation);
        Assert.Equal((w, h), (fast.Width, fast.Height));
        foreach ((double fx, double fy) in new[] { (0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75) })
        {
            (byte b, byte g, byte r) expected = Pixel(reference, w, fx, fy);
            (byte b, byte g, byte r) actual = Pixel(fast.Pixels, fast.Width, fx, fy);
            Assert.True(Close(expected, actual), $"orientation {orientation} at ({fx},{fy}): WinRT {expected} vs WIC {actual}");
        }
    }

    [Fact]
    public async Task LargeImage_IsScaledToFit_PreservingAspect()
    {
        string path = Path.Combine(_dir, "large.jpg");
        await WriteQuadrantJpegAsync(path, 4000, 3000, orientation: 1);

        DecodedPixels? result = WicPreviewDecoder.DecodeToFit(path, 400, 400, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal((400u, 300u), (result.Width, result.Height));
        Assert.Equal((4000u, 3000u), (result.SourceWidth, result.SourceHeight));
        Assert.Equal(400 * 300 * 4, result.Pixels.Length);
    }

    [Fact]
    public async Task RotatedImage_FitsInDisplayOrientation()
    {
        // Stored 4000x3000, displayed 3000x4000 (orientation 6 = rotate 90°).
        string path = Path.Combine(_dir, "rotated.jpg");
        await WriteQuadrantJpegAsync(path, 4000, 3000, orientation: 6);

        DecodedPixels? result = WicPreviewDecoder.DecodeToFit(path, 400, 400, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal((300u, 400u), (result.Width, result.Height));
        Assert.Equal((3000u, 4000u), (result.SourceWidth, result.SourceHeight));
    }

    [Fact]
    public async Task SmallImage_IsNotUpscaled()
    {
        string path = Path.Combine(_dir, "small.jpg");
        await WriteQuadrantJpegAsync(path, 64, 48, orientation: 1);

        DecodedPixels? result = WicPreviewDecoder.DecodeToFit(path, 2000, 2000, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal((64u, 48u), (result.Width, result.Height));
    }

    [Fact]
    public async Task Png_WithoutMetadata_Works()
    {
        string path = Path.Combine(_dir, "image.png");
        await WriteAsync(path, BitmapEncoder.PngEncoderId, 120, 80, orientation: null);

        DecodedPixels? result = WicPreviewDecoder.DecodeToFit(path, 60, 60, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal((60u, 40u), (result.Width, result.Height));
        Assert.Equal(1, result.Orientation);
    }

    [Fact]
    public async Task OverPixelBudget_IsRefusedBeforeDecoding()
    {
        string path = Path.Combine(_dir, "budget.jpg");
        await WriteQuadrantJpegAsync(path, 200, 100, orientation: 1);

        Assert.Throws<ImageTooLargeException>(() =>
            WicPreviewDecoder.DecodeToFit(path, 100, 100, maxSourcePixels: 19_999, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryReadDimensions_AppliesOrientation_WithoutDecoding()
    {
        string upright = Path.Combine(_dir, "dims1.jpg");
        string rotated = Path.Combine(_dir, "dims6.jpg");
        await WriteQuadrantJpegAsync(upright, 320, 200, orientation: 1);
        await WriteQuadrantJpegAsync(rotated, 320, 200, orientation: 6);

        Assert.Equal((320u, 200u), WicPreviewDecoder.TryReadDimensions(upright));
        Assert.Equal((200u, 320u), WicPreviewDecoder.TryReadDimensions(rotated));
    }

    [Fact]
    public void TryReadDimensions_UnreadableFile_ReturnsNull()
    {
        string path = Path.Combine(_dir, "text.jpg");
        File.WriteAllText(path, "not an image");
        Assert.Null(WicPreviewDecoder.TryReadDimensions(path));
    }

    [Fact]
    public async Task Prepare_CopiesIntoCallerBuffer_WithPaddedRows()
    {
        string path = Path.Combine(_dir, "prepared.jpg");
        await WriteQuadrantJpegAsync(path, 300, 200, orientation: 6);
        DecodedPixels expected = WicPreviewDecoder.DecodeToFit(path, 150, 150, cancellationToken: TestContext.Current.CancellationToken)!;

        using WicPreparedImage prepared = WicPreviewDecoder.Prepare(path, 150, 150, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.Equal((expected.Width, expected.Height, 6), (prepared.Width, prepared.Height, prepared.Orientation));
        Assert.Equal((200u, 300u), (prepared.SourceWidth, prepared.SourceHeight));

        // Like a SoftwareBitmap plane: rows may be longer than Width * 4.
        uint stride = (prepared.Width * 4) + 64;
        byte[] padded = new byte[stride * prepared.Height];
        unsafe
        {
            fixed (byte* p = padded)
            {
                prepared.CopyPixels(p, stride, (uint)padded.Length);
            }
        }

        for (uint y = 0; y < prepared.Height; y++)
        {
            Assert.True(
                padded.AsSpan((int)(y * stride), (int)prepared.Width * 4).SequenceEqual(expected.Pixels.AsSpan((int)(y * prepared.Width * 4), (int)prepared.Width * 4)),
                $"row {y} differs");
        }
    }

    [Fact]
    public async Task Prepare_RejectsTooSmallBuffer_AndUseAfterDispose()
    {
        string path = Path.Combine(_dir, "small-buffer.jpg");
        await WriteQuadrantJpegAsync(path, 64, 48, orientation: 1);
        WicPreparedImage prepared = WicPreviewDecoder.Prepare(path, 100, 100, cancellationToken: TestContext.Current.CancellationToken)!;
        byte[] tooSmall = new byte[(64 * 4 * 48) - 1];

        unsafe
        {
            fixed (byte* p = tooSmall)
            {
                byte* destination = p;
                Assert.Throws<ArgumentOutOfRangeException>(() => prepared.CopyPixels(destination, 64 * 4, (uint)tooSmall.Length));
            }
        }

        prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(() => prepared.CopyPixels());
    }

    [Fact]
    public void CorruptFile_Throws_SoCallerCanFallBack()
    {
        string path = Path.Combine(_dir, "broken.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49]);

        Assert.ThrowsAny<Exception>(() => WicPreviewDecoder.DecodeToFit(path, 100, 100, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 8)]
    [InlineData(3, 2)]
    [InlineData(4, 16)]
    [InlineData(5, 11)]
    [InlineData(6, 1)]
    [InlineData(7, 9)]
    [InlineData(8, 3)]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    public void TransformFor_MapsAllExifOrientations(int orientation, int expected)
    {
        Assert.Equal(expected, WicPreviewDecoder.TransformFor(orientation));
    }

    private static Task WriteQuadrantJpegAsync(string path, uint width, uint height, int orientation) =>
        WriteAsync(path, BitmapEncoder.JpegEncoderId, width, height, orientation);

    private static async Task WriteAsync(string path, Guid encoderId, uint width, uint height, int? orientation)
    {
        byte[] pixels = new byte[width * height * 4];
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                (byte b, byte g, byte r) = Quadrants[(y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1)];
                long i = ((long)y * width + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }

        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        if (orientation is { } o)
        {
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                ["System.Photo.Orientation"] = new BitmapTypedValue((ushort)o, PropertyType.UInt16),
            });
        }

        await encoder.FlushAsync();
    }

    private static async Task<(uint Width, uint Height, byte[] Pixels)> DecodeWithWinRtAsync(string path)
    {
        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        PixelDataProvider data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
        return (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight, data.DetachPixelData());
    }

    private static (byte B, byte G, byte R) Pixel(byte[] pixels, uint width, double fx, double fy)
    {
        uint height = (uint)(pixels.Length / 4 / width);
        long i = ((long)(fy * height) * width + (long)(fx * width)) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2]);
    }

    private static bool Close((byte B, byte G, byte R) a, (byte B, byte G, byte R) b) =>
        Math.Abs(a.B - b.B) <= 24 && Math.Abs(a.G - b.G) <= 24 && Math.Abs(a.R - b.R) <= 24;
}
