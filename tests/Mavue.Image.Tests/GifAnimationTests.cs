using System.Text;
using Mavue.Image.Gif;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.Image.Tests;

[Trait("Category", "Image")]
public sealed class GifAnimationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mavue-gif-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Solid(int w, int h, byte b, byte g, byte r, byte a = 255)
    {
        byte[] p = new byte[w * h * 4];
        for (int i = 0; i < p.Length; i += 4)
        {
            p[i] = b;
            p[i + 1] = g;
            p[i + 2] = r;
            p[i + 3] = a;
        }

        return p;
    }

    private static (byte B, byte G, byte R, byte A) Pixel(GifComposer composer, int x, int y)
    {
        int i = ((y * composer.Width) + x) * 4;
        return (composer.Canvas[i], composer.Canvas[i + 1], composer.Canvas[i + 2], composer.Canvas[i + 3]);
    }

    [Theory]
    [InlineData(0, 100)]   // 0 → browser default
    [InlineData(1, 100)]   // 10 ms → browser default
    [InlineData(2, 20)]
    [InlineData(10, 100)]
    [InlineData(150, 1500)]
    [InlineData(65535, 655350)] // longest a GIF can say: still just a timer interval
    public void NormalizeDelay(int centiseconds, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), GifComposer.NormalizeDelay(centiseconds));

    [Fact]
    public void ParseLoopCount_FromNetscapeBlock()
    {
        Assert.Equal(0, GifComposer.ParseLoopCount([3, 1, 0, 0, 0]));
        Assert.Equal(5, GifComposer.ParseLoopCount([3, 1, 5, 0, 0]));
        Assert.Equal(258, GifComposer.ParseLoopCount([1, 2, 1]));
        Assert.Null(GifComposer.ParseLoopCount([]));
        Assert.Null(GifComposer.ParseLoopCount([9, 9]));
    }

    [Fact]
    public void Composer_DrawsAtOffset_KeepsTransparentPixels_AndClips()
    {
        var composer = new GifComposer(4, 4);
        composer.Apply(new GifFrame(Solid(4, 4, 0, 0, 200), 0, 0, 4, 4, TimeSpan.FromMilliseconds(100), GifDisposal.Keep));

        byte[] partial = Solid(3, 3, 0, 200, 0);
        partial[3] = 0; // top-left pixel of the patch is transparent
        composer.Apply(new GifFrame(partial, 2, 2, 3, 3, TimeSpan.FromMilliseconds(100), GifDisposal.Keep)); // overhangs the canvas

        Assert.Equal((0, 0, 200, 255), Pixel(composer, 0, 0));
        Assert.Equal((0, 0, 200, 255), Pixel(composer, 2, 2)); // transparent → previous pixel shows through
        Assert.Equal((0, 200, 0, 255), Pixel(composer, 3, 3));
    }

    [Fact]
    public void Composer_RestoreBackground_ClearsThePreviousFrameRect()
    {
        var composer = new GifComposer(4, 4);
        composer.Apply(new GifFrame(Solid(2, 2, 200, 0, 0), 0, 0, 2, 2, TimeSpan.FromMilliseconds(100), GifDisposal.RestoreBackground));
        composer.Apply(new GifFrame(Solid(1, 1, 0, 200, 0), 3, 3, 1, 1, TimeSpan.FromMilliseconds(100), GifDisposal.Keep));

        Assert.Equal((0, 0, 0, 0), Pixel(composer, 0, 0));
        Assert.Equal((0, 200, 0, 255), Pixel(composer, 3, 3));
    }

    [Fact]
    public void Composer_RestorePrevious_PutsBackTheCanvasBeforeThatFrame()
    {
        var composer = new GifComposer(2, 2);
        composer.Apply(new GifFrame(Solid(2, 2, 0, 0, 200), 0, 0, 2, 2, TimeSpan.FromMilliseconds(100), GifDisposal.Keep));
        composer.Apply(new GifFrame(Solid(1, 1, 200, 0, 0), 0, 0, 1, 1, TimeSpan.FromMilliseconds(100), GifDisposal.RestorePrevious));
        Assert.Equal((200, 0, 0, 255), Pixel(composer, 0, 0));

        composer.Apply(new GifFrame(Solid(1, 1, 0, 200, 0), 1, 1, 1, 1, TimeSpan.FromMilliseconds(100), GifDisposal.Keep));

        Assert.Equal((0, 0, 200, 255), Pixel(composer, 0, 0)); // the blue patch was removed again
        Assert.Equal((0, 200, 0, 255), Pixel(composer, 1, 1));
    }

    [Fact]
    public void Composer_Reset_ClearsForANewLoop()
    {
        var composer = new GifComposer(2, 2);
        composer.Apply(new GifFrame(Solid(2, 2, 1, 2, 3), 0, 0, 2, 2, TimeSpan.FromMilliseconds(100), GifDisposal.Keep));

        composer.Reset();

        Assert.All(composer.Canvas, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Reader_ReadsFramesDelaysAndLoop_FromAWindowsEncodedGif()
    {
        string path = Path.Combine(_dir, "anim.gif");
        await WriteGifAsync(path, 16, 12, [(0, 0, 220), (0, 220, 0), (220, 0, 0)], delayCentiseconds: 15, loop: true);

        using GifAnimationReader? reader = await GifAnimationReader.OpenAsync(path, Ct);

        Assert.NotNull(reader);
        Assert.Equal(3, reader.FrameCount);
        Assert.Equal((16, 12), (reader.Width, reader.Height));
        Assert.Equal(0, reader.LoopCount);
        Assert.Null(reader.TotalPlays); // forever

        var composer = new GifComposer(reader.Width, reader.Height);
        var colors = new List<(byte, byte, byte, byte)>();
        for (int i = 0; i < reader.FrameCount; i++)
        {
            GifFrame frame = await reader.ReadFrameAsync(i, Ct);
            Assert.Equal(TimeSpan.FromMilliseconds(150), frame.Delay);
            composer.Apply(frame);
            colors.Add(Pixel(composer, 8, 6));
        }

        Assert.Equal(3, colors.Distinct().Count()); // each frame shows its own color
    }

    [Fact]
    public async Task Reader_WithoutNetscapeBlock_PlaysOnce()
    {
        string path = Path.Combine(_dir, "once.gif");
        await WriteGifAsync(path, 8, 8, [(0, 0, 220), (0, 220, 0)], delayCentiseconds: 10, loop: false);

        using GifAnimationReader? reader = await GifAnimationReader.OpenAsync(path, Ct);

        Assert.NotNull(reader);
        Assert.Null(reader.LoopCount);
        Assert.Equal(1, reader.TotalPlays);
    }

    [Fact]
    public async Task Reader_RejectsNonGif()
    {
        string path = Path.Combine(_dir, "not.gif");
        await File.WriteAllTextAsync(path, "not a gif", Ct);

        using GifAnimationReader? reader = await GifAnimationReader.OpenAsync(path, Ct);

        Assert.Null(reader);
    }

    /// <summary>Writes a multi-frame GIF with Windows' encoder (also used by the E2E harness).</summary>
    internal static async Task WriteGifAsync(string path, int width, int height, (byte B, byte G, byte R)[] frames, ushort delayCentiseconds, bool loop)
    {
        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);
        if (loop)
        {
            await encoder.BitmapContainerProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                ["/appext/Application"] = new BitmapTypedValue(Encoding.ASCII.GetBytes("NETSCAPE2.0"), PropertyType.UInt8Array),
                ["/appext/Data"] = new BitmapTypedValue(new byte[] { 3, 1, 0, 0, 0 }, PropertyType.UInt8Array),
            });
        }

        for (int i = 0; i < frames.Length; i++)
        {
            if (i > 0)
            {
                await encoder.GoToNextFrameAsync();
            }

            (byte b, byte g, byte r) = frames[i];
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, Solid(width, height, b, g, r));
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                ["/grctlext/Delay"] = new BitmapTypedValue(delayCentiseconds, PropertyType.UInt16),
            });
        }

        await encoder.FlushAsync();
    }
}
