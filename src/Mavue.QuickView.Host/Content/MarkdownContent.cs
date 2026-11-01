using System.Globalization;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Mavue.Core.Text;
using Mavue.Core.Text.Syntax;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Content.Markdown;
using Mavue.QuickView.Host.Content.Text;
using Mavue.QuickView.Host.Presentation;
using Mavue.Shell;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// Markdown drawn as native XAML (SPEC §3.1 "Markdown preview"; docs/ARCHITECTURE.md §4.7.12): no browser, no
/// scripts, no network. The file is read whole up to the tier's limit (larger files are shown as source text),
/// parsed by Markdig off the UI thread (nesting bounded), and drawn one top-level block at a time as it scrolls into
/// view. Ctrl+U (or the button) switches to the colored source. Links open only on a click: web and mail addresses
/// in the browser or mail program, #anchors in the document; links to other files do nothing. Images next to the
/// document are shown, remote images are not downloaded.
/// </summary>
internal sealed class MarkdownContent : IPreviewContent, IViewTransformContent, ISelectionContent
{
    private const double BaseFontSize = 14;

    private static MarkdownPipeline? s_pipeline;

    private readonly PreviewServices _s;
    private readonly IPreviewHost _host;
    private readonly TextPreviewer _source;
    private readonly Func<VirtualKey, bool> _keys;
    private MarkdownView? _view;
    private RenderedDocument? _document;
    private CancellationTokenSource? _rendering;
    private bool _shown;
    private bool _showSource;
    private bool _sourceForced;

    public MarkdownContent(PreviewServices services, IPreviewHost host)
    {
        _s = services;
        _host = host;
        _source = new TextPreviewer(services, host);
        _keys = OnKey;
        services.Window.SourceToggled += OnSourceToggled;
        services.Window.WrapToggled += wrap =>
        {
            if (_shown && (_showSource || _sourceForced))
            {
                _source.SetWrap(wrap);
                _s.Window.SetTextTools(_sourceForced ? null : true, wrap);
                _s.Settings.WrapPlainText = wrap;
                _s.Settings.Save(settings => settings with { WrapPlainText = wrap });
            }
        };
        services.Window.ThemeChanged += () =>
        {
            if (_shown && _view is not null && _document is not null && !_showSource)
            {
                _view.RefreshTheme(_s.Window);
                Redraw();
            }
        };
    }

    public PreviewContentKind Kind => PreviewContentKind.Markdown;

    public bool HasSelection => _shown && SelectedText.Length > 0;

    private string SelectedText => _showSource || _sourceForced ? _source.SelectedText : _view?.SelectedText ?? string.Empty;

    /// <summary>Markdown, parsed with the GitHub-style extensions; HTML in the document is parsed but never rendered as HTML.</summary>
    private static MarkdownPipeline Pipeline => s_pipeline ??= new MarkdownPipelineBuilder
    {
        MaximumNestingDepth = 64,
    }
        .UsePipeTables()
        .UseGridTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseYamlFrontMatter()
        .UseFootnotes()
        .UseAlertBlocks()
        .UseDefinitionLists()
        .UseMathematics()
        .UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub)
        .Build();

    public async Task ShowAsync(PreviewItem item, CancellationToken cancellation)
    {
        _shown = true;
        _s.ImageView.ReleaseDisplayedBitmap();
        _s.ImageView.RecordShown(SurfaceImageKind.None, default, 0, _host.DisplayScale());
        ClearRendered();
        _source.Close();
        TextPreviewLimits limits = TextPreviewLimits.For(_s.Resources.Policy.Tier);
        _sourceForced = item.Facts.Length > limits.MaxMarkdownBytes;
        if (_showSource || _sourceForced)
        {
            string? notice = _sourceForced
                ? string.Format(CultureInfo.CurrentCulture, _s.Strings.GetString("QuickView_Markdown_TooLarge"), ByteSizeText.Format(limits.MaxMarkdownBytes))
                : null;
            await ShowSourceAsync(item, notice, cancellation);
            return;
        }

        long start = QuickViewTimeline.Now;
        (string? text, TextEncodingInfo? encoding, int lines) = await Task.Run(() => Read(item.Path, limits), cancellation);
        if (text is null || encoding is null)
        {
            await NotTextAsync(item, cancellation);
            return;
        }

        MarkdownDocument parsed = await Task.Run(() => Markdig.Markdown.Parse(text, Pipeline), cancellation);
        cancellation.ThrowIfCancellationRequested();
        double parseMs = Ms(start);
        var blocks = parsed.ToList();
        _document = new RenderedDocument(item, blocks, Anchors(blocks), System.IO.Path.GetDirectoryName(item.Path), encoding, lines);
        _view ??= new MarkdownView(_s.Window);
        _view.RefreshTheme(_s.Window);
        _view.SetNotice(null);
        _s.Window.Surface.ShowStatusOnly(string.Empty);
        _s.Window.ShowCard(_view.Root);
        Redraw();
        _host.Display.MarkContent(item.RequestId);
        _s.Window.ContentKeyHandler = _keys;
        _s.Window.SetTextTools(source: false, wrap: null);
        _s.Window.SetViewControls(true, false, TextPreviewer.ZoomText);
        _host.SetInfo(string.Format(
            CultureInfo.CurrentCulture,
            _s.Strings.GetString("QuickView_InfoText"),
            "Markdown",
            encoding.Name,
            string.Format(CultureInfo.CurrentCulture, _s.Strings.GetString("QuickView_Text_Lines"), lines.ToString("N0", CultureInfo.CurrentCulture)),
            ByteSizeText.Format(item.Facts.Length)));
        _s.Timeline.Mark(item.RequestId, "full-set", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["decoder"] = "markdown",
            ["blocks"] = blocks.Count,
            ["encoding"] = encoding.Name,
            ["parseMs"] = parseMs,
        });
        await QuickViewPresence.NextFrameAsync();
        if (!cancellation.IsCancellationRequested)
        {
            _s.Timeline.Mark(item.RequestId, "full-visible", QuickViewTimeline.Now, new Dictionary<string, object?> { ["realizedBlocks"] = _view.RealizedBlocks });
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
            if (ReferenceEquals(_s.Window.CardContent, _view?.Root) || ReferenceEquals(_s.Window.CardContent, _source.Root))
            {
                _s.Window.ShowCard(null);
            }

            _s.Window.SetTextTools(null, null);
        }

        if (ReferenceEquals(_s.Window.ContentKeyHandler, _keys))
        {
            _s.Window.ContentKeyHandler = null;
        }

        _shown = false;
        ClearRendered();
        if (scope == ContentReset.Hidden)
        {
            _source.Release();
            _view = null;
            _showSource = false;
        }
        else
        {
            _source.Close();
        }
    }

    /// <summary>Both views follow the window's size by themselves.</summary>
    public AreaResponse OnAreaChanged() => _shown ? AreaResponse.Handled : AreaResponse.ReloadIfChanged;

    public void Dispose()
    {
        ClearRendered();
        _source.Release();
    }

    public void Zoom(QuickViewZoomCommand command, int wheelDelta, Point? anchor)
    {
        if (!_shown || !_host.IsVisible)
        {
            return;
        }

        if (_showSource || _sourceForced)
        {
            _s.Window.SetViewControls(true, false, _source.Zoom(command, wheelDelta));
            return;
        }

        double next = TextPreviewer.Step(TextPreviewer.Scale, command, wheelDelta);
        if (Math.Abs(next - TextPreviewer.Scale) > 0.001)
        {
            TextPreviewer.Scale = next;
            Redraw(keepPlace: true);
        }

        _s.Window.SetViewControls(true, false, TextPreviewer.ZoomText);
        _s.Timeline.Mark(_host.CurrentRequest, "zoom", QuickViewTimeline.Now, new Dictionary<string, object?> { ["command"] = command.ToString(), ["factor"] = TextPreviewer.Scale, ["content"] = "markdown" });
    }

    public void Rotate(int quarterTurns)
    {
    }

    public Task CopySelectionAsync()
    {
        string selected = SelectedText;
        if (selected.Length > 0)
        {
            var package = new DataPackage();
            package.SetText(selected);
            Clipboard.SetContent(package);
        }

        return Task.CompletedTask;
    }

    private async Task ShowSourceAsync(PreviewItem item, string? notice, CancellationToken cancellation)
    {
        bool wrap = _s.Settings.WrapPlainText;
        bool shown = await _source.ShowAsync(item, SyntaxLanguages.ById("markdown"), "Markdown", wrap, lineNumbers: true, notice, cancellation);
        if (!shown)
        {
            await NotTextAsync(item, cancellation);
            return;
        }

        _host.Display.MarkContent(item.RequestId);
        _s.Window.ContentKeyHandler = _keys;
        _s.Window.SetTextTools(_sourceForced ? null : true, wrap);
        _s.Window.SetViewControls(true, false, TextPreviewer.ZoomText);
    }

    private async Task NotTextAsync(PreviewItem item, CancellationToken cancellation)
    {
        _shown = false;
        _host.Display.MarkMessage(item.RequestId);
        _s.Timeline.Mark(item.RequestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = "text-binary" });
        await _host.ShowInfoCardAsync(item, "QuickView_Text_Binary", cancellation);
    }

    /// <summary>Reads and decodes the whole file (bounded by the caller's size check); null text when it is binary.</summary>
    private static (string? Text, TextEncodingInfo? Encoding, int Lines) Read(string path, TextPreviewLimits limits)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = Math.Min(stream.Length, limits.MaxMarkdownBytes);
        byte[] bytes = new byte[length];
        int read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        TextDetection detection = TextEncodingDetector.Detect(bytes.AsSpan(0, read), wholeFile: true);
        if (!detection.IsText || detection.Encoding is not { } encoding)
        {
            return (null, null, 0);
        }

        string text = encoding.Encoding.GetString(bytes, encoding.PreambleLength, read - encoding.PreambleLength);
        int lines = text.Length == 0 ? 0 : text.AsSpan().Count('\n') + (text.EndsWith('\n') ? 0 : 1);
        return (text, encoding, lines);
    }

    /// <summary>Heading identifiers (GitHub style) → the top-level block they are in, for #anchor links.</summary>
    private static Dictionary<string, int> Anchors(List<Block> blocks)
    {
        var anchors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < blocks.Count; i++)
        {
            IEnumerable<HeadingBlock> headings = blocks[i] switch
            {
                HeadingBlock heading => [heading],
                ContainerBlock container => container.Descendants<HeadingBlock>(),
                _ => [],
            };
            foreach (HeadingBlock heading in headings)
            {
                if (heading.TryGetAttributes()?.Id is { Length: > 0 } id)
                {
                    anchors.TryAdd(id, i);
                }
            }
        }

        return anchors;
    }

    private void Redraw(bool keepPlace = false)
    {
        if (_view is null || _document is null)
        {
            return;
        }

        _rendering?.Cancel();
        _rendering?.Dispose();
        _rendering = new CancellationTokenSource();
        double place = keepPlace ? _view.VerticalRatio : 0;
        var renderer = new MarkdownRenderer(
            Theme(),
            BaseFontSize * TextPreviewer.Scale,
            new MarkdownImages(_document.Folder),
            key => _s.Strings.GetString(key),
            OnLink,
            _view.Track,
            _host.DisplayScale(),
            _rendering.Token,
            (origin, drawn) => _s.Timeline.Mark(_host.CurrentRequest, "markdown-image", QuickViewTimeline.Now, new Dictionary<string, object?> { ["origin"] = origin.ToString(), ["drawn"] = drawn }));
        _view.Show(_document.Blocks, renderer, place);
    }

    private MarkdownTheme Theme()
    {
        QuickViewWindow w = _s.Window;
        var syntax = new Dictionary<SyntaxTokenKind, Microsoft.UI.Xaml.Media.Brush>();
        foreach (SyntaxTokenKind kind in Enum.GetValues<SyntaxTokenKind>())
        {
            if (kind != SyntaxTokenKind.Text)
            {
                syntax[kind] = w.ThemeBrush("QvSyntax" + (kind == SyntaxTokenKind.StringLiteral ? "String" : kind.ToString()) + "Brush");
            }
        }

        return new MarkdownTheme(
            w.ThemeBrush("QvTextBrush"),
            w.ThemeBrush("QvSecondaryTextBrush"),
            w.ThemeBrush("QvDividerBrush"),
            w.ThemeBrush("QvCodeBlockBrush"),
            w.ThemeBrush("QvLinkBrush"),
            w.ThemeBrush("QvSyntaxStringBrush"),
            syntax,
            w.ThemeBrush("QvSyntaxKeywordBrush"),
            w.ThemeBrush("QvSyntaxInsertedBrush"),
            w.ThemeBrush("QvSyntaxPreprocessorBrush"),
            w.ThemeBrush("QvSyntaxWarningBrush"),
            w.ThemeBrush("QvSyntaxErrorBrush"));
    }

    /// <summary>
    /// A link was clicked: #anchors scroll the document; http, https and mailto open in the default program (never in
    /// the E2E tests); anything else (other files, javascript:, file:, custom schemes) does nothing.
    /// </summary>
    private void OnLink(string url)
    {
        if (!_shown || _document is null)
        {
            return;
        }

        string trimmed = url.Trim();
        if (trimmed.StartsWith('#'))
        {
            string id = Uri.UnescapeDataString(trimmed[1..]);
            if (_document.Anchors.TryGetValue(id, out int index))
            {
                _view?.ScrollToBlock(index);
            }

            _s.Timeline.Mark(_host.CurrentRequest, "markdown-anchor", QuickViewTimeline.Now, new Dictionary<string, object?> { ["found"] = _document.Anchors.ContainsKey(id) });
            return;
        }

        bool web = Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" or "mailto";
        bool opened = web && !_s.Options.NoLaunch && ShellActions.OpenUri(uri!.AbsoluteUri);
        _s.Timeline.Mark(_host.CurrentRequest, "uri-opened", QuickViewTimeline.Now, new Dictionary<string, object?> { ["scheme"] = uri?.Scheme ?? "relative", ["allowed"] = web, ["opened"] = opened });
    }

    private void OnSourceToggled(bool source)
    {
        if (!_shown || _sourceForced || (_document is null && !_showSource))
        {
            return;
        }

        _showSource = source;
        long id = SelectionWorker.NextRequestId();
        _s.Timeline.Mark(id, "text-source", QuickViewTimeline.Now, new Dictionary<string, object?> { ["source"] = source, ["content"] = "markdown" });
        _host.MarkNavigation(id, "source-toggle");
        _host.ShowCurrentAgain(id);
    }

    private bool OnKey(VirtualKey key)
    {
        if (!_shown)
        {
            return false;
        }

        return _showSource || _sourceForced ? _source.HandleKey(key) : _view?.HandleKey(key) ?? false;
    }

    private void ClearRendered()
    {
        _rendering?.Cancel();
        _rendering?.Dispose();
        _rendering = null;
        _view?.Clear();
        _document = null;
    }

    private static double Ms(long since) => Math.Round((QuickViewTimeline.Now - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 1);

    private sealed record RenderedDocument(PreviewItem Item, List<Block> Blocks, Dictionary<string, int> Anchors, string? Folder, TextEncodingInfo Encoding, int Lines);
}
