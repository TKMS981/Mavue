using System.Globalization;
using Mavue.Core.Text;
using Mavue.Core.Text.Syntax;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Presentation;
using Microsoft.UI.Xaml;
using VirtualKey = Windows.System.VirtualKey;

namespace Mavue.QuickView.Host.Content.Text;

/// <summary>
/// Shows one text file in a <see cref="TextLinesView"/> (docs/ARCHITECTURE.md §4.7.12): opens it, detects the
/// encoding, shows the first screen as soon as the first megabyte is indexed, and keeps indexing the rest on a
/// low-priority thread (cancelled when the item changes). Used by the text content, and by Markdown and HTML for their
/// source views and fallbacks. Owns the document, the line source, the index thread and (until released) the view.
/// </summary>
internal sealed class TextPreviewer : IDisposable
{
    /// <summary>Text sizes of the zoom steps, relative to the base size.</summary>
    private static readonly double[] ZoomSteps = [0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4];

    private const double BaseFontSize = 13;

    private readonly PreviewServices _s;
    private readonly IPreviewHost _host;
    private TextLinesView? _view;
    private TextDocument? _document;
    private DocumentLineSource? _source;
    private CancellationTokenSource? _indexing;
    private PreviewItem? _item;
    private string _kindName = string.Empty;
    private string? _notice;
    private long _indexStarted;

    public TextPreviewer(PreviewServices services, IPreviewHost host)
    {
        _s = services;
        _host = host;
        services.Window.ThemeChanged += () => _view?.RefreshTheme();
    }

    /// <summary>The view's element (null until the first file).</summary>
    public FrameworkElement? Root => _view?.Root;

    /// <summary>Text size relative to the base size (shared by every text view of the session).</summary>
    public static double Scale { get; set; } = 1;

    public bool IsOpen => _document is not null;

    public bool Wrap => _view?.Wrap ?? false;

    public string SelectedText => _view?.SelectedText ?? string.Empty;

    /// <summary>"100%" for the zoom button.</summary>
    public static string ZoomText => ViewerZoom.Percent(Scale);

    /// <summary>
    /// Shows <paramref name="item"/> as text. Returns false when the content is binary (nothing shown).
    /// The view is put on the card layer; the caller records the content as shown.
    /// </summary>
    /// <param name="kindName">Shown on the info line ("C#", "Text", "Markdown").</param>
    /// <param name="notice">A note above the text (e.g. why Markdown is shown as source).</param>
    public async Task<bool> ShowAsync(PreviewItem item, SyntaxLanguage? language, string kindName, bool wrap, bool lineNumbers, string? notice, CancellationToken cancellation)
    {
        Close();
        long start = QuickViewTimeline.Now;
        TextPreviewLimits limits = TextPreviewLimits.For(_s.Resources.Policy.Tier);
        TextDocument? document = await Task.Run(() => TextDocument.Open(item.Path, limits, language, cancellation), cancellation);
        if (document is null)
        {
            _s.Timeline.Mark(item.RequestId, "text-open", QuickViewTimeline.Now, new Dictionary<string, object?> { ["binary"] = true, ["ms"] = Ms(start) });
            return false;
        }

        if (cancellation.IsCancellationRequested)
        {
            document.Dispose();
            cancellation.ThrowIfCancellationRequested();
        }

        _document = document;
        _item = item;
        _kindName = kindName;
        _notice = notice;
        _source = new DocumentLineSource(document);
        _source.Failed += ex => _s.Timeline.Mark(_host.CurrentRequest, "text-read-failed", QuickViewTimeline.Now, new Dictionary<string, object?> { ["type"] = ex.GetType().Name });
        _s.Timeline.Mark(item.RequestId, "text-open", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["encoding"] = document.Encoding.Name,
            ["language"] = language?.Id ?? "plain",
            ["bytes"] = document.Length,
            ["ms"] = Ms(start),
        });

        // Index in the background; the first screen waits only for the first megabyte.
        var firstBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _indexing = new CancellationTokenSource();
        StartIndexing(item, document, _source, firstBlock, _indexing.Token);
        using (cancellation.Register(() => firstBlock.TrySetCanceled()))
        {
            await firstBlock.Task;
        }

        cancellation.ThrowIfCancellationRequested();
        _source.Refresh();
        _view ??= new TextLinesView(_s.Window);
        _view.SetFontSize(BaseFontSize * Scale);
        _view.Attach(_source, Path.GetFileName(item.Path), wrap, lineNumbers);
        _view.SetNotice(notice);
        _view.SetEmptyMessage(document.Length <= document.Encoding.PreambleLength ? _s.Strings.GetString("QuickView_Text_Empty") : null);
        int screen = (int)Math.Ceiling(Math.Max(600, item.AreaHeight) / 12.0) + 20;
        await _source.LoadAsync(0, Math.Min(_source.LineCount, screen) - 1, cancellation);
        cancellation.ThrowIfCancellationRequested();

        _s.Window.Surface.ShowStatusOnly(string.Empty);
        _s.Window.ShowCard(_view.Root);
        _view.ScrollToLine(0);
        UpdateInfo();
        _s.Timeline.Mark(item.RequestId, "full-set", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["decoder"] = "text",
            ["lines"] = _source.LineCount,
            ["encoding"] = document.Encoding.Name,
            ["language"] = language?.Id ?? "plain",
            ["wrap"] = wrap,
        });
        await QuickViewPresence.NextFrameAsync();
        if (!cancellation.IsCancellationRequested)
        {
            _s.Timeline.Mark(item.RequestId, "full-visible", QuickViewTimeline.Now, new Dictionary<string, object?> { ["visibleLines"] = _view.VisibleLines });
        }

        return true;
    }

    /// <summary>Stops indexing and reading and closes the file (the view stays for the next file).</summary>
    public void Close()
    {
        _indexing?.Cancel();
        _indexing?.Dispose();
        _indexing = null;
        _view?.Detach();
        _source?.Dispose();
        _source = null;
        _document?.Dispose();
        _document = null;
        _item = null;
    }

    /// <summary>Also lets go of the view (Quick View closed).</summary>
    public void Release()
    {
        Close();
        _view = null;
    }

    public void Dispose() => Release();

    public void SetWrap(bool wrap) => _view?.SetWrap(wrap);

    public bool HandleKey(VirtualKey key) => _document is not null && (_view?.HandleKey(key) ?? false);

    /// <summary>Applies a zoom command to the text size; returns the new zoom text.</summary>
    public string Zoom(QuickViewZoomCommand command, int wheelDelta)
    {
        Scale = Step(Scale, command, wheelDelta);
        _view?.SetFontSize(BaseFontSize * Scale);
        return ZoomText;
    }

    /// <summary>The next text size for a zoom command (shared with the Markdown view).</summary>
    public static double Step(double scale, QuickViewZoomCommand command, int wheelDelta)
    {
        int index = Array.FindIndex(ZoomSteps, s => s >= scale - 0.001);
        index = index < 0 ? ZoomSteps.Length - 1 : index;
        return command switch
        {
            QuickViewZoomCommand.In => ZoomSteps[Math.Min(index + (ZoomSteps[index] > scale + 0.001 ? 0 : 1), ZoomSteps.Length - 1)],
            QuickViewZoomCommand.Out => ZoomSteps[Math.Max(index - 1, 0)],
            QuickViewZoomCommand.Wheel when wheelDelta > 0 => ZoomSteps[Math.Min(index + 1, ZoomSteps.Length - 1)],
            QuickViewZoomCommand.Wheel when wheelDelta < 0 => ZoomSteps[Math.Max(index - 1, 0)],
            QuickViewZoomCommand.Wheel => scale,
            _ => 1,
        };
    }

    private void StartIndexing(PreviewItem item, TextDocument document, DocumentLineSource source, TaskCompletionSource firstBlock, CancellationToken cancellation)
    {
        _indexStarted = QuickViewTimeline.Now;
        long requestId = item.RequestId;
        long lastUpdate = 0;
        var thread = new Thread(() =>
        {
            try
            {
                document.BuildIndex(item.Path, cancellation, () =>
                {
                    firstBlock.TrySetResult();
                    long now = Environment.TickCount64;
                    if (document.Index.IsComplete || now - lastUpdate > 150)
                    {
                        lastUpdate = now;
                        _s.Dispatcher.TryEnqueue(() =>
                        {
                            if (ReferenceEquals(_source, source))
                            {
                                source.Refresh();
                                UpdateInfo();
                            }
                        });
                    }
                });
                _s.Dispatcher.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(_source, source))
                    {
                        return;
                    }

                    source.Refresh();
                    UpdateInfo();
                    _view?.SetNotice(TruncationNotice(document) ?? _notice);
                    _s.Timeline.Mark(requestId, "text-index", QuickViewTimeline.Now, new Dictionary<string, object?>
                    {
                        ["lines"] = document.Index.LineCount,
                        ["bytes"] = document.Index.IndexedBytes,
                        ["truncated"] = document.Index.IsTruncated,
                        ["indexMemory"] = document.Index.MemoryBytes,
                        ["longestLine"] = document.Index.LongestLineBytes,
                        ["ms"] = Ms(_indexStarted),
                    });
                });
            }
            catch (OperationCanceledException)
            {
                firstBlock.TrySetCanceled();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                firstBlock.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Quick View text index",
        };
        thread.Start();
    }

    private string? TruncationNotice(TextDocument document)
    {
        if (!document.Index.IsTruncated)
        {
            return null;
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            _s.Strings.GetString("QuickView_Text_Truncated"),
            document.Index.LineCount.ToString("N0", CultureInfo.CurrentCulture),
            ByteSizeText.Format(document.Index.IndexedBytes),
            ByteSizeText.Format(document.Length));
    }

    private void UpdateInfo()
    {
        if (_document is not { } document || _source is null || _item is null || _host.CurrentPath != _item.Path)
        {
            return;
        }

        LineBreakKinds breaks = document.Index.LineBreaks;
        string newline = breaks switch
        {
            LineBreakKinds.CrLf => "CRLF",
            LineBreakKinds.Lf => "LF",
            LineBreakKinds.Cr => "CR",
            LineBreakKinds.None => string.Empty,
            _ => _s.Strings.GetString("QuickView_Text_MixedBreaks"),
        };
        string lines = string.Format(
            CultureInfo.CurrentCulture,
            _s.Strings.GetString(document.Index.IsComplete ? "QuickView_Text_Lines" : "QuickView_Text_LinesCounting"),
            _source.LineCount.ToString("N0", CultureInfo.CurrentCulture));
        string encoding = newline.Length > 0 ? $"{document.Encoding.Name} · {newline}" : document.Encoding.Name;
        _host.SetInfo(string.Format(CultureInfo.CurrentCulture, _s.Strings.GetString("QuickView_InfoText"), _kindName, encoding, lines, ByteSizeText.Format(document.Length)));
    }

    private static double Ms(long since) => Math.Round((QuickViewTimeline.Now - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 1);
}
