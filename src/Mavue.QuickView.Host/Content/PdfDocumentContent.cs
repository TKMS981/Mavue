using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Presentation;
using Mavue.Shell;
using Mavue.Viewer;
using Mavue.Viewer.Controls;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// A PDF in the session shared with Mavue.App (<see cref="PdfSession"/>: continuous / two-page / single layouts, find,
/// selection, outline, links, presentation) and the shared PDF controls. The document view replaces the image area;
/// pages are drawn in the background near the screen only. The document is open only while it is shown, so the
/// resident process never keeps the file. The timing log gets the same marks as an image (full-set when the current
/// page is drawn, then full-visible), so latency and E2E checks apply unchanged.
/// </summary>
internal sealed class PdfDocumentContent : IPreviewContent, IViewTransformContent, IPagedContent, ISelectionContent
{
    private readonly PreviewServices _s;
    private readonly IPreviewHost _host;
    private readonly PdfSession _pdf = new();
    private readonly PdfSearchBar _search;
    private readonly PdfSidebar _sidebar;
    private ZoomSetting? _viewZoom;
    private FileFacts? _facts;
    private long _pdfRequest;
    private bool _firstFrameMarked;
    private PdfPresentationWindow? _presentation;

    public PdfDocumentContent(PreviewServices services, IPreviewHost host)
    {
        _s = services;
        _host = host;
        QuickViewWindow window = services.Window;
        _search = new PdfSearchBar(services.Strings.GetString) { Session = _pdf, Trace = Trace };
        _sidebar = new PdfSidebar(services.Strings.GetString) { Session = _pdf, Trace = Trace };
        _search.Closed += () =>
        {
            window.SetSearchVisible(false);
            window.FocusContent();
        };
        window.AttachPdfControls(_search, _sidebar);
        window.PdfCommandRequested += OnCommand;
        window.DocumentWheelHandler = OnDocumentWheel;
        _pdf.Trace = Trace;
        _pdf.ViewChanged += OnViewChanged;
        _pdf.UriRequested += uri =>
        {
            // Only http/https/mailto reach here (PdfiumDocument filters links); never opened by the E2E tests.
            bool opened = !_s.Options.NoLaunch && ShellActions.OpenUri(uri);
            _s.Timeline.Mark(_host.CurrentRequest, "uri-opened", QuickViewTimeline.Now, new Dictionary<string, object?> { ["uri"] = uri, ["opened"] = opened });
        };
    }

    public PreviewContentKind Kind => PreviewContentKind.PdfDocument;

    public bool HasAuxiliaryWindow => _presentation is not null;

    public bool HasSelection => _pdf.HasSelection;

    public async Task ShowAsync(PreviewItem item, CancellationToken cancellation)
    {
        long requestId = item.RequestId;
        _facts = item.Facts;
        _s.Window.Surface.ShowDocument(_pdf.View);
        _s.ImageView.ReleaseDisplayedBitmap();
        _pdfRequest = requestId;
        _firstFrameMarked = false;
        ZoomSetting zoom = _viewZoom ?? ZoomSetting.For(_s.Settings.ScaleMode);
        PdfSessionOpenResult result = await _pdf.OpenAsync(item.Path, zoom, _s.Settings.PdfLayout, cancellation);
        if (result == PdfSessionOpenResult.Cancelled)
        {
            cancellation.ThrowIfCancellationRequested();
            return;
        }

        if (result != PdfSessionOpenResult.Opened)
        {
            Close();
            _s.Window.Surface.ShowStatusOnly(_s.Strings.GetString(result == PdfSessionOpenResult.Password ? "QuickView_Error_PdfPassword" : "QuickView_Error_Failed"));
            _host.Display.MarkMessage(requestId);
            _s.Timeline.Mark(requestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = result.ToString() });
            return;
        }

        _host.Display.MarkContent(requestId);
        _host.PlannedArea = (item.AreaWidth, item.AreaHeight);
        _s.Window.SetPdfTools(true, _s.Settings.PdfSidebarOpen, _pdf.Layout);
        _s.ImageView.RecordShown(SurfaceImageKind.None, default, 0, _host.DisplayScale());
        _s.Window.SetViewControls(true, true, ViewerZoom.Percent(_pdf.Factor));
        OnViewChanged();
        _host.StartPrefetch(item.AreaWidth, item.AreaHeight); // neighbor files; the view renders its own pages
    }

    public void Reset(ContentReset scope, string reason)
    {
        if (scope != ContentReset.Reload)
        {
            Close();
            _viewZoom = null;
        }
    }

    /// <summary>The document view lays itself out for a new area.</summary>
    public AreaResponse OnAreaChanged() => _pdf.IsOpen ? AreaResponse.Ignore : AreaResponse.ReloadIfChanged;

    public void Dispose() => Close();

    public void Zoom(QuickViewZoomCommand command, int wheelDelta, Point? anchor)
    {
        if (!_host.IsVisible || !_pdf.IsOpen)
        {
            return;
        }

        ZoomSetting zoom = command switch
        {
            QuickViewZoomCommand.Fit => ZoomSetting.For(_s.Settings.ScaleMode),
            QuickViewZoomCommand.ActualSize => ZoomSetting.ActualSize,
            QuickViewZoomCommand.In => new ZoomSetting(ZoomMode.Custom, ViewerZoom.StepIn(_pdf.Factor)),
            QuickViewZoomCommand.Out => new ZoomSetting(ZoomMode.Custom, ViewerZoom.StepOut(_pdf.Factor)),
            _ => new ZoomSetting(ZoomMode.Custom, ViewerZoom.Wheel(_pdf.Factor, wheelDelta)),
        };
        _viewZoom = zoom.Mode == ZoomMode.Custom ? zoom : null;
        _pdf.SetZoom(zoom.Mode == ZoomMode.Custom ? zoom with { Factor = ViewerZoom.Clamp(zoom.Factor, 10_000, 10_000) } : zoom, anchor);
        _s.Timeline.Mark(_host.CurrentRequest, "zoom", QuickViewTimeline.Now, new Dictionary<string, object?> { ["command"] = command.ToString(), ["factor"] = Math.Round(_pdf.Factor, 4), ["pdf"] = true });
    }

    public void Rotate(int quarterTurns)
    {
        if (_host.IsVisible && _pdf.IsOpen)
        {
            _pdf.Rotate(quarterTurns);
            _s.Timeline.Mark(_host.CurrentRequest, "rotate", QuickViewTimeline.Now, new Dictionary<string, object?> { ["quarterTurns"] = _pdf.QuarterTurns, ["pdf"] = true });
        }
    }

    /// <summary>Page steps (keys, hook, buttons), logged like a page render.</summary>
    public void StepPage(int delta, string source, long? inputQpc)
    {
        if (_host.IsVisible && _host.CurrentPath is not null && _pdf.IsOpen && _pdf.PageCount > 1)
        {
            StepPage(delta, source, inputQpc, target: null);
        }
    }

    public async Task CopySelectionAsync() => await _pdf.CopySelectionAsync();

    private void StepPage(int delta, string source, long? inputQpc, int? target)
    {
        int before = _pdf.CurrentPage;
        bool moved;
        if (target is { } page)
        {
            moved = page >= 0 && page < _pdf.PageCount && page != before;
            if (moved)
            {
                _pdf.GoToPage(page);
            }
        }
        else
        {
            moved = _pdf.StepPage(delta);
        }

        if (!moved)
        {
            _s.Timeline.Mark(_host.CurrentRequest, "pdf-page-edge", QuickViewTimeline.Now, new Dictionary<string, object?> { ["index"] = before, ["count"] = _pdf.PageCount, ["source"] = source });
            return;
        }

        LogPageMove(source, inputQpc);
    }

    /// <summary>The wheel over the PDF: a page turn in the single-page layout (logged like the page keys).</summary>
    private bool OnDocumentWheel(int delta)
    {
        int before = _pdf.CurrentPage;
        bool used = _pdf.HandleWheel(delta);
        if (used && _pdf.CurrentPage != before)
        {
            LogPageMove("wheel", null);
        }

        return used;
    }

    /// <summary>A page move of the session as a request of its own (nav "pdf-page", full-set, full-visible).</summary>
    private void LogPageMove(string source, long? inputQpc)
    {
        long id = SelectionWorker.NextRequestId();
        if (inputQpc is { } input)
        {
            _s.Timeline.Mark(id, "nav-input", input);
        }

        _s.Timeline.Mark(id, "pdf-page", QuickViewTimeline.Now, new Dictionary<string, object?> { ["index"] = _pdf.CurrentPage, ["count"] = _pdf.PageCount, ["source"] = source });
        _host.MarkNavigation(id, "pdf-page");
        _host.AdoptRequest(id);
        _pdfRequest = id;
        _firstFrameMarked = false;
        _ = MarkPageShownAsync(id, rendered: null);
    }

    /// <summary>full-set / full-visible for the page now current (after it is drawn, or at once when already drawn).</summary>
    private async Task MarkPageShownAsync(long requestId, (int Width, int Height)? rendered)
    {
        if (_firstFrameMarked || requestId != _pdfRequest)
        {
            return;
        }

        _firstFrameMarked = true;
        int page = _pdf.CurrentPage;
        (double width, double height) = _pdf.PageSizeDips(page);
        (int layoutWidth, int layoutHeight) = _s.Window.LayoutImageAreaPixels();
        (uint plannedWidth, uint plannedHeight) = _host.PlannedArea;
        _s.Timeline.Mark(requestId, "full-set", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["sourceWidth"] = Math.Round(width),
            ["sourceHeight"] = Math.Round(height),
            ["decodedWidth"] = rendered?.Width,
            ["decodedHeight"] = rendered?.Height,
            ["decodeMs"] = 0,
            ["decoder"] = "pdfium-view",
            ["areaWidth"] = plannedWidth,
            ["areaHeight"] = plannedHeight,
            ["layoutAreaWidth"] = layoutWidth,
            ["layoutAreaHeight"] = layoutHeight,
            ["dpi"] = NativeMethods.GetDpiForWindow(_s.Window.Handle),
            ["pageIndex"] = page,
            ["pageCount"] = _pdf.PageCount,
            ["layout"] = _pdf.Layout.ToString(),
        });
        await QuickViewPresence.NextFrameAsync();
        if (requestId == _pdfRequest)
        {
            _s.Timeline.Mark(requestId, "full-visible", QuickViewTimeline.Now);
        }
    }

    /// <summary>
    /// Session diagnostics into the timing log: the first drawn current page completes the request (full-set /
    /// full-visible), and page positions are logged for tests (window client, physical pixels).
    /// </summary>
    private void Trace(string name, IReadOnlyDictionary<string, object?> detail)
    {
        long request = _host.CurrentRequest;
        _s.Timeline.Mark(request, name, QuickViewTimeline.Now, detail);
        if (name is not ("pdf-rendered" or "pdf-scrolled") || !detail.TryGetValue("page", out object? value) || value is not int page)
        {
            return;
        }

        if (name == "pdf-rendered" && page == _pdf.CurrentPage && detail.TryGetValue("width", out object? w) && w is int width && detail.TryGetValue("height", out object? h) && h is int height)
        {
            _ = MarkPageShownAsync(_pdfRequest, (width, height));
        }

        _s.Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            foreach (int p in name == "pdf-scrolled" ? new[] { page, page + 1 } : [page])
            {
                if (_pdf.PageRectInWindow(p) is { } rect)
                {
                    _s.Timeline.Mark(_host.CurrentRequest, "pdf-geometry", QuickViewTimeline.Now, new Dictionary<string, object?>
                    {
                        ["page"] = p,
                        ["x"] = Math.Round(rect.X),
                        ["y"] = Math.Round(rect.Y),
                        ["width"] = Math.Round(rect.Width),
                        ["height"] = Math.Round(rect.Height),
                    });
                }
            }
        });
    }

    /// <summary>The session's page, zoom or layout changed: info line, page box, buttons, hook keys.</summary>
    private void OnViewChanged()
    {
        if (!_host.IsVisible || !_pdf.IsOpen || _facts is not { } facts)
        {
            return;
        }

        int page = _pdf.CurrentPage;
        _host.SetPaging(_pdf.PageCount > 1, _pdf.CanStepPage(-1), _pdf.CanStepPage(+1));
        _s.Window.SetPageNumber(page, _pdf.PageCount);
        _s.Window.SetViewControls(true, true, ViewerZoom.Percent(_pdf.Factor));
        _s.Window.SetPdfTools(true, _s.Settings.PdfSidebarOpen, _pdf.Layout);
        _host.SetInfo(PreviewInfoText.PdfPage(_s.Strings, facts, page, _pdf.PageCount));
    }

    private async void OnCommand(QuickViewPdfCommand command, int argument)
    {
        if (!_host.IsVisible || !_pdf.IsOpen)
        {
            return;
        }

        _s.Timeline.Mark(_host.CurrentRequest, "pdf-command", QuickViewTimeline.Now, new Dictionary<string, object?> { ["command"] = command.ToString(), ["argument"] = argument });
        switch (command)
        {
            case QuickViewPdfCommand.Find:
                _s.Window.SetSearchVisible(true);
                _search.Open();
                break;
            case QuickViewPdfCommand.FindNext:
                await _search.FindAsync(argument);
                break;
            case QuickViewPdfCommand.Sidebar:
                _s.Settings.PdfSidebarOpen = argument != 0;
                _s.Window.SetPdfTools(true, _s.Settings.PdfSidebarOpen, _pdf.Layout);
                bool open = _s.Settings.PdfSidebarOpen;
                _s.Settings.Save(s => s with { ShowPdfSidebar = open });
                break;
            case QuickViewPdfCommand.Layout:
                var layout = (PdfLayoutMode)argument;
                _s.Settings.PdfLayout = layout;
                _pdf.SetLayout(layout);
                _s.Window.SetPdfTools(true, _s.Settings.PdfSidebarOpen, layout);
                _s.Timeline.Mark(_host.CurrentRequest, "pdf-layout", QuickViewTimeline.Now, new Dictionary<string, object?> { ["mode"] = layout.ToString() });
                _s.Settings.Save(s => s with { PdfLayout = layout });
                break;
            case QuickViewPdfCommand.GoToPage:
                StepPage(0, "page-box", null, argument);
                break;
            case QuickViewPdfCommand.FocusPageBox:
                _s.Window.FocusPageBox();
                break;
            case QuickViewPdfCommand.SelectAll:
                await _pdf.SelectAllAsync();
                break;
            case QuickViewPdfCommand.Presentation:
                StartPresentation();
                break;
        }
    }

    /// <summary>Presents the PDF full screen on Quick View's monitor; Quick View stays open behind it.</summary>
    private void StartPresentation()
    {
        _presentation?.Close();
        _presentation = PdfPresentationWindow.Start(_pdf, _s.Window.Handle, _s.Strings.GetString("Pdf_PresentationHint"), Trace);
        if (_presentation is { } window)
        {
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_presentation, window))
                {
                    _presentation = null;
                }
            };
        }
    }

    /// <summary>Closes the document and its tools (another item, hide, failure).</summary>
    private void Close()
    {
        _presentation?.Close();
        _presentation = null;
        _search.Close();
        _s.Window.SetSearchVisible(false);
        if (_pdf.IsOpen)
        {
            _pdf.Close();
        }

        if (_s.Window.Surface.ShowsDocument)
        {
            _s.Window.Surface.ClearDocument();
        }

        _s.Window.SetPdfTools(false, _s.Settings.PdfSidebarOpen, _s.Settings.PdfLayout);
    }
}
