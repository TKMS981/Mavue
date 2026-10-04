using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XamlImage = Microsoft.UI.Xaml.Controls.Image;

namespace Mavue.Viewer.Controls;

/// <summary>
/// Display area shared by Mavue.App and Quick View: a decoded image (with an optional placeholder underneath),
/// a video or audio player, or a message. It only presents what it is given; deciding what to decode, at which
/// size, and when to stop playback belongs to the caller (<see cref="DocumentViewer"/> in the app, the Quick View
/// controller in the resident process).
/// <para>
/// Images are sized explicitly so a bitmap decoded for the area is shown 1:1 (one decoded pixel per screen pixel);
/// scrolling is enabled only when an image is larger than the area. Future zoom/pan belongs here (the
/// <see cref="ScrollViewer"/> already hosts the image).
/// </para>
/// </summary>
public sealed partial class ViewerSurface : UserControl
{
    /// <summary>Space around the content in device-independent pixels.</summary>
    public const double ContentPadding = 12;

    // Mouse wheel: high-resolution wheels report small deltas; one page per notch (WHEEL_DELTA = 120).
    private const int WheelNotch = 120;

    private readonly ScrollViewer _scroller;
    private readonly XamlImage _thumbnail;
    private readonly XamlImage _full;
    private readonly Grid _mediaHost;
    private readonly StackPanel _audioPanel;
    private readonly FontIcon _audioIcon;
    private readonly TextBlock _audioState;
    private readonly MediaPlayerElement _media;
    private readonly TextBlock _status;
    private readonly Grid _root;
    private UIElement? _document;
    private int _wheelAccumulated;

    // Panning with the mouse: where the drag started and the scroll offsets at that moment.
    private uint? _panPointer;
    private Windows.Foundation.Point _panStart;
    private (double X, double Y) _panOffsets;

    public ViewerSurface()
    {
        _thumbnail = new XamlImage { Stretch = Stretch.Uniform };
        _full = new XamlImage { Stretch = Stretch.Uniform };
        var imageHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        imageHost.Children.Add(_thumbnail);
        imageHost.Children.Add(_full);
        _scroller = new ScrollViewer
        {
            Padding = new Thickness(ContentPadding),
            IsTabStop = false,
            ZoomMode = ZoomMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = imageHost,
        };

        // Video and audio: Media Foundation through MediaPlayerElement with the built-in transport controls
        // (play/pause, seek bar, volume, cast). Zoom is off: the viewer sizes the picture (the WinUI 3 template has
        // no full-window button).
        _audioIcon = new FontIcon
        {
            Glyph = "",
            FontSize = 96,
            FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
        };
        _audioState = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 16 };
        _audioPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
            Visibility = Visibility.Collapsed,
        };
        _audioPanel.Children.Add(_audioIcon);
        _audioPanel.Children.Add(_audioState);
        _media = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _media.TransportControls.IsZoomButtonVisible = false;

        // Keyboard focus never stays in the transport controls, so the host's keys (arrows, Space, Esc, PageUp/Down)
        // keep their meaning; the controls are used with the mouse or the host's media shortcuts.
        _media.GotFocus += (_, _) => DispatcherQueue.TryEnqueue(() => KeyboardFocusTarget?.Focus(FocusState.Programmatic));
        _mediaHost = new Grid { Padding = new Thickness(ContentPadding), Visibility = Visibility.Collapsed };
        _mediaHost.Children.Add(_audioPanel);
        _mediaHost.Children.Add(_media);

        _status = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560,
            TextAlignment = TextAlignment.Center,
        };

        _root = new Grid();
        _root.Children.Add(_scroller);
        _root.Children.Add(_mediaHost);
        _root.Children.Add(_status);
        Content = _root;
        IsTabStop = false;

        // Dragging a zoomed image with the mouse or pen moves it (touch pans through the ScrollViewer itself).
        _scroller.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPanPressed), handledEventsToo: true);
        _scroller.AddHandler(PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPanMoved), handledEventsToo: true);
        _scroller.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPanEnded), handledEventsToo: true);
        _scroller.PointerCaptureLost += OnPanEnded;
        SizeChanged += (_, _) => AreaChanged?.Invoke(); // the surface itself: the image area is collapsed while a video plays
        Loaded += (_, _) =>
        {
            if (XamlRoot is { } xamlRoot)
            {
                xamlRoot.Changed -= OnXamlRootChanged;
                xamlRoot.Changed += OnXamlRootChanged;
            }
        };
    }

    /// <summary>
    /// Raised when the content area may have changed size: the window was resized, or moved to a monitor with
    /// another scale. Callers re-plan the current content if the area really changed.
    /// </summary>
    public event Action? AreaChanged;

    /// <summary>Element that gets keyboard focus back when the transport controls take it.</summary>
    public UIElement? KeyboardFocusTarget { get; set; }

    /// <summary>XAML's scale (physical pixels per device-independent pixel); may lag behind a monitor change.</summary>
    public double PixelScale => XamlRoot?.RasterizationScale ?? 1.0;

    /// <summary>True while a video or audio file is shown.</summary>
    public bool ShowsMedia => _mediaHost.Visibility == Visibility.Visible;

    /// <summary>True when the full image (not only the placeholder) is on screen.</summary>
    public bool HasFullImage => _full.Source is not null && _thumbnail.Source is null;

    /// <summary>The message shown over the content area ("" for none).</summary>
    public string Status => _status.Text;

    /// <summary>
    /// Text and icon colors. Without a call the theme's colors apply; Quick View sets fixed light colors because its
    /// background is always dark.
    /// </summary>
    public void SetTextColors(Brush text, Brush icon)
    {
        _status.Foreground = text;
        _audioState.Foreground = text;
        _audioIcon.Foreground = icon;
    }

    /// <summary>
    /// The content area according to the last XAML layout, in physical pixels (padding excluded). Measured on the
    /// surface itself: the image area is collapsed while a video plays, and its size then reads 0.
    /// </summary>
    public (int Width, int Height) LayoutAreaPixels()
    {
        double scale = PixelScale;
        return ((int)Math.Max(0, Math.Floor((ActualWidth - (2 * ContentPadding)) * scale)),
                (int)Math.Max(0, Math.Floor((ActualHeight - (2 * ContentPadding)) * scale)));
    }

    /// <summary>
    /// Sizes both images to an exact box (device-independent pixels) so a bitmap decoded for that box is shown 1:1
    /// and the placeholder occupies the same place. Null restores "fit the area".
    /// </summary>
    /// <param name="size">Element size, or null when the final size is not known yet.</param>
    /// <param name="scrollable">Allow scrolling (actual size larger than the area).</param>
    public void SetImageBox((double Width, double Height)? size, bool scrollable)
    {
        foreach (XamlImage image in new[] { _thumbnail, _full })
        {
            if (size is { } box)
            {
                image.Width = box.Width;
                image.Height = box.Height;
                image.Stretch = Stretch.Fill;
            }
            else
            {
                image.Width = double.NaN;
                image.Height = double.NaN;
                image.Stretch = Stretch.Uniform;
            }
        }

        ScrollBarVisibility bar = scrollable ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        ScrollMode mode = scrollable ? ScrollMode.Auto : ScrollMode.Disabled;
        _scroller.HorizontalScrollBarVisibility = bar;
        _scroller.VerticalScrollBarVisibility = bar;
        _scroller.HorizontalScrollMode = mode;
        _scroller.VerticalScrollMode = mode;
        if (!scrollable)
        {
            _scroller.ChangeView(0, 0, null, disableAnimation: true);
        }
    }

    /// <summary>True when the image is larger than the area and can be moved (scroll bars, drag, arrow keys).</summary>
    public bool IsPannable => _scroller.VerticalScrollMode != ScrollMode.Disabled && (_scroller.ScrollableWidth > 0 || _scroller.ScrollableHeight > 0);

    /// <summary>Center of the visible area in surface coordinates (the anchor of keyboard zoom).</summary>
    public Windows.Foundation.Point ViewportCenter => new(ActualWidth / 2, ActualHeight / 2);

    /// <summary>
    /// Changes the size of the shown image to <paramref name="size"/> (device-independent pixels) at once, stretching
    /// the current bitmap until a sharper one replaces it, and keeps the point at <paramref name="anchor"/> (surface
    /// coordinates) where it is. Scrolling is enabled when the image becomes larger than the area.
    /// </summary>
    public void ZoomImage((double Width, double Height) size, Windows.Foundation.Point anchor)
    {
        double oldWidth = double.IsNaN(_full.Width) ? _full.ActualWidth : _full.Width;
        double oldHeight = double.IsNaN(_full.Height) ? _full.ActualHeight : _full.Height;
        double offsetX = _scroller.HorizontalOffset;
        double offsetY = _scroller.VerticalOffset;
        SetImageBox(size, scrollable: true);
        _scroller.UpdateLayout();
        if (oldWidth <= 0 || oldHeight <= 0)
        {
            return;
        }

        // Content coordinates exclude the padding around the image.
        double pointerX = anchor.X - ContentPadding;
        double pointerY = anchor.Y - ContentPadding;
        double x = Core.Viewing.ViewerZoom.AnchoredOffset(offsetX, pointerX, size.Width / oldWidth, _scroller.ExtentWidth - (2 * ContentPadding), _scroller.ViewportWidth - (2 * ContentPadding));
        double y = Core.Viewing.ViewerZoom.AnchoredOffset(offsetY, pointerY, size.Height / oldHeight, _scroller.ExtentHeight - (2 * ContentPadding), _scroller.ViewportHeight - (2 * ContentPadding));
        _scroller.ChangeView(Math.Min(x, _scroller.ScrollableWidth), Math.Min(y, _scroller.ScrollableHeight), null, disableAnimation: true);
    }

    /// <summary>Moves a zoomed image by (<paramref name="dx"/>, <paramref name="dy"/>) device-independent pixels; false when it cannot move that way.</summary>
    public bool ScrollBy(double dx, double dy)
    {
        if (!IsPannable)
        {
            return false;
        }

        double x = Math.Clamp(_scroller.HorizontalOffset + dx, 0, _scroller.ScrollableWidth);
        double y = Math.Clamp(_scroller.VerticalOffset + dy, 0, _scroller.ScrollableHeight);
        if (Math.Abs(x - _scroller.HorizontalOffset) < 0.5 && Math.Abs(y - _scroller.VerticalOffset) < 0.5)
        {
            return false;
        }

        _scroller.ChangeView(x, y, null, disableAnimation: true);
        return true;
    }

    /// <summary>Current scroll offsets (diagnostics and tests).</summary>
    public (double X, double Y) ScrollOffsets => (_scroller.HorizontalOffset, _scroller.VerticalOffset);

    /// <summary>Size of the shown image element in device-independent pixels (diagnostics and tests).</summary>
    public (double Width, double Height) ImageElementSize => (_full.ActualWidth, _full.ActualHeight);

    public void SetStatus(string status) => _status.Text = status;

    /// <summary>
    /// Shows only a message: any image of a previous item is removed so it cannot be mistaken for the current
    /// file (e.g. when the current file cannot be shown).
    /// </summary>
    public void ShowStatusOnly(string status)
    {
        ClearImages();
        _status.Text = status;
    }

    /// <summary>Shows a placeholder (e.g. a cached shell thumbnail) until the full image arrives.</summary>
    public void SetThumbnail(ImageSource source)
    {
        ReplaceSource(_thumbnail, source);
        ReplaceSource(_full, null); // a previous item's full image would cover the new placeholder
    }

    public void SetFullImage(ImageSource source)
    {
        ReplaceSource(_full, source);
        ReplaceSource(_thumbnail, null);
        _status.Text = string.Empty;
    }

    /// <summary>Puts a not-yet-decoded image above the placeholder; the placeholder stays until <see cref="CommitFullImage"/>.</summary>
    public void SetFullImagePending(ImageSource source) => ReplaceSource(_full, source);

    /// <summary>Drops the placeholder once the full image is decoded.</summary>
    public void CommitFullImage()
    {
        ReplaceSource(_thumbnail, null);
        _status.Text = string.Empty;
    }

    public void ClearImages()
    {
        ReplaceSource(_thumbnail, null);
        ReplaceSource(_full, null);
    }

    /// <summary>Shows the media area with <paramref name="player"/> attached; the image area is hidden.</summary>
    public void ShowMedia(Windows.Media.Playback.MediaPlayer player)
    {
        ClearImages();
        _status.Text = string.Empty;
        _scroller.Visibility = Visibility.Collapsed;
        _audioPanel.Visibility = Visibility.Collapsed;
        _mediaHost.Visibility = Visibility.Visible;
        _media.SetMediaPlayer(player);
    }

    /// <summary>Video: the picture fits the area but is never enlarged beyond <paramref name="maxWidth"/> × <paramref name="maxHeight"/> (DIPs).</summary>
    public void SetVideoLayout(double maxWidth, double maxHeight)
    {
        _audioPanel.Visibility = Visibility.Collapsed;
        _media.VerticalAlignment = VerticalAlignment.Center;
        _media.HorizontalAlignment = HorizontalAlignment.Center;
        _media.Height = double.NaN;
        _media.MinWidth = 360; // room for the controls
        _media.MaxWidth = maxWidth > 0 ? Math.Max(maxWidth, _media.MinWidth) : double.PositiveInfinity;
        _media.MaxHeight = maxHeight > 0 ? maxHeight : double.PositiveInfinity;
    }

    /// <summary>Audio: a note icon with the playback state, and the transport controls along the bottom.</summary>
    public void SetAudioLayout()
    {
        _audioPanel.Visibility = Visibility.Visible;
        _media.MinWidth = 0;
        _media.MaxWidth = 720;
        _media.MaxHeight = double.PositiveInfinity;
        _media.Height = 96;
        _media.HorizontalAlignment = HorizontalAlignment.Stretch;
        _media.VerticalAlignment = VerticalAlignment.Bottom;
    }

    public void SetAudioState(string text) => _audioState.Text = text;

    /// <summary>True while a document view (e.g. <see cref="PdfDocumentView"/>) is shown instead of an image.</summary>
    public bool ShowsDocument => _document is not null;

    /// <summary>
    /// Shows <paramref name="view"/> (a control that scrolls and draws by itself, such as <see cref="PdfDocumentView"/>)
    /// in place of the image area; images and media are cleared.
    /// </summary>
    public void ShowDocument(UIElement view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (ReferenceEquals(_document, view))
        {
            return;
        }

        ClearDocument();
        ClearImages();
        _status.Text = string.Empty;
        _scroller.Visibility = Visibility.Collapsed;
        _document = view;
        _root.Children.Insert(_root.Children.IndexOf(_status), view);
    }

    /// <summary>Removes the document view and returns to the image area.</summary>
    public void ClearDocument()
    {
        if (_document is null)
        {
            return;
        }

        _root.Children.Remove(_document);
        _document = null;
        _scroller.Visibility = _mediaHost.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Detaches the player and returns to the image area.</summary>
    public void ClearMedia()
    {
        _media.SetMediaPlayer(null);
        _mediaHost.Visibility = Visibility.Collapsed;
        _audioPanel.Visibility = Visibility.Collapsed;
        _audioState.Text = string.Empty;
        _scroller.Visibility = Visibility.Visible;
    }

    /// <summary>A new page starts at its top (actual-size mode scrolls).</summary>
    public void ScrollToTop() => _scroller.ChangeView(0, 0, null, disableAnimation: true);

    /// <summary>
    /// Mouse wheel over a paged document: returns -1 (previous page) or +1 (next page) once a full notch has
    /// accumulated, or null. When the page is larger than the area, the wheel scrolls and turns the page only at the
    /// top or bottom edge.
    /// </summary>
    public int? WheelPageStep(int wheelDelta)
    {
        bool scrollable = _scroller.VerticalScrollMode != ScrollMode.Disabled && _scroller.ScrollableHeight > 0;
        if (scrollable)
        {
            bool atTop = _scroller.VerticalOffset <= 0.5;
            bool atBottom = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 0.5;
            if (!((wheelDelta > 0 && atTop) || (wheelDelta < 0 && atBottom)))
            {
                _wheelAccumulated = 0;
                return null;
            }
        }

        _wheelAccumulated += wheelDelta;
        if (Math.Abs(_wheelAccumulated) < WheelNotch)
        {
            return null;
        }

        int step = _wheelAccumulated > 0 ? -1 : +1; // wheel up = previous page
        _wheelAccumulated = 0;
        return step;
    }

    /// <summary>Forgets a partial wheel notch (another document or page set).</summary>
    public void ResetWheel() => _wheelAccumulated = 0;

    private void OnPanPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(this);
        if (!IsPannable || e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_scroller.CapturePointer(e.Pointer))
        {
            _panPointer = e.Pointer.PointerId;
            _panStart = point.Position;
            _panOffsets = (_scroller.HorizontalOffset, _scroller.VerticalOffset);
            e.Handled = true;
        }
    }

    private void OnPanMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_panPointer != e.Pointer.PointerId)
        {
            return;
        }

        Windows.Foundation.Point position = e.GetCurrentPoint(this).Position;
        _scroller.ChangeView(_panOffsets.X - (position.X - _panStart.X), _panOffsets.Y - (position.Y - _panStart.Y), null, disableAnimation: true);
        e.Handled = true;
    }

    private void OnPanEnded(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_panPointer == e.Pointer.PointerId)
        {
            _panPointer = null;
            _scroller.ReleasePointerCapture(e.Pointer);
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => AreaChanged?.Invoke();

    /// <summary>
    /// Sets an image source and disposes the previous one. Decoded surfaces (SoftwareBitmapSource) hold native
    /// memory that is otherwise released only when the garbage collector finalizes them, which kept the resident
    /// Quick View process large after it closed (measured).
    /// </summary>
    private static void ReplaceSource(XamlImage image, ImageSource? source)
    {
        ImageSource? previous = image.Source;
        image.Source = source;
        if (previous is IDisposable disposable && !ReferenceEquals(previous, source))
        {
            disposable.Dispose();
        }
    }
}
