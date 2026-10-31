using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Mavue.Image.Wic;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Performance;
using Mavue.QuickView.Preview;
using Mavue.QuickView.Shell;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mavue.QuickView.Host.Presentation;

/// <summary>
/// The filmstrip (docs/ARCHITECTURE.md §4.7.8): small pictures of the files around the shown one in Explorer's order,
/// or of the multi-selection, under the preview. Off by default; while it is off, or Quick View is hidden, nothing
/// runs. The range of Explorer's view is read only after the shown item is on screen, and only when the shown file
/// nears an end of the loaded range (<see cref="FilmstripModel"/>). Pictures are loaded only for the items in view, on
/// a thread of their own (never the one the preview's thumbnail uses): Windows' thumbnail cache first, then — for
/// local images within the tier's limits — Mavue's own decoder, else the type's icon. Cloud-only files are never
/// downloaded. Pictures are kept in a bounded cache that is emptied when Quick View closes.
/// </summary>
internal sealed class FilmstripPresenter : IDisposable
{
    private const double ItemSize = 72;
    private const double ItemSpacing = 6;
    private const double PictureSize = 64;
    private const int MaxCachedPictures = 300;

    private readonly QuickViewWindow _window;
    private readonly QuickViewTimeline _timeline;
    private readonly ResidentResources _resources;
    private readonly Func<SelectionWorker?> _worker;
    private readonly FilmstripModel _model = new();
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _strip;
    private readonly List<Button> _buttons = [];
    private readonly Dictionary<string, BgraImage> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _pictureOrder = new();
    private StaThread? _thread; // created on first use
    private CancellationTokenSource _loading = new();
    private bool _reloadPending;
    private bool _reading;
    private bool _pinned;
    private string? _shownPath;

    public FilmstripPresenter(QuickViewWindow window, QuickViewTimeline timeline, ResidentResources resources, Func<SelectionWorker?> worker)
    {
        _window = window;
        _timeline = timeline;
        _resources = resources;
        _worker = worker;
        _strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = ItemSpacing, Padding = new Thickness(12, 8, 12, 8) };
        _scroller = new ScrollViewer
        {
            Content = _strip,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = false,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_scroller, "QuickViewFilmstrip");

        // The wheel scrolls the strip sideways (no Ctrl needed: the strip has no zoom).
        _scroller.PointerWheelChanged += (_, e) =>
        {
            int delta = e.GetCurrentPoint(_scroller).Properties.MouseWheelDelta;
            _scroller.ChangeView(_scroller.HorizontalOffset - delta, null, null, disableAnimation: true);
            e.Handled = true;
        };
        _scroller.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate)
            {
                LoadVisiblePictures();
            }
        };
        _scroller.SizeChanged += (_, _) => LoadVisiblePictures();
        window.AttachFilmstrip(_scroller);
    }

    /// <summary>A file of the strip was clicked.</summary>
    public event Action<FilmstripItem>? ItemInvoked;

    /// <summary>The strip is switched on (a setting, remembered).</summary>
    public bool Enabled { get; private set; }

    /// <summary>The items currently loaded (for stepping through them while pinned).</summary>
    public FilmstripModel Model => _model;

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        _window.SetFilmstripVisible(enabled);
        if (!enabled)
        {
            Clear();
        }
    }

    /// <summary>Pinned: the strip keeps its items (Explorer's view is not read again).</summary>
    public void SetPinned(bool pinned)
    {
        _pinned = pinned;
        if (!pinned && _reloadPending)
        {
            Reload();
        }
    }

    /// <summary>
    /// The controller is about to show <paramref name="path"/>: moves the highlight at once (cheap). A new range of the
    /// view, if needed, is read after the item is on screen (<see cref="OnItemShown"/>).
    /// </summary>
    public void OnItemShowing(string path, PreviewNavigator navigator)
    {
        _shownPath = path;
        if (!Enabled)
        {
            return;
        }

        if (navigator.Mode == NavigationMode.MultipleItems)
        {
            if (!_model.IsSelection || !_model.Items.Select(i => i.Path).SequenceEqual(navigator.Items, StringComparer.OrdinalIgnoreCase))
            {
                _model.LoadSelection(navigator.Items, path);
                Rebuild();
            }
            else
            {
                _model.Track(path);
                UpdateHighlight();
            }

            return;
        }

        if (_model.IsSelection)
        {
            _model.Clear(); // back to a single item: show the folder again
            Rebuild();
        }

        _reloadPending |= _model.Track(path) == FilmstripUpdate.NeedsReload;
        UpdateHighlight();
    }

    /// <summary>The shown item is on screen: now read a new range of Explorer's view if one is needed.</summary>
    public void OnItemShown()
    {
        if (Enabled && _reloadPending && !_pinned)
        {
            Reload();
        }
    }

    /// <summary>Quick View closed: drop the items, the pictures and any work.</summary>
    public void OnHidden()
    {
        Clear();
        _pinned = false;
        _shownPath = null;
    }

    public void Dispose()
    {
        _loading.Cancel();
        _loading.Dispose();
        _thread?.Dispose();
    }

    private void Clear()
    {
        CancelLoading();
        _model.Clear();
        _reloadPending = false;
        _pictures.Clear();
        _pictureOrder.Clear();
        Rebuild();
    }

    private void CancelLoading()
    {
        _loading.Cancel();
        _loading.Dispose();
        _loading = new CancellationTokenSource();
    }

    private async void Reload()
    {
        if (_reading || _worker() is not { } worker)
        {
            return;
        }

        _reading = true;
        _reloadPending = false;
        long start = QuickViewTimeline.Now;
        try
        {
            (IReadOnlyList<(int Index, string Path)> items, int viewCount) = await worker.ReadViewRangeAsync(FilmstripModel.DefaultRadius);
            if (!Enabled || _pinned || _model.IsSelection)
            {
                return;
            }

            _model.LoadViewRange([.. items.Select(i => new FilmstripItem(i.Index, i.Path))], viewCount, _shownPath);
            _timeline.Mark(0, "filmstrip-range", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["count"] = items.Count,
                ["viewCount"] = viewCount,
                ["startQpc"] = start,
            });
            Rebuild();
        }
        finally
        {
            _reading = false;
        }
    }

    private void Rebuild()
    {
        CancelLoading();
        IReadOnlyList<FilmstripItem> items = _model.Items;
        while (_buttons.Count < items.Count)
        {
            var image = new Microsoft.UI.Xaml.Controls.Image { Width = PictureSize, Height = PictureSize, Stretch = Stretch.Uniform };
            var button = new Button
            {
                Width = ItemSize,
                Height = ItemSize,
                Padding = new Thickness(2),
                IsTabStop = false,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(2),
                Content = image,
            };
            button.Click += (sender, _) =>
            {
                if (sender is Button { Tag: FilmstripItem item })
                {
                    _window.FocusContent(); // keep Space/Esc on the window
                    ItemInvoked?.Invoke(item);
                }
            };
            _buttons.Add(button);
        }

        _strip.Children.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            Button button = _buttons[i];
            FilmstripItem item = items[i];
            button.Tag = item;
            string name = Path.GetFileName(item.Path);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            ((Microsoft.UI.Xaml.Controls.Image)button.Content).Source = _pictures.TryGetValue(item.Path, out BgraImage? cached) ? ToBitmap(cached) : null;
            _strip.Children.Add(button);
        }

        UpdateHighlight();
        LoadVisiblePictures();
    }

    private void UpdateHighlight()
    {
        Brush accent = new SolidColorBrush(_uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent));
        var none = new SolidColorBrush(Colors.Transparent);
        for (int i = 0; i < _strip.Children.Count; i++)
        {
            ((Button)_strip.Children[i]).BorderBrush = i == _model.CurrentIndex ? accent : none;
        }

        if (_model.CurrentIndex >= 0 && _scroller.ViewportWidth > 0)
        {
            // Keep the shown file in view, centered when the strip has to scroll.
            double center = 12 + (_model.CurrentIndex * (ItemSize + ItemSpacing)) + (ItemSize / 2);
            double target = Math.Clamp(center - (_scroller.ViewportWidth / 2), 0, Math.Max(0, _scroller.ExtentWidth - _scroller.ViewportWidth));
            _scroller.ChangeView(target, null, null, disableAnimation: true);
        }
    }

    /// <summary>Requests pictures for the items in view (and a few beyond), the shown one first.</summary>
    private void LoadVisiblePictures()
    {
        if (!Enabled || _model.Items.Count == 0 || _scroller.ViewportWidth <= 0)
        {
            return;
        }

        double step = ItemSize + ItemSpacing;
        int first = Math.Max(0, (int)Math.Floor((_scroller.HorizontalOffset - 12) / step) - 2);
        int last = Math.Min(_model.Items.Count - 1, (int)Math.Ceiling((_scroller.HorizontalOffset + _scroller.ViewportWidth) / step) + 2);
        var wanted = Enumerable.Range(first, Math.Max(0, last - first + 1))
            .OrderBy(i => Math.Abs(i - Math.Max(0, _model.CurrentIndex)))
            .Select(i => _model.Items[i].Path)
            .Where(p => !_pictures.ContainsKey(p))
            .ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        CancelLoading();
        CancellationToken cancellation = _loading.Token;
        int size = (int)Math.Ceiling(PictureSize * (_window.RasterizationScale > 0 ? _window.RasterizationScale : 1));
        PreviewResourcePolicy policy = _resources.Policy;
        _thread ??= new StaThread("Mavue.QuickView.Filmstrip");
        foreach (string path in wanted)
        {
            _ = LoadPictureAsync(path, size, policy, cancellation);
        }
    }

    private async Task LoadPictureAsync(string path, int size, PreviewResourcePolicy policy, CancellationToken cancellation)
    {
        BgraImage? picture;
        try
        {
            picture = await _thread!.InvokeAsync(() => cancellation.IsCancellationRequested ? null : Picture(path, size, policy));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return; // one picture less; a neighbor may be damaged or hostile
        }

        if (picture is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        Remember(path, picture);
        foreach (Button button in _buttons)
        {
            if (button.Tag is FilmstripItem item && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                ((Microsoft.UI.Xaml.Controls.Image)button.Content).Source = ToBitmap(picture);
            }
        }
    }

    /// <summary>Runs on the filmstrip thread: cache, then (local, modest images only) Mavue's decoder, then the type's icon.</summary>
    private static BgraImage? Picture(string path, int size, PreviewResourcePolicy policy)
    {
        BgraImage? cached = ShellThumbnail.TryGetCached(path, size);
        if (cached is not null)
        {
            return cached;
        }

        FileFacts facts = FileFacts.Read(path);
        if (facts.Access == PreviewAccess.Allowed && FileFormatKinds.IsImage(facts.Format) && facts.Format != FileFormat.Svg &&
            facts.Length <= policy.MaxPrefetchFileBytes)
        {
            try
            {
                DecodedPixels? decoded = WicPreviewDecoder.DecodeToFit(path, (uint)size, (uint)size, policy.MaxPrefetchSourcePixels);
                if (decoded is not null)
                {
                    return new BgraImage((int)decoded.Width, (int)decoded.Height, decoded.Pixels);
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ImageTooLargeException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Fall back to the icon.
            }
        }

        return facts.Access == PreviewAccess.NotAFile && !Directory.Exists(path) ? null : ShellThumbnail.TryGetIcon(path, size);
    }

    private void Remember(string path, BgraImage picture)
    {
        if (_pictures.ContainsKey(path))
        {
            return;
        }

        _pictures[path] = picture;
        _pictureOrder.AddLast(path);
        while (_pictureOrder.Count > MaxCachedPictures && _pictureOrder.First is { } oldest)
        {
            _pictures.Remove(oldest.Value);
            _pictureOrder.RemoveFirst();
        }
    }

    private static WriteableBitmap ToBitmap(BgraImage image)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(image.Pixels, 0, image.Pixels.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }
}
