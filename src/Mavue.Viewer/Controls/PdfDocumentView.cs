using System.Text;
using Mavue.Core.Viewing;
using Mavue.Pdf;
using Mavue.Viewer.Playback;
using Mavue.Viewer.Rendering;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using XamlImage = Microsoft.UI.Xaml.Controls.Image;

namespace Mavue.Viewer.Controls;

/// <summary>Position in a document's text.</summary>
/// <param name="Page">Zero-based page.</param>
/// <param name="Index">Zero-based character on that page.</param>
public readonly record struct PdfTextPosition(int Page, int Index) : IComparable<PdfTextPosition>
{
    public int CompareTo(PdfTextPosition other) => Page != other.Page ? Page.CompareTo(other.Page) : Index.CompareTo(other.Index);

    public static bool operator <(PdfTextPosition left, PdfTextPosition right) => left.CompareTo(right) < 0;

    public static bool operator >(PdfTextPosition left, PdfTextPosition right) => left.CompareTo(right) > 0;

    public static bool operator <=(PdfTextPosition left, PdfTextPosition right) => left.CompareTo(right) <= 0;

    public static bool operator >=(PdfTextPosition left, PdfTextPosition right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// A PDF document on screen (main window): single page, continuous or two-page layout (<see cref="PdfPageLayout"/>),
/// zoom, rotation, text selection, search highlights and links. Pages are drawn by PDFium off the UI thread.
/// <para>
/// Memory: elements and bitmaps exist only for the pages within one screen above and below the visible area
/// (whatever the number of pages); other pages are released as they scroll away. The UI thread never calls PDFium
/// directly (PDFium is serialized process-wide and a large page can take a while to draw), so selection, hit tests
/// and highlights are computed in the background and applied when ready.
/// </para>
/// </summary>
public sealed partial class PdfDocumentView : UserControl
{
    /// <summary>Largest bitmap for one page; beyond it the page is stretched.</summary>
    private const long MaxPagePixels = 24_000_000;

    private static readonly SolidColorBrush SelectionBrush = new(ColorHelper.FromArgb(0x60, 0x33, 0x88, 0xFF));
    private static readonly SolidColorBrush MatchBrush = new(ColorHelper.FromArgb(0x70, 0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush CurrentMatchBrush = new(ColorHelper.FromArgb(0x90, 0xFF, 0x8C, 0x00));

    private readonly ScrollViewer _scroller;
    private readonly Canvas _canvas;
    private readonly Dictionary<int, PageVisual> _visuals = [];
    private readonly Stack<PageVisual> _pool = new();
    private readonly Dictionary<int, IReadOnlyList<PdfLink>> _links = [];
    private readonly Dictionary<int, IReadOnlyList<PdfRect>> _selectionRects = [];
    private readonly Dictionary<int, IReadOnlyList<(PdfRect Rect, int Match)>> _matchRects = [];
    private readonly DispatcherQueueTimer _renderTimer;
    private readonly DispatcherQueue _dispatcher;

    private IPdfDocument? _document;
    private PdfSize[] _pageSizes = [];
    private PdfPageLayout? _layout;
    private int _quarterTurns;
    private PdfLayoutMode _mode = PdfLayoutMode.Continuous;
    private ZoomSetting _zoom = ZoomSetting.Fit;
    private double _factor = 1;
    private int _singlePage;
    private int _currentPage;
    private int _generation;
    private bool _renderLoopRunning;
    private bool _renderSuspended;
    private int _wheelAccumulated;

    // After a programmatic move (page keys, outline, links, search) the target page stays current while the scroll
    // gets there; recomputing from the old offset in between turned the page back (measured).
    private (int Page, double Offset, bool Reached, long Since)? _pin;

    // Selection: the character where the drag started and the one under the pointer now (both included); active once
    // the drag reached another character (a plain click selects nothing).
    private PdfTextPosition? _anchor;
    private PdfTextPosition? _focus;
    private bool _selectionActive;
    private uint? _dragPointer;
    private Point _pressPoint;
    private PdfLink? _pressedLink;
    private int _selectionVersion;
    private Point? _pendingHit;
    private bool _hitInFlight;

    private List<PdfTextMatch> _matches = [];
    private Dictionary<int, List<int>> _matchesByPage = [];
    private int _currentMatch = -1;

    public PdfDocumentView()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _canvas = new Canvas { Background = new SolidColorBrush(Colors.Transparent) };
        _scroller = new ScrollViewer
        {
            IsTabStop = false,
            ZoomMode = Microsoft.UI.Xaml.Controls.ZoomMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            Content = _canvas,
        };
        Content = _scroller;
        IsTabStop = false;
        _renderTimer = _dispatcher.CreateTimer();
        _renderTimer.Interval = TimeSpan.FromMilliseconds(160);
        _renderTimer.IsRepeating = false;
        _renderTimer.Tick += (_, _) =>
        {
            _renderSuspended = false;
            UpdateRealized();
        };

        _scroller.ViewChanged += (_, e) =>
        {
            UpdateRealized();
            if (!e.IsIntermediate)
            {
                Trace?.Invoke("pdf-scrolled", new Dictionary<string, object?> { ["page"] = _currentPage, ["offset"] = Math.Round(_scroller.VerticalOffset, 1) });
            }
        };
        _scroller.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5 || Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5)
            {
                Relayout(keepPage: true);
            }
        };
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCaptureLost += (_, _) => EndDrag();
        _canvas.DoubleTapped += OnDoubleTapped;
    }

    /// <summary>Current page, zoom, layout or orientation changed.</summary>
    public event Action? ViewChanged;

    /// <summary>The user clicked a link to a web address (http, https, mailto).</summary>
    public event Action<string>? UriRequested;

    /// <summary>The selected text changed (possibly to nothing).</summary>
    public event Action? SelectionChanged;

    /// <summary>Diagnostics (tests): rendered pages, layout, link and selection events.</summary>
    public PlaybackTrace? Trace { get; set; }

    public int PageCount => _pageSizes.Length;

    /// <summary>The page the reader is on.</summary>
    public int CurrentPage => _currentPage;

    public PdfLayoutMode Mode => _mode;

    public ZoomSetting Zoom => _zoom;

    /// <summary>Display pixels per source pixel (1 = 100 %, the page at 96 dpi on this monitor).</summary>
    public double Factor => _factor;

    public int QuarterTurns => _quarterTurns;

    public bool HasSelection => Range() is not null;

    /// <summary>Pages with an element (and possibly a bitmap) right now — bounded by the screen, not the document.</summary>
    public int RealizedPageCount => _visuals.Count;

    public IReadOnlyList<PdfTextMatch> Matches => _matches;

    public int CurrentMatch => _currentMatch;

    /// <summary>Page size (points, page rotation applied) of <paramref name="page"/>.</summary>
    public PdfSize PageSize(int page) => page >= 0 && page < _pageSizes.Length ? _pageSizes[page] : new PdfSize(0, 0);

    /// <summary>Shows <paramref name="document"/> (owned by the caller) at its first page.</summary>
    /// <param name="pageSizes">Every page's size (read off the UI thread by the caller).</param>
    public void Open(IPdfDocument document, PdfSize[] pageSizes, ZoomSetting zoom, PdfLayoutMode mode)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pageSizes);
        Close();
        _document = document;
        _pageSizes = pageSizes;
        _zoom = zoom;
        _mode = mode;
        _quarterTurns = 0;
        _singlePage = 0;
        _currentPage = 0;
        Relayout(keepPage: false);
        _scroller.ChangeView(0, 0, null, disableAnimation: true);
        UpdateRealized();
    }

    /// <summary>Releases every page bitmap and forgets the document (the caller disposes it).</summary>
    public void Close()
    {
        _generation++;
        foreach (PageVisual visual in _visuals.Values)
        {
            visual.Release();
        }

        _visuals.Clear();
        _pool.Clear();
        _canvas.Children.Clear();
        _links.Clear();
        _selectionRects.Clear();
        _matchRects.Clear();
        _matches = [];
        _matchesByPage = [];
        _currentMatch = -1;
        _anchor = _focus = null;
        _selectionActive = false;
        _pin = null;
        _document = null;
        _pageSizes = [];
        _layout = null;
    }

    public void SetMode(PdfLayoutMode mode)
    {
        if (_mode == mode || _document is null)
        {
            _mode = mode;
            return;
        }

        int page = _currentPage;
        _mode = mode;
        _singlePage = page;
        Relayout(keepPage: false);
        GoToPage(page);
        ViewChanged?.Invoke();
        Trace?.Invoke("pdf-scrolled", new Dictionary<string, object?> { ["page"] = _currentPage, ["offset"] = Math.Round(_scroller.VerticalOffset, 1) });
    }

    /// <summary>Fit, fit width or a factor; around the viewport center or <paramref name="anchor"/> (view coordinates).</summary>
    public void SetZoom(ZoomSetting zoom, Point? anchor = null)
    {
        if (_document is null)
        {
            _zoom = zoom;
            return;
        }

        Point center = anchor ?? new Point(_scroller.ViewportWidth / 2, _scroller.ViewportHeight / 2);
        (int Page, double Fx, double Fy)? keep = ContentAnchor(center);
        _zoom = zoom.Mode == Core.Viewing.ZoomMode.Custom ? zoom with { Factor = ViewerZoom.Clamp(zoom.Factor, MaxPageWidth(), MaxPageHeight()) } : zoom;
        Relayout(keepPage: keep is null);
        if (keep is { } k && _layout?.Find(k.Page) is { } placed)
        {
            _scroller.UpdateLayout();
            _scroller.ChangeView(placed.X + (k.Fx * placed.Width) - center.X, placed.Y + (k.Fy * placed.Height) - center.Y, null, disableAnimation: true);
        }

        SuspendRendering();
        Trace?.Invoke("pdf-zoom", new Dictionary<string, object?> { ["mode"] = _zoom.Mode.ToString(), ["factor"] = Math.Round(_factor, 4) });
        ViewChanged?.Invoke();
    }

    public void Rotate(int quarterTurns)
    {
        if (_document is null)
        {
            return;
        }

        int page = _currentPage;
        _quarterTurns = (((_quarterTurns + quarterTurns) % 4) + 4) % 4;
        _generation++; // every bitmap, link and highlight is for the old orientation
        foreach (PageVisual visual in _visuals.Values)
        {
            visual.Release();
            _canvas.Children.Remove(visual.Root);
        }

        _visuals.Clear();
        _links.Clear();
        _selectionRects.Clear();
        _matchRects.Clear();
        Relayout(keepPage: false);
        GoToPage(page);
        Trace?.Invoke("pdf-rotate", new Dictionary<string, object?> { ["quarterTurns"] = _quarterTurns });
        ViewChanged?.Invoke();
    }

    /// <summary>Shows <paramref name="page"/> (and the point on it, in display points, when given).</summary>
    public void GoToPage(int page, (double X, double Y)? point = null)
    {
        if (_document is null || _pageSizes.Length == 0)
        {
            return;
        }

        page = Math.Clamp(page, 0, _pageSizes.Length - 1);
        if (_mode == PdfLayoutMode.SinglePage && page != _singlePage)
        {
            _singlePage = page;
            ClearVisuals();
            Relayout(keepPage: false);
        }

        if (_layout?.Find(page) is not { } placed)
        {
            return;
        }

        _scroller.UpdateLayout();
        double scale = DipScale;
        double y = point is { } p ? placed.Y + (p.Y * scale) - 24 : placed.Y - PdfPageLayout.Padding;
        double x = point is { } q && placed.Width > _scroller.ViewportWidth ? placed.X + (q.X * scale) - 24 : _scroller.HorizontalOffset;
        y = Math.Clamp(y, 0, Math.Max(0, _layout.ExtentHeight - _scroller.ViewportHeight));
        _pin = (page, y, false, Environment.TickCount64);
        _scroller.ChangeView(Math.Max(0, x), y, null, disableAnimation: true);
        SetCurrentPage(page);
        UpdateRealized();
    }

    /// <summary>Previous/next page (a spread in two-page layout); false at the first or last page.</summary>
    public bool StepPage(int delta)
    {
        int target = PdfPageLayout.StepPage(_currentPage, delta, _pageSizes.Length, _mode);
        int currentRow = PdfPageLayout.StepPage(_currentPage, 0, _pageSizes.Length, _mode);
        if (target == currentRow)
        {
            return false;
        }

        GoToPage(target);
        return true;
    }

    public bool CanStepPage(int delta) =>
        PdfPageLayout.StepPage(_currentPage, delta, _pageSizes.Length, _mode) != PdfPageLayout.StepPage(_currentPage, 0, _pageSizes.Length, _mode);

    /// <summary>Moves the view; false when it cannot move that way.</summary>
    public bool ScrollBy(double dx, double dy)
    {
        double x = Math.Clamp(_scroller.HorizontalOffset + dx, 0, _scroller.ScrollableWidth);
        double y = Math.Clamp(_scroller.VerticalOffset + dy, 0, _scroller.ScrollableHeight);
        if (Math.Abs(x - _scroller.HorizontalOffset) < 0.5 && Math.Abs(y - _scroller.VerticalOffset) < 0.5)
        {
            return false;
        }

        _scroller.ChangeView(x, y, null, disableAnimation: true);
        return true;
    }

    /// <summary>
    /// Single-page layout: the wheel turns the page once a notch has accumulated at the top/bottom edge. Returns true
    /// when the wheel was used for a page turn (continuous layouts scroll normally).
    /// </summary>
    public bool HandleWheel(int wheelDelta)
    {
        if (_mode != PdfLayoutMode.SinglePage || _pageSizes.Length < 2)
        {
            return false;
        }

        bool atTop = _scroller.VerticalOffset <= 0.5;
        bool atBottom = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 0.5;
        if (!((wheelDelta > 0 && atTop) || (wheelDelta < 0 && atBottom)))
        {
            _wheelAccumulated = 0;
            return false;
        }

        _wheelAccumulated += wheelDelta;
        if (Math.Abs(_wheelAccumulated) < 120)
        {
            return true;
        }

        int step = _wheelAccumulated > 0 ? -1 : +1;
        _wheelAccumulated = 0;
        StepPage(step);
        return true;
    }

    /// <summary>Selects all the text of the document.</summary>
    public async Task SelectAllAsync()
    {
        if (_document is not { } document || _pageSizes.Length == 0)
        {
            return;
        }

        int last = _pageSizes.Length - 1;
        int count = await Task.Run(() => document.GetCharCount(last));
        if (!ReferenceEquals(document, _document))
        {
            return;
        }

        _anchor = new PdfTextPosition(0, 0);
        _focus = new PdfTextPosition(last, Math.Max(0, count - 1));
        _selectionActive = true;
        OnSelectionChanged();
    }

    public void ClearSelection()
    {
        if (_anchor is null && _focus is null)
        {
            return;
        }

        _anchor = _focus = null;
        _selectionActive = false;
        OnSelectionChanged();
    }

    /// <summary>The selected text (pages joined with line breaks), read off the UI thread; empty without a selection.</summary>
    public Task<string> GetSelectedTextAsync()
    {
        if (_document is not { } document || Range() is not { } range)
        {
            return Task.FromResult(string.Empty);
        }

        (PdfTextPosition start, PdfTextPosition end) = range;
        return Task.Run(() =>
        {
            var text = new StringBuilder();
            for (int page = start.Page; page <= end.Page; page++)
            {
                int count = document.GetCharCount(page);
                int from = page == start.Page ? start.Index : 0;
                int to = page == end.Page ? Math.Min(end.Index, count) : count;
                if (page > start.Page)
                {
                    text.Append("\r\n");
                }

                text.Append(document.GetText(page, from, to - from));
            }

            return text.ToString();
        });
    }

    /// <summary>Highlights search results; <paramref name="current"/> is shown (scrolled to) and drawn in another color.</summary>
    public void SetMatches(IReadOnlyList<PdfTextMatch> matches, int current)
    {
        _matches = [.. matches];
        _matchesByPage = _matches.Select((m, i) => (m, i)).GroupBy(p => p.m.PageIndex).ToDictionary(g => g.Key, g => g.Select(p => p.i).ToList());
        _matchRects.Clear();
        _currentMatch = matches.Count == 0 ? -1 : Math.Clamp(current, 0, matches.Count - 1);
        foreach (PageVisual visual in _visuals.Values)
        {
            DrawOverlay(visual);
        }

        RequestMatchRects();
        if (_currentMatch >= 0)
        {
            _ = ShowMatchAsync(_currentMatch);
        }
    }

    /// <summary>Makes <paramref name="index"/> the current result and scrolls it into view.</summary>
    public async Task ShowMatchAsync(int index)
    {
        if (_document is not { } document || index < 0 || index >= _matches.Count)
        {
            return;
        }

        _currentMatch = index;
        PdfTextMatch match = _matches[index];
        int turns = _quarterTurns;
        IReadOnlyList<PdfRect> rects = await Task.Run(() => document.GetTextBounds(match.PageIndex, turns, match.CharIndex, match.CharCount));
        if (!ReferenceEquals(document, _document) || _currentMatch != index)
        {
            return;
        }

        if (rects.Count > 0)
        {
            PdfRect r = rects[0];
            GoToPage(match.PageIndex);
            if (_layout?.Find(match.PageIndex) is { } placed)
            {
                double scale = DipScale;
                double y = placed.Y + (r.Top * scale);
                double x = placed.X + (r.Left * scale);
                double targetY = y - (_scroller.ViewportHeight / 3);
                double targetX = x < _scroller.HorizontalOffset || x > _scroller.HorizontalOffset + _scroller.ViewportWidth - 40 ? x - 40 : _scroller.HorizontalOffset;
                _scroller.ChangeView(Math.Max(0, targetX), Math.Max(0, targetY), null, disableAnimation: true);
            }
        }
        else
        {
            GoToPage(match.PageIndex);
        }

        foreach (PageVisual visual in _visuals.Values)
        {
            DrawOverlay(visual);
        }

        Trace?.Invoke("pdf-match", new Dictionary<string, object?> { ["index"] = index, ["page"] = match.PageIndex, ["char"] = match.CharIndex });
    }

    /// <summary>Goes to a destination (outline entry or link).</summary>
    public async Task GoToDestinationAsync(PdfDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_document is not { } document || destination.PageIndex < 0 || destination.PageIndex >= _pageSizes.Length)
        {
            return;
        }

        int turns = _quarterTurns;
        (double X, double Y)? point = await Task.Run(() => document.DestinationPoint(destination, turns));
        if (ReferenceEquals(document, _document))
        {
            GoToPage(destination.PageIndex, point is { } p ? (destination.PageX is null ? 0 : p.X, p.Y) : null);
            Trace?.Invoke("pdf-goto", new Dictionary<string, object?> { ["page"] = destination.PageIndex, ["y"] = point?.Y });
        }
    }

    /// <summary>Screen rectangle (window client, physical pixels) of a page that has an element, for tests.</summary>
    public Rect? PageRectInWindow(int page)
    {
        if (!_visuals.TryGetValue(page, out PageVisual? visual) || XamlRoot is null)
        {
            return null;
        }

        GeneralTransform transform = visual.Root.TransformToVisual(null);
        Rect rect = transform.TransformBounds(new Rect(0, 0, visual.Root.Width, visual.Root.Height));
        double scale = XamlRoot.RasterizationScale;
        return new Rect(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale);
    }

    /// <summary>DIPs per display point at the current zoom.</summary>
    private double DipScale => _factor * PdfiumRendering.DipsPerPoint;

    private double PixelScale => XamlRoot?.RasterizationScale ?? 1;

    private static (PdfTextPosition Start, PdfTextPosition End) Ordered(PdfTextPosition a, PdfTextPosition b) => a <= b ? (a, b) : (b, a);

    /// <summary>The selection as a start and an exclusive end, or null.</summary>
    private (PdfTextPosition Start, PdfTextPosition End)? Range()
    {
        if (!_selectionActive || _anchor is not { } a || _focus is not { } f)
        {
            return null;
        }

        (PdfTextPosition start, PdfTextPosition last) = Ordered(a, f);
        return (start, last with { Index = last.Index + 1 });
    }

    private List<(double Width, double Height)> LayoutSizes()
    {
        bool swap = (_quarterTurns & 1) == 1;
        var sizes = new List<(double, double)>(_pageSizes.Length);
        foreach (PdfSize size in _pageSizes)
        {
            (double w, double h) = (size.Width * PdfiumRendering.DipsPerPoint, size.Height * PdfiumRendering.DipsPerPoint);
            sizes.Add(swap ? (h, w) : (w, h));
        }

        return sizes;
    }

    private double MaxPageWidth() => _pageSizes.Length == 0 ? 1 : _pageSizes.Max(s => Math.Max(s.Width, s.Height)) * PdfiumRendering.DipsPerPoint * PixelScale;

    private double MaxPageHeight() => MaxPageWidth();

    /// <summary>Recomputes the layout for the viewport, zoom, mode and orientation; repositions the elements.</summary>
    private void Relayout(bool keepPage)
    {
        if (_document is null)
        {
            return;
        }

        int page = _currentPage;
        double viewportWidth = _scroller.ViewportWidth > 0 ? _scroller.ViewportWidth : ActualWidth;
        double viewportHeight = _scroller.ViewportHeight > 0 ? _scroller.ViewportHeight : ActualHeight;
        List<(double Width, double Height)> sizes = LayoutSizes();
        double fit = PdfPageLayout.FitFactor(sizes, _mode, _zoom, viewportWidth, viewportHeight, _mode == PdfLayoutMode.SinglePage ? _singlePage : page);

        // The layout works in DIPs at 100 % = 96 dpi; the zoom factor is relative to physical pixels (as for images),
        // which is the same thing on a 100 % monitor.
        double previous = _factor;
        _factor = _zoom.Mode == Core.Viewing.ZoomMode.Custom ? _zoom.Factor : fit;
        _layout = PdfPageLayout.Create(sizes, _mode, _factor, viewportWidth, viewportHeight, _singlePage);
        _canvas.Width = _layout.ExtentWidth;
        _canvas.Height = _layout.ExtentHeight;
        foreach (PageVisual visual in _visuals.Values)
        {
            if (_layout.Find(visual.Index) is { } placed)
            {
                visual.Place(placed);
                DrawOverlay(visual);
            }
        }

        if (keepPage && _mode != PdfLayoutMode.SinglePage && _layout.Find(page) is { } current)
        {
            _scroller.UpdateLayout();
            _scroller.ChangeView(_scroller.HorizontalOffset, Math.Max(0, current.Y - PdfPageLayout.Padding), null, disableAnimation: true);
        }

        UpdateRealized();
        if (Math.Abs(previous - _factor) > 1e-6)
        {
            ViewChanged?.Invoke(); // e.g. "fit" changed with the window size
        }
    }

    /// <summary>Which page and where on it (fractions) is under a viewport point.</summary>
    private (int Page, double Fx, double Fy)? ContentAnchor(Point viewportPoint)
    {
        if (_layout is null)
        {
            return null;
        }

        double x = _scroller.HorizontalOffset + viewportPoint.X;
        double y = _scroller.VerticalOffset + viewportPoint.Y;
        PlacedPage? page = _layout.PageAt(x, y) ?? _layout.Find(_currentPage);
        return page is { } p ? (p.Index, (x - p.X) / p.Width, (y - p.Y) / p.Height) : null;
    }

    private void SuspendRendering()
    {
        _renderSuspended = true;
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private void ClearVisuals()
    {
        foreach (PageVisual visual in _visuals.Values)
        {
            visual.Release();
            _canvas.Children.Remove(visual.Root);
        }

        _visuals.Clear();
    }

    /// <summary>Creates elements for the pages near the viewport, releases the others, starts drawing.</summary>
    private void UpdateRealized()
    {
        if (_layout is null || _document is null)
        {
            return;
        }

        double viewport = Math.Max(1, _scroller.ViewportHeight);
        double top = _scroller.VerticalOffset - viewport;
        double bottom = _scroller.VerticalOffset + (2 * viewport);
        var wanted = _layout.VisiblePages(top, bottom).ToDictionary(p => p.Index);
        foreach (int index in _visuals.Keys.Where(i => !wanted.ContainsKey(i)).ToList())
        {
            PageVisual visual = _visuals[index];
            visual.Release();
            visual.Root.Visibility = Visibility.Collapsed;
            _visuals.Remove(index);
            _pool.Push(visual);
            _links.Remove(index);
            _selectionRects.Remove(index);
            _matchRects.Remove(index);
        }

        foreach ((int index, PlacedPage placed) in wanted)
        {
            if (!_visuals.TryGetValue(index, out PageVisual? visual))
            {
                visual = _pool.Count > 0 ? _pool.Pop() : CreateVisual();
                visual.Index = index;
                visual.Root.Visibility = Visibility.Visible;
                visual.Place(placed);
                _visuals[index] = visual;
                RequestLinks(index);
                RequestOverlayRects(index);
                DrawOverlay(visual);
            }
        }

        int current = _layout.CurrentPage(_scroller.VerticalOffset, viewport);
        if (_pin is { } pin)
        {
            bool there = Math.Abs(_scroller.VerticalOffset - pin.Offset) < 2;
            if (there || (!pin.Reached && Environment.TickCount64 - pin.Since < 1000))
            {
                current = pin.Page; // arrived, or still on the way
                _pin = pin with { Reached = pin.Reached || there };
            }
            else
            {
                _pin = null; // the user scrolled away
            }
        }

        SetCurrentPage(current);
        if (!_renderSuspended)
        {
            _ = RenderLoopAsync();
        }
    }

    private PageVisual CreateVisual()
    {
        var visual = new PageVisual();
        _canvas.Children.Add(visual.Root);
        return visual;
    }

    private void SetCurrentPage(int page)
    {
        if (page == _currentPage)
        {
            return;
        }

        _currentPage = page;
        ViewChanged?.Invoke();
    }

    /// <summary>Draws the pages near the viewport one by one (the visible ones first) at the size they are shown.</summary>
    private async Task RenderLoopAsync()
    {
        if (_renderLoopRunning)
        {
            return;
        }

        _renderLoopRunning = true;
        try
        {
            while (_document is { } document && !_renderSuspended && NextToRender() is { } next)
            {
                (PageVisual visual, int width, int height) = next;
                int index = visual.Index;
                int generation = _generation;
                int turns = _quarterTurns;
                visual.Pending = (width, height, turns);
                SoftwareBitmap? bitmap = null;
                try
                {
                    bitmap = await Task.Run(() => PdfiumRendering.Render(document, index, width, height, turns));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A broken page: keep the white page, do not retry for this size.
                    visual.Failed = (width, height, turns);
                    Trace?.Invoke("pdf-render-error", new Dictionary<string, object?> { ["page"] = index, ["type"] = ex.GetType().Name });
                }

                visual.Pending = null;
                if (bitmap is null)
                {
                    continue;
                }

                if (generation != _generation || !ReferenceEquals(document, _document) || visual.Index != index || !_visuals.ContainsKey(index))
                {
                    bitmap.Dispose();
                    continue;
                }

                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(bitmap);
                if (generation != _generation || visual.Index != index || !_visuals.ContainsKey(index))
                {
                    source.Dispose();
                    bitmap.Dispose();
                    continue;
                }

                visual.SetBitmap(bitmap, source, (width, height, turns));
                Trace?.Invoke("pdf-rendered", new Dictionary<string, object?> { ["page"] = index, ["width"] = width, ["height"] = height, ["realized"] = _visuals.Count });
            }
        }
        finally
        {
            _renderLoopRunning = false;
        }
    }

    /// <summary>The page to draw next: visible pages before the ones above/below, nearest to the current page first.</summary>
    private (PageVisual Visual, int Width, int Height)? NextToRender()
    {
        double top = _scroller.VerticalOffset;
        double bottom = top + _scroller.ViewportHeight;
        (PageVisual, int, int)? best = null;
        double bestScore = double.MaxValue;
        foreach (PageVisual visual in _visuals.Values)
        {
            (int w, int h) = DesiredPixels(visual);
            var key = (w, h, _quarterTurns);
            if (visual.Rendered is { } done && Math.Abs(done.Width - w) <= 1 && Math.Abs(done.Height - h) <= 1 && done.Turns == key.Item3)
            {
                continue;
            }

            if (visual.Failed == key || visual.Pending == key)
            {
                continue;
            }

            bool visible = visual.Placed.Bottom > top && visual.Placed.Y < bottom;
            double score = (visible ? 0 : 1_000_000) + Math.Abs(visual.Index - _currentPage);
            if (score < bestScore)
            {
                bestScore = score;
                best = (visual, w, h);
            }
        }

        return best;
    }

    private (int Width, int Height) DesiredPixels(PageVisual visual)
    {
        double scale = PixelScale;
        double width = Math.Max(1, Math.Round(visual.Placed.Width * scale));
        double height = Math.Max(1, Math.Round(visual.Placed.Height * scale));
        if (width * height > MaxPagePixels)
        {
            double reduce = Math.Sqrt(MaxPagePixels / (width * height));
            width = Math.Floor(width * reduce);
            height = Math.Floor(height * reduce);
        }

        return ((int)width, (int)height);
    }

    private void RequestLinks(int page)
    {
        if (_document is not { } document || _links.ContainsKey(page))
        {
            return;
        }

        int turns = _quarterTurns;
        int generation = _generation;
        _ = Task.Run(() =>
        {
            try
            {
                return document.GetLinks(page, turns);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException or ArgumentOutOfRangeException)
            {
                return (IReadOnlyList<PdfLink>)[];
            }
        }).ContinueWith(
            task =>
            {
                if (generation == _generation && _visuals.ContainsKey(page))
                {
                    _links[page] = task.Result;
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Selection and search rectangles of a page, computed in the background.</summary>
    private void RequestOverlayRects(int page)
    {
        if (_document is not { } document)
        {
            return;
        }

        int turns = _quarterTurns;
        int generation = _generation;
        int selectionVersion = _selectionVersion;
        (int Start, int Count)? selected = SelectionOn(page);
        List<int> matchIndexes = _matchesByPage.TryGetValue(page, out List<int>? list) ? list : [];
        List<PdfTextMatch> matches = matchIndexes.Select(i => _matches[i]).ToList();
        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<PdfRect> selection = selected is { } s && s.Count > 0 ? document.GetTextBounds(page, turns, s.Start, s.Count) : [];
                var found = new List<(PdfRect, int)>();
                for (int i = 0; i < matches.Count; i++)
                {
                    foreach (PdfRect rect in document.GetTextBounds(page, turns, matches[i].CharIndex, matches[i].CharCount))
                    {
                        found.Add((rect, matchIndexes[i]));
                    }
                }

                return (selection, (IReadOnlyList<(PdfRect, int)>)found);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException or ArgumentOutOfRangeException)
            {
                return ((IReadOnlyList<PdfRect>)[], (IReadOnlyList<(PdfRect, int)>)[]);
            }
        }).ContinueWith(
            task =>
            {
                if (generation != _generation || !_visuals.TryGetValue(page, out PageVisual? visual))
                {
                    return;
                }

                if (selectionVersion == _selectionVersion)
                {
                    _selectionRects[page] = task.Result.Item1;
                }

                _matchRects[page] = task.Result.Item2;
                DrawOverlay(visual);
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RequestMatchRects()
    {
        foreach (int page in _visuals.Keys)
        {
            RequestOverlayRects(page);
        }
    }

    /// <summary>Characters of <paramref name="page"/> inside the selection, or null.</summary>
    private (int Start, int Count)? SelectionOn(int page)
    {
        if (Range() is not { } range)
        {
            return null;
        }

        (PdfTextPosition start, PdfTextPosition end) = range;
        if (page < start.Page || page > end.Page)
        {
            return null;
        }

        int from = page == start.Page ? start.Index : 0;
        int to = page == end.Page ? end.Index : int.MaxValue / 2;
        return (from, Math.Max(0, to - from));
    }

    /// <summary>Draws the selection and search rectangles of a page (from the cached rectangles, at the current zoom).</summary>
    private void DrawOverlay(PageVisual visual)
    {
        visual.Overlay.Children.Clear();
        double scale = DipScale;
        if (_matchRects.TryGetValue(visual.Index, out IReadOnlyList<(PdfRect Rect, int Match)>? matches))
        {
            foreach ((PdfRect rect, int match) in matches)
            {
                AddRect(visual.Overlay, rect, scale, match == _currentMatch ? CurrentMatchBrush : MatchBrush);
            }
        }

        if (SelectionOn(visual.Index) is not null && _selectionRects.TryGetValue(visual.Index, out IReadOnlyList<PdfRect>? selection))
        {
            foreach (PdfRect rect in selection)
            {
                AddRect(visual.Overlay, rect, scale, SelectionBrush);
            }
        }
    }

    private static void AddRect(Canvas overlay, PdfRect rect, double scale, Brush brush)
    {
        var shape = new Rectangle { Width = Math.Max(1, rect.Width * scale), Height = Math.Max(1, rect.Height * scale), Fill = brush, IsHitTestVisible = false };
        Canvas.SetLeft(shape, rect.Left * scale);
        Canvas.SetTop(shape, rect.Top * scale);
        overlay.Children.Add(shape);
    }

    private void OnSelectionChanged()
    {
        Trace?.Invoke("pdf-selection", new Dictionary<string, object?>
        {
            ["active"] = _selectionActive,
            ["anchor"] = _anchor is { } a ? $"{a.Page}:{a.Index}" : null,
            ["focus"] = _focus is { } f ? $"{f.Page}:{f.Index}" : null,
        });
        _selectionVersion++;
        _selectionRects.Clear();
        foreach (PageVisual visual in _visuals.Values)
        {
            DrawOverlay(visual);
            RequestOverlayRects(visual.Index);
        }

        SelectionChanged?.Invoke();
    }

    // ---------------------------------------------------------------- pointer: links and text selection

    /// <summary>Page under a canvas point and the point in display points of that page.</summary>
    private (PlacedPage Page, double X, double Y)? Hit(Point point)
    {
        if (_layout?.PageAt(point.X, point.Y) is not { } page)
        {
            return null;
        }

        double scale = DipScale;
        return (page, (point.X - page.X) / scale, (point.Y - page.Y) / scale);
    }

    private PdfLink? LinkAt(Point point)
    {
        if (Hit(point) is not { } hit || !_links.TryGetValue(hit.Page.Index, out IReadOnlyList<PdfLink>? links))
        {
            return null;
        }

        foreach (PdfLink link in links)
        {
            if (link.Bounds.Contains(hit.X, hit.Y))
            {
                return link;
            }
        }

        return null;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed || e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            return;
        }

        _pressPoint = point.Position;
        _pressedLink = LinkAt(point.Position);
        if (_canvas.CapturePointer(e.Pointer))
        {
            _dragPointer = e.Pointer.PointerId;
        }

        if (_pressedLink is null)
        {
            bool extend = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) && _anchor is not null;
            if (!extend && (_anchor is not null || _selectionActive))
            {
                _anchor = null;
                _focus = null;
                _selectionActive = false;
                OnSelectionChanged();
            }

            _ = HitTextAsync(point.Position, startSelection: !extend);
        }

        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Point position = e.GetCurrentPoint(_canvas).Position;
        if (_dragPointer == e.Pointer.PointerId)
        {
            if (_pressedLink is not null && Distance(position, _pressPoint) > 4)
            {
                _pressedLink = null; // dragging from a link selects text instead
            }

            if (_pressedLink is null)
            {
                _pendingHit = position;
                PumpHits();
                AutoScroll(e.GetCurrentPoint(_scroller).Position);
            }

            return;
        }

        ProtectedCursor = InputSystemCursor.Create(LinkAt(position) is not null ? InputSystemCursorShape.Hand : InputSystemCursorShape.IBeam);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId)
        {
            return;
        }

        Point position = e.GetCurrentPoint(_canvas).Position;
        PdfLink? link = _pressedLink;
        EndDrag();
        _canvas.ReleasePointerCapture(e.Pointer);
        if (link is not null && Distance(position, _pressPoint) <= 4)
        {
            Trace?.Invoke("pdf-link", new Dictionary<string, object?>
            {
                ["kind"] = link.Target is PdfPageTarget ? "page" : "uri",
                ["page"] = (link.Target as PdfPageTarget)?.Destination.PageIndex,
                ["uri"] = (link.Target as PdfUriTarget)?.Uri,
            });
            switch (link.Target)
            {
                case PdfPageTarget page:
                    _ = GoToDestinationAsync(page.Destination);
                    break;
                case PdfUriTarget uri:
                    UriRequested?.Invoke(uri.Uri);
                    break;
            }
        }
    }

    private void EndDrag()
    {
        _dragPointer = null;
        _pressedLink = null;
    }

    /// <summary>Double click: select the word under the pointer.</summary>
    private async void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_document is not { } document || Hit(e.GetPosition(_canvas)) is not { } hit)
        {
            return;
        }

        int turns = _quarterTurns;
        int page = hit.Page.Index;
        (int Start, int End)? word = await Task.Run(() =>
        {
            int index = document.CharIndexAt(page, turns, hit.X, hit.Y, 2);
            if (index < 0)
            {
                return ((int, int)?)null;
            }

            int count = document.GetCharCount(page);
            int from = Math.Max(0, index - 64);
            string text = document.GetText(page, from, Math.Min(128, count - from));
            int at = index - from;
            if (at >= text.Length || !char.IsLetterOrDigit(text[at]))
            {
                return (index, index + 1);
            }

            int start = at;
            int end = at;
            while (start > 0 && char.IsLetterOrDigit(text[start - 1]))
            {
                start--;
            }

            while (end < text.Length && char.IsLetterOrDigit(text[end]))
            {
                end++;
            }

            return (from + start, from + end);
        });
        if (word is { } w && ReferenceEquals(document, _document))
        {
            _anchor = new PdfTextPosition(page, w.Start);
            _focus = new PdfTextPosition(page, Math.Max(w.Start, w.End - 1));
            _selectionActive = true;
            OnSelectionChanged();
        }
    }

    /// <summary>Text position under the pointer, computed in the background; the newest pointer position wins.</summary>
    private async Task HitTextAsync(Point point, bool startSelection)
    {
        if (_document is not { } document || Hit(point) is not { } hit)
        {
            return;
        }

        int turns = _quarterTurns;
        int page = hit.Page.Index;
        int index = await Task.Run(() =>
        {
            try
            {
                int exact = document.CharIndexAt(page, turns, hit.X, hit.Y, 2);
                return exact >= 0 ? exact : document.CharIndexAt(page, turns, hit.X, hit.Y, 24);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException)
            {
                return -1;
            }
        });
        if (!ReferenceEquals(document, _document) || index < 0)
        {
            return;
        }

        var position = new PdfTextPosition(page, index);
        if (startSelection)
        {
            _anchor = position;
            _focus = position;
            return; // a click alone selects nothing
        }

        if (_anchor is null || _focus == position)
        {
            return;
        }

        _focus = position;
        _selectionActive = true;
        OnSelectionChanged();
    }

    private void PumpHits()
    {
        if (_hitInFlight || _pendingHit is not { } point)
        {
            return;
        }

        _pendingHit = null;
        _hitInFlight = true;
        _ = HitTextAsync(point, startSelection: false).ContinueWith(
            _ =>
            {
                _hitInFlight = false;
                PumpHits();
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Selecting beyond the top or bottom edge scrolls.</summary>
    private void AutoScroll(Point viewportPoint)
    {
        if (viewportPoint.Y < 0)
        {
            ScrollBy(0, Math.Max(-60, viewportPoint.Y));
        }
        else if (viewportPoint.Y > _scroller.ViewportHeight)
        {
            ScrollBy(0, Math.Min(60, viewportPoint.Y - _scroller.ViewportHeight));
        }
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>One page on the canvas: white paper, the drawn bitmap, and an overlay for highlights.</summary>
    private sealed class PageVisual
    {
        public PageVisual()
        {
            Image = new XamlImage { Stretch = Stretch.Fill, IsHitTestVisible = false };
            Overlay = new Canvas { IsHitTestVisible = false };
            Root = new Grid
            {
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x40, 0, 0, 0)),
                BorderThickness = new Thickness(0.5),
                IsHitTestVisible = false,
            };
            Root.Children.Add(Image);
            Root.Children.Add(Overlay);
        }

        public Grid Root { get; }

        public XamlImage Image { get; }

        public Canvas Overlay { get; }

        public int Index { get; set; } = -1;

        public PlacedPage Placed { get; private set; }

        public (int Width, int Height, int Turns)? Rendered { get; private set; }

        public (int Width, int Height, int Turns)? Pending { get; set; }

        public (int Width, int Height, int Turns)? Failed { get; set; }

        private SoftwareBitmap? Bitmap { get; set; }

        private SoftwareBitmapSource? Source { get; set; }

        public void Place(PlacedPage placed)
        {
            Placed = placed;
            Root.Width = placed.Width;
            Root.Height = placed.Height;
            Canvas.SetLeft(Root, placed.X);
            Canvas.SetTop(Root, placed.Y);
        }

        public void SetBitmap(SoftwareBitmap bitmap, SoftwareBitmapSource source, (int Width, int Height, int Turns) key)
        {
            Image.Source = source;
            Source?.Dispose();
            Bitmap?.Dispose();
            Bitmap = bitmap;
            Source = source;
            Rendered = key;
            Failed = null;
        }

        public void Release()
        {
            Image.Source = null;
            Source?.Dispose();
            Bitmap?.Dispose();
            Source = null;
            Bitmap = null;
            Rendered = null;
            Pending = null;
            Failed = null;
            Overlay.Children.Clear();
            Index = -1;
        }
    }
}
