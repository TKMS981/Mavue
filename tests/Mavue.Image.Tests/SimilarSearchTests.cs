using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.Core.Similarity;
using Mavue.Image.Similarity;
using Mavue.Image.Wic;
using Mavue.Metadata;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.Image.Tests;

/// <summary>The similar-image search on real files, and clean copies of real JPEGs through Windows' decoder.</summary>
public sealed class SimilarSearchTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "similar-search-" + Guid.NewGuid().ToString("N"));

    public SimilarSearchTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WriteAsync(string path, Guid encoder, uint width, uint height, int seed, bool noise = false)
    {
        byte[] pixels = new byte[width * height * 4];
        var random = new Random(seed);
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                uint i = ((y * width) + x) * 4;
                bool spot = Math.Pow((x / (double)width) - (0.3 + (seed % 3 * 0.2)), 2) + Math.Pow((y / (double)height) - 0.5, 2) < 0.03;
                pixels[i] = noise ? (byte)random.Next(256) : (byte)(spot ? 40 : 255 * x / width);
                pixels[i + 1] = noise ? (byte)random.Next(256) : (byte)(spot ? 220 : 255 * y / height);
                pixels[i + 2] = noise ? (byte)random.Next(256) : (byte)(spot ? 250 : 120);
                pixels[i + 3] = 255;
            }
        }

        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path));
        StorageFile file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder bitmap = await BitmapEncoder.CreateAsync(encoder, stream);
        bitmap.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await bitmap.FlushAsync();
    }

    private SimilarSearchRequest Request(string target, bool subfolders = false, int maxFiles = 1000) =>
        new(target, _folder, subfolders, maxFiles, 72, 60, 64, Path.Combine(_folder, "cache", "signatures.bin"), 2);

    [Fact]
    public async Task FindsCopiesAndResizedVersions_NotOtherPictures()
    {
        string target = Path.Combine(_folder, "target.png");
        await WriteAsync(target, BitmapEncoder.PngEncoderId, 400, 300, seed: 1);
        await WriteAsync(Path.Combine(_folder, "copy.jpg"), BitmapEncoder.JpegEncoderId, 400, 300, seed: 1);
        await WriteAsync(Path.Combine(_folder, "small.png"), BitmapEncoder.PngEncoderId, 120, 90, seed: 1);
        await WriteAsync(Path.Combine(_folder, "other.png"), BitmapEncoder.PngEncoderId, 400, 300, seed: 7, noise: true);
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not an image");
        File.WriteAllBytes(Path.Combine(_folder, "broken.jpg"), [0xFF, 0xD8, 0xFF, 1, 2, 3]);

        var messages = new List<SimilarSearchMessage>();
        SimilarImageSearch.Run(Request(target), m => { lock (messages) { messages.Add(m); } }, Ct);
        string[] results = [.. messages.Where(m => m.Type == SimilarSearchMessage.ResultType).Select(m => Path.GetFileName(m.Path!))];
        Assert.Contains("copy.jpg", results);
        Assert.Contains("small.png", results);
        Assert.DoesNotContain("other.png", results);
        Assert.DoesNotContain("target.png", results); // not itself
        SimilarSearchMessage done = messages.Last();
        Assert.Equal(SimilarSearchMessage.DoneType, done.Type);
        Assert.Equal(4, done.Examined); // the text file is not listed; the broken JPEG is examined and skipped
        Assert.True(done.Skipped >= 1);
        SimilarSearchMessage first = messages.First(m => m.Type == SimilarSearchMessage.ResultType);
        Assert.Equal(first.ThumbnailWidth * first.ThumbnailHeight * 4, Convert.FromBase64String(first.Thumbnail!).Length);
        Assert.True(first.ThumbnailWidth <= 64 && first.ThumbnailHeight <= 64);

        // A second search reads the signatures from the cache.
        var again = new List<SimilarSearchMessage>();
        SimilarImageSearch.Run(Request(target), m => { lock (again) { again.Add(m); } }, Ct);
        Assert.True(again.Last().CacheHits >= 4, $"cache hits {again.Last().CacheHits}");
    }

    [Fact]
    public async Task Listing_IsBounded_AndSearchCanBeCancelled()
    {
        string target = Path.Combine(_folder, "target.png");
        await WriteAsync(target, BitmapEncoder.PngEncoderId, 64, 64, seed: 2);
        for (int i = 0; i < 6; i++)
        {
            await WriteAsync(Path.Combine(_folder, $"{i}.png"), BitmapEncoder.PngEncoderId, 64, 64, seed: i);
        }

        (List<FileInfo> files, _, bool truncated) = SimilarImageSearch.List(Request(target, maxFiles: 3), Ct);
        Assert.Equal(3, files.Count);
        Assert.True(truncated);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SimilarImageSearch.Run(Request(target), _ => { }, cancelled.Token));
    }

    [Fact]
    public async Task UnreadableTarget_IsReported()
    {
        string target = Path.Combine(_folder, "broken.png");
        File.WriteAllBytes(target, [0x89, 0x50, 0x4E, 0x47, 1, 2]);
        var messages = new List<SimilarSearchMessage>();
        SimilarImageSearch.Run(Request(target), messages.Add, Ct);
        Assert.Equal("target-unreadable", Assert.Single(messages).Code);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RealJpeg_CleanCopy_DecodesWithTheSameSizeAndPixels()
    {
        string original = Path.Combine(_folder, "photo.jpg");
        await WriteAsync(original, BitmapEncoder.JpegEncoderId, 320, 200, seed: 4);

        // Add an EXIF block with a location (Windows' encoder wrote a clean JPEG).
        byte[] jpeg = File.ReadAllBytes(original);
        byte[] exif = MinimalExifWithGps();
        byte[] withExif = [0xFF, 0xD8, 0xFF, 0xE1, (byte)((exif.Length + 8) >> 8), (byte)(exif.Length + 8), .. "Exif\0\0"u8, .. exif, .. jpeg[2..]];
        File.WriteAllBytes(original, withExif);
        Assert.Contains(PrivacyCheck.Review(ImageMetadataReader.Read(original)), f => f.Kind == PrivacyKind.Location);

        string clean = Path.Combine(_folder, "clean.jpg");
        using (var input = File.OpenRead(original))
        using (var output = File.Create(clean))
        {
            MetadataStripper.Strip(input, output, StripOptions.Default);
        }

        Assert.Empty(PrivacyCheck.Review(ImageMetadataReader.Read(clean, findTrailer: true)));
        DecodedPixels a = WicPreviewDecoder.DecodeToFit(original, 320, 200, long.MaxValue, Ct)!;
        DecodedPixels b = WicPreviewDecoder.DecodeToFit(clean, 320, 200, long.MaxValue, Ct)!;
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        Assert.Equal(a.Pixels, b.Pixels); // the compressed data is untouched
    }

    private static byte[] MinimalExifWithGps()
    {
        // II*, IFD0 with one GPS pointer; GPS IFD with latitude ref + latitude.
        byte[] o = new byte[8 + 2 + 12 + 4 + 2 + (12 * 2) + 4 + 24];
        "II*\0"u8.CopyTo(o);
        o[4] = 8;
        o[8] = 1; // one entry
        o[10] = 0x25; o[11] = 0x88; o[12] = 4; o[14] = 1; // 0x8825 LONG 1
        o[18] = 26; // GPS IFD at 26
        int gps = 26;
        o[gps] = 2;
        o[gps + 2] = 1; o[gps + 4] = 2; o[gps + 6] = 2; o[gps + 10] = (byte)'N'; // LatitudeRef "N"
        o[gps + 14] = 2; o[gps + 16] = 5; o[gps + 18] = 3; o[gps + 22] = 56; // Latitude RATIONAL ×3 at 56
        for (int i = 0; i < 3; i++)
        {
            o[56 + (i * 8)] = (byte)(10 + i);
            o[56 + (i * 8) + 4] = 1;
        }

        return o;
    }
}
