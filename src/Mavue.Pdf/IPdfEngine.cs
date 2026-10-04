namespace Mavue.Pdf;

/// <summary>
/// Opens PDF documents. Implementations are swappable (docs/ARCHITECTURE.md §1, principle 3); the product engine is
/// PDFium (<see cref="Pdfium.PdfiumEngine"/>).
/// </summary>
public interface IPdfEngine
{
    /// <summary>True when the engine can be used on this machine (its native library loads).</summary>
    bool IsAvailable { get; }

    /// <summary>Opens a file without loading it fully into memory; throws <see cref="PdfOpenException"/>.</summary>
    IPdfDocument Open(string path, string? password = null);
}

/// <summary>
/// An open PDF document. Pages are loaded lazily; positions are display points (see <see cref="Pdfium.PdfiumDocument"/>).
/// Members are thread-safe.
/// </summary>
public interface IPdfDocument : IDisposable
{
    int PageCount { get; }

    /// <summary>Page size in PDF points (1/72 inch), with page rotation applied.</summary>
    PdfSize GetPageSize(int pageIndex);

    /// <summary>Draws a page (white background, BGRA) into a caller-owned buffer.</summary>
    void RenderPage(int pageIndex, nint buffer, int width, int height, int stride, int quarterTurns = 0);

    int GetCharCount(int pageIndex);

    int CharIndexAt(int pageIndex, int quarterTurns, double x, double y, double tolerance);

    string GetText(int pageIndex, int start, int count);

    IReadOnlyList<PdfRect> GetTextBounds(int pageIndex, int quarterTurns, int start, int count);

    IReadOnlyList<PdfTextMatch> Find(int pageIndex, string query, bool matchCase = false, bool wholeWord = false, int limit = 1000);

    IReadOnlyList<PdfLink> GetLinks(int pageIndex, int quarterTurns);

    IReadOnlyList<PdfOutlineItem> GetOutline();

    (double X, double Y)? DestinationPoint(PdfDestination destination, int quarterTurns);
}
