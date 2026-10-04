using Mavue.Core.Viewing;
using Mavue.Pdf;
using Mavue.Pdf.Pdfium;
using Mavue.Viewer.Controls;
using Mavue.Viewer.Playback;
using Mavue.Viewer.Rendering;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace Mavue.Viewer;

/// <summary>Outcome of opening a PDF in a <see cref="PdfSession"/>.</summary>
public enum PdfSessionOpenResult
{
    Opened,
    Password,
    Failed,

    /// <summary>Another file was requested meanwhile.</summary>
    Cancelled,
}

/// <summary>
/// One PDF shown with PDFium, shared by the main window and Quick View: the document, its
/// <see cref="PdfDocumentView"/> (layouts, zoom, rotation, selection, highlights, links), the search state, the outline,
/// selected text and page thumbnails. The PDF UI around it (<see cref="PdfSearchBar"/>, <see cref="PdfSidebar"/>,
/// <see cref="PdfPresentationWindow"/>) works on a session, so both hosts use the same code.
/// <para>
/// All members are used on the UI thread; PDFium work runs in the background. The document is closed (in the
/// background, so a page being drawn never blocks the UI) when another file opens or <see cref="Close"/> is called.
/// </para>
/// </summary>
public sealed class PdfSession
{
    /// <summary>Most search results kept (beyond, the search stops).</summary>
    public const int MaxSearchResults = 10_000;

    private PdfiumDocument? _document;
    private int _openVersion;
    private PlaybackTrace? _trace;
    private List<PdfTextMatch> _matches = [];

    public PdfSession()
    {
        View = new PdfDocumentView();
        View.ViewChanged += () =>
        {
            if (_document is not null)
            {
                ViewChanged?.Invoke();
            }
        };
        View.UriRequested += uri => UriRequested?.Invoke(uri);
        View.SelectionChanged += () => SelectionChanged?.Invoke();
    }

    /// <summary>PDFium can be used (pdfium.dll loads); otherwise hosts show PDFs as images (Windows.Data.Pdf).</summary>
    public static bool IsAvailable => PdfiumLibrary.IsAvailable;

    /// <summary>The control that shows the pages (put it on a <see cref="ViewerSurface"/> with ShowDocument).</summary>
    public PdfDocumentView View { get; }

    /// <summary>A document was opened (new outline, thumbnails, no search results).</summary>
    public event Action? Opened;

    /// <summary>The document was closed.</summary>
    public event Action? Closed;

    /// <summary>Current page, zoom, layout or orientation changed.</summary>
    public event Action? ViewChanged;

    /// <summary>Search results or the current result changed.</summary>
    public event Action? SearchChanged;

    public event Action? SelectionChanged;

    /// <summary>A link to a web address was clicked (http, https or mailto); the host decides how to open it.</summary>
    public event Action<string>? UriRequested;

    /// <summary>Diagnostics (tests).</summary>
    public PlaybackTrace? Trace
    {
        get => _trace;
        set
        {
            _trace = value;
            View.Trace = value;
        }
    }

    public bool IsOpen => _document is not null;

    public string? Path { get; private set; }

    public IPdfDocument? Document => _document;

    public int PageCount => _document is null ? 0 : View.PageCount;

    public int CurrentPage => View.CurrentPage;

    public double Factor => View.Factor;

    public ZoomSetting Zoom => View.Zoom;

    public PdfLayoutMode Layout => View.Mode;

    public int QuarterTurns => View.QuarterTurns;

    public bool HasSelection => IsOpen && View.HasSelection;

    /// <summary>The query of the current search ("" when none).</summary>
    public string SearchQuery { get; private set; } = string.Empty;

    public bool SearchMatchCase { get; private set; }

    public IReadOnlyList<PdfTextMatch> Matches => _matches;

    public int CurrentMatch => View.CurrentMatch;

    /// <summary>
    /// Opens <paramref name="path"/> and shows it in <see cref="View"/>, which must already be in the visual tree
    /// (it is laid out once before the pages are placed, so the first zoom is final). Page sizes are read in the
    /// background with the document.
    /// </summary>
    /// <param name="password">For an encrypted PDF (null: none). A wrong or missing password gives <see cref="PdfSessionOpenResult.Password"/>.</param>
    public async Task<PdfSessionOpenResult> OpenAsync(string path, ZoomSetting zoom, PdfLayoutMode layout, CancellationToken cancellation, string? password = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Close();
        int version = ++_openVersion;
        PdfiumDocument document;
        PdfSize[] sizes;
        try
        {
            (document, sizes) = await Task.Run(
                () =>
                {
                    PdfiumDocument opened = PdfiumDocument.Open(path, password);
                    try
                    {
                        var pageSizes = new PdfSize[opened.PageCount];
                        for (int i = 0; i < pageSizes.Length; i++)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            pageSizes[i] = opened.GetPageSize(i);
                        }

                        return (opened, pageSizes);
                    }
                    catch
                    {
                        opened.Dispose();
                        throw;
                    }
                },
                cancellation);
        }
        catch (PdfOpenException ex)
        {
            Trace?.Invoke("pdf-open-failed", new Dictionary<string, object?> { ["error"] = ex.Error.ToString() });
            return ex.Error == PdfOpenError.Password ? PdfSessionOpenResult.Password : PdfSessionOpenResult.Failed;
        }
        catch (OperationCanceledException)
        {
            return PdfSessionOpenResult.Cancelled;
        }

        if (cancellation.IsCancellationRequested || version != _openVersion)
        {
            DisposeLater(document);
            return PdfSessionOpenResult.Cancelled;
        }

        if (sizes.Length == 0)
        {
            DisposeLater(document);
            return PdfSessionOpenResult.Failed;
        }

        // One layout pass first: the view needs its size to choose the zoom.
        await NextFrameAsync();
        if (cancellation.IsCancellationRequested || version != _openVersion)
        {
            DisposeLater(document);
            return PdfSessionOpenResult.Cancelled;
        }

        _document = document;
        Path = path;
        View.Open(document, sizes, zoom, layout);
        Trace?.Invoke("pdf-opened", new Dictionary<string, object?> { ["pages"] = sizes.Length, ["engine"] = "pdfium", ["layout"] = layout.ToString() });
        Opened?.Invoke();
        return PdfSessionOpenResult.Opened;
    }

    /// <summary>Lets go of the document (the file is released in the background).</summary>
    public void Close()
    {
        _openVersion++;
        if (_document is not { } document)
        {
            return;
        }

        _document = null;
        Path = null;
        _matches = [];
        SearchQuery = string.Empty;
        View.Close();
        DisposeLater(document);
        Closed?.Invoke();
    }

    public void SetLayout(PdfLayoutMode layout) => View.SetMode(layout);

    public void SetZoom(ZoomSetting zoom, Point? anchor = null) => View.SetZoom(zoom, anchor);

    public void Rotate(int quarterTurns) => View.Rotate(quarterTurns > 0 ? 1 : -1);

    public bool StepPage(int delta) => View.StepPage(delta);

    public bool CanStepPage(int delta) => IsOpen && View.CanStepPage(delta);

    public void GoToPage(int page) => View.GoToPage(page);

    public bool HandleWheel(int wheelDelta) => IsOpen && View.HandleWheel(wheelDelta);

    public bool Scroll(double dx, double dy) => IsOpen && View.ScrollBy(dx, dy);

    public Task GoToDestinationAsync(PdfDestination destination) => IsOpen ? View.GoToDestinationAsync(destination) : Task.CompletedTask;

    /// <summary>
    /// Finds <paramref name="query"/> in every page (16 pages per background step, at most
    /// <see cref="MaxSearchResults"/>), highlights the results and shows the first one on or after the current page.
    /// </summary>
    public async Task<IReadOnlyList<PdfTextMatch>> SearchAsync(string query, bool matchCase, IProgress<(int Pages, int Matches)>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_document is not { } document)
        {
            return [];
        }

        SearchQuery = query;
        SearchMatchCase = matchCase;
        if (query.Length == 0)
        {
            ClearSearch();
            return [];
        }

        var results = new List<PdfTextMatch>();
        int pages = document.PageCount;
        for (int start = 0; start < pages && results.Count < MaxSearchResults; start += 16)
        {
            int end = Math.Min(pages, start + 16);
            int from = start;
            List<PdfTextMatch> chunk = await Task.Run(
                () =>
                {
                    var found = new List<PdfTextMatch>();
                    for (int page = from; page < end; page++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        found.AddRange(document.Find(page, query, matchCase, limit: 1000));
                    }

                    return found;
                },
                cancellation);
            results.AddRange(chunk.Take(MaxSearchResults - results.Count));
            progress?.Report((end, results.Count));
        }

        if (!ReferenceEquals(document, _document))
        {
            return [];
        }

        int current = View.CurrentPage;
        int first = results.FindIndex(m => m.PageIndex >= current);
        _matches = results;
        View.SetMatches(results, first < 0 ? 0 : first);
        Trace?.Invoke("pdf-search", new Dictionary<string, object?> { ["matches"] = results.Count, ["pages"] = pages });
        SearchChanged?.Invoke();
        return results;
    }

    /// <summary>Shows result <paramref name="index"/> (scrolls to it and marks it current).</summary>
    public async Task ShowMatchAsync(int index)
    {
        if (!IsOpen)
        {
            return;
        }

        await View.ShowMatchAsync(index);
        SearchChanged?.Invoke();
    }

    /// <summary>The next (+1) or previous (-1) result, wrapping around; false without results.</summary>
    public async Task<bool> StepMatchAsync(int direction)
    {
        if (_matches.Count == 0)
        {
            return false;
        }

        int next = (((View.CurrentMatch + direction) % _matches.Count) + _matches.Count) % _matches.Count;
        await ShowMatchAsync(next);
        return true;
    }

    public void ClearSearch()
    {
        _matches = [];
        SearchQuery = string.Empty;
        if (IsOpen)
        {
            View.SetMatches([], -1);
        }

        SearchChanged?.Invoke();
    }

    /// <summary>The text around a result, for a results list.</summary>
    public Task<string> MatchContextAsync(PdfTextMatch match)
    {
        if (_document is not { } document)
        {
            return Task.FromResult(string.Empty);
        }

        return Task.Run(() =>
        {
            try
            {
                int start = Math.Max(0, match.CharIndex - 30);
                string text = document.GetText(match.PageIndex, start, (match.CharIndex - start) + match.CharCount + 40);
                return string.Join(' ', text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException or ArgumentOutOfRangeException)
            {
                return string.Empty;
            }
        });
    }

    /// <summary>The outline (table of contents / bookmarks); empty when the document has none.</summary>
    public async Task<IReadOnlyList<PdfOutlineItem>> GetOutlineAsync()
    {
        if (_document is not { } document)
        {
            return [];
        }

        try
        {
            return await Task.Run(document.GetOutline);
        }
        catch (ObjectDisposedException)
        {
            return [];
        }
    }

    public Task<string> GetSelectedTextAsync() => IsOpen ? View.GetSelectedTextAsync() : Task.FromResult(string.Empty);

    public Task SelectAllAsync() => IsOpen ? View.SelectAllAsync() : Task.CompletedTask;

    public void ClearSelection() => View.ClearSelection();

    /// <summary>Puts the selected text on the clipboard; false when nothing is selected.</summary>
    public async Task<bool> CopySelectionAsync()
    {
        string text = await GetSelectedTextAsync();
        if (text.Length == 0)
        {
            return false;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        Trace?.Invoke("text-copied", new Dictionary<string, object?> { ["length"] = text.Length });
        return true;
    }

    /// <summary>A page drawn to fit <paramref name="maxWidth"/> × <paramref name="maxHeight"/> pixels (thumbnails), or null.</summary>
    public async Task<SoftwareBitmap?> RenderThumbnailAsync(int page, uint maxWidth, uint maxHeight, CancellationToken cancellation)
    {
        if (_document is not { } document || page < 0 || page >= document.PageCount)
        {
            return null;
        }

        try
        {
            return await Task.Run(
                () =>
                {
                    PdfSize size = document.GetPageSize(page);
                    double scale = Math.Min(maxWidth / size.Width, maxHeight / size.Height);
                    cancellation.ThrowIfCancellationRequested();
                    return PdfiumRendering.Render(document, page, (int)Math.Max(1, size.Width * scale), (int)Math.Max(1, size.Height * scale), 0);
                },
                cancellation);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Page size in DIPs at 100 % of <paramref name="page"/>.</summary>
    public (double Width, double Height) PageSizeDips(int page)
    {
        PdfSize size = View.PageSize(page);
        return (size.Width * PdfiumRendering.DipsPerPoint, size.Height * PdfiumRendering.DipsPerPoint);
    }

    /// <summary>Tests: where a page is in the window (client area, physical pixels).</summary>
    public Rect? PageRectInWindow(int page) => IsOpen ? View.PageRectInWindow(page) : null;

    private static void DisposeLater(PdfiumDocument document) => _ = Task.Run(document.Dispose);

    private static Task NextFrameAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            completion.TrySetResult();
        };
        CompositionTarget.Rendering += handler;
        return completion.Task;
    }
}
