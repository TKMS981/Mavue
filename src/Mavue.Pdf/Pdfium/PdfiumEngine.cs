namespace Mavue.Pdf.Pdfium;

/// <summary>PDFium (bblanchon/pdfium-binaries, docs/DEPENDENCIES.md §3) as the <see cref="IPdfEngine"/>.</summary>
public sealed class PdfiumEngine : IPdfEngine
{
    public static readonly PdfiumEngine Instance = new();

    public bool IsAvailable => PdfiumLibrary.IsAvailable;

    public IPdfDocument Open(string path, string? password = null) => PdfiumDocument.Open(path, password);
}
