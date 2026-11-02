using Mavue.Core.Similarity;
using Mavue.Core.Viewing;
using Mavue.Image.Export;
using Mavue.Image.Similarity;
using Mavue.Image.Wic;
using Mavue.Metadata;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.Image.Tests;

/// <summary>Turned and mirrored copies in the similar search, and converted exports without metadata (ARCHITECTURE §4.7.14–15).</summary>
public sealed class RotationAndExportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "rotation-export-" + Guid.NewGuid().ToString("N"));

    public RotationAndExportTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An asymmetric picture (a bright corner square, a diagonal band), optionally turned before encoding.</summary>
    internal static byte[] Picture(int width, int height)
    {
        byte[] p = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                bool corner = x < width / 4 && y < height / 3;
                bool band = Math.Abs((x * height / width) - y) < height / 10;
                p[i] = (byte)(corner ? 30 : band ? 200 : 60 + (x * 120 / width));
                p[i + 1] = (byte)(corner ? 220 : band ? 40 : 80 + (y * 100 / height));
                p[i + 2] = (byte)(corner ? 250 : band ? 90 : 140);
                p[i + 3] = 255;
            }
        }

        return p;
    }

    internal static async Task WriteAsync(string path, Guid encoderId, byte[] pixels, int width, int height)
    {
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path));
        StorageFile file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    private static (byte[] Pixels, int Width, int Height) Turn(byte[] pixels, int width, int height, ViewOrientation orientation)
    {
        (int w, int h) = orientation.Oriented(width, height);
        byte[] output = new byte[w * h * 4];
        PixelOrientation.Apply(pixels, width, height, width * 4, output, w * 4, orientation);
        return (output, w, h);
    }

    [Fact]
    public async Task TurnedAndMirroredCopies_AreFound_OnlyWithRotations()
    {
        byte[] picture = Picture(400, 300);
        string target = Path.Combine(_folder, "target.png");
        await WriteAsync(target, BitmapEncoder.PngEncoderId, picture, 400, 300);
        (byte[] turned, int tw, int th) = Turn(picture, 400, 300, new ViewOrientation(1, false));
        await WriteAsync(Path.Combine(_folder, "turned.png"), BitmapEncoder.PngEncoderId, turned, tw, th);
        (byte[] mirrored, int mw, int mh) = Turn(picture, 400, 300, new ViewOrientation(0, true));
        await WriteAsync(Path.Combine(_folder, "mirrored.png"), BitmapEncoder.PngEncoderId, mirrored, mw, mh);

        List<SimilarSearchMessage> Search(bool rotations)
        {
            var messages = new List<SimilarSearchMessage>();
            var request = new SimilarSearchRequest(target, _folder, false, 100, 72, 10, 32, Path.Combine(_folder, "cache.bin"), 1, rotations);
            SimilarImageSearch.Run(request, m => { lock (messages) { messages.Add(m); } }, Ct);
            return [.. messages.Where(m => m.Type == SimilarSearchMessage.ResultType)];
        }

        List<SimilarSearchMessage> with = Search(rotations: true);
        SimilarSearchMessage turnedResult = Assert.Single(with, m => Path.GetFileName(m.Path) == "turned.png");
        SimilarSearchMessage mirroredResult = Assert.Single(with, m => Path.GetFileName(m.Path) == "mirrored.png");
        Assert.Equal(90, turnedResult.Rotation);
        Assert.True(mirroredResult.Mirrored);

        List<SimilarSearchMessage> without = Search(rotations: false);
        Assert.DoesNotContain(without, m => Path.GetFileName(m.Path) == "turned.png");
        Assert.Equal(8, SimilarImageSearch.Orientations(true).Distinct().Count());
        Assert.Single(SimilarImageSearch.Orientations(false));
    }

    [Fact]
    public async Task ConvertedExport_AppliesOrientation_KeepsChosenValues_AndVerifies()
    {
        // A JPEG stored sideways (EXIF orientation 6) with a camera name and a location.
        string source = Path.Combine(_folder, "sideways.jpg");
        await WriteAsync(source, BitmapEncoder.JpegEncoderId, Picture(320, 200), 320, 200);
        byte[] jpeg = File.ReadAllBytes(source);
        byte[] exif = SidewaysExif();
        File.WriteAllBytes(source, [0xFF, 0xD8, 0xFF, 0xE1, (byte)((exif.Length + 8) >> 8), (byte)(exif.Length + 8), .. "Exif\0\0"u8, .. exif, .. jpeg[2..]]);

        string output = Path.Combine(_folder, "clean.png");
        var options = new CleanExportOptions(new StripOptions(RemoveCameraDetails: false), ExportFormat.Png);
        CleanExportResult result;
        await using (var stream = File.Create(output))
        {
            result = await CleanExport.WriteAsync(source, stream, options, Ct);
        }

        Assert.True(result.Converted);
        Assert.True(CleanExport.Verify(output, options.Strip, (200, 320))); // upright: the sides are swapped
        ImageMetadata after = ImageMetadataReader.Read(output);
        Assert.Equal("Mavue Cam", after.Exif!.Make);
        Assert.Null(after.Exif.Orientation);
        Assert.Null(after.Exif.Gps);
    }

    [Fact]
    public async Task LosslessExport_KeepsTheType()
    {
        string source = Path.Combine(_folder, "plain.jpg");
        await WriteAsync(source, BitmapEncoder.JpegEncoderId, Picture(160, 120), 160, 120);
        CleanExportPlan plan = CleanExport.Plan(source);
        Assert.True(plan.CanKeepFormat);
        Assert.False(plan.Converts(ExportFormat.Original));
        Assert.Equal(".jpg", plan.ExtensionFor(ExportFormat.Original));
        Assert.True(plan.Converts(ExportFormat.Png));

        var memory = new MemoryStream();
        CleanExportResult result = await CleanExport.WriteAsync(source, memory, new CleanExportOptions(StripOptions.Default), Ct);
        Assert.False(result.Converted);
        Assert.Equal(0xFF, memory.ToArray()[0]);
    }

    [Fact]
    public async Task UnreadableImages_AreReported()
    {
        string source = Path.Combine(_folder, "fake.heic");
        File.WriteAllBytes(source, [0, 0, 0, 24, .. "ftypheic"u8, 0, 0, 0, 0, .. "mif1heic"u8, 1, 2, 3, 4]);
        var options = new CleanExportOptions(StripOptions.Default, ExportFormat.Jpeg);
        await Assert.ThrowsAsync<NotSupportedException>(() => CleanExport.WriteAsync(source, new MemoryStream(), options, Ct));
        Assert.False(CleanExport.Plan(source).CanKeepFormat);
    }

    [Fact]
    public async Task TooLargeImages_AreNotConverted()
    {
        string source = Path.Combine(_folder, "big.png");
        await WriteAsync(source, BitmapEncoder.PngEncoderId, Picture(300, 300), 300, 300);
        var options = new CleanExportOptions(StripOptions.Default, ExportFormat.Jpeg, MaxPixels: 10_000);
        await Assert.ThrowsAsync<ImageTooLargeException>(() => CleanExport.WriteAsync(source, new MemoryStream(), options, Ct));
    }

    /// <summary>II*; IFD0: Make "Mavue Cam", Orientation 6, GPS pointer; GPS: latitude.</summary>
    internal static byte[] SidewaysExif()
    {
        var w = new List<byte>();
        void U16(int v) => w.AddRange([(byte)v, (byte)(v >> 8)]);
        void U32(int v) => w.AddRange([(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]);
        void Entry(int tag, int type, int count, int value) { U16(tag); U16(type); U32(count); U32(value); }
        // header 8, IFD0 (3 entries) 2+36+4 = 42 → 50, GPS (2 entries) 2+24+4 = 30 → 80, "Mavue Cam\0" at 80 (10), rationals at 90.
        w.AddRange("II*\0"u8.ToArray());
        U32(8);
        U16(3);
        Entry(0x010F, 2, 10, 80);
        Entry(0x0112, 3, 1, 6);
        Entry(0x8825, 4, 1, 50);
        U32(0);
        U16(2);
        Entry(0x0001, 2, 2, 'N');
        Entry(0x0002, 5, 3, 90);
        U32(0);
        w.AddRange("Mavue Cam\0"u8.ToArray());
        foreach (int v in new[] { 35, 1, 40, 1, 52, 1 })
        {
            U32(v);
        }

        return [.. w];
    }
}
