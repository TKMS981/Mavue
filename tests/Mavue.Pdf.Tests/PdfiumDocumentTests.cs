using System.Runtime.InteropServices;
using Mavue.Pdf.Pdfium;

namespace Mavue.Pdf.Tests;

[Trait("Category", "Pdf")]
public sealed class PdfiumDocumentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "PDF テスト " + Guid.NewGuid().ToString("N"));

    public PdfiumDocumentTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Write(string name, byte[] content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private PdfiumDocument OpenTest(int pages = 3, int rotatedPage = -1) => PdfiumDocument.Open(Write("文書.pdf", TestPdf.Create(pages, rotatedPage)));

    [Fact]
    public void Library_IsAvailable() => Assert.True(PdfiumLibrary.IsAvailable);

    [Fact]
    public void Open_ReadsPagesAndSizes_FromAUnicodePath()
    {
        using PdfiumDocument document = OpenTest(3, rotatedPage: 1);
        Assert.Equal(3, document.PageCount);
        Assert.Equal(new PdfSize(595, 842), document.GetPageSize(0));
        Assert.Equal(new PdfSize(842, 595), document.GetPageSize(1)); // /Rotate 90 applied
        Assert.Throws<ArgumentOutOfRangeException>(() => document.GetPageSize(3));
    }

    [Fact]
    public void File_StaysUsableByOthers_WhileOpen()
    {
        string path = Write("shared.pdf", TestPdf.Create(1));
        using PdfiumDocument document = PdfiumDocument.Open(path);
        string renamed = path + ".renamed";
        File.Move(path, renamed); // delete sharing: rename works while the document is open
        Assert.Equal(1, document.PageCount);
        Assert.Contains("Page 1", document.GetText(0, 0, document.GetCharCount(0)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[0], PdfOpenError.Format)]
    [InlineData(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', (byte)'1', (byte)'.', (byte)'7', (byte)'\n', 0x00, 0xFF }, PdfOpenError.Format)]
    public void Damaged_IsRejectedWithAReason(byte[] content, PdfOpenError expected)
    {
        string path = Write("broken.pdf", content);
        PdfOpenException error = Assert.Throws<PdfOpenException>(() => PdfiumDocument.Open(path));
        Assert.Equal(expected, error.Error);
    }

    [Fact]
    public void Missing_IsAFileError() =>
        Assert.Equal(PdfOpenError.File, Assert.Throws<PdfOpenException>(() => PdfiumDocument.Open(Path.Combine(_directory, "none.pdf"))).Error);

    [Fact]
    public unsafe void Render_DrawsThePage_IntoTheCallersBuffer()
    {
        using PdfiumDocument document = OpenTest();
        const int width = 119, height = 168; // A4 at 20 %
        byte[] pixels = new byte[width * height * 4];
        fixed (byte* buffer = pixels)
        {
            document.RenderPage(0, (nint)buffer, width, height, width * 4);
        }

        // Corner: white margin (opaque); center: the light page fill; the alpha channel is opaque everywhere.
        Assert.Equal([255, 255, 255, 255], pixels[..4]);
        int center = ((height / 2 * width) + (width / 2)) * 4;
        Assert.True(pixels[center] > 200 && pixels[center + 3] == 255);
        Assert.All(Enumerable.Range(0, width * height), i => Assert.Equal(255, pixels[(i * 4) + 3]));
    }

    [Fact]
    public void Text_IsExtracted_AndPositionsMapBack()
    {
        using PdfiumDocument document = OpenTest();
        int count = document.GetCharCount(1);
        string text = document.GetText(1, 0, count);
        Assert.Contains("Page 2", text, StringComparison.Ordinal);
        Assert.Contains("quick brown fox", text, StringComparison.Ordinal);

        // "Page 2" is drawn at 36 pt from (60, 760) in page space: about 60 pt from the left, 82 pt from the top.
        IReadOnlyList<PdfRect> bounds = document.GetTextBounds(1, 0, 0, 6);
        PdfRect first = Assert.Single(bounds);
        Assert.InRange(first.Left, 55, 65);
        Assert.InRange(first.Top, 50, 85);
        int index = document.CharIndexAt(1, 0, first.Left + 2, (first.Top + first.Bottom) / 2, 2);
        Assert.Equal(0, index);
        Assert.Equal(-1, document.CharIndexAt(1, 0, 590, 830, 1)); // empty corner
    }

    [Fact]
    public void Text_Positions_FollowTheViewRotation()
    {
        using PdfiumDocument document = OpenTest();
        PdfRect upright = document.GetTextBounds(0, 0, 0, 6)[0];
        PdfRect turned = document.GetTextBounds(0, 1, 0, 6)[0];

        // A quarter turn clockwise: the line now runs downwards near the right edge (842 pt wide when turned).
        Assert.True(turned.Height > turned.Width);
        Assert.InRange(turned.Left, 842 - upright.Bottom - 2, 842 - upright.Top + 2);
        Assert.InRange(turned.Top, upright.Left - 2, upright.Left + 2);
    }

    [Fact]
    public void Find_ReturnsEveryOccurrence_CaseInsensitiveByDefault()
    {
        using PdfiumDocument document = OpenTest(4);
        for (int page = 0; page < 4; page++)
        {
            PdfTextMatch match = Assert.Single(document.Find(page, TestPdf.EveryPageWord.ToUpperInvariant()));
            Assert.Equal(TestPdf.EveryPageWord.Length, match.CharCount);
            Assert.Equal(TestPdf.EveryPageWord, document.GetText(page, match.CharIndex, match.CharCount));
        }

        Assert.Empty(document.Find(0, TestPdf.EveryPageWord.ToUpperInvariant(), matchCase: true));
        Assert.Single(document.Find(3, TestPdf.LastPagePhrase));
        Assert.Empty(document.Find(0, TestPdf.LastPagePhrase));
        Assert.Empty(document.Find(0, string.Empty));
    }

    [Fact]
    public void Links_ToAPage_AndToTheWeb_JavascriptIsDropped()
    {
        using PdfiumDocument document = OpenTest(3);
        IReadOnlyList<PdfLink> links = document.GetLinks(0, 0);
        PdfLink internalLink = Assert.Single(links, l => l.Target is PdfPageTarget);
        PdfPageTarget target = (PdfPageTarget)internalLink.Target;
        Assert.Equal(2, target.Destination.PageIndex);
        Assert.Equal(420, target.Destination.PageY);
        (double X, double Y)? point = document.DestinationPoint(target.Destination, 0);
        Assert.Equal(842 - 420, point!.Value.Y, 1);

        // Annotation rectangle [55 595 240 618] in page space → top 842 - 618 = 224.
        Assert.InRange(internalLink.Bounds.Top, 223, 225);
        Assert.InRange(internalLink.Bounds.Left, 54, 56);

        Assert.Contains(links, l => l.Target is PdfUriTarget { Uri: TestPdf.WebLink });
        Assert.DoesNotContain(links, l => l.Target is PdfUriTarget u && u.Uri.StartsWith("javascript", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(document.GetLinks(1, 0));
    }

    [Fact]
    public void Outline_ListsTheChapters_WithTheirPages()
    {
        using PdfiumDocument document = OpenTest(5);
        IReadOnlyList<PdfOutlineItem> outline = document.GetOutline();
        Assert.Equal(5, outline.Count);
        Assert.Equal("Chapter 3", outline[2].Title);
        Assert.Equal(2, outline[2].Destination?.PageIndex);
        Assert.Empty(outline[2].Children);
    }

    [Fact]
    public void ManyPages_KeepOnlyAFewLoaded()
    {
        using PdfiumDocument document = OpenTest(60);
        for (int page = 0; page < 60; page++)
        {
            Assert.Single(document.Find(page, TestPdf.EveryPageWord));
        }

        // Pages are loaded on demand; the cache is bounded (internal), so walking the whole document works and the
        // first page can be read again afterwards.
        Assert.Contains("Page 1", document.GetText(0, 0, 10), StringComparison.Ordinal);
    }

    [Fact]
    public void Disposed_Throws_AndConcurrentUseIsSerialized()
    {
        PdfiumDocument document = OpenTest(8);
        Parallel.For(0, 64, i => Assert.Single(document.Find(i % 8, TestPdf.EveryPageWord)));
        document.Dispose();
        document.Dispose();
        Assert.Throws<ObjectDisposedException>(() => document.GetCharCount(0));
    }

    [Fact]
    public void OpenFromStream_Works()
    {
        using var stream = new MemoryStream(TestPdf.Create(2));
        using PdfiumDocument document = PdfiumDocument.Open(stream);
        Assert.Equal(2, document.PageCount);
        Assert.Equal(PdfiumEngine.Instance.IsAvailable, PdfiumLibrary.IsAvailable);
    }

    [Fact]
    public void Arguments_AreChecked()
    {
        using PdfiumDocument document = OpenTest(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.RenderPage(0, 0, 10, 10, 40));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.RenderPage(0, Marshal.AllocHGlobal(1), 10, 10, 8));
        Assert.Equal(string.Empty, document.GetText(0, 100_000, 10));
    }

    [Fact]
    public void EncryptedPdf_NeedsItsPassword()
    {
        string path = Write("保護.pdf", TestPdf.CreateEncrypted("mavue"));

        Assert.Equal(PdfOpenError.Password, Assert.Throws<PdfOpenException>(() => PdfiumDocument.Open(path)).Error);
        Assert.Equal(PdfOpenError.Password, Assert.Throws<PdfOpenException>(() => PdfiumDocument.Open(path, "wrong")).Error);

        using PdfiumDocument document = PdfiumDocument.Open(path, "mavue");
        Assert.Equal(1, document.PageCount);
        Assert.Contains(TestPdf.SecretPhrase, document.GetText(0, 0, document.GetCharCount(0)), StringComparison.Ordinal);

        using PdfiumDocument byOwner = PdfiumDocument.Open(path, "owner-secret"); // the owner password opens it too
        Assert.Equal(1, byOwner.PageCount);
    }
}
