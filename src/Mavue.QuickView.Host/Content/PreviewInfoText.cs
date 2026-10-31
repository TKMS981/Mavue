using System.Globalization;
using Mavue.Core.Viewing;
using Mavue.Viewer.Rendering;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// The info line under the content (format, size, pixel dimensions, pages, duration). The metadata boundary of
/// Quick View: richer information (EXIF inspector, privacy check) is added here and in its own panels, never in
/// the controller. Values are shown only; they are never logged.
/// </summary>
internal static class PreviewInfoText
{
    /// <summary>Format and size, or pixel dimensions and page numbers once decoded.</summary>
    public static string For(ResourceLoader strings, FileFacts facts, DecodedImage? decoded)
    {
        string size = ByteSizeText.Format(facts.Length);
        if (decoded is { PageCount: > 1 } pages)
        {
            return string.Format(CultureInfo.CurrentCulture, strings.GetString("QuickView_InfoPdfPage"), pages.PageIndex + 1, pages.PageCount, size);
        }

        if (decoded is { PageCount: > 0 } pdf)
        {
            return string.Format(CultureInfo.CurrentCulture, strings.GetString("QuickView_InfoPdf"), pdf.PageCount, size);
        }

        return decoded is { SourceWidth: > 0 } image
            ? string.Format(CultureInfo.CurrentCulture, strings.GetString("QuickView_InfoImage"), facts.Format, image.SourceWidth, image.SourceHeight, size)
            : string.Format(CultureInfo.CurrentCulture, strings.GetString("QuickView_InfoBasic"), facts.Format, size);
    }

    /// <summary>A drawing or image of known size (SVG).</summary>
    public static string Image(ResourceLoader strings, FileFacts facts, double width, double height) =>
        string.Format(CultureInfo.CurrentCulture, strings.GetString("QuickView_InfoImage"), facts.Format, Math.Round(width), Math.Round(height), ByteSizeText.Format(facts.Length));

    /// <summary>The current page of a PDF document (or its page count when it has one page).</summary>
    public static string PdfPage(ResourceLoader strings, FileFacts facts, int page, int pageCount)
    {
        string size = ByteSizeText.Format(facts.Length);
        return pageCount > 1
            ? string.Format(CultureInfo.CurrentCulture, strings.GetString("QuickView_InfoPdfPage"), page + 1, pageCount, size)
            : string.Format(CultureInfo.CurrentCulture, strings.GetString("QuickView_InfoPdf"), pageCount, size);
    }

    public static string Video(ResourceLoader strings, FileFacts facts, uint width, uint height, TimeSpan duration) =>
        string.Format(CultureInfo.CurrentCulture, strings.GetString("QuickView_InfoVideo"), facts.Format, width, height, MediaControlMath.FormatDuration(duration), ByteSizeText.Format(facts.Length));

    public static string Audio(ResourceLoader strings, FileFacts facts, TimeSpan duration) =>
        string.Format(CultureInfo.CurrentCulture, strings.GetString("QuickView_InfoAudio"), facts.Format, MediaControlMath.FormatDuration(duration), ByteSizeText.Format(facts.Length));
}
