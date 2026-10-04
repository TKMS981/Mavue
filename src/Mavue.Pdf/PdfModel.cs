namespace Mavue.Pdf;

/// <summary>A size in PDF points (1/72 inch).</summary>
public readonly record struct PdfSize(double Width, double Height);

/// <summary>
/// A rectangle on a displayed page, in points from the page's top-left corner as it is shown (the page's own
/// rotation and the viewer's rotation applied). Multiply by the zoom to get screen units.
/// </summary>
public readonly record struct PdfRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>A place in the document: a page and, when the document says so, a point on it (PDF page space).</summary>
/// <param name="PageIndex">Zero-based page.</param>
/// <param name="PageX">Horizontal position in page space (points from the left of the media box), if given.</param>
/// <param name="PageY">Vertical position in page space (points from the bottom), if given.</param>
public sealed record PdfDestination(int PageIndex, double? PageX = null, double? PageY = null);

/// <summary>Where a link goes.</summary>
public abstract record PdfLinkTarget;

/// <summary>A place in the same document.</summary>
public sealed record PdfPageTarget(PdfDestination Destination) : PdfLinkTarget;

/// <summary>A web address (http, https or mailto; other schemes are not reported).</summary>
public sealed record PdfUriTarget(string Uri) : PdfLinkTarget;

/// <summary>A link on a page: its area and its target.</summary>
public sealed record PdfLink(PdfRect Bounds, PdfLinkTarget Target);

/// <summary>An entry of the document outline (bookmarks / table of contents).</summary>
public sealed record PdfOutlineItem(string Title, PdfDestination? Destination, IReadOnlyList<PdfOutlineItem> Children);

/// <summary>A search hit: characters <see cref="CharIndex"/> .. + <see cref="CharCount"/> of a page's text.</summary>
public readonly record struct PdfTextMatch(int PageIndex, int CharIndex, int CharCount);

/// <summary>Why a PDF could not be opened.</summary>
public enum PdfOpenError
{
    Unknown = 0,
    File,
    Format,
    Password,
    Security,
    TooLarge,
}

/// <summary>The document could not be opened (<see cref="Error"/> says why).</summary>
public sealed class PdfOpenException : Exception
{
    public PdfOpenException()
    {
    }

    public PdfOpenException(string message)
        : base(message)
    {
    }

    public PdfOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PdfOpenException(PdfOpenError error)
        : base("The PDF could not be opened: " + error)
    {
        Error = error;
    }

    public PdfOpenError Error { get; }
}
