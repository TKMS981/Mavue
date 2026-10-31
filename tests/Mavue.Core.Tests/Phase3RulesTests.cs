using Mavue.Core.Archives;
using Mavue.Core.Formats;
using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

/// <summary>Quick Preview Phase 3 rules: which files are archives, safe display of entry names, wheel seeking and the waveform columns.</summary>
[Trait("Category", "QuickView")]
public class Phase3RulesTests
{
    private static FileFacts Facts(FileFormat format, PreviewAccess access = PreviewAccess.Allowed) =>
        new(FileAttributes.Normal, 1234, format, access);

    [Theory]
    [InlineData(".zip")]
    [InlineData(".ZIP")]
    [InlineData(".cbz")]
    public void ZipAndComicArchives_AreZip_ByExtension(string extension)
    {
        Assert.Equal(FileFormat.Zip, FileFormatDetector.FromExtension(extension));
        byte[] zip = [0x50, 0x4B, 0x03, 0x04, .. new byte[40]];
        Assert.Equal(FileFormat.Zip, FileFormatDetector.Detect(zip, extension));
        Assert.Equal(FileKindCategory.Archive, UniversalFileInfo.CategoryFor(extension, FileFormat.Zip));
        Assert.Equal("application/zip", UniversalFileInfo.MimeTypeFor(extension, FileFormat.Zip));
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".jar")]
    [InlineData(".epub")]
    public void OtherZipContainers_KeepTheirOwnType(string extension)
    {
        byte[] zip = [0x50, 0x4B, 0x03, 0x04, .. new byte[40]];
        Assert.Equal(FileFormat.Unknown, FileFormatDetector.Detect(zip, extension));
    }

    [Fact]
    public void Zip_GoesToTheArchivePreview_ButCloudOnlyArchivesAreNotRead()
    {
        Assert.Equal(PreviewContentKind.Archive, PreviewContentRouting.Choose(Facts(FileFormat.Zip), true, ".zip"));
        Assert.Equal(PreviewContentKind.Archive, PreviewContentRouting.Choose(Facts(FileFormat.Zip), false, ".cbz"));
        Assert.Equal(PreviewContentKind.Universal, PreviewContentRouting.Choose(Facts(FileFormat.Zip, PreviewAccess.CloudPlaceholder), true, ".zip"));
        Assert.Equal(PreviewContentKind.Universal, PreviewContentRouting.Choose(Facts(FileFormat.Zip, PreviewAccess.NotAFile), true, ".zip"));
        Assert.False(PreviewPrefetchPlan.PreparesNeighbors(PreviewContentKind.Archive)); // neighbors are never listed ahead
        Assert.False(FileFormatKinds.MayHaveTransparency(FileFormat.Zip));
        Assert.DoesNotContain(FileFormat.Zip, ViewerFormats.All); // no file association or Explorer handler is taken for ZIP
    }

    [Theory]
    [InlineData("photo‮gpj.exe", ArchiveNameCheck.BidiControl)]
    [InlineData("a/⁦b.txt", ArchiveNameCheck.BidiControl)]
    [InlineData("tab\there.txt", ArchiveNameCheck.InvalidCharacters)]
    [InlineData("del\u007F.txt", ArchiveNameCheck.InvalidCharacters)]
    [InlineData("日本語/写真.png", ArchiveNameCheck.Safe)]
    public void EntryNames_WithDisguisingCharacters_AreFlagged(string name, ArchiveNameCheck expected) =>
        Assert.Equal(expected, ArchiveEntryName.Check(name));

    [Fact]
    public void EntryNames_AreShownWithoutControlOrDirectionCharacters()
    {
        Assert.Equal("photo�gpj.exe", ArchiveEntryName.ForDisplay("photo‮gpj.exe"));
        Assert.Equal("a�b", ArchiveEntryName.ForDisplay("a\nb"));
        string plain = "写真 01.jpg";
        Assert.Same(plain, ArchiveEntryName.ForDisplay(plain));
    }

    [Fact]
    public void WheelSeek_IsFiveSecondsPerNotch_UpIsForward_ShiftIsThirty()
    {
        int accumulated = 0;
        Assert.Equal(TimeSpan.FromSeconds(5), MediaControlMath.WheelSeek(120, large: false, ref accumulated));
        Assert.Equal(TimeSpan.FromSeconds(-10), MediaControlMath.WheelSeek(-240, large: false, ref accumulated));
        Assert.Equal(TimeSpan.FromSeconds(30), MediaControlMath.WheelSeek(120, large: true, ref accumulated));
        Assert.Equal(0, accumulated);
    }

    [Fact]
    public void WheelSeek_AddsUpTouchpadDeltas()
    {
        int accumulated = 0;
        Assert.Equal(TimeSpan.Zero, MediaControlMath.WheelSeek(40, false, ref accumulated));
        Assert.Equal(TimeSpan.Zero, MediaControlMath.WheelSeek(40, false, ref accumulated));
        Assert.Equal(TimeSpan.FromSeconds(5), MediaControlMath.WheelSeek(50, false, ref accumulated));
        Assert.Equal(10, accumulated);
        Assert.Equal(TimeSpan.Zero, MediaControlMath.WheelSeek(-100, false, ref accumulated)); // -90: not a notch yet
        Assert.Equal(-90, accumulated);
    }

    [Fact]
    public void WaveformColumns_KeepEveryPeak()
    {
        float[] buckets = [0.1f, 0.9f, 0.2f, 0.3f, 0.0f, 0.5f, 0.4f, 0.1f];

        Assert.Equal([0.9f, 0.3f, 0.5f, 0.4f], MediaControlMath.WaveformColumns(buckets, 4));
        Assert.Equal([0.9f, 0.5f], MediaControlMath.WaveformColumns(buckets, 2));
        Assert.Equal(16, MediaControlMath.WaveformColumns(buckets, 16).Length); // fewer buckets than columns: repeated
        Assert.Equal(0.9f, MediaControlMath.WaveformColumns(buckets, 16)[2]);
        Assert.Empty(MediaControlMath.WaveformColumns(buckets, 0));
        Assert.Equal([1f], MediaControlMath.WaveformColumns([3f], 1)); // clamped
    }
}
