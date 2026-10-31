using Mavue.Core.Viewing;
using Mavue.Image.Wic;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.Image.Tests;

/// <summary>
/// The pixel inspector's reads (docs/ARCHITECTURE.md §4.7.8): exact stored values with straight alpha, tiles clipped
/// to the image, and — for every EXIF orientation — the upright pixel mapped with <see cref="PixelInspection.StoredPixel"/>
/// has the color Windows' own decoder shows there.
/// </summary>
[Trait("Category", "Image")]
public sealed class WicPixelReaderTests : IDisposable
{
    // Quadrant colors (B, G, R) of the stored image: top-left, top-right, bottom-left, bottom-right.
    private static readonly (byte B, byte G, byte R)[] Quadrants = [(0, 0, 230), (0, 200, 0), (220, 0, 0), (0, 220, 220)];
    private readonly string _dir = Directory.CreateTempSubdirectory("mavue-pixels-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Png_ReadsExactValues_WithStraightAlpha()
    {
        string path = Path.Combine(_dir, "exact.png");
        await WritePngAsync(path, 300, 200, (x, y) => ((byte)(x % 256), (byte)(y % 256), (byte)((x + y) % 256), (byte)(x < 150 ? 255 : 128)));

        StoredImageLayout? layout = WicPixelReader.ReadLayout(path);
        Assert.Equal(new StoredImageLayout(300, 200, 1), layout);

        PixelTile? tile = WicPixelReader.ReadTile(path, new PixelRect(0, 0, 256, 256), TestContext.Current.CancellationToken);
        Assert.NotNull(tile);
        Assert.Equal(new PixelRect(0, 0, 256, 200), tile.Rect); // clipped to the image
        Assert.Equal(new PixelColor(10, 20, 30, 255), tile.At(10, 20));
        Assert.Equal(new PixelColor(200, 100, 44, 128), tile.At(200, 100)); // not premultiplied
        Assert.Null(tile.At(256, 0));
    }

    [Fact]
    public async Task EdgeTile_IsClipped_AndReadsTheRightPixels()
    {
        string path = Path.Combine(_dir, "edge.png");
        await WritePngAsync(path, 300, 200, (x, y) => ((byte)(x % 256), (byte)(y % 256), 7, 255));

        PixelRect rect = PixelInspection.TileAround(299, 199, 300, 200);
        PixelTile? tile = WicPixelReader.ReadTile(path, rect, TestContext.Current.CancellationToken);

        Assert.NotNull(tile);
        Assert.Equal(new PixelRect(256, 0, 44, 200), tile.Rect);
        Assert.Equal(new PixelColor(299 % 256, 199, 7, 255), tile.At(299, 199));
        Assert.Equal(new PixelColor(0, 0, 7, 255), tile.At(256, 0));
    }

    [Fact]
    public async Task RectangleOutsideTheImage_IsNull()
    {
        string path = Path.Combine(_dir, "small.png");
        await WritePngAsync(path, 10, 10, (_, _) => (1, 2, 3, 255));
        Assert.Null(WicPixelReader.ReadTile(path, new PixelRect(10, 0, 5, 5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NotAnImage_IsNull()
    {
        string path = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(path, "not an image");
        Assert.Null(WicPixelReader.ReadLayout(path));
        Assert.Null(WicPixelReader.ReadTile(path, new PixelRect(0, 0, 4, 4), TestContext.Current.CancellationToken));
        Assert.Null(WicPixelReader.ReadLayout(Path.Combine(_dir, "missing.png")));
    }

    [Fact]
    public void OversizedOrEmptyRectangles_AreRefused()
    {
        string path = Path.Combine(_dir, "any.png");
        Assert.Throws<ArgumentOutOfRangeException>(() => WicPixelReader.ReadTile(path, new PixelRect(0, 0, 2048, 2048), TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => WicPixelReader.ReadTile(path, new PixelRect(0, 0, 0, 10), TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => WicPixelReader.ReadTile(path, new PixelRect(-1, 0, 10, 10), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task ExifOrientation_UprightPixelMatchesWindowsDecoder(int orientation)
    {
        string path = Path.Combine(_dir, $"o{orientation}.jpg");
        await WriteQuadrantJpegAsync(path, 160, 96, orientation);
        (uint width, uint height, byte[] reference) = await DecodeUprightWithWindowsAsync(path);

        StoredImageLayout layout = WicPixelReader.ReadLayout(path)!.Value;
        Assert.Equal(orientation, layout.Orientation);
        Assert.Equal(((int)width, (int)height), layout.Upright);

        foreach ((double fx, double fy) in new[] { (0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75) })
        {
            int x = (int)(fx * width);
            int y = (int)(fy * height);
            (int sx, int sy) = PixelInspection.StoredPixel(x, y, layout.StoredWidth, layout.StoredHeight, layout.Orientation);
            PixelTile tile = WicPixelReader.ReadTile(path, PixelInspection.TileAround(sx, sy, layout.StoredWidth, layout.StoredHeight), TestContext.Current.CancellationToken)!;
            PixelColor actual = tile.At(sx, sy)!.Value;
            long i = (((long)y * width) + x) * 4;
            (byte b, byte g, byte r) expected = (reference[i], reference[i + 1], reference[i + 2]);
            Assert.True(
                Math.Abs(expected.b - actual.B) <= 24 && Math.Abs(expected.g - actual.G) <= 24 && Math.Abs(expected.r - actual.R) <= 24,
                $"orientation {orientation} at upright ({x},{y}) → stored ({sx},{sy}): Windows {expected} vs inspector {actual}");
        }
    }

    private static async Task WritePngAsync(string path, uint width, uint height, Func<uint, uint, (byte R, byte G, byte B, byte A)> color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                (byte r, byte g, byte b, byte a) = color(x, y);
                long i = (((long)y * width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, a);
            }
        }

        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    private static async Task WriteQuadrantJpegAsync(string path, uint width, uint height, int orientation)
    {
        byte[] pixels = new byte[width * height * 4];
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                (byte b, byte g, byte r) = Quadrants[(y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1)];
                long i = (((long)y * width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
            }
        }

        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
        {
            ["System.Photo.Orientation"] = new BitmapTypedValue((ushort)orientation, Windows.Foundation.PropertyType.UInt16),
        });
        await encoder.FlushAsync();
    }

    private static async Task<(uint Width, uint Height, byte[] Pixels)> DecodeUprightWithWindowsAsync(string path)
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
}
