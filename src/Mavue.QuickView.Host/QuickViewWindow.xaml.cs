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

    /// <summary>Win32 handle of the window.</summary>
    public nint Handle { get; }

    public void ResetContent(string fileName)
    {
        ThumbnailImage.Source = null;
        FullImage.Source = null;
        StatusText.Text = string.Empty;
        NameText.Text = fileName;
        InfoText.Text = string.Empty;
    }

    public void SetInfo(string info) => InfoText.Text = info;

    public void SetStatus(string status) => StatusText.Text = status;

    public void SetThumbnail(BgraImage image)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(image.Pixels, 0, image.Pixels.Length);
        }

        bitmap.Invalidate();
        ThumbnailImage.Source = bitmap;
    }

    public bool HasFullImage => FullImage.Source is not null && ThumbnailImage.Source is null;

    public void SetFullImage(ImageSource source)
    {
        FullImage.Source = source;
        ThumbnailImage.Source = null;
        StatusText.Text = string.Empty;
    }

    /// <summary>Puts a not-yet-decoded image above the thumbnail; the thumbnail stays until <see cref="CommitFullImage"/>.</summary>
    public void SetFullImagePending(ImageSource source) => FullImage.Source = source;

    /// <summary>Drops the thumbnail once the full image is decoded.</summary>
    public void CommitFullImage()
    {
        ThumbnailImage.Source = null;
        StatusText.Text = string.Empty;
    }

    public void ClearImages()
    {
        ThumbnailImage.Source = null;
        FullImage.Source = null;
    }

    public void FocusContent() => Root.Focus(FocusState.Programmatic);

    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown)
        {
            return; // ignore auto-repeat so a held Space cannot toggle repeatedly
        }

        if (e.Key is VirtualKey.Escape or VirtualKey.Space)
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        }
    }
}
