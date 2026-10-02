using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.QuickView.Shell;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Mavue.QuickView.Host;

/// <summary>
/// The single, reused Quick View window. It is created once at process start and only shown/hidden
/// afterwards; it is never closed until the resident process exits.
/// </summary>
public sealed partial class QuickViewWindow : Window
{
    private bool _exiting;

    public QuickViewWindow()
    {
        InitializeComponent();
        Handle = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        Scroller.SizeChanged += (_, _) => ImageAreaChanged?.Invoke();
        Root.Loaded += (_, _) => Root.XamlRoot.Changed += (_, _) => ImageAreaChanged?.Invoke();
        AppWindow.Closing += (_, args) =>
        {
            // The title-bar close button hides the window; the process stays resident.
            if (!_exiting)
            {
                args.Cancel = true;
                CloseRequested?.Invoke();
            }
        };
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
        }
    }

    /// <summary>Raised for Esc, Space and the title-bar close button.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised for arrow keys while the window has keyboard focus: -1 for ←/↑, +1 for →/↓.</summary>
    public event Action<int>? NavigateRequested;

    /// <summary>Win32 handle of the window.</summary>
    public nint Handle { get; }

    /// <summary>
    /// Raised when the image area may have changed size: the window was resized, or moved to a monitor
    /// with another scale. The controller re-plans the current item if the area really changed.
    /// </summary>
    public event Action? ImageAreaChanged;

    /// <summary>XAML's scale (physical pixels per device-independent pixel); may lag behind a monitor change.</summary>
    public double RasterizationScale => Root.XamlRoot?.RasterizationScale ?? 1.0;

    /// <summary>
    /// Space around the image area in device-independent pixels (padding and the info bar). These do not
    /// depend on the monitor scale, so the controller combines them with the window's real client size and
    /// DPI instead of using the last layout, which is stale right after the window moves to another monitor.
    /// </summary>
    /// <summary>Diagnostics: the image area according to the last XAML layout, in physical pixels.</summary>
    public (int Width, int Height) LayoutImageAreaPixels()
    {
        double scale = RasterizationScale;
        return ((int)Math.Floor((Scroller.ActualWidth - Scroller.Padding.Left - Scroller.Padding.Right) * scale),
                (int)Math.Floor((Scroller.ActualHeight - Scroller.Padding.Top - Scroller.Padding.Bottom) * scale));
    }

    public (double Width, double Height) ImageChromeDip()
    {
        double infoBar = InfoBar.ActualHeight > 0 ? InfoBar.ActualHeight : 56; // before the first layout
        return (Scroller.Padding.Left + Scroller.Padding.Right, Scroller.Padding.Top + Scroller.Padding.Bottom + infoBar);
    }

    /// <summary>
    /// Sizes both images to an exact box (device-independent pixels) so a bitmap decoded for that box
    /// is shown 1:1 and the placeholder thumbnail occupies the same place. Null restores "fit the area".
    /// </summary>
    /// <param name="size">Element size, or null when the final size is not known yet.</param>
    /// <param name="scrollable">Allow scrolling (actual size larger than the area).</param>
    public void SetImageBox((double Width, double Height)? size, bool scrollable)
    {
        foreach (Microsoft.UI.Xaml.Controls.Image image in new[] { ThumbnailImage, FullImage })
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

        var bar = scrollable ? Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto : Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled;
        var mode = scrollable ? Microsoft.UI.Xaml.Controls.ScrollMode.Auto : Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
        Scroller.HorizontalScrollBarVisibility = bar;
        Scroller.VerticalScrollBarVisibility = bar;
        Scroller.HorizontalScrollMode = mode;
        Scroller.VerticalScrollMode = mode;
        if (!scrollable)
        {
            Scroller.ChangeView(0, 0, null, disableAnimation: true);
        }
    }

    public void ResetContent(string fileName)
    {
        ReplaceSource(ThumbnailImage, null);
        ReplaceSource(FullImage, null);
        StatusText.Text = string.Empty;
        NameText.Text = fileName;
        InfoText.Text = string.Empty;
    }

    public void SetInfo(string info) => InfoText.Text = info;

    /// <summary>Switches to another item without blanking the window; the old image stays until a new one arrives.</summary>
    public void BeginNextItem(string fileName)
    {
        NameText.Text = fileName;
        InfoText.Text = string.Empty;
        StatusText.Text = string.Empty;
    }

    public void SetStatus(string status) => StatusText.Text = status;

    /// <summary>
    /// Shows only a message: any image of a previous item is removed so it cannot be mistaken for
    /// the current file (e.g. when the current file cannot be previewed).
    /// </summary>
    public void ShowStatusOnly(string status)
    {
        ClearImages();
        StatusText.Text = status;
    }

    public void SetThumbnail(BgraImage image)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(image.Pixels, 0, image.Pixels.Length);
        }

        bitmap.Invalidate();
        ReplaceSource(ThumbnailImage, bitmap);
        ReplaceSource(FullImage, null); // a previous item's full image would cover the new thumbnail
    }

    public bool HasFullImage => FullImage.Source is not null && ThumbnailImage.Source is null;

    public void SetFullImage(ImageSource source)
    {
        ReplaceSource(FullImage, source);
        ReplaceSource(ThumbnailImage, null);
        StatusText.Text = string.Empty;
    }

    /// <summary>Puts a not-yet-decoded image above the thumbnail; the thumbnail stays until <see cref="CommitFullImage"/>.</summary>
    public void SetFullImagePending(ImageSource source) => ReplaceSource(FullImage, source);

    /// <summary>Drops the thumbnail once the full image is decoded.</summary>
    public void CommitFullImage()
    {
        ReplaceSource(ThumbnailImage, null);
        StatusText.Text = string.Empty;
    }

    public void ClearImages()
    {
        ReplaceSource(ThumbnailImage, null);
        ReplaceSource(FullImage, null);
    }

    /// <summary>
    /// Sets an image source and disposes the previous one. Decoded surfaces (SoftwareBitmapSource) hold
    /// native memory that is otherwise released only when the garbage collector finalizes them, which kept
    /// the resident process large after Quick View closed (measured).
    /// </summary>
    private static void ReplaceSource(Microsoft.UI.Xaml.Controls.Image image, ImageSource? source)
    {
        ImageSource? previous = image.Source;
        image.Source = source;
        if (previous is IDisposable disposable && !ReferenceEquals(previous, source))
        {
            disposable.Dispose();
        }
    }

    public void FocusContent() => Root.Focus(FocusState.Programmatic);

    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left or VirtualKey.Up:
                e.Handled = true;
                NavigateRequested?.Invoke(-1); // auto-repeat allowed: holding an arrow keeps stepping
                break;
            case VirtualKey.Right or VirtualKey.Down:
                e.Handled = true;
                NavigateRequested?.Invoke(+1);
                break;
            case VirtualKey.Escape or VirtualKey.Space when !e.KeyStatus.WasKeyDown:
                // Auto-repeat ignored so a held Space cannot toggle repeatedly.
                e.Handled = true;
                CloseRequested?.Invoke();
                break;
        }
    }
}
