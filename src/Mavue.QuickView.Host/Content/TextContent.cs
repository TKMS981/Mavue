using Mavue.Core.Text.Syntax;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Content.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// Plain text and source code (SPEC §3.1 "Text preview", "Code preview with syntax highlighting";
/// docs/ARCHITECTURE.md §4.7.12). Read only: the file is never written, run or interpreted. Only visible lines are
/// read, decoded and colored; the line index is built in the background and bounded (very large files show their
/// first gigabytes, very long lines are cut). Binary content behind a text name gets the information card.
/// Keys while Quick View has the keyboard: ↑/↓, PageUp/PageDown, Home/End scroll, W wraps lines, Ctrl+wheel and
/// Ctrl+=/− change the text size; ←/→ still move between files.
/// </summary>
internal sealed class TextContent : IPreviewContent, IViewTransformContent, ISelectionContent
{
    private readonly PreviewServices _s;
    private readonly IPreviewHost _host;
    private readonly TextPreviewer _previewer;
    private readonly Func<VirtualKey, bool> _keys;
    private bool _shown;
    private bool _code;

    public TextContent(PreviewServices services, IPreviewHost host)
    {
        _s = services;
        _host = host;
        _previewer = new TextPreviewer(services, host);
        _keys = key => _shown && _previewer.HandleKey(key);
        services.Window.WrapToggled += OnWrapToggled;
    }

    public PreviewContentKind Kind => PreviewContentKind.Text;

    public bool HasSelection => _shown && _previewer.SelectedText.Length > 0;

    public async Task ShowAsync(PreviewItem item, CancellationToken cancellation)
    {
        _shown = true;
        _s.ImageView.ReleaseDisplayedBitmap();
        _s.ImageView.RecordShown(SurfaceImageKind.None, default, 0, _host.DisplayScale());
        SyntaxLanguage? language = SyntaxLanguages.ForPath(item.Path);
        _code = language is not null && language.Id != "log";
        bool wrap = _code ? _s.Settings.WrapCode : _s.Settings.WrapPlainText;
        string kindName = language?.DisplayName ?? _s.Strings.GetString("QuickView_Text_Plain");
        bool shown = await _previewer.ShowAsync(item, language, kindName, wrap, lineNumbers: _code, notice: null, cancellation);
        if (!shown)
        {
            _shown = false;
            _host.Display.MarkMessage(item.RequestId);
            _s.Timeline.Mark(item.RequestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = "text-binary" });
            await _host.ShowInfoCardAsync(item, "QuickView_Text_Binary", cancellation);
            return;
        }

        _host.Display.MarkContent(item.RequestId);
        _s.Window.ContentKeyHandler = _keys;
        _s.Window.SetTextTools(source: null, wrap: wrap);
        _s.Window.SetViewControls(true, false, TextPreviewer.ZoomText);
    }

    public void Reset(ContentReset scope, string reason)
    {
        if (scope == ContentReset.Reload)
        {
            return; // the same file again (e.g. the window was resized): the view lays itself out
        }

        if (_shown || _previewer.IsOpen)
        {
            if (ReferenceEquals(_s.Window.CardContent, _previewer.Root))
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
        if (scope == ContentReset.Hidden)
        {
            _previewer.Release();
            TextPreviewer.Scale = 1;
        }
        else
        {
            _previewer.Close();
        }
    }

    /// <summary>The view follows the window's size by itself.</summary>
    public AreaResponse OnAreaChanged() => _shown ? AreaResponse.Handled : AreaResponse.ReloadIfChanged;

    public void Dispose() => _previewer.Release();

    public void Zoom(QuickViewZoomCommand command, int wheelDelta, Point? anchor)
    {
        if (_shown && _host.IsVisible)
        {
            string text = _previewer.Zoom(command, wheelDelta);
            _s.Window.SetViewControls(true, false, text);
            _s.Timeline.Mark(_host.CurrentRequest, "zoom", QuickViewTimeline.Now, new Dictionary<string, object?> { ["command"] = command.ToString(), ["factor"] = TextPreviewer.Scale, ["content"] = "text" });
        }
    }

    public void Rotate(int quarterTurns)
    {
    }

    public Task CopySelectionAsync()
    {
        string selected = _previewer.SelectedText;
        if (selected.Length > 0)
        {
            var package = new DataPackage();
            package.SetText(selected);
            Clipboard.SetContent(package);
        }

        return Task.CompletedTask;
    }

    private void OnWrapToggled(bool wrap)
    {
        if (!_shown)
        {
            return;
        }

        _previewer.SetWrap(wrap);
        _s.Window.SetTextTools(null, wrap);
        if (_code)
        {
            _s.Settings.WrapCode = wrap;
            _s.Settings.Save(settings => settings with { WrapCode = wrap });
        }
        else
        {
            _s.Settings.WrapPlainText = wrap;
            _s.Settings.Save(settings => settings with { WrapPlainText = wrap });
        }

        _s.Timeline.Mark(_host.CurrentRequest, "text-wrap", QuickViewTimeline.Now, new Dictionary<string, object?> { ["wrap"] = wrap, ["code"] = _code });
    }
}
