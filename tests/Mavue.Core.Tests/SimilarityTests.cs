using Mavue.Core.Similarity;

namespace Mavue.Core.Tests;

/// <summary>Signatures, distances, the signature cache and the worker protocol (ARCHITECTURE §4.7.14).</summary>
[Trait("Category", "QuickView")]
public sealed class SimilarityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "similar-" + Guid.NewGuid().ToString("N"));

    public SimilarityTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>A test picture: a diagonal gradient with a bright disc whose place depends on <paramref name="variant"/>.</summary>
    private static byte[] Picture(int width, int height, int variant, int brightness = 0)
    {
        byte[] p = new byte[width * height * 4];
        double cx = width * (0.3 + (0.4 * (variant % 3) / 2.0)), cy = height * (0.35 + (0.3 * (variant / 3 % 2)));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                double fx = x / (double)width, fy = y / (double)height;
                bool disc = Math.Pow((x - cx) / width, 2) + Math.Pow((y - cy) / height, 2) < 0.02;
                int r = disc ? 250 : (int)(200 * fx) + (variant * 13 % 40);
                int g = disc ? 240 : (int)(180 * fy);
                int b = disc ? 60 : (int)(120 * (1 - fx));
                p[i] = (byte)Math.Clamp(b + brightness, 0, 255);
                p[i + 1] = (byte)Math.Clamp(g + brightness, 0, 255);
                p[i + 2] = (byte)Math.Clamp(r + brightness, 0, 255);
                p[i + 3] = 255;
            }
        }

        return p;
    }

    private static (CoarseSignature, FineSignature) Sign(byte[] pixels, int width, int height) =>
        (ImageSignatures.Coarse(pixels, width, height, (double)width / height), ImageSignatures.Fine(pixels, width, height));

    [Fact]
    public void SamePicture_IsFullySimilar()
    {
        (CoarseSignature c, FineSignature f) = Sign(Picture(64, 48, 1), 64, 48);
        Assert.Equal(100, ImageSignatures.Similarity(c, f, c, f));
        Assert.True(ImageSignatures.MayBeSimilar(c, c));
    }

    [Fact]
    public void ResizedAndBrightened_StaySimilar()
    {
        (CoarseSignature c1, FineSignature f1) = Sign(Picture(128, 96, 1), 128, 96);
        (CoarseSignature c2, FineSignature f2) = Sign(Picture(40, 30, 1), 40, 30);
        (CoarseSignature c3, FineSignature f3) = Sign(Picture(128, 96, 1, brightness: 20), 128, 96);
        Assert.True(ImageSignatures.MayBeSimilar(c1, c2));
        Assert.True(ImageSignatures.Similarity(c1, f1, c2, f2) >= 85, $"resized: {ImageSignatures.Similarity(c1, f1, c2, f2)} p={System.Numerics.BitOperations.PopCount(f1.PerceptualHash ^ f2.PerceptualHash)} d={System.Numerics.BitOperations.PopCount(c1.DifferenceHash ^ c2.DifferenceHash)} a={System.Numerics.BitOperations.PopCount(c1.AverageHash ^ c2.AverageHash)} color={string.Join(",", f1.ColorLayout.Zip(f2.ColorLayout, (x, y) => Math.Abs(x - y)).Take(12))}");
        Assert.True(ImageSignatures.Similarity(c1, f1, c3, f3) >= 85, $"brightened: {ImageSignatures.Similarity(c1, f1, c3, f3)}");
    }

    [Fact]
    public void DifferentPictures_AreNotSimilar()
    {
        (CoarseSignature c1, FineSignature f1) = Sign(Picture(64, 48, 0), 64, 48);
        byte[] noise = new byte[64 * 48 * 4];
        new Random(3).NextBytes(noise);
        (CoarseSignature c2, FineSignature f2) = Sign(noise, 64, 48);
        Assert.True(ImageSignatures.Similarity(c1, f1, c2, f2) < 72, $"noise: {ImageSignatures.Similarity(c1, f1, c2, f2)}");
    }

    [Fact]
    public void BadInput_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => ImageSignatures.Coarse(new byte[10], 4, 4, 1));
        Assert.Throws<ArgumentException>(() => ImageSignatures.Fine(new byte[16], 0, 4));
        (CoarseSignature c, _) = Sign(Picture(1, 1, 0), 1, 1); // a single pixel works
        Assert.True(c.Aspect > 0);
    }

    [Fact]
    public void Cache_MatchesOnPathSizeAndTime_AndRoundTrips()
    {
        var cache = new SignatureCache();
        string file = Path.Combine(_folder, "a.jpg");
        var time = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        (CoarseSignature c, FineSignature f) = Sign(Picture(32, 32, 2), 32, 32);
        cache.Set(file, 100, time, c, null);
        Assert.True(cache.TryGet(file, 100, time, out CoarseSignature got, out FineSignature? fine));
        Assert.Equal(c, got);
        Assert.Null(fine);
        Assert.False(cache.TryGet(file, 101, time, out _, out _)); // changed size
        Assert.False(cache.TryGet(file, 100, time.AddSeconds(1), out _, out _)); // changed time
        cache.Set(file, 100, time, c, f);

        string path = Path.Combine(_folder, "cache.bin");
        Assert.True(cache.Save(path));
        SignatureCache loaded = SignatureCache.Load(path);
        Assert.True(loaded.TryGet(file.ToUpperInvariant(), 100, time, out _, out FineSignature? loadedFine)); // paths compare case-insensitively
        Assert.Equal(f.PerceptualHash, loadedFine!.Value.PerceptualHash);
        Assert.Equal(f.ColorLayout, loadedFine.Value.ColorLayout);
        Assert.DoesNotContain("a.jpg", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase); // paths are not stored
    }

    [Fact]
    public void Cache_IsBounded_AndIgnoresDamagedFiles()
    {
        var cache = new SignatureCache(maxEntries: 16);
        var time = DateTime.UtcNow;
        for (int i = 0; i < 100; i++)
        {
            cache.Set(Path.Combine(_folder, $"{i}.jpg"), i, time, default, null);
        }

        Assert.True(cache.Count <= 16 + 2);
        string path = Path.Combine(_folder, "damaged.bin");
        File.WriteAllBytes(path, [.. "MVSIG1\0\0"u8, 0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3]);
        Assert.Equal(0, SignatureCache.Load(path).Count);
        File.WriteAllText(path, "not a cache");
        Assert.Equal(0, SignatureCache.Load(path).Count);
    }

    [Fact]
    public void Protocol_RoundTrips()
    {
        var message = new SimilarSearchMessage { Type = SimilarSearchMessage.ProgressType, Stage = SimilarSearchStage.Fine, Done = 3, Total = 9, Candidates = 2 };
        SimilarSearchMessage back = SimilarSearchMessage.FromJson(message.ToJson())!;
        Assert.Equal(message, back);
        Assert.Contains("\"stage\":\"Fine\"", message.ToJson(), StringComparison.Ordinal);
        Assert.Null(SimilarSearchMessage.FromJson("{not json"));
    }
}
