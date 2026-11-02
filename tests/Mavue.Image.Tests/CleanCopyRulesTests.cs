using Mavue.Core.IO;
using Mavue.Core.Viewing;
using Mavue.Image.Export;
using Mavue.Image.Wic;
using Mavue.Metadata;
using Windows.Graphics.Imaging;

namespace Mavue.Image.Tests;

/// <summary>
/// The rules and the first step shared by Quick View's check before sharing and the main app's "Export without
/// metadata" (ARCHITECTURE §4.7.13, §4.7.15): names, messages, limits, temporary clean-up and the review.
/// </summary>
public sealed class CleanCopyRulesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "clean-copy-" + Guid.NewGuid().ToString("N"));

    public CleanCopyRulesTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void NameNextTo_FindsAnUnusedName_AndNeverTheOriginal()
    {
        string original = Path.Combine(_folder, "photo.jpg");
        File.WriteAllBytes(original, [1]);
        string first = CleanCopyRules.NameNextTo(original, "{0} (no metadata)", ".jpg");
        Assert.Equal(Path.Combine(_folder, "photo (no metadata).jpg"), first);
        File.WriteAllBytes(first, [1]);
        Assert.Equal(Path.Combine(_folder, "photo (no metadata) (2).jpg"), CleanCopyRules.NameNextTo(original, "{0} (no metadata)", ".jpg"));

        // A pattern without a suffix would give the original's name: still refused by IsOriginal.
        Assert.True(CleanCopyRules.IsOriginal(original, Path.Combine(_folder, "PHOTO.JPG")));
        Assert.True(CleanCopyRules.IsOriginal(original, Path.Combine(_folder, "sub", "..", "photo.jpg")));
        Assert.False(CleanCopyRules.IsOriginal(original, first));
    }

    [Fact]
    public void Failures_GetTheSameMessageInBothApps()
    {
        Assert.Equal(CleanCopyFailure.NotVerified, CleanCopyRules.Classify(new SafeWriteValidationException("x")));
        Assert.Equal(CleanCopyFailure.NoCodec, CleanCopyRules.Classify(new CleanExportException("unsupported")));
        Assert.Equal(CleanCopyFailure.NoCodec, CleanCopyRules.Classify(new NotSupportedException()));
        Assert.Equal(CleanCopyFailure.TooLarge, CleanCopyRules.Classify(new CleanExportException("too-large")));
        Assert.Equal(CleanCopyFailure.TooLarge, CleanCopyRules.Classify(new ImageTooLargeException(1, 1)));
        Assert.Equal(CleanCopyFailure.NotWritable, CleanCopyRules.Classify(new UnauthorizedAccessException()));
        Assert.Equal(CleanCopyFailure.Failed, CleanCopyRules.Classify(new CleanExportException("start-failed")));
        Assert.Equal(CleanCopyFailure.Failed, CleanCopyRules.Classify(new IOException()));
        Assert.Equal(CleanCopyFailure.Failed, CleanCopyRules.Classify(new ArgumentException()));
        Assert.Null(CleanCopyRules.Classify(new InvalidOperationException()));
        Assert.Null(CleanCopyRules.Classify(new OperationCanceledException()));
    }

    [Fact]
    public void WorkerMemory_GrowsWithTheMachine()
    {
        Assert.True(CleanCopyRules.WorkerMemoryLimit(PerformanceTier.Low) < CleanCopyRules.WorkerMemoryLimit(PerformanceTier.Balanced));
        Assert.True(CleanCopyRules.WorkerMemoryLimit(PerformanceTier.Balanced) < CleanCopyRules.WorkerMemoryLimit(PerformanceTier.High));
        Assert.Equal(1L << 30, CleanCopyRules.WorkerMemoryLimit(PerformanceTier.Low));
    }

    [Fact]
    public void OldTemporaryCopies_AreDeleted_RunningWorkIsKept()
    {
        DateTime now = DateTime.UtcNow;
        string old = Directory.CreateDirectory(Path.Combine(_folder, "old")).FullName;
        File.WriteAllBytes(Path.Combine(old, "a.jpg"), [1]);
        Directory.SetCreationTimeUtc(old, now.AddHours(-30));
        string recent = Directory.CreateDirectory(Path.Combine(_folder, "recent")).FullName;
        string work = Directory.CreateDirectory(Path.Combine(_folder, CleanCopyRules.WorkFolder)).FullName;
        Directory.SetCreationTimeUtc(work, now.AddDays(-10));
        string leftover = Path.Combine(work, "left.jpg");
        string running = Path.Combine(work, "running.jpg");
        File.WriteAllBytes(leftover, [1]);
        File.WriteAllBytes(running, [1]);
        File.SetLastWriteTimeUtc(leftover, now.AddHours(-30));

        CleanCopyRules.DeleteOldTemporaryCopies(_folder, now);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(work)); // the work folder itself stays
        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(running));

        CleanCopyRules.DeleteOldTemporaryCopies(Path.Combine(_folder, "missing"), now); // nothing there: no error
    }

    [Fact]
    public async Task Review_JpegWithLocation_CanBeCopiedAsItIs()
    {
        string source = Path.Combine(_folder, "located.jpg");
        await RotationAndExportTests.WriteAsync(source, BitmapEncoder.JpegEncoderId, RotationAndExportTests.Picture(160, 120), 160, 120);
        byte[] jpeg = File.ReadAllBytes(source);
        byte[] exif = RotationAndExportTests.SidewaysExif();
        File.WriteAllBytes(source, [0xFF, 0xD8, 0xFF, 0xE1, (byte)((exif.Length + 8) >> 8), (byte)(exif.Length + 8), .. "Exif\0\0"u8, .. exif, .. jpeg[2..]]);

        bool fallbackUsed = false;
        PrivacyReview review = await PrivacyReview.ReadAsync(source, (_, _) => { fallbackUsed = true; return Task.FromResult<IReadOnlyList<PrivacyFinding>>([]); }, Ct);
        Assert.False(fallbackUsed); // Mavue reads JPEG itself
        Assert.Equal(MetadataContainer.Jpeg, review.Metadata.Container);
        Assert.True(review.HasPrivateData);
        Assert.Contains(review.Findings, f => f.Kind == PrivacyKind.Location && f.Level == PrivacyLevel.High);
        Assert.True(review.Plan.CanKeepFormat);
        Assert.True(review.CanDecode);
        Assert.False(review.CannotCopy);
    }

    [Fact]
    public async Task Review_UndecodableHeif_CannotBeCopied_AndSaysSoWithoutThrowing()
    {
        string fake = Path.Combine(_folder, "fake.heic");
        File.WriteAllBytes(fake, [0, 0, 0, 24, .. "ftypheic"u8, 0, 0, 0, 0, .. "mif1heic"u8, 1, 2, 3, 4]);
        PrivacyReview review = await PrivacyReview.ReadAsync(fake, null, Ct);
        Assert.Equal(MetadataContainer.Heif, review.Metadata.Container);
        Assert.False(review.Plan.CanKeepFormat);
        Assert.False(review.CanDecode);
        Assert.True(review.CannotCopy);

        // A file that is gone: an empty review, not an exception.
        PrivacyReview missing = await PrivacyReview.ReadAsync(Path.Combine(_folder, "missing.jpg"), null, Ct);
        Assert.Equal(MetadataContainer.Unsupported, missing.Metadata.Container);
        Assert.True(missing.CannotCopy);
        Assert.False(missing.HasPrivateData);
    }

    [Fact]
    public async Task Review_UnreadTypes_UseTheFallback()
    {
        string bmp = Path.Combine(_folder, "plain.bmp");
        await RotationAndExportTests.WriteAsync(bmp, BitmapEncoder.BmpEncoderId, RotationAndExportTests.Picture(64, 48), 64, 48);
        PrivacyReview review = await PrivacyReview.ReadAsync(
            bmp,
            (_, _) => Task.FromResult<IReadOnlyList<PrivacyFinding>>([new(PrivacyKind.CaptureTime, PrivacyLevel.Medium, "2026")]),
            Ct);
        Assert.Equal(MetadataContainer.Unsupported, review.Metadata.Container);
        Assert.Single(review.Findings, f => f.Kind == PrivacyKind.CaptureTime);
        Assert.False(review.Plan.CanKeepFormat); // converted
        Assert.True(review.CanDecode);
        Assert.False(review.CannotCopy);
        Assert.Equal("", PrivacyReview.Glyph(PrivacyLevel.High));
        Assert.Equal("", PrivacyReview.Glyph(PrivacyLevel.Kept));
    }
}
