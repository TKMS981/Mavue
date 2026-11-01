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

    /// <summary>Plain text and source code (syntax colored), only visible lines read (docs/ARCHITECTURE.md §4.7.12).</summary>
    Text,

    /// <summary>Markdown drawn as native XAML (no browser).</summary>
    Markdown,

    /// <summary>HTML in a locked-down WebView2: no scripts, no requests beyond the file itself, no downloads.</summary>
    Html,
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

        if (FileFormatKinds.IsText(facts.Format))
        {
            // Reading a cloud placeholder would download it; the card shows its information instead.
            return facts.Access != PreviewAccess.Allowed ? PreviewContentKind.Universal : facts.Format switch
            {
                FileFormat.Markdown => PreviewContentKind.Markdown,
                FileFormat.Html => PreviewContentKind.Html,
                _ => PreviewContentKind.Text,
            };
        }

        if (facts.Format == FileFormat.Pdf && facts.Access == PreviewAccess.Allowed && pdfDocumentViewAvailable)
        {
            return PreviewContentKind.PdfDocument;
        }

        if (facts.Format == FileFormat.Unknown && IsKnownNonImage(extension))
        {
            return PreviewContentKind.Universal;
        }

        // A file of no known type whose first bytes are text (a configuration file with an unusual extension, a
        // README without one) is shown as text; Windows Imaging Component could not decode it anyway.
        if (facts.Format == FileFormat.Unknown && facts.LooksLikeText && facts.Access == PreviewAccess.Allowed && !IsImageExtension(extension))
        {
            return PreviewContentKind.Text;
        }

        // Images, PDF pages without PDFium, cloud placeholders of images (cached thumbnail) and unknown formats,
        // which Windows Imaging Component may still decode (e.g. JPEG XR, DDS); what it cannot decode falls back to
        // the information card.
        return PreviewContentKind.Raster;
    }

    private static bool IsImageExtension(string? extension) =>
        extension is { Length: > 1 } && UniversalFileInfo.CategoryFor(extension) == FileKindCategory.Image;

    /// <summary>Extensions of documents, archives, programs, fonts and the like: never images.</summary>
    private static bool IsKnownNonImage(string? extension) =>
        extension is { Length: > 1 } &&
        UniversalFileInfo.CategoryFor(extension) is not (FileKindCategory.Other or FileKindCategory.Image or FileKindCategory.Video
            or FileKindCategory.Audio or FileKindCategory.Pdf);
}
