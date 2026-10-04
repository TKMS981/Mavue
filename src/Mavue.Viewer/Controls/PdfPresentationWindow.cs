using Mavue.Pdf;
using Mavue.Viewer.Playback;
using Mavue.Viewer.Rendering;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.System;
using XamlImage = Microsoft.UI.Xaml.Controls.Image;

namespace Mavue.Viewer.Controls;

/// <summary>
/// Presentation of a PDF (SPEC §9 "Presentation/slideshow"): one page at a time, full screen on the monitor of the
/// window it was started from, on black. → ↓ PageDown Space Enter or a click: next page; ← ↑ PageUp Backspace or a
/// right click: previous; the wheel steps too; Home/End: first/last; Esc ends. Pages are drawn by PDFium at the
/// screen's size in the background, the next page ahead; only the shown and the next page are kept. The presentation
/// ends by itself when its document is closed (another file opened in the host).
/// </summary>
public sealed partial class PdfPresentationWindow : Window
{
    private readonly PdfSession _session;
    private readonly IPdfDocument _document;
    private readonly XamlImage _image;
    private readonly TextBlock _hint;
    private readonly Grid _root;
    private readonly int _pageCount;
    private int _page;
    private int _renderVersion;
    private (int Page, SoftwareBitmap Bitmap)? _ahead;
    private SoftwareBitmap? _shownBitmap;
    private SoftwareBitmapSource? _shownSource;

    private PdfPresentationWindow(PdfSession session, IPdfDocument document, int page, string hint)
    {
        _session = session;
        _document = document;
        _pageCount = document.PageCount;
        _page = Math.Clamp(page, 0, _pageCount - 1);
        _image = new XamlImage { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _hint = new TextBlock
        {
            Text = hint,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(24),
            IsHitTestVisible = false,
        };
        _root = new Grid { Background = new SolidColorBrush(Colors.Black), IsTabStop = true };
        _root.Children.Add(_image);
        _root.Children.Add(_hint);
        _root.KeyDown += OnKeyDown;
        _root.PointerPressed += OnPointerPressed;
        _root.PointerWheelChanged += (_, e) => Step(e.GetCurrentPoint(_root).Properties.MouseWheelDelta > 0 ? -1 : +1);
        _root.SizeChanged += (_, _) => _ = ShowAsync();
        Content = _root;
        Title = "Mavue";
        _session.Closed += Close;
        Closed += (_, _) =>
        {
            _session.Closed -= Close;
            _renderVersion++;
            _image.Source = null;
            _shownSource?.Dispose();
            _shownBitmap?.Dispose();
            _ahead?.Bitmap.Dispose();
            Trace?.Invoke("presentation-end", new Dictionary<string, object?> { ["page"] = _page });
        };
    }

    /// <summary>Diagnostics (tests).</summary>
    public PlaybackTrace? Trace { get; set; }

    /// <summary>The page shown (zero-based).</summary>
    public int Page => _page;

    /// <summary>
    /// Starts presenting the session's document at its current page, full screen on the monitor of
    /// <paramref name="owner"/>; null when no document is open.
    /// </summary>
    /// <param name="hint">Short help shown in a corner (e.g. "Esc to end").</param>
    public static PdfPresentationWindow? Start(PdfSession session, nint owner, string hint, PlaybackTrace? trace = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Document is not { PageCount: > 0 } document)
        {
            return null;
        }

        var window = new PdfPresentationWindow(session, document, session.CurrentPage, hint) { Trace = trace };
        DisplayArea area = owner != 0
            ? DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(owner), DisplayAreaFallback.Nearest)
            : DisplayArea.Primary;
        RectInt32 bounds = area.OuterBounds;
        window.AppWindow.MoveAndResize(bounds);
        window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        window.Activate();
        window._root.Focus(FocusState.Programmatic);
        trace?.Invoke("presentation-start", new Dictionary<string, object?> { ["page"] = window._page, ["pages"] = document.PageCount });
        _ = window.ShowAsync();
        return window;
    }

    private void Step(int delta) => GoTo(_page + delta);

    private void GoTo(int page)
    {
        page = Math.Clamp(page, 0, _pageCount - 1);
        if (page == _page)
        {
            return;
        }

        _page = page;
        _ = ShowAsync();
    }

    /// <summary>Draws the current page for the screen (or takes the page drawn ahead), then draws the next one ahead.</summary>
    private async Task ShowAsync()
    {
        int version = ++_renderVersion;
        int page = _page;
        double scale = _root.XamlRoot?.RasterizationScale ?? 1;
        int width = (int)Math.Max(1, _root.ActualWidth * scale);
        int height = (int)Math.Max(1, _root.ActualHeight * scale);
        if (_root.ActualWidth <= 0)
        {
            return;
        }

        SoftwareBitmap? bitmap = null;
        if (_ahead is { } ahead && ahead.Page == page && ahead.Bitmap.PixelWidth <= width && ahead.Bitmap.PixelHeight <= height)
        {
            bitmap = ahead.Bitmap;
            _ahead = null;
        }

        try
        {
            bitmap ??= await Task.Run(() => RenderFit(page, width, height));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return; // the document closed meanwhile
        }

        if (version != _renderVersion)
        {
            bitmap.Dispose();
            return;
        }

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        if (version != _renderVersion)
        {
            source.Dispose();
            bitmap.Dispose();
            return;
        }

        _image.Width = bitmap.PixelWidth / scale;
        _image.Height = bitmap.PixelHeight / scale;
        _image.Source = source;
        _shownSource?.Dispose();
        _shownBitmap?.Dispose();
        _shownSource = source;
        _shownBitmap = bitmap;
        _hint.Opacity = page == 0 ? 1 : 0;
        Trace?.Invoke("presentation-page", new Dictionary<string, object?> { ["page"] = page, ["width"] = bitmap.PixelWidth, ["height"] = bitmap.PixelHeight });

        // Next page ahead (only one is kept).
        int next = page + 1;
        if (next < _pageCount && _ahead?.Page != next)
        {
            try
            {
                SoftwareBitmap nextBitmap = await Task.Run(() => RenderFit(next, width, height));
                if (version == _renderVersion)
                {
                    _ahead?.Bitmap.Dispose();
                    _ahead = (next, nextBitmap);
                }
                else
                {
                    nextBitmap.Dispose();
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException or ArgumentOutOfRangeException)
            {
                // Closed meanwhile.
            }
        }
    }

    private SoftwareBitmap RenderFit(int page, int width, int height)
    {
        PdfSize size = _document.GetPageSize(page);
        double scale = Math.Min(width / size.Width, height / size.Height);
        return PdfiumRendering.Render(_document, page, (int)Math.Max(1, size.Width * scale), (int)Math.Max(1, size.Height * scale), 0);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        switch (e.Key)
        {
            case VirtualKey.Right or VirtualKey.Down or VirtualKey.PageDown or VirtualKey.Space or VirtualKey.Enter or VirtualKey.N:
                Step(+1);
                break;
            case VirtualKey.Left or VirtualKey.Up or VirtualKey.PageUp or VirtualKey.Back or VirtualKey.P:
                Step(-1);
                break;
            case VirtualKey.Home:
                GoTo(0);
                break;
            case VirtualKey.End:
                GoTo(_pageCount - 1);
                break;
            case VirtualKey.Escape:
                Close();
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(_root);
        Step(point.Properties.IsRightButtonPressed ? -1 : +1);
    }
}
