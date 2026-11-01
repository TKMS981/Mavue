using Mavue.Core.Formats;
using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

/// <summary>The single place where a file's facts choose its Quick View content (ARCHITECTURE §4.7.1).</summary>
[Trait("Category", "QuickView")]
public class PreviewContentRoutingTests
{
    private static FileFacts Facts(FileFormat format, PreviewAccess access = PreviewAccess.Allowed) =>
        new(FileAttributes.Normal, 1234, format, access);

    public static TheoryData<FileFormat> AllFormats()
    {
        var data = new TheoryData<FileFormat>();
        foreach (FileFormat format in Enum.GetValues<FileFormat>())
        {
            data.Add(format);
        }

        return data;
    }

    [Theory]
    [InlineData(FileFormat.Jpeg)]
    [InlineData(FileFormat.Png)]
    [InlineData(FileFormat.Gif)]
    [InlineData(FileFormat.Bmp)]
    [InlineData(FileFormat.Tiff)]
    [InlineData(FileFormat.WebP)]
    [InlineData(FileFormat.Ico)]
    [InlineData(FileFormat.Heif)]
    [InlineData(FileFormat.Avif)]
    [InlineData(FileFormat.Jpeg2000)]
    [InlineData(FileFormat.JpegXl)]
    [InlineData(FileFormat.CameraRaw)]
    public void RasterImages_GoToRaster(FileFormat format)
    {
        Assert.Equal(PreviewContentKind.Raster, PreviewContentRouting.Choose(Facts(format), pdfDocumentViewAvailable: true));
        Assert.Equal(PreviewContentKind.Raster, PreviewContentRouting.Choose(Facts(format), pdfDocumentViewAvailable: false));
    }

    [Fact]
    public void UnknownFormat_GoesToRaster_WhichMayStillDecodeIt()
    {
        // e.g. JPEG XR or DDS through an installed WIC codec; Raster reports "unsupported" otherwise.
        Assert.Equal(PreviewContentKind.Raster, PreviewContentRouting.Choose(Facts(FileFormat.Unknown), true));
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void MediaFormats_GoToMedia(FileFormat format)
    {
        if (!FileFormatKinds.IsMedia(format))
        {
            return;
        }

        Assert.Equal(PreviewContentKind.Media, PreviewContentRouting.Choose(Facts(format), true));
        Assert.Equal(PreviewContentKind.Media, PreviewContentRouting.Choose(Facts(format), false));
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void CloudOnlyMedia_IsNotPlayed(FileFormat format)
    {
        if (!FileFormatKinds.IsMedia(format))
        {
            return;
        }

        // Playing a cloud placeholder would download it.
        Assert.Equal(PreviewContentKind.Universal, PreviewContentRouting.Choose(Facts(format, PreviewAccess.CloudPlaceholder), true));
    }

    [Theory]
    [InlineData(PreviewAccess.Allowed)]
    [InlineData(PreviewAccess.CloudPlaceholder)]
    public void Svg_GoesToSvg_NeverToWic(PreviewAccess access)
    {
        Assert.Equal(PreviewContentKind.Svg, PreviewContentRouting.Choose(Facts(FileFormat.Svg, access), true));
        Assert.Equal(PreviewContentKind.Svg, PreviewContentRouting.Choose(Facts(FileFormat.Svg, access), false));
    }

    [Fact]
    public void Pdf_WithPdfium_GoesToDocumentView()
    {
        Assert.Equal(PreviewContentKind.PdfDocument, PreviewContentRouting.Choose(Facts(FileFormat.Pdf), pdfDocumentViewAvailable: true));
    }

    [Fact]
    public void Pdf_WithoutPdfium_IsRenderedAsPageImages()
    {
        Assert.Equal(PreviewContentKind.Raster, PreviewContentRouting.Choose(Facts(FileFormat.Pdf), pdfDocumentViewAvailable: false));
    }

    [Theory]
    [InlineData(PreviewAccess.CloudPlaceholder)]
    [InlineData(PreviewAccess.TooLarge)]
    public void Pdf_NotReadable_FallsBackToRaster_ForTheCachedThumbnailOrMessage(PreviewAccess access)
    {
        // The document view would read (and hydrate) the file; Raster shows the cached thumbnail or the message.
        Assert.Equal(PreviewContentKind.Raster, PreviewContentRouting.Choose(Facts(FileFormat.Pdf, access), pdfDocumentViewAvailable: true));
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void NotAFile_AlwaysGoesToUniversal(FileFormat format)
    {
        Assert.Equal(PreviewContentKind.Universal, PreviewContentRouting.Choose(Facts(format, PreviewAccess.NotAFile), true));
        Assert.Equal(PreviewContentKind.Universal, PreviewContentRouting.Choose(Facts(format, PreviewAccess.NotAFile), false));
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void EveryFormatAndAccess_ChoosesADefinedKind(FileFormat format)
    {
        foreach (PreviewAccess access in Enum.GetValues<PreviewAccess>())
        {
            foreach (bool pdfium in new[] { true, false })
            {
                PreviewContentKind kind = PreviewContentRouting.Choose(Facts(format, access), pdfium);
                Assert.True(Enum.IsDefined(kind), $"{format}/{access}/{pdfium} → {kind}");
            }
        }
    }

    [Fact]
    public void RealFiles_AreRoutedByTheirContent_NotTheirExtension()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string pdfNamedJpg = Path.Combine(folder, "really a pdf.jpg");
            File.WriteAllBytes(pdfNamedJpg, "%PDF-1.7\n%âãÏÓ\n"u8.ToArray());
            string text = Path.Combine(folder, "notes.txt");
            File.WriteAllText(text, "plain text");

            Assert.Equal(PreviewContentKind.PdfDocument, PreviewContentRouting.Choose(FileFacts.Read(pdfNamedJpg), true));
            Assert.Equal(PreviewContentKind.Text, PreviewContentRouting.Choose(FileFacts.Read(text), true));
            Assert.Equal(PreviewContentKind.Universal, PreviewContentRouting.Choose(FileFacts.Read(folder), true));
            Assert.Equal(PreviewContentKind.Universal, PreviewContentRouting.Choose(FileFacts.Read(Path.Combine(folder, "missing.png")), true));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".XLSX")]
    [InlineData(".txt")]
    [InlineData(".zip")]
    [InlineData(".exe")]
    [InlineData(".ttf")]
    [InlineData(".lnk")]
    [InlineData(".iso")]
    [InlineData(".cs")]
    public void UnrecognizedContent_WithANonImageExtension_GoesToTheInformationCard(string extension)
    {
        Assert.Equal(PreviewContentKind.Universal, PreviewContentRouting.Choose(Facts(FileFormat.Unknown), true, extension));
        Assert.Equal(PreviewContentKind.Universal, PreviewContentRouting.Choose(Facts(FileFormat.Unknown, PreviewAccess.CloudPlaceholder), true, extension));
    }

    [Theory]
    [InlineData(".jpg")]   // a damaged JPEG: the decoder reports it, then the card
    [InlineData(".dds")]   // a codec may be installed
    [InlineData(".jxr")]
    [InlineData(".xyz")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(null)]
    public void UnrecognizedContent_OtherwiseStillTriesTheImageDecoder(string? extension)
    {
        Assert.Equal(PreviewContentKind.Raster, PreviewContentRouting.Choose(Facts(FileFormat.Unknown), true, extension));
    }

    [Fact]
    public void RecognizedContent_WinsOverTheExtension()
    {
        // A PNG named .txt is an image; a PDF named .docx is a PDF.
        Assert.Equal(PreviewContentKind.Raster, PreviewContentRouting.Choose(Facts(FileFormat.Png), true, ".txt"));
        Assert.Equal(PreviewContentKind.PdfDocument, PreviewContentRouting.Choose(Facts(FileFormat.Pdf), true, ".docx"));
        Assert.Equal(PreviewContentKind.Media, PreviewContentRouting.Choose(Facts(FileFormat.Mp4), true, ".zip"));
    }

    [Fact]
    public void NullFacts_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PreviewContentRouting.Choose(null!, true));
    }
}
