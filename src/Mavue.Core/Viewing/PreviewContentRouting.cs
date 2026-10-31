using Mavue.Core.Formats;

namespace Mavue.Core.Viewing;

/// <summary>
/// The kinds of preview content. Each kind is shown by one replaceable component of the Quick View host
/// (docs/ARCHITECTURE.md §4.7); adding a file type means adding a kind, a rule in <see cref="PreviewContentRouting"/>
/// and one component, never a branch in the Quick View controller.
/// </summary>
public enum PreviewContentKind
{
    /// <summary>Anything that cannot be shown as content: file information and messages (Universal Preview).</summary>
    Universal = 0,

    /// <summary>Raster images through Windows Imaging Component (also animated GIF, and PDF pages without PDFium).</summary>
    Raster,

    /// <summary>SVG drawings (Direct2D through XAML).</summary>
    Svg,

    /// <summary>PDF documents in the shared PDFium document view.</summary>
    PdfDocument,

    /// <summary>Video and audio through Media Foundation.</summary>
    Media,

    /// <summary>ZIP archives: the listing from the central directory and the images inside, read on demand.</summary>
    Archive,
}

/// <summary>
/// Chooses the content kind for a file from facts read before any decoding. The single place where the file type
/// decides what shows it (pure, unit-tested).
/// </summary>
public static class PreviewContentRouting
{
    /// <param name="facts">Attributes, format and access of the file.</param>
    /// <param name="pdfDocumentViewAvailable">PDFium is loaded and the document view may be used.</param>
    /// <param name="extension">The file's extension (".docx"), when known: a file whose content is not recognized and
    /// whose extension names a known non-image type goes straight to the information card instead of an image decoder.</param>
    public static PreviewContentKind Choose(FileFacts facts, bool pdfDocumentViewAvailable, string? extension = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Access == PreviewAccess.NotAFile)
        {
            return PreviewContentKind.Universal;
        }

        if (FileFormatKinds.IsArchive(facts.Format))
        {
            // The listing is at the end of the file: reading a cloud placeholder would download it.
            return facts.Access == PreviewAccess.CloudPlaceholder ? PreviewContentKind.Universal : PreviewContentKind.Archive;
        }

        if (FileFormatKinds.IsMedia(facts.Format))
        {
            // Playing a cloud placeholder would download it.
            return facts.Access == PreviewAccess.CloudPlaceholder ? PreviewContentKind.Universal : PreviewContentKind.Media;
        }

        if (facts.Format == FileFormat.Svg)
        {
            return PreviewContentKind.Svg;
        }

        if (facts.Format == FileFormat.Pdf && facts.Access == PreviewAccess.Allowed && pdfDocumentViewAvailable)
        {
            return PreviewContentKind.PdfDocument;
        }

        if (facts.Format == FileFormat.Unknown && IsKnownNonImage(extension))
        {
            return PreviewContentKind.Universal;
        }

        // Images, PDF pages without PDFium, cloud placeholders of images (cached thumbnail) and unknown formats,
        // which Windows Imaging Component may still decode (e.g. JPEG XR, DDS); what it cannot decode falls back to
        // the information card.
        return PreviewContentKind.Raster;
    }

    /// <summary>Extensions of documents, archives, programs, fonts and the like: never images.</summary>
    private static bool IsKnownNonImage(string? extension) =>
        extension is { Length: > 1 } &&
        UniversalFileInfo.CategoryFor(extension) is not (FileKindCategory.Other or FileKindCategory.Image or FileKindCategory.Video
            or FileKindCategory.Audio or FileKindCategory.Pdf);
}
