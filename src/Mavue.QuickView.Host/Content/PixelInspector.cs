using Mavue.Core.Viewing;
using Mavue.Image.Wic;
using Mavue.QuickView.Diagnostics;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// The pixel inspector (docs/ARCHITECTURE.md §4.7.8): X/Y, RGB, HEX and alpha of the file's pixel under the pointer.
/// Works only while it is switched on and the shown content offers its pixels (<see cref="IPixelSourceContent"/>).
/// The pixel is read from the file at full resolution, one aligned tile at a time (<see cref="WicPixelReader"/>), off
/// the UI thread; while a tile is read only the position is shown. At most one read runs; pointer moves in between
/// only replace the next tile to read. Everything is dropped when the item changes or Quick View closes.
/// </summary>
internal sealed class PixelInspector : IDisposable
{
    private readonly QuickViewWindow _window;
    private readonly QuickViewTimeline _timeline;
    private readonly SurfaceImageView _view;
    private readonly Func<IPreviewContent?> _active;
    private readonly Func<long> _request;

    private CancellationTokenSource _item = new();
    private string? _path;
    private StoredImageLayout? _layout;
    private bool _layoutRequested;
    private PixelTile? _tile;
    private PixelRect? _pendingTile;
    private bool _reading;
    private bool _unreadable;
    private (double X, double Y, double Width, double Height)? _lastPointer;

    public PixelInspector(QuickViewWindow window, QuickViewTimeline timeline, SurfaceImageView view, Func<IPreviewContent?> active, Func<long> request)
    {
        _window = window;
        _timeline = timeline;
        _view = view;
        _active = active;
        _request = request;
    }

    /// <summary>The inspector is switched on (a setting, remembered).</summary>
    public bool Enabled { get; private set; }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        _window.SetInspectorOn(enabled);
        if (!enabled)
        {
            Forget();
        }
    }

    /// <summary>The shown item changed (or was re-shown): forget its tile and layout.</summary>
    public void OnItemChanged()
    {
        Forget();
        _window.HideInspectorPanel();
    }

    /// <summary>The item's content is on screen: offer the inspector if its pixels can be read.</summary>
    public void OnContentShown() => _window.SetInspectorAvailable(SourcePath() is not null);

    public void OnHidden()
    {
        Forget();
        _window.HideInspectorPanel();
    }

    /// <summary>The pointer moved over the surface (only reported while the inspector is on).</summary>
    public void OnPointer((double X, double Y, double Width, double Height)? pointer)
    {
        _lastPointer = pointer;
        if (!Enabled || pointer is not { } p || SourcePath() is not { } path)
        {
            _window.HideInspectorPanel();
            return;
        }

        if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            Forget();
            _path = path;
        }

        if (_layout is not { } layout)
        {
            RequestLayout(path);
            return;
        }

        (int uprightWidth, int uprightHeight) = layout.Upright;
        if (PixelInspection.ImagePixelAt(p.X, p.Y, p.Width, p.Height, uprightWidth, uprightHeight, _view.Orientation) is not { } pixel)
        {
            _window.HideInspectorPanel();
            return;
        }

        (int storedX, int storedY) = PixelInspection.StoredPixel(pixel.X, pixel.Y, layout.StoredWidth, layout.StoredHeight, layout.Orientation);
        PixelColor? color = _tile?.At(storedX, storedY);
        _window.ShowInspector(pixel.X, pixel.Y, color);
        if (color is null && !_unreadable)
        {
            RequestTile(path, PixelInspection.TileAround(storedX, storedY, layout.StoredWidth, layout.StoredHeight));
        }
    }

    public void Dispose()
    {
        _item.Cancel();
        _item.Dispose();
    }

    private string? SourcePath() => (_active() as IPixelSourceContent)?.PixelSourcePath;

    private void Forget()
    {
        _item.Cancel();
        _item.Dispose();
        _item = new CancellationTokenSource();
        _path = null;
        _layout = null;
        _layoutRequested = false;
        _tile = null;
        _pendingTile = null;
        _reading = false;
        _unreadable = false;
    }

    private async void RequestLayout(string path)
    {
        if (_layoutRequested)
        {
            return;
        }

        _layoutRequested = true;
        CancellationToken cancellation = _item.Token;
        StoredImageLayout? layout;
        try
        {
            layout = await Task.Run(() => WicPixelReader.ReadLayout(path));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            layout = null; // never let a bad file take the resident process down
        }
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        _layout = layout;
        if (layout is not null)
        {
            OnPointer(_lastPointer);
        }
    }

    private async void RequestTile(string path, PixelRect rect)
    {
        _pendingTile = rect;
        if (_reading)
        {
            return; // the running read picks up the newest request when it ends
        }

        _reading = true;
        CancellationToken cancellation = _item.Token;
        while (_pendingTile is { } next && !cancellation.IsCancellationRequested)
        {
            _pendingTile = null;
            long start = QuickViewTimeline.Now;
            PixelTile? tile;
            try
            {
                tile = await Task.Run(() => WicPixelReader.ReadTile(path, next, cancellation), cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _timeline.Mark(_request(), "inspector-error", QuickViewTimeline.Now, new Dictionary<string, object?> { ["type"] = ex.GetType().Name });
                tile = null;
            }

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _tile = tile;
            _timeline.Mark(_request(), "inspector-tile", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["ok"] = tile is not null,
                ["width"] = next.Width,
                ["height"] = next.Height,
                ["startQpc"] = start,
            });
            if (tile is null)
            {
                _unreadable = true;
                break; // the file cannot be read any more: keep showing the position only
            }

            OnPointer(_lastPointer); // shows the color, or queues the tile the pointer has moved to
        }

        if (!cancellation.IsCancellationRequested)
        {
            _reading = false;
        }
    }
}
