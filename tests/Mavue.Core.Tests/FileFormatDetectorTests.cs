using System.Text;
using Mavue.Core.Formats;

namespace Mavue.Core.Tests;

[Trait("Category", "FileFormat")]
public class FileFormatDetectorTests
{
    public static TheoryData<string, FileFormat> Signatures => new()
    {
        { "25 50 44 46 2D 31 2E 37 0A", FileFormat.Pdf },
        { "FF D8 FF E0 00 10 4A 46 49 46", FileFormat.Jpeg },
        { "89 50 4E 47 0D 0A 1A 0A 00 00 00 0D", FileFormat.Png },
        { "47 49 46 38 39 61 01 00", FileFormat.Gif },
        { "47 49 46 38 37 61 01 00", FileFormat.Gif },
        { "42 4D 3A 00 00 00 00 00 00 00 36 00 00 00", FileFormat.Bmp },
        { "49 49 2A 00 08 00 00 00", FileFormat.Tiff },
        { "4D 4D 00 2A 00 00 00 08", FileFormat.Tiff },
        { "49 49 2B 00 08 00 00 00", FileFormat.Tiff },
        { "52 49 46 46 24 00 00 00 57 45 42 50 56 50 38 20", FileFormat.WebP },
        { "00 00 01 00 01 00 10 10", FileFormat.Ico },
        { "00 00 00 0C 6A 50 20 20 0D 0A 87 0A", FileFormat.Jpeg2000 },
        { "FF 4F FF 51 00 2F", FileFormat.Jpeg2000 },
        { "FF 0A FA 7F", FileFormat.JpegXl },
        { "00 00 00 0C 4A 58 4C 20 0D 0A 87 0A", FileFormat.JpegXl },
        // ftyp box, size 0x18: major "heic", minor 0, compatible "mif1" "heic"
        { "00 00 00 18 66 74 79 70 68 65 69 63 00 00 00 00 6D 69 66 31 68 65 69 63", FileFormat.Heif },
        // major "mif1" with compatible "avif": AVIF must win over the generic HEIF brand
        { "00 00 00 1C 66 74 79 70 6D 69 66 31 00 00 00 00 6D 69 66 31 61 76 69 66 6D 69 61 66", FileFormat.Avif },
        { "00 00 00 18 66 74 79 70 61 76 69 66 00 00 00 00 61 76 69 66 6D 69 66 31", FileFormat.Avif },
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public void Detect_RecognizesSignature(string hex, FileFormat expected)
    {
        Assert.Equal(expected, FileFormatDetector.Detect(Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- comment -->\n<!DOCTYPE svg>\n<svg width=\"1\"/>")]
    [InlineData("﻿  <svg/>")]
    public void Detect_RecognizesSvgText(string text)
    {
        Assert.Equal(FileFormat.Svg, FileFormatDetector.Detect(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Detect_ToleratesJunkBeforePdfHeader()
    {
        byte[] data = [.. Encoding.ASCII.GetBytes("garbage\r\n"), .. Encoding.ASCII.GetBytes("%PDF-1.4\n")];
        Assert.Equal(FileFormat.Pdf, FileFormatDetector.Detect(data));
    }

    [Fact]
    public void Detect_StrongSignatureWinsOverEmbeddedPdfMarker()
    {
        // A JPEG whose EXIF comment happens to contain "%PDF-" must remain a JPEG.
        byte[] data = [0xFF, 0xD8, 0xFF, 0xE1, .. Encoding.ASCII.GetBytes("Exif %PDF-1.7")];
        Assert.Equal(FileFormat.Jpeg, FileFormatDetector.Detect(data));
    }

    [Fact]
    public void Detect_ContentWinsOverMisleadingExtension()
    {
        byte[] png = Convert.FromHexString("89504E470D0A1A0A0000000D");
        Assert.Equal(FileFormat.Png, FileFormatDetector.Detect(png, ".jpg"));
    }

    [Theory]
    [InlineData(".heic", FileFormat.Heif)]
    [InlineData("HEIF", FileFormat.Heif)]
    [InlineData(".JPG", FileFormat.Jpeg)]
    [InlineData(".jxl", FileFormat.JpegXl)]
    [InlineData(".txt", FileFormat.Unknown)]
    [InlineData("", FileFormat.Unknown)]
    [InlineData(null, FileFormat.Unknown)]
    public void Detect_FallsBackToExtension(string? extension, FileFormat expected)
    {
        Assert.Equal(expected, FileFormatDetector.Detect(ReadOnlySpan<byte>.Empty, extension));
    }

    [Fact]
    public void Detect_ReadsOnlyTheHeaderFromLargeStreams()
    {
        using var stream = new MemoryStream(new byte[10 * 1024 * 1024]);
        stream.Write("%PDF-2.0"u8);
        stream.Position = 0;

        Assert.Equal(FileFormat.Pdf, FileFormatDetector.Detect(stream));
        Assert.True(stream.Position <= FileFormatDetector.HeaderLength);
    }

    [Fact]
    public void Detect_HandlesJapaneseUnicodePaths()
    {
        string dir = Directory.CreateTempSubdirectory("mavue-テスト-").FullName;
        try
        {
            string path = Path.Combine(dir, "写真 𠮷野家 – résumé.png");
            File.WriteAllBytes(path, Convert.FromHexString("89504E470D0A1A0A0000000D"));
            Assert.Equal(FileFormat.Png, FileFormatDetector.Detect(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Detect_EmptyInputIsUnknown()
    {
        Assert.Equal(FileFormat.Unknown, FileFormatDetector.Detect(ReadOnlySpan<byte>.Empty));
    }
}
