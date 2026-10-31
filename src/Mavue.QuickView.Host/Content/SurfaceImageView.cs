using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace Mavue.QuickView.Host.Content;

/// <summary>What the surface's image element shows (for zoom and rotation).</summary>
internal enum SurfaceImageKind
{
    None,
    Raster,

    /// <summary>A PDF page rendered as an image (without PDFium).</summary>
    Pdf,
    Gif,
    Svg,
}

/// <summary>
/// The view state of the image element of the shared viewer surface, used by every content drawn as an image
/// (raster, animated GIF, PDF page images, SVG): the zoom and rotation chosen in the window for the current item
/// (reset for every other item), what is shown (its size at 100 %, upright, physical pixels) and at which factor, and
/// the private bitmap copy behind the displayed image. A zoom step is applied at once by resizing the element; a
/// raster image or page is decoded again at the new size afterwards by its content.
/// </summary>
internal sealed class SurfaceImageView
{
    private readonly QuickViewWindow _window;
    private readonly QuickViewTimeline _timeline;

    // The bitmap behind the displayed full image. It is a private copy: cached bitmaps are never handed to a
    // SoftwareBitmapSource, because disposing a replaced source also closed the cached bitmap and a later cache hit
    // then failed with E_INVALIDARG (measured with fast arrow-key navigation).
    private SoftwareBitmap? _displayedBitmap;

    public SurfaceImageView(QuickViewWindow window, QuickViewTimeline timeline)
    {
        _window = window;
        _timeline = timeline;
    }

    /// <summary>Zoom chosen in the window for the current item; null: the size from the settings.</summary>
    public ZoomSetting? ViewZoom { get; set; }

    /// <summary>Quarter turns and flips chosen in the window (the file is never changed).</summary>
    public ViewOrientation Orientation { get; set; } = ViewOrientation.Identity;

    public SurfaceImageKind Shown { get; private set; }

    /// <summary>Size of what is shown at 100 % (physical pixels).</summary>
    public (double Width, double Height) ShownSource { get; private set; }

    /// <summary>Display pixels per source pixel of what is shown.</summary>
    public double ShownFactor { get; private set; }

    /// <summary>Forgets zoom, rotation and what is shown (another item).</summary>
    public void Reset()
    {
        ViewZoom = null;
        Orientation = ViewOrientation.Identity;
        Shown = SurfaceImageKind.None;
        ShownSource = default;
        ShownFactor = 0;
    }

    /// <summary>Makes <paramref name="bitmap"/> the displayed bitmap and releases the previous one.</summary>
    public void SetDisplayedBitmap(SoftwareBitmap? bitmap)
    {
        SoftwareBitmap? previous = _displayedBitmap;
        _displayedBitmap = bitmap;
        if (!ReferenceEquals(previous, bitmap))
        {
            previous?.Dispose();
        }
    }

    /// <summary>The surface no longer shows the bitmap (another content, a message, hidden): free its pixels.</summary>
    public void ReleaseDisplayedBitmap() => SetDisplayedBitmap(null);

    /// <summary>Records what is on screen (size at 100 % and element width) and updates the zoom controls.</summary>
    /// <param name="kind">None for content that is not the image element (the controls then hide).</param>
    public void RecordShown(SurfaceImageKind kind, (double Width, double Height) source, double elementWidth, double displayScale)
    {
        Shown = kind;
        ShownSource = source;
        (double orientedWidth, _) = (kind == SurfaceImageKind.Svg ? ViewOrientation.Identity : Orientation).Oriented(source.Width, source.Height);
        ShownFactor = orientedWidth > 0 ? elementWidth * displayScale / orientedWidth : 0;
        _window.SetViewControls(kind != SurfaceImageKind.None, IsRotatable(kind), ShownFactor > 0 ? ViewerZoom.Percent(ShownFactor) : string.Empty);
    }

    /// <summary>
    /// Zoom from the window, applied at once by resizing what is shown (around the pointer for the wheel).
    /// Returns true when a sharper decode at the new size should follow (raster images and page images).
    /// </summary>
    public bool Zoom(QuickViewZoomCommand command, int wheelDelta, Point? anchor, ImageScaleMode scaleMode, (uint Width, uint Height) area, double displayScale, long requestId)
    {
        if (Shown == SurfaceImageKind.None || ShownFactor <= 0)
        {
            return false;
        }

        ViewOrientation orientation = Shown == SurfaceImageKind.Svg ? ViewOrientation.Identity : Orientation;
        (double width, double height) = orientation.Oriented(ShownSource.Width, ShownSource.Height);
        double factor;
        if (command == QuickViewZoomCommand.Fit)
        {
            ViewZoom = null; // back to the size from the settings
            factor = ViewerZoom.FactorFor(ZoomSetting.For(scaleMode), width, height, area.Width, area.Height, allowEnlarge: Shown == SurfaceImageKind.Pdf);
        }
        else
        {
            double target = command switch
            {
                QuickViewZoomCommand.In => ViewerZoom.StepIn(ShownFactor),
                QuickViewZoomCommand.Out => ViewerZoom.StepOut(ShownFactor),
                QuickViewZoomCommand.ActualSize => 1,
                _ => ViewerZoom.Wheel(ShownFactor, wheelDelta),
            };
            factor = ViewerZoom.Clamp(target, width, height);
            ViewZoom = new ZoomSetting(ZoomMode.Custom, factor);
        }

        (uint displayWidth, uint displayHeight) = ViewerZoom.DisplaySize(width, height, factor);
        (double, double) element = DisplaySizing.ElementSize(displayWidth, displayHeight, displayScale);
        _window.Surface.ZoomImage(element, anchor ?? _window.Surface.ViewportCenter);
        ShownFactor = factor;
        _window.SetViewControls(true, IsRotatable(Shown), ViewerZoom.Percent(factor));
        _timeline.Mark(requestId, "zoom", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["command"] = command.ToString(),
            ["factor"] = Math.Round(factor, 4),
            ["width"] = displayWidth,
            ["height"] = displayHeight,
        });
        return Shown is SurfaceImageKind.Raster or SurfaceImageKind.Pdf;
    }

    /// <summary>Raster images, GIFs and page images can be turned (SVG cannot yet).</summary>
    public static bool IsRotatable(SurfaceImageKind kind) => kind is SurfaceImageKind.Raster or SurfaceImageKind.Pdf or SurfaceImageKind.Gif;
}
