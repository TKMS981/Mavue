using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.QuickView.Shell;
using Mavue.Viewer.Controls;
using Mavue.Viewer.Playback;
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

        // Shared display area (Mavue.Viewer). Quick View's background is always dark, so its texts are light.
        Surface = new ViewerSurface { KeyboardFocusTarget = Root };
        Surface.SetTextColors(new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xE0, 0xE0, 0xE0)), new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xD0, 0xD0, 0xD0)));
        Surface.AreaChanged += () => ImageAreaChanged?.Invoke();
        SurfaceHost.Children.Add(Surface);

        // handledEventsToo: the ScrollViewer marks wheel events handled even when it cannot scroll.
        Root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), handledEventsToo: true);
        var strings = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
        SetButtonText(PreviousPageButton, strings.GetString("QuickView_PreviousPage"));
        SetButtonText(NextPageButton, strings.GetString("QuickView_NextPage"));

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

    /// <summary>
    /// Raised for PageUp/PageDown (window focused), the mouse wheel over the window, and the page buttons:
    /// -1 for the previous page, +1 for the next. The controller ignores it unless a multi-page PDF is shown.
    /// </summary>
    public event Action<int, string>? PageRequested;

    /// <summary>Diagnostics: every key the window receives (before it is handled).</summary>
    public event Action<VirtualKey>? KeyReceived;

    /// <summary>Diagnostics: type and name of the element that has keyboard focus, or "none".</summary>
    public string FocusDescription() =>
        Root.XamlRoot is { } root && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is { } focused
            ? focused is FrameworkElement { Name.Length: > 0 } named ? $"{focused.GetType().Name}#{named.Name}" : focused.GetType().Name
            : "none";

    /// <summary>Media shortcuts while the window has keyboard focus (see <see cref="MediaCommand"/>).</summary>
    public event Action<MediaCommand>? MediaCommandRequested;

    /// <summary>Image, video/audio and messages (shared with Mavue.App).</summary>
    public ViewerSurface Surface { get; }

    /// <summary>Win32 handle of the window.</summary>
    public nint Handle { get; }

    /// <summary>
    /// Raised when the image area may have changed size: the window was resized, or moved to a monitor
    /// with another scale. The controller re-plans the current item if the area really changed.
    /// </summary>
    public event Action? ImageAreaChanged;

    /// <summary>XAML's scale (physical pixels per device-independent pixel); may lag behind a monitor change.</summary>
    public double RasterizationScale => Surface.PixelScale;

    /// <summary>Diagnostics: the image area according to the last XAML layout, in physical pixels.</summary>
    public (int Width, int Height) LayoutImageAreaPixels() => Surface.LayoutAreaPixels();

    /// <summary>
    /// Space around the image area in device-independent pixels (padding and the info bar). These do not
    /// depend on the monitor scale, so the controller combines them with the window's real client size and
    /// DPI instead of using the last layout, which is stale right after the window moves to another monitor.
    /// </summary>
    public (double Width, double Height) ImageChromeDip()
    {
        double infoBar = InfoBar.ActualHeight > 0 ? InfoBar.ActualHeight : 56; // before the first layout
        return (2 * ViewerSurface.ContentPadding, (2 * ViewerSurface.ContentPadding) + infoBar);
    }

    public void ResetContent(string fileName)
    {
        Surface.ShowStatusOnly(string.Empty);
        NameText.Text = fileName;
        InfoText.Text = string.Empty;
    }

    public void SetInfo(string info) => InfoText.Text = info;

    /// <summary>Switches to another item without blanking the window; the old image stays until a new one arrives.</summary>
    public void BeginNextItem(string fileName)
    {
        NameText.Text = fileName;
        InfoText.Text = string.Empty;
        Surface.SetStatus(string.Empty);
    }

    public void SetThumbnail(BgraImage image)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(image.Pixels, 0, image.Pixels.Length);
        }

        bitmap.Invalidate();
        Surface.SetThumbnail(bitmap);
    }

    public void FocusContent() => Root.Focus(FocusState.Programmatic);

    /// <summary>Shows the page buttons for a multi-page PDF (enabled only where a step is possible), or hides them.</summary>
    public void SetPageControls(bool visible, bool canPrevious, bool canNext)
    {
        PageButtons.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviousPageButton.IsEnabled = canPrevious;
        NextPageButton.IsEnabled = canNext;
        Surface.ResetWheel();
    }

    private static void SetButtonText(Microsoft.UI.Xaml.Controls.Button button, string text)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, text);
    }

    private void OnPreviousPageClick(object sender, RoutedEventArgs e) => RequestPageFromButton(-1);

    private void OnNextPageClick(object sender, RoutedEventArgs e) => RequestPageFromButton(+1);

    private void RequestPageFromButton(int delta)
    {
        Root.Focus(FocusState.Programmatic); // keep Space/Esc on the window, not on the button
        PageRequested?.Invoke(delta, "button");
    }

    private static MediaCommand? MediaShortcut(VirtualKey key, bool control) => (key, control) switch
    {
        (VirtualKey.Enter, false) => MediaCommand.TogglePlay,
        (VirtualKey.Left, true) => MediaCommand.SeekBackward,
        (VirtualKey.Right, true) => MediaCommand.SeekForward,
        (VirtualKey.Up, true) => MediaCommand.VolumeUp,
        (VirtualKey.Down, true) => MediaCommand.VolumeDown,
        _ => null,
    };

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (PageButtons.Visibility != Visibility.Visible)
        {
            return;
        }

        if (Surface.WheelPageStep(e.GetCurrentPoint(Root).Properties.MouseWheelDelta) is { } step)
        {
            e.Handled = true;
            PageRequested?.Invoke(step, "wheel");
        }
    }

    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        KeyReceived?.Invoke(e.Key);
        bool control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (Surface.ShowsMedia && MediaShortcut(e.Key, control) is { } command)
        {
            // Media keys use Enter and Ctrl+arrows, so plain arrows (files), Space/Esc (close) and PageUp/PageDown keep their meaning.
            e.Handled = true;
            if (command != MediaCommand.TogglePlay || !e.KeyStatus.WasKeyDown) // a held Enter toggles once
            {
                MediaCommandRequested?.Invoke(command);
            }

            return;
        }

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
            case VirtualKey.PageUp:
                e.Handled = true;
                PageRequested?.Invoke(-1, "window-page");
                break;
            case VirtualKey.PageDown:
                e.Handled = true;
                PageRequested?.Invoke(+1, "window-page");
                break;
            case VirtualKey.Escape or VirtualKey.Space when !e.KeyStatus.WasKeyDown:
                // Auto-repeat ignored so a held Space cannot toggle repeatedly.
                e.Handled = true;
                CloseRequested?.Invoke();
                break;
        }
    }
}
