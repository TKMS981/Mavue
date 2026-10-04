namespace Mavue.Core.Viewing;

/// <summary>How the pages of a document are arranged.</summary>
public enum PdfLayoutMode
{
    /// <summary>One page at a time; PageUp/PageDown and the wheel at the edges turn pages.</summary>
    SinglePage = 0,

    /// <summary>All pages one below the other (scrolling).</summary>
    Continuous,

    /// <summary>Two pages side by side (1–2, 3–4, …), rows one below the other.</summary>
    TwoPages,

    /// <summary>Two pages side by side with the cover alone (1, 2–3, 4–5, …), like a printed book.</summary>
    TwoPagesCover,
}

/// <summary>A page placed in the view, in device-independent pixels from the top-left of the content.</summary>
public readonly record struct PlacedPage(int Index, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// Places the pages of a document for a zoom factor and a viewport (pure arithmetic, no UI). Sizes are the pages at
/// 100 % in device-independent pixels (points × 96 / 72), already turned as shown. Rows are centered horizontally;
/// a single page is also centered vertically when it is smaller than the viewport.
/// <para>
/// Only arithmetic over arrays is done, so documents with thousands of pages lay out in microseconds; the view creates
/// elements only for <see cref="VisiblePages"/>.
/// </para>
/// </summary>
public sealed class PdfPageLayout
{
    /// <summary>Space around the content.</summary>
    public const double Padding = 12;

    /// <summary>Space between rows.</summary>
    public const double RowGap = 12;

    /// <summary>Space between the two pages of a spread.</summary>
    public const double SpreadGap = 6;

    private readonly IReadOnlyList<(double Width, double Height)> _sizes;
    private readonly PlacedPage[] _placed;
    private readonly int[] _rowFirstPlaced;
    private readonly double[] _rowTops;

    private PdfPageLayout(IReadOnlyList<(double Width, double Height)> sizes, PdfLayoutMode mode, double factor, PlacedPage[] placed, int[] rowFirstPlaced, double[] rowTops, double width, double height)
    {
        _sizes = sizes;
        Mode = mode;
        Factor = factor;
        _placed = placed;
        _rowFirstPlaced = rowFirstPlaced;
        _rowTops = rowTops;
        ExtentWidth = width;
        ExtentHeight = height;
    }

    public PdfLayoutMode Mode { get; }

    public double Factor { get; }

    /// <summary>Content size (at least the viewport).</summary>
    public double ExtentWidth { get; }

    public double ExtentHeight { get; }

    /// <summary>Pages in the layout, in order (one page in <see cref="PdfLayoutMode.SinglePage"/>).</summary>
    public IReadOnlyList<PlacedPage> Pages => _placed;

    /// <param name="sizes">Every page at 100 % (DIPs), turned as shown.</param>
    /// <param name="mode">Arrangement.</param>
    /// <param name="factor">Zoom (1 = 100 %).</param>
    /// <param name="viewportWidth">Visible width (DIPs).</param>
    /// <param name="viewportHeight">Visible height (DIPs).</param>
    /// <param name="singlePage">The page shown in <see cref="PdfLayoutMode.SinglePage"/>.</param>
    public static PdfPageLayout Create(IReadOnlyList<(double Width, double Height)> sizes, PdfLayoutMode mode, double factor, double viewportWidth, double viewportHeight, int singlePage = 0)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        factor = factor > 0 && double.IsFinite(factor) ? factor : 1;
        List<int[]> rows = Rows(sizes.Count, mode, singlePage);
        var placed = new List<PlacedPage>(sizes.Count);
        int[] rowFirst = new int[rows.Count];
        double[] rowTops = new double[rows.Count];
        double y = Padding;
        double widest = 0;
        var rowWidths = new double[rows.Count];
        for (int r = 0; r < rows.Count; r++)
        {
            rowWidths[r] = RowWidth(sizes, rows[r], factor);
            widest = Math.Max(widest, rowWidths[r]);
        }

        double extentWidth = Math.Max(viewportWidth, widest + (2 * Padding));
        for (int r = 0; r < rows.Count; r++)
        {
            rowFirst[r] = placed.Count;
            rowTops[r] = y;
            double rowHeight = rows[r].Max(i => sizes[i].Height * factor);
            double x = (extentWidth - rowWidths[r]) / 2;
            foreach (int index in rows[r])
            {
                (double w, double h) = (sizes[index].Width * factor, sizes[index].Height * factor);
                placed.Add(new PlacedPage(index, x, y + ((rowHeight - h) / 2), w, h));
                x += w + SpreadGap;
            }

            y += rowHeight + RowGap;
        }

        double contentHeight = rows.Count == 0 ? 2 * Padding : y - RowGap + Padding;
        double extentHeight = Math.Max(viewportHeight, contentHeight);
        if (mode == PdfLayoutMode.SinglePage && placed.Count > 0 && contentHeight < viewportHeight)
        {
            double shift = (viewportHeight - contentHeight) / 2;
            for (int i = 0; i < placed.Count; i++)
            {
                placed[i] = placed[i] with { Y = placed[i].Y + shift };
            }

            rowTops[0] += shift;
        }

        return new PdfPageLayout(sizes, mode, factor, [.. placed], rowFirst, rowTops, extentWidth, extentHeight);
    }

    /// <summary>
    /// The factor that shows the row of <paramref name="page"/> whole (<see cref="ZoomMode.Fit"/>) or the widest row
    /// across the viewport (<see cref="ZoomMode.FitWidth"/>); <see cref="ZoomMode.Custom"/> returns its own factor.
    /// </summary>
    public static double FitFactor(IReadOnlyList<(double Width, double Height)> sizes, PdfLayoutMode mode, ZoomSetting zoom, double viewportWidth, double viewportHeight, int page)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        if (zoom.Mode == ZoomMode.Custom || sizes.Count == 0)
        {
            return zoom.Factor;
        }

        List<int[]> rows = Rows(sizes.Count, mode, page);
        double availableWidth = Math.Max(1, viewportWidth - (2 * Padding));
        double availableHeight = Math.Max(1, viewportHeight - (2 * Padding));
        if (zoom.Mode == ZoomMode.FitWidth)
        {
            double best = double.MaxValue;
            foreach (int[] row in rows)
            {
                double gaps = SpreadGap * (row.Length - 1);
                best = Math.Min(best, (availableWidth - gaps) / Math.Max(1, row.Sum(i => sizes[i].Width)));
            }

            return Math.Max(ViewerZoom.MinFactor, best);
        }

        int[] current = rows.FirstOrDefault(r => r.Contains(Math.Clamp(page, 0, sizes.Count - 1))) ?? rows[0];
        double width = (availableWidth - (SpreadGap * (current.Length - 1))) / Math.Max(1, current.Sum(i => sizes[i].Width));
        double height = availableHeight / Math.Max(1, current.Max(i => sizes[i].Height));
        return Math.Max(ViewerZoom.MinFactor, Math.Min(width, height));
    }

    /// <summary>Where <paramref name="pageIndex"/> is, or null when it is not in the layout (single-page mode).</summary>
    public PlacedPage? Find(int pageIndex)
    {
        foreach (PlacedPage page in _placed)
        {
            if (page.Index == pageIndex)
            {
                return page;
            }
        }

        return null;
    }

    /// <summary>Pages that intersect the vertical range [<paramref name="top"/>, <paramref name="bottom"/>) of the content.</summary>
    public IReadOnlyList<PlacedPage> VisiblePages(double top, double bottom)
    {
        var result = new List<PlacedPage>();
        if (_placed.Length == 0)
        {
            return result;
        }

        // Binary search for the first row whose top is at or below "top", then step back one row.
        int row = Array.BinarySearch(_rowTops, top);
        row = row < 0 ? Math.Max(0, ~row - 1) : row;
        for (int i = _rowFirstPlaced[row]; i < _placed.Length; i++)
        {
            PlacedPage page = _placed[i];
            if (page.Y >= bottom)
            {
                break;
            }

            if (page.Bottom > top)
            {
                result.Add(page);
            }
        }

        return result;
    }

    /// <summary>The page the reader is on: the one crossing a line a third down the viewport, or the nearest one.</summary>
    public int CurrentPage(double viewportTop, double viewportHeight)
    {
        if (_placed.Length == 0)
        {
            return 0;
        }

        if (Mode == PdfLayoutMode.SinglePage)
        {
            return _placed[0].Index;
        }

        double line = viewportTop + (viewportHeight / 3);
        IReadOnlyList<PlacedPage> around = VisiblePages(line - 1, line + 1);
        if (around.Count > 0)
        {
            return around[0].Index;
        }

        // In a gap between rows: the next page below the line (or the last page).
        foreach (PlacedPage page in VisiblePages(line, double.MaxValue))
        {
            return page.Index;
        }

        return _placed[^1].Index;
    }

    /// <summary>Page under a content point, or null.</summary>
    public PlacedPage? PageAt(double x, double y)
    {
        foreach (PlacedPage page in VisiblePages(y - 1, y + 1))
        {
            if (page.Contains(x, y))
            {
                return page;
            }
        }

        return null;
    }

    /// <summary>The page reached by stepping <paramref name="delta"/> rows from <paramref name="current"/> (a spread counts as one row).</summary>
    public static int StepPage(int current, int delta, int pageCount, PdfLayoutMode mode)
    {
        if (pageCount <= 0)
        {
            return 0;
        }

        int target = mode switch
        {
            PdfLayoutMode.TwoPages => current - (current % 2) + (2 * delta),
            PdfLayoutMode.TwoPagesCover => CoverRowStart(current) + (delta > 0 ? (current == 0 ? 1 : 2) * delta : (2 * delta)),
            _ => current + delta,
        };
        if (mode == PdfLayoutMode.TwoPagesCover)
        {
            target = target <= 0 ? 0 : CoverRowStart(Math.Min(target, pageCount - 1));
        }

        return Math.Clamp(target, 0, pageCount - 1);
    }

    /// <summary>Number of pages known to the layout.</summary>
    public int PageCount => _sizes.Count;

    /// <summary>First page of the row of <paramref name="page"/> when the cover is alone (0, 1, 3, 5, …).</summary>
    private static int CoverRowStart(int page) => page <= 0 ? 0 : page - ((page - 1) % 2);

    private static List<int[]> Rows(int count, PdfLayoutMode mode, int singlePage)
    {
        var rows = new List<int[]>();
        if (count == 0)
        {
            return rows;
        }

        switch (mode)
        {
            case PdfLayoutMode.SinglePage:
                rows.Add([Math.Clamp(singlePage, 0, count - 1)]);
                break;
            case PdfLayoutMode.TwoPages:
                for (int i = 0; i < count; i += 2)
                {
                    rows.Add(i + 1 < count ? [i, i + 1] : [i]);
                }

                break;
            case PdfLayoutMode.TwoPagesCover:
                rows.Add([0]);
                for (int i = 1; i < count; i += 2)
                {
                    rows.Add(i + 1 < count ? [i, i + 1] : [i]);
                }

                break;
            default:
                for (int i = 0; i < count; i++)
                {
                    rows.Add([i]);
                }

                break;
        }

        return rows;
    }

    private static double RowWidth(IReadOnlyList<(double Width, double Height)> sizes, int[] row, double factor) =>
        row.Sum(i => sizes[i].Width * factor) + (SpreadGap * (row.Length - 1));
}
