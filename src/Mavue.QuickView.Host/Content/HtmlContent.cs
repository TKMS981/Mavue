using System.Globalization;
using Mavue.Core.Text;
using Mavue.Core.Text.Syntax;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Content.Html;
using Mavue.QuickView.Host.Content.Text;
using Mavue.QuickView.Host.Presentation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// HTML in a locked-down WebView2 (SPEC §3.1 "Safe HTML preview"; docs/ARCHITECTURE.md §4.7.12): no scripts, no
/// requests beyond the file itself (images, styles, fonts and frames from anywhere — network or disk — are refused,
/// data: images and inline styles are drawn), no navigation away, no new windows, downloads, external programs or
/// permissions (<see cref="HtmlBrowser"/>). The file is read whole up to the tier's limit (larger files are shown as
/// source). The browser is created for the first HTML file and ended when Quick View closes. Ctrl+U shows the
/// colored source; without WebView2 the source is shown with a note.
/// </summary>
internal sealed class HtmlContent : IPreviewContent, IViewTransformContent, ISelectionContent
{
    private readonly PreviewServices _s;
    private readonly IPreviewHost _host;
    private readonly TextPreviewer _source;
    private readonly Func<VirtualKey, bool> _keys;
    private HtmlBrowser? _browser;
    private bool _shown;
    private bool _showSource;
    private bool _sourceOnly;
    private int _blockMarks;

    public HtmlContent(PreviewServices services, IPreviewHost host)
    {
        _s = services;
        _host = host;
        _source = new TextPreviewer(services, host);
        _keys = key => _shown && (_showSource || _sourceOnly) && _source.HandleKey(key);
        services.Window.SourceToggled += OnSourceToggled;
        services.Window.WrapToggled += wrap =>
        {
            if (_shown && (_showSource || _sourceOnly))
            {
                _source.SetWrap(wrap);
                _s.Window.SetTextTools(_sourceOnly ? null : true, wrap);
                _s.Settings.WrapCode = wrap;
                _s.Settings.Save(settings => settings with { WrapCode = wrap });
            }
        };
    }

    public PreviewContentKind Kind => PreviewContentKind.Html;

    public bool HasSelection => _shown && (_showSource || _sourceOnly) && _source.SelectedText.Length > 0;

    public async Task ShowAsync(PreviewItem item, CancellationToken cancellation)
    {
        _shown = true;
        _blockMarks = 0;
        _s.ImageView.ReleaseDisplayedBitmap();
        _s.ImageView.RecordShown(SurfaceImageKind.None, default, 0, _host.DisplayScale());
        _source.Close();
        HideBrowser();
        TextPreviewLimits limits = TextPreviewLimits.For(_s.Resources.Policy.Tier);
        _sourceOnly = false;
        if (item.Facts.Length > limits.MaxHtmlBytes)
        {
            _sourceOnly = true;
            await ShowSourceAsync(item, string.Format(CultureInfo.CurrentCulture, _s.Strings.GetString("QuickView_Html_TooLarge"), ByteSizeText.Format(limits.MaxHtmlBytes)), cancellation);
            return;
        }

        if (_showSource)
        {
            await ShowSourceAsync(item, null, cancellation);
            return;
        }

        long start = QuickViewTimeline.Now;
        (byte[]? content, TextEncodingInfo? encoding) = await Task.Run(() => Read(item.Path, limits), cancellation);
        if (content is null || encoding is null)
        {
            await NotTextAsync(item, cancellation);
            return;
        }

        _browser ??= CreateBrowser();
        bool ready = await _browser.EnsureAsync();
        cancellation.ThrowIfCancellationRequested();
        if (!ready || _browser.Element is null)
        {
            _sourceOnly = true;
            await ShowSourceAsync(item, _s.Strings.GetString("QuickView_Html_NoWebView"), cancellation);
            return;
        }

        string? charset = HtmlCharset.For(content, encoding);
        _s.Window.ShowCard(null);
        _s.Window.Surface.ShowStatusOnly(string.Empty);
        _s.Window.SetWebView(_browser.Element, visible: true);
        bool loaded = await _browser.ShowAsync(content, charset, cancellation);
        cancellation.ThrowIfCancellationRequested();
        _host.Display.MarkContent(item.RequestId);
        _s.Window.ContentKeyHandler = _keys;
        _s.Window.SetTextTools(source: false, wrap: null);
        _host.SetInfo(string.Format(
            CultureInfo.CurrentCulture,
            _s.Strings.GetString("QuickView_InfoHtml"),
            charset ?? encoding.Name,
            ByteSizeText.Format(item.Facts.Length)));
        HtmlBlockReport report = _browser.Report();
        _s.Timeline.Mark(item.RequestId, "full-set", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["decoder"] = "html",
            ["loaded"] = loaded,
            ["charset"] = charset ?? "bom",
            ["blockedRequests"] = report.Requests,
            ["blockedNavigations"] = report.Navigations,
            ["ms"] = Math.Round((QuickViewTimeline.Now - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 1),
            ["runtime"] = HtmlBrowser.RuntimeVersion(),
        });
        await QuickViewPresence.NextFrameAsync();
        if (!cancellation.IsCancellationRequested)
        {
            _s.Timeline.Mark(item.RequestId, "full-visible", QuickViewTimeline.Now);
        }
    }

    public void Reset(ContentReset scope, string reason)
    {
        if (scope == ContentReset.Reload)
        {
            return; // the same file again: ShowAsync replaces what is shown
        }

        if (_shown)
        {
            if (ReferenceEquals(_s.Window.CardContent, _source.Root))
            {
                _s.Window.ShowCard(null);
            }

            _s.Window.SetTextTools(null, null);
            ReportBlocked();
        }

        if (ReferenceEquals(_s.Window.ContentKeyHandler, _keys))
        {
            _s.Window.ContentKeyHandler = null;
        }

        _shown = false;
        if (scope == ContentReset.Hidden)
        {
            // Quick View closed: the browser processes end (memory back to the system).
            _s.Window.SetWebView(null, false);
            _browser?.Close();
            _browser = null;
            _source.Release();
            _showSource = false;
        }
        else
        {
            HideBrowser();
            _source.Close();
        }
    }

    public AreaResponse OnAreaChanged() => _shown ? AreaResponse.Handled : AreaResponse.ReloadIfChanged;

    public void Dispose()
    {
        _browser?.Close();
        _source.Release();
    }

    /// <summary>The page zooms with Ctrl+wheel inside the browser; the source view uses the text size.</summary>
    public void Zoom(QuickViewZoomCommand command, int wheelDelta, Point? anchor)
    {
        if (_shown && _host.IsVisible && (_showSource || _sourceOnly))
        {
            _s.Window.SetViewControls(true, false, _source.Zoom(command, wheelDelta));
        }
    }

    public void Rotate(int quarterTurns)
    {
    }

    public Task CopySelectionAsync()
    {
        string selected = _source.SelectedText;
        if (selected.Length > 0)
        {
            var package = new DataPackage();
            package.SetText(selected);
            Clipboard.SetContent(package);
        }

        return Task.CompletedTask;
    }

    private HtmlBrowser CreateBrowser()
    {
        var browser = new HtmlBrowser();
        browser.Failed += why => _s.Timeline.Mark(_host.CurrentRequest, "html-browser-failed", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = why });
        browser.Blocked += (kind, detail) =>
        {
            // Kind and scheme only (never the address); at most a few per document.
            if (_blockMarks++ < 20)
            {
                _s.Timeline.Mark(_host.CurrentRequest, "html-blocked", QuickViewTimeline.Now, new Dictionary<string, object?> { ["kind"] = kind, ["detail"] = detail });
            }
        };
        return browser;
    }

    private void HideBrowser()
    {
        if (_browser?.Element is not null)
        {
            _s.Window.SetWebView(_browser.Element, visible: false);
            _browser.Blank();
        }
    }

    private void ReportBlocked()
    {
        if (_browser is null || _showSource || _sourceOnly)
        {
            return;
        }

        HtmlBlockReport report = _browser.Report();
        _s.Timeline.Mark(_host.CurrentRequest, "html-blocked-total", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["requests"] = report.Requests,
            ["navigations"] = report.Navigations,
            ["newWindows"] = report.NewWindows,
            ["downloads"] = report.Downloads,
            ["externalLaunches"] = report.ExternalLaunches,
            ["permissions"] = report.Permissions,
        });
    }

    private async Task ShowSourceAsync(PreviewItem item, string? notice, CancellationToken cancellation)
    {
        bool wrap = _s.Settings.WrapCode;
        bool shown = await _source.ShowAsync(item, SyntaxLanguages.ById("html"), "HTML", wrap, lineNumbers: true, notice, cancellation);
        if (!shown)
        {
            await NotTextAsync(item, cancellation);
            return;
        }

        _host.Display.MarkContent(item.RequestId);
        _s.Window.ContentKeyHandler = _keys;
        _s.Window.SetTextTools(_sourceOnly ? null : true, wrap);
        _s.Window.SetViewControls(true, false, TextPreviewer.ZoomText);
    }

    private async Task NotTextAsync(PreviewItem item, CancellationToken cancellation)
    {
        _shown = false;
        _host.Display.MarkMessage(item.RequestId);
        _s.Timeline.Mark(item.RequestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = "text-binary" });
        await _host.ShowInfoCardAsync(item, "QuickView_Text_Binary", cancellation);
    }

    private static (byte[]? Content, TextEncodingInfo? Encoding) Read(string path, TextPreviewLimits limits)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = new byte[Math.Min(stream.Length, limits.MaxHtmlBytes)];
        int read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (read < bytes.Length)
        {
            Array.Resize(ref bytes, read);
        }

        TextDetection detection = TextEncodingDetector.Detect(bytes, wholeFile: true);
        return detection.IsText ? (bytes, detection.Encoding) : (null, null);
    }

    private void OnSourceToggled(bool source)
    {
        if (!_shown || _sourceOnly)
        {
            return;
        }

        ReportBlocked(); // what the page shown so far tried
        _showSource = source;
        long id = SelectionWorker.NextRequestId();
        _s.Timeline.Mark(id, "text-source", QuickViewTimeline.Now, new Dictionary<string, object?> { ["source"] = source, ["content"] = "html" });
        _host.MarkNavigation(id, "source-toggle");
        _host.ShowCurrentAgain(id);
    }
}
