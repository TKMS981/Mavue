using Mavue.Core.Formats;

namespace Mavue.Core.Tests;

[Trait("Category", "FileFormat")]
public sealed class MediaFormatDetectionTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    public static TheoryData<string, string?, FileFormat> Signatures => new()
    {
        // ISO BMFF: movie and audio brands (image brands are covered by FileFormatDetectorTests)
        { "00 00 00 18 66 74 79 70 69 73 6F 6D 00 00 02 00 69 73 6F 6D 61 76 63 31", ".mp4", FileFormat.Mp4 },
        { "00 00 00 14 66 74 79 70 71 74 20 20 00 00 02 00 71 74 20 20", ".mov", FileFormat.QuickTime },
        { "00 00 00 18 66 74 79 70 4D 34 41 20 00 00 02 00 4D 34 41 20 69 73 6F 6D", ".m4a", FileFormat.M4a },
        // Generic brand (as written by Media Foundation for AAC audio): the extension decides.
        { "00 00 00 18 66 74 79 70 6D 70 34 32 00 00 00 00 6D 70 34 31 69 73 6F 6D", ".m4a", FileFormat.M4a },
        { "00 00 00 18 66 74 79 70 6D 70 34 32 00 00 00 00 6D 70 34 31 69 73 6F 6D", ".mp4", FileFormat.Mp4 },
        { "00 00 00 18 66 74 79 70 6D 70 34 32 00 00 00 00 6D 70 34 31 69 73 6F 6D", ".jpg", FileFormat.Mp4 },
        // EBML with DocType
        { "1A 45 DF A3 9F 42 86 81 01 42 F7 81 01 42 F2 81 04 42 F3 81 08 42 82 84 77 65 62 6D", ".webm", FileFormat.WebM },
        { "1A 45 DF A3 A3 42 86 81 01 42 F7 81 01 42 F2 81 04 42 F3 81 08 42 82 88 6D 61 74 72 6F 73 6B 61", ".mkv", FileFormat.Matroska },
        // RIFF
        { "52 49 46 46 24 00 00 00 57 41 56 45 66 6D 74 20", ".wav", FileFormat.Wav },
        { "52 49 46 46 24 00 00 00 41 56 49 20 4C 49 53 54", ".avi", FileFormat.Avi },
        // ASF: video or audio by extension
        { "30 26 B2 75 8E 66 CF 11 A6 D9 00 AA 00 62 CE 6C", ".wmv", FileFormat.WindowsMediaVideo },
        { "30 26 B2 75 8E 66 CF 11 A6 D9 00 AA 00 62 CE 6C", ".wma", FileFormat.WindowsMediaAudio },
        { "66 4C 61 43 00 00 00 22", ".flac", FileFormat.Flac },
        { "4F 67 67 53 00 02 00 00 00 00 00 00 00 00 01 4F 70 75 73 48 65 61 64", ".opus", FileFormat.OggAudio },
        { "4F 67 67 53 00 02 00 00 00 00 00 00 00 00 80 74 68 65 6F 72 61", ".ogg", FileFormat.OggVideo },
        { "00 00 01 BA 44 00 04 00 04 01", ".mpg", FileFormat.MpegProgramStream },
        { "49 44 33 04 00 00 00 00 00 21", ".mp3", FileFormat.Mp3 },
        { "FF FB 90 64 00", ".mp3", FileFormat.Mp3 },
        { "FF F1 50 80 02 1F FC", ".aac", FileFormat.Aac },
        // Content wins over a misleading extension.
        { "FF FB 90 64 00", ".mp4", FileFormat.Mp3 },
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public void Detect_RecognizesMediaSignature(string hex, string? extension, FileFormat expected) =>
        Assert.Equal(expected, FileFormatDetector.Detect(Hex(hex), extension));

    [Fact]
    public void Detect_TransportStream_BySyncBytes()
    {
        byte[] packets = new byte[188 * 2];
        packets[0] = 0x47;
        packets[188] = 0x47;
        Assert.Equal(FileFormat.MpegTransportStream, FileFormatDetector.Detect(packets, ".ts"));
    }

    [Fact]
    public void Detect_ImagesKeepTheirFormat()
    {
        // GIF starts with 0x47 ('G') like a transport stream packet, JPEG with 0xFF like an MPEG audio frame.
        byte[] gif = new byte[400];
        "GIF89a"u8.CopyTo(gif);
        gif[188] = 0x47;
        Assert.Equal(FileFormat.Gif, FileFormatDetector.Detect(gif, ".gif"));
        Assert.Equal(FileFormat.Jpeg, FileFormatDetector.Detect(Hex("FF D8 FF E0 00 10"), ".jpg"));
    }

    [Theory]
    [InlineData(".mp4", FileFormat.Mp4)]
    [InlineData(".MKV", FileFormat.Matroska)]
    [InlineData(".m4b", FileFormat.M4a)]
    [InlineData(".opus", FileFormat.OggAudio)]
    [InlineData(".wma", FileFormat.WindowsMediaAudio)]
    public void Extension_Fallback(string extension, FileFormat expected) =>
        Assert.Equal(expected, FileFormatDetector.Detect([0, 1, 2, 3], extension));

    [Fact]
    public void Kinds_SeparateVideoAudioAndImages()
    {
        Assert.True(FileFormatKinds.IsVideo(FileFormat.Mp4));
        Assert.True(FileFormatKinds.IsVideo(FileFormat.WebM));
        Assert.False(FileFormatKinds.IsAudio(FileFormat.Mp4));
        Assert.True(FileFormatKinds.IsAudio(FileFormat.Flac));
        Assert.True(FileFormatKinds.IsMedia(FileFormat.M4a));
        Assert.False(FileFormatKinds.IsMedia(FileFormat.Gif));
        Assert.False(FileFormatKinds.IsMedia(FileFormat.Pdf));
        Assert.False(FileFormatKinds.IsMedia(FileFormat.Unknown));
    }
}
