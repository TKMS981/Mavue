using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.Core.Viewing;
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
    private string _pageCountFormat = "/ {0}";

    public QuickViewWindow()
    {
        InitializeComponent();
        Handle = Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        // Shared display area (Mavue.Viewer); its text colors follow the theme (ApplyTheme).
        Surface = new ViewerSurface { KeyboardFocusTarget = Root };
        Surface.AreaChanged += () => ImageAreaChanged?.Invoke();
        SurfaceHost.Children.Add(Surface);

        // handledEventsToo: the ScrollViewer marks wheel events handled even when it cannot scroll.
        Root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), handledEventsToo: true);

        // Pixel inspector: pointer over the image (only while the inspector is on). Drag out of the image (copy).
        SurfaceHost.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnSurfacePointerMoved), handledEventsToo: true);
        SurfaceHost.PointerExited += (_, _) => ImagePointerMoved?.Invoke(null);

        // Dragging the file out: from its name, or from the image while it fits (a larger image pans on drag).
        WatchDragGesture(FileChip, _ => true);
        WatchDragGesture(SurfaceHost, e => !Surface.IsPannable && Surface.PointerOnImage(e) is not null);
        var strings = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
        SetButtonText(PreviousPageButton, strings.GetString("QuickView_PreviousPage"));
        SetButtonText(NextPageButton, strings.GetString("QuickView_NextPage"));
        SetButtonText(ZoomOutButton, strings.GetString("QuickView_ZoomOut"));
        SetButtonText(ZoomInButton, strings.GetString("QuickView_ZoomIn"));
        SetButtonText(ZoomFitButton, strings.GetString("QuickView_ZoomFit"));
        SetButtonText(RotateButton, strings.GetString("QuickView_Rotate"));
        OpenWithItem.Text = strings.GetString("QuickView_OpenWith");
        SetButtonText(OpenInMavueButton, strings.GetString("QuickView_OpenInMavue"));
        SetButtonText(MoreButton, strings.GetString("QuickView_More"));
        TransparencyGridItem.Text = strings.GetString("QuickView_TransparencyGrid");
        SetButtonText(SidebarButton, strings.GetString("QuickView_Sidebar"));
        SetButtonText(SearchButton, strings.GetString("QuickView_Search"));
        SetButtonText(PresentationButton, strings.GetString("QuickView_Presentation"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LayoutButton, strings.GetString("QuickView_Layout"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(LayoutButton, strings.GetString("QuickView_Layout"));
        LayoutSingleItem.Text = strings.GetString("QuickView_LayoutSingle") + "  (Ctrl+Shift+1)";
        LayoutContinuousItem.Text = strings.GetString("QuickView_LayoutContinuous") + "  (Ctrl+Shift+2)";
        LayoutTwoPagesItem.Text = strings.GetString("QuickView_LayoutTwoPages") + "  (Ctrl+Shift+3)";
        LayoutTwoPagesCoverItem.Text = strings.GetString("QuickView_LayoutTwoPagesCover") + "  (Ctrl+Shift+4)";
        SetButtonText(FilmstripButton, strings.GetString("QuickView_Filmstrip"));
        SetButtonText(InspectorButton, strings.GetString("QuickView_PixelInspector"));
        _pinText = strings.GetString("QuickView_Pin");
        _unpinText = strings.GetString("QuickView_Unpin");
        SetButtonText(PinButton, _pinText);
        SetButtonText(FileChip, strings.GetString("QuickView_DragFile"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PageBox, strings.GetString("QuickView_PageBox"));
        _pageCountFormat = strings.GetString("QuickView_PageCount");

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

        // Windows' own title bar stays as it is (it shows the file's name; Mica makes it and the tool bar one band).
        // Extending content into it or recoloring its buttons cost ~10 ms on every show (measured 2026-10-30).
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

        SetBackdrop(true);
        ApplyTheme(IsDarkTheme());
        _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(() => ApplyTheme(IsDarkTheme()));
        // ColorValuesChanged also reports high contrast turning on or off (AccessibilitySettings.HighContrastChanged
        // cannot be subscribed to without a CoreWindow: 0x80070490).
        InitializeToolbarKeyboard();
    }

    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibility = new();
    private bool _backdrop;
    private bool _backdropWanted = true;

    /// <summary>Height of the top bar in device-independent pixels (fixed, so the image area is known before layout).</summary>
    public const double TopBarHeight = 44;

    /// <summary>
    /// Mica (Base: the least tinted by the wallpaper) behind the chrome — top bar and filmstrip — only; the stage
    /// behind the image is always opaque. Off (solid chrome) for the Low performance tier and in high contrast;
    /// Windows itself shows a solid color when transparency effects are off or energy saver is on.
    /// </summary>
    public void SetBackdrop(bool wanted)
    {
        _backdropWanted = wanted;
        bool on = wanted && !_accessibility.HighContrast && Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported();
        if (on == _backdrop && (SystemBackdrop is not null) == on)
        {
            return;
        }

        _backdrop = on;
        SystemBackdrop = on ? new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base } : null;
        ApplyChromeBackground();
    }

    /// <summary>True while Mica shows behind the chrome.</summary>
    public bool HasBackdrop => _backdrop;

    private void ApplyChromeBackground()
    {
        Brush chrome = _backdrop ? new SolidColorBrush(Colors.Transparent) : Themed("QvChromeSolidBrush");
        TopBar.Background = chrome;
        FilmstripHost.Background = chrome;
        Root.Background = _backdrop ? new SolidColorBrush(Colors.Transparent) : Themed("QvChromeSolidBrush");
    }

    /// <summary>A brush of the window's theme dictionaries for the current theme (for contents that build their own UI).</summary>
    public Brush ThemeBrush(string key) => Themed(key);

    /// <summary>A brush of the window's theme dictionaries (Default = dark, Light, HighContrast) for the current theme.</summary>
    private Brush Themed(string key)
    {
        string theme = _accessibility.HighContrast ? "HighContrast" : IsDark ? "Default" : "Light";
        return (Brush)((ResourceDictionary)Root.Resources.ThemeDictionaries[theme])[key];
    }

    /// <summary>The Windows app theme (Settings › Personalization › Colors › app mode).</summary>
    private static bool IsDarkTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not 1;
    }

    /// <summary>
    /// The Windows app theme: dark (stage #1E1E1E, also the harness's reference color for "image on screen"), light,
    /// or high contrast (system colors, no backdrop). Colors come from the theme dictionaries in the XAML; the viewer
    /// surface's texts follow. Windows' title bar follows the app mode by itself.
    /// </summary>
    public void ApplyTheme(bool dark)
    {
        IsDark = dark;
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        SetBackdrop(_backdropWanted); // off in high contrast, back on afterwards

        ApplyChromeBackground();
        Surface.SetTextColors(Themed("QvPrimaryTextBrush"), Themed("QvSecondaryTextBrush"));
        Surface.SetTransparencyGridColors(CheckerColor("QvCheckerColor1"), CheckerColor("QvCheckerColor2"));
    }

    private Windows.UI.Color CheckerColor(string key)
    {
        string theme = _accessibility.HighContrast ? "HighContrast" : IsDark ? "Default" : "Light";
        return (Windows.UI.Color)((ResourceDictionary)Root.Resources.ThemeDictionaries[theme])[key];
    }

    /// <summary>The theme Quick View currently shows.</summary>
    public bool IsDark { get; private set; } = true;

    /// <summary>Raised for Esc, Space and the title-bar close button.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised for arrow keys while the window has keyboard focus: -1 for ←/↑, +1 for →/↓.</summary>
    public event Action<int>? NavigateRequested;

    /// <summary>
    /// Raised for PageUp/PageDown (window focused), the mouse wheel over the window, and the page buttons:
    /// -1 for the previous page, +1 for the next. The controller ignores it unless a multi-page PDF is shown.
    /// </summary>
    public event Action<int, string>? PageRequested;

    /// <summary>Zoom from the window: the command and, for the mouse wheel, its delta and the pointer position on the surface.</summary>
    public event Action<QuickViewZoomCommand, int, Windows.Foundation.Point?>? ZoomRequested;

    /// <summary>Rotate a quarter turn clockwise (+1) or counter-clockwise (-1).</summary>
    public event Action<int>? RotateRequested;

    /// <summary>A file command from the window (buttons, Ctrl+C, Ctrl+O, Enter on an animated GIF).</summary>
    public event Action<QuickViewFileCommand>? FileCommandRequested;

    /// <summary>A PDF command from the window (buttons and keys while Quick View is active).</summary>
    public event Action<QuickViewPdfCommand, int>? PdfCommandRequested;

    /// <summary>
    /// The mouse wheel over a document view (PDF): returns true when it was used (a page turn in the single-page
    /// layout); otherwise the view scrolls by itself.
    /// </summary>
    public Func<int, bool>? DocumentWheelHandler { get; set; }

    /// <summary>Mouse wheel over video or audio (delta, Shift held); returns true when it was used (seeking).</summary>
    public Func<int, bool, bool>? MediaWheelHandler { get; set; }

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
        double search = SearchHost.Visibility == Visibility.Visible ? SearchHost.ActualHeight : 0;
        double sidebar = SidebarHost.Visibility == Visibility.Visible ? SidebarHost.Width : 0;
        double filmstrip = FilmstripHost.Visibility == Visibility.Visible ? FilmstripHeight : 0;
        return ((2 * ViewerSurface.ContentPadding) + sidebar, (2 * ViewerSurface.ContentPadding) + TopBarHeight + search + filmstrip);
    }

    /// <summary>Height of the filmstrip row in device-independent pixels (fixed, so the image area is known before layout).</summary>
    public const double FilmstripHeight = 88;

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

    private string _pinText = "Pin";
    private string _unpinText = "Unpin";

    /// <summary>The filmstrip was switched on or off (button or F).</summary>
    public event Action<bool>? FilmstripToggled;

    /// <summary>The pixel inspector was switched on or off (button or I).</summary>
    public event Action<bool>? InspectorToggled;

    /// <summary>Pin was switched on or off (button or P).</summary>
    public event Action<bool>? PinToggled;

    /// <summary>
    /// A drag of the file starts (from its name, or from the image while it fits, after the pointer moved past the
    /// system's drag distance): the handler puts the file on the data package or cancels.
    /// </summary>
    public event Action<DragStartingEventArgs>? FileDragStarting;

    /// <summary>The pointer moved over the surface: its position on the full image and the image size (DIPs), or null outside it.</summary>
    public event Action<(double X, double Y, double Width, double Height)?>? ImagePointerMoved;

    /// <summary>The pixel inspector is on (pointer moves are reported only then).</summary>
    public bool InspectorActive { get; private set; }

    /// <summary>Places the filmstrip's element (once).</summary>
    public void AttachFilmstrip(UIElement filmstrip) => FilmstripHost.Children.Add(filmstrip);

    /// <summary>Shows or hides the filmstrip row and sets its button.</summary>
    public void SetFilmstripVisible(bool visible)
    {
        FilmstripHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FilmstripButton.IsChecked = visible;
    }

    /// <summary>The inspector applies to what is shown (a raster image): its button is offered.</summary>
    public void SetInspectorAvailable(bool available)
    {
        InspectorButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            HideInspectorPanel();
        }
    }

    /// <summary>Sets the inspector's on/off state (the panel appears when the pointer is over the image).</summary>
    public void SetInspectorOn(bool on)
    {
        InspectorActive = on;
        InspectorButton.IsChecked = on;
        if (!on)
        {
            HideInspectorPanel();
        }
    }

    /// <summary>Shows the pixel under the pointer; <paramref name="color"/> null while its tile is being read.</summary>
    public void ShowInspector(int x, int y, Core.Viewing.PixelColor? color)
    {
        InspectorPosition.Text = Core.Viewing.PixelInspection.PositionText(x, y);
        if (color is { } c)
        {
            InspectorSwatch.Background = new SolidColorBrush(ColorHelper.FromArgb(c.A, c.R, c.G, c.B));
            InspectorRgb.Text = Core.Viewing.PixelInspection.RgbText(c);
            InspectorHex.Text = c.Hex;
            InspectorAlpha.Text = Core.Viewing.PixelInspection.AlphaText(c);
        }
        else
        {
            InspectorSwatch.Background = null;
            InspectorRgb.Text = "…";
            InspectorHex.Text = string.Empty;
            InspectorAlpha.Text = string.Empty;
        }

        // Keep the panel away from the pointer: the bottom-left corner, or the bottom-right when the pointer is there.
        double panelWidth = InspectorPanel.ActualWidth > 0 ? InspectorPanel.ActualWidth : 220;
        double panelHeight = InspectorPanel.ActualHeight > 0 ? InspectorPanel.ActualHeight : 90;
        bool pointerInCorner = _pointerOnHost.X < panelWidth + 48 && _pointerOnHost.Y > SurfaceHost.ActualHeight - panelHeight - 48;
        InspectorPanel.HorizontalAlignment = pointerInCorner ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(InspectorPanel, string.Join(", ", InspectorPosition.Text, InspectorRgb.Text, InspectorHex.Text, InspectorAlpha.Text));
        InspectorPanel.Visibility = Visibility.Visible;
    }

    public void HideInspectorPanel() => InspectorPanel.Visibility = Visibility.Collapsed;

    /// <summary>Shows the pin state on its button.</summary>
    public void SetPinned(bool pinned)
    {
        PinButton.IsChecked = pinned;
        PinButton.Content = pinned ? "" : ""; // filled pin while pinned
        SetButtonText(PinButton, pinned ? _unpinText : _pinText);
    }

    /// <summary>Shows the information card over the surface, or removes it (null).</summary>
    public void ShowCard(UIElement? card)
    {
        CardHost.Children.Clear();
        if (card is not null)
        {
            CardHost.Children.Add(card);
        }

        CardHost.Visibility = card is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>True while the information card is shown.</summary>
    public bool ShowsCard => CardHost.Visibility == Visibility.Visible;

    /// <summary>What the card layer shows (a content removes only its own card).</summary>
    public UIElement? CardContent => CardHost.Children.Count > 0 ? CardHost.Children[0] : null;

    /// <summary>
    /// Keys offered to the shown content before the window's own meaning (arrows: files, Space/Esc: close) applies;
    /// returns true when the content used the key (e.g. arrows step through the images of an archive).
    /// </summary>
    public Func<VirtualKey, bool>? ContentKeyHandler { get; set; }

    /// <summary>Closes Quick View as Space/Esc would (for content controls that take those keys first).</summary>
    public void RequestClose() => CloseRequested?.Invoke();

    private Windows.Foundation.Point _pointerOnHost;

    private void OnSurfacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!InspectorActive)
        {
            return;
        }

        _pointerOnHost = e.GetCurrentPoint(SurfaceHost).Position;
        ImagePointerMoved?.Invoke(Surface.PointerOnImage(e));
    }

    /// <summary>
    /// Detects a drag on <paramref name="element"/>: a left-button press accepted by <paramref name="canStart"/>, then a
    /// move beyond the system's drag rectangle (SM_CXDRAG/SM_CYDRAG). A click without moving stays a click.
    /// </summary>
    private void WatchDragGesture(UIElement element, Func<PointerRoutedEventArgs, bool> canStart)
    {
        uint? pointer = null;
        Windows.Foundation.Point start = default;
        element.DragStarting += (_, args) => FileDragStarting?.Invoke(args);
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(Root);
            pointer = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && point.Properties.IsLeftButtonPressed && canStart(e)
                ? e.Pointer.PointerId
                : null;
            start = point.Position;
            if (pointer is not null && element == FileChip)
            {
                FileChip.CapturePointer(e.Pointer); // the name is small: keep receiving moves once the pointer leaves it
            }
        }), handledEventsToo: true);
        element.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (pointer != e.Pointer.PointerId)
            {
                return;
            }

            Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(Root);
            if (!point.Properties.IsLeftButtonPressed)
            {
                pointer = null;
                return;
            }

            double scale = RasterizationScale > 0 ? RasterizationScale : 1;
            double dx = Math.Abs(point.Position.X - start.X) * scale, dy = Math.Abs(point.Position.Y - start.Y) * scale;
            if (dx > NativeMethods.GetSystemMetrics(68 /* SM_CXDRAG */) || dy > NativeMethods.GetSystemMetrics(69 /* SM_CYDRAG */))
            {
                pointer = null;
                element.ReleasePointerCaptures();
                e.Handled = true;

                // WinUI's own drag (an OLE drag loop does not work in a WinUI process: WinUI turns on mouse-in-pointer,
                // measured 2026-10-29). DragStarting below puts the file on the data package.
                _ = element.StartDragAsync(e.GetCurrentPoint(element));
            }
        }), handledEventsToo: true);
        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => pointer = null), handledEventsToo: true);
    }

    private void OnFilmstripClick(object sender, RoutedEventArgs e) => FromButton(() => FilmstripToggled?.Invoke(FilmstripButton.IsChecked == true));

    private void OnInspectorClick(object sender, RoutedEventArgs e) => FromButton(() => InspectorToggled?.Invoke(InspectorButton.IsChecked == true));

    private void OnPinClick(object sender, RoutedEventArgs e) => FromButton(() => PinToggled?.Invoke(PinButton.IsChecked == true));

    /// <summary>The checkerboard behind transparent images was switched on or off (the "…" menu).</summary>
    public event Action<bool>? TransparencyGridToggled;

    /// <summary>Shows the checkerboard setting in the menu and applies it to the surface.</summary>
    public void SetTransparencyGridOn(bool on)
    {
        TransparencyGridItem.IsChecked = on;
        Surface.TransparencyGridEnabled = on;
    }

    private void OnTransparencyGridClick(object sender, RoutedEventArgs e) => FromButton(() => TransparencyGridToggled?.Invoke(TransparencyGridItem.IsChecked));

    /// <summary>The top bar's tools are reached with F6 (they are not tab stops otherwise, so Space and Esc stay with the window).</summary>
    private void InitializeToolbarKeyboard()
    {
        ToolBar.TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Cycle;
        ToolBar.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!FocusIsInToolbar())
            {
                SetToolbarTabStops(false);
            }
        });
    }

    private void EnterToolbar()
    {
        SetToolbarTabStops(true);
        Microsoft.UI.Xaml.Controls.Control? first = ToolbarControls().FirstOrDefault(c => c.Visibility == Visibility.Visible && c.IsEnabled && IsShown(c));
        first?.Focus(FocusState.Keyboard);
    }

    private bool FocusIsInToolbar()
    {
        if (Root.XamlRoot is null || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) is not DependencyObject focused)
        {
            return false;
        }

        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ToolBar))
            {
                return true;
            }
        }

        return false;
    }

    private void SetToolbarTabStops(bool on)
    {
        foreach (Microsoft.UI.Xaml.Controls.Control control in ToolbarControls())
        {
            if (control is not Microsoft.UI.Xaml.Controls.TextBox)
            {
                control.IsTabStop = on;
            }
        }
    }

    /// <summary>The buttons of the top bar in visual order (also those inside the groups).</summary>
    private List<Microsoft.UI.Xaml.Controls.Control> ToolbarControls()
    {
        var found = new List<Microsoft.UI.Xaml.Controls.Control>();
        void Walk(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                if (child is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or Microsoft.UI.Xaml.Controls.TextBox)
                {
                    found.Add((Microsoft.UI.Xaml.Controls.Control)child);
                }
                else
                {
                    Walk(child);
                }
            }
        }

        Walk(ToolBar);
        return found;
    }

    /// <summary>The element and all its ancestors up to the bar are visible.</summary>
    private bool IsShown(UIElement element)
    {
        for (DependencyObject? node = element; node is not null && !ReferenceEquals(node, ToolBar); node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Shows the zoom and rotate buttons (images, GIFs, PDF pages, SVG) with the current zoom, or hides them.</summary>
    public void SetViewControls(bool zoomable, bool rotatable, string zoomText)
    {
        ViewButtons.Visibility = zoomable ? Visibility.Visible : Visibility.Collapsed;
        RotateButton.Visibility = rotatable ? Visibility.Visible : Visibility.Collapsed;
        ZoomFitButton.Content = zoomText;
    }

    /// <summary>Puts the shared PDF controls in their places (once).</summary>
    public void AttachPdfControls(UIElement searchBar, UIElement sidebar)
    {
        SearchHost.Children.Add(searchBar);
        SidebarHost.Children.Add(sidebar);
    }

    /// <summary>Shows the PDF tools (PDFium document shown) or hides them with the search bar and the sidebar.</summary>
    public void SetPdfTools(bool visible, bool sidebarOpen, PdfLayoutMode layout)
    {
        PdfTools.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PageBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PageCountText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarButton.IsChecked = sidebarOpen;
        SidebarHost.Visibility = visible && sidebarOpen ? Visibility.Visible : Visibility.Collapsed;
        LayoutSingleItem.IsChecked = layout == PdfLayoutMode.SinglePage;
        LayoutContinuousItem.IsChecked = layout == PdfLayoutMode.Continuous;
        LayoutTwoPagesItem.IsChecked = layout == PdfLayoutMode.TwoPages;
        LayoutTwoPagesCoverItem.IsChecked = layout == PdfLayoutMode.TwoPagesCover;
    }

    /// <summary>The search bar row is shown while the shared search bar is open.</summary>
    public void SetSearchVisible(bool visible) => SearchHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Current page in the page box ("3" of "/ 10"), unless the user is typing in it.</summary>
    public void SetPageNumber(int page, int count)
    {
        if (PageBox.FocusState == FocusState.Unfocused)
        {
            PageBox.Text = (page + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        }

        PageCountText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, _pageCountFormat, count);
    }

    /// <summary>True while a text box of the window (find, page number) has the keyboard.</summary>
    public bool IsTyping => Root.XamlRoot is { } root && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is Microsoft.UI.Xaml.Controls.TextBox;

    /// <summary>"Open in Mavue" is offered only when Mavue.exe was found.</summary>
    public void SetOpenInMavueAvailable(bool available) =>
        OpenInMavueButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows the page buttons for a multi-page PDF (enabled only where a step is possible), or hides them.</summary>
    public void SetPageControls(bool visible, bool canPrevious, bool canNext)
    {
        PageButtons.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviousPageButton.IsEnabled = canPrevious;
        NextPageButton.IsEnabled = canNext;
        Surface.ResetWheel();
    }

    private static void SetButtonText(UIElement button, string text)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, text);
    }

    private void OnPreviousPageClick(object sender, RoutedEventArgs e) => RequestPageFromButton(-1);

    private void OnNextPageClick(object sender, RoutedEventArgs e) => RequestPageFromButton(+1);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => FromButton(() => ZoomRequested?.Invoke(QuickViewZoomCommand.Out, 0, null));

    private void OnZoomInClick(object sender, RoutedEventArgs e) => FromButton(() => ZoomRequested?.Invoke(QuickViewZoomCommand.In, 0, null));

    private void OnZoomFitClick(object sender, RoutedEventArgs e) => FromButton(() => ZoomRequested?.Invoke(QuickViewZoomCommand.Fit, 0, null));

    private void OnRotateClick(object sender, RoutedEventArgs e) => FromButton(() => RotateRequested?.Invoke(+1));

    private void OnOpenWithClick(object sender, RoutedEventArgs e) => FromButton(() => FileCommandRequested?.Invoke(QuickViewFileCommand.OpenWith));

    private void OnOpenInMavueClick(object sender, RoutedEventArgs e) => FromButton(() => FileCommandRequested?.Invoke(QuickViewFileCommand.OpenInMavue));

    private void FromButton(Action action)
    {
        Root.Focus(FocusState.Programmatic); // keep Space/Esc on the window, not on the button
        action();
    }

    private void OnSidebarClick(object sender, RoutedEventArgs e) => FromButton(() => PdfCommandRequested?.Invoke(QuickViewPdfCommand.Sidebar, SidebarButton.IsChecked == true ? 1 : 0));

    private void OnSearchClick(object sender, RoutedEventArgs e) => PdfCommandRequested?.Invoke(QuickViewPdfCommand.Find, 0);

    private void OnPresentationClick(object sender, RoutedEventArgs e) => FromButton(() => PdfCommandRequested?.Invoke(QuickViewPdfCommand.Presentation, 0));

    private void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out PdfLayoutMode mode))
        {
            FromButton(() => PdfCommandRequested?.Invoke(QuickViewPdfCommand.Layout, (int)mode));
        }
    }

    private void OnPageBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (int.TryParse(PageBox.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out int page))
            {
                PdfCommandRequested?.Invoke(QuickViewPdfCommand.GoToPage, page - 1);
            }

            Root.Focus(FocusState.Programmatic);
        }
        else if (e.Key is VirtualKey.Escape or VirtualKey.Space)
        {
            e.Handled = true;
            if (e.Key == VirtualKey.Escape)
            {
                Root.Focus(FocusState.Programmatic);
            }
        }
    }

    private void RequestPageFromButton(int delta)
    {
        Root.Focus(FocusState.Programmatic); // keep Space/Esc on the window, not on the button
        PageRequested?.Invoke(delta, "button");
    }

    /// <summary>PDF keys while Quick View is active: Ctrl+F, F3, Ctrl+A, Ctrl+G, F5, Ctrl+Shift+1–4.</summary>
    private static (QuickViewPdfCommand Command, int Argument)? PdfKey(VirtualKey key, bool control)
    {
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        return (key, control, shift) switch
        {
            (VirtualKey.F, true, _) => (QuickViewPdfCommand.Find, 0),
            (VirtualKey.F3, false, _) => (QuickViewPdfCommand.FindNext, shift ? -1 : +1),
            (VirtualKey.A, true, false) => (QuickViewPdfCommand.SelectAll, 0),
            (VirtualKey.G, true, false) => (QuickViewPdfCommand.FocusPageBox, 0),
            (VirtualKey.F5, false, false) => (QuickViewPdfCommand.Presentation, 0),
            (VirtualKey.Number1, true, true) => (QuickViewPdfCommand.Layout, (int)PdfLayoutMode.SinglePage),
            (VirtualKey.Number2, true, true) => (QuickViewPdfCommand.Layout, (int)PdfLayoutMode.Continuous),
            (VirtualKey.Number3, true, true) => (QuickViewPdfCommand.Layout, (int)PdfLayoutMode.TwoPages),
            (VirtualKey.Number4, true, true) => (QuickViewPdfCommand.Layout, (int)PdfLayoutMode.TwoPagesCover),
            _ => null,
        };
    }

    /// <summary>Focuses the page box (Ctrl+G).</summary>
    public void FocusPageBox()
    {
        if (PageBox.Visibility == Visibility.Visible && PageBox.Focus(FocusState.Keyboard))
        {
            PageBox.SelectAll();
        }
    }

    /// <summary>Ctrl + "+" / "-" / 0 / 1 (main keyboard or numeric keypad).</summary>
    private static QuickViewZoomCommand? ZoomKey(VirtualKey key) => key switch
    {
        (VirtualKey)187 or VirtualKey.Add => QuickViewZoomCommand.In,
        (VirtualKey)189 or VirtualKey.Subtract => QuickViewZoomCommand.Out,
        VirtualKey.Number0 or VirtualKey.NumberPad0 => QuickViewZoomCommand.Fit,
        VirtualKey.Number1 or VirtualKey.NumberPad1 => QuickViewZoomCommand.ActualSize,
        _ => null,
    };

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
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            // Ctrl + wheel zooms around the pointer (Quick View does not need to be active: Windows sends the wheel to
            // the window under the pointer).
            e.Handled = true;
            if (ViewButtons.Visibility == Visibility.Visible)
            {
                Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(Surface);
                ZoomRequested?.Invoke(QuickViewZoomCommand.Wheel, point.Properties.MouseWheelDelta, point.Position);
            }

            return;
        }

        if (Surface.ShowsMedia && MediaWheelHandler is { } mediaWheel)
        {
            // The wheel seeks over video and audio (up = forward); Shift for larger steps.
            if (mediaWheel(e.GetCurrentPoint(Root).Properties.MouseWheelDelta, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift)))
            {
                e.Handled = true;
            }

            return;
        }

        if (Surface.ShowsDocument)
        {
            if (DocumentWheelHandler?.Invoke(e.GetCurrentPoint(Root).Properties.MouseWheelDelta) == true)
            {
                e.Handled = true; // single-page layout: a page turn; otherwise the document scrolls by itself
            }

            return;
        }

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

        // F6: into the top bar's tools and back; Esc in the tools returns to the content (it does not close).
        if (e.Key == VirtualKey.F6 || (e.Key == VirtualKey.Escape && FocusIsInToolbar()))
        {
            e.Handled = true;
            if (e.Key == VirtualKey.F6 && !FocusIsInToolbar())
            {
                EnterToolbar();
            }
            else
            {
                SetToolbarTabStops(false);
                FocusContent();
            }

            return;
        }

        if (e.Handled || IsTyping)
        {
            return; // the find box or the page box has the keyboard (Space types a space there)
        }

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

        if (control && ViewButtons.Visibility == Visibility.Visible && ZoomKey(e.Key) is { } zoom)
        {
            e.Handled = true;
            ZoomRequested?.Invoke(zoom, 0, null);
            return;
        }

        if (PdfTools.Visibility == Visibility.Visible && PdfKey(e.Key, control) is { } pdf)
        {
            e.Handled = true;
            PdfCommandRequested?.Invoke(pdf.Command, pdf.Argument);
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.R when control && RotateButton.Visibility == Visibility.Visible:
                e.Handled = true;
                bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                RotateRequested?.Invoke(shift ? -1 : +1);
                return;
            case VirtualKey.C when control:
                e.Handled = true;
                FileCommandRequested?.Invoke(QuickViewFileCommand.Copy);
                return;
            case VirtualKey.O when control && OpenInMavueButton.Visibility == Visibility.Visible:
                e.Handled = true;
                FileCommandRequested?.Invoke(QuickViewFileCommand.OpenInMavue);
                return;
            case VirtualKey.Enter when !e.KeyStatus.WasKeyDown:
                e.Handled = true;
                FileCommandRequested?.Invoke(QuickViewFileCommand.ToggleAnimation);
                return;
            case VirtualKey.F when !control && !e.KeyStatus.WasKeyDown:
                e.Handled = true;
                FilmstripToggled?.Invoke(FilmstripHost.Visibility != Visibility.Visible);
                return;
            case VirtualKey.I when !control && !e.KeyStatus.WasKeyDown && InspectorButton.Visibility == Visibility.Visible:
                e.Handled = true;
                InspectorToggled?.Invoke(!InspectorActive);
                return;
            case VirtualKey.P when !control && !e.KeyStatus.WasKeyDown:
                e.Handled = true;
                PinToggled?.Invoke(PinButton.IsChecked != true);
                return;
        }

        if (!control && ContentKeyHandler?.Invoke(e.Key) == true)
        {
            e.Handled = true;
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

/// <summary>Zoom commands of the Quick View window.</summary>
public enum QuickViewZoomCommand
{
    In,
    Out,
    Fit,
    ActualSize,
    Wheel,
}

/// <summary>File commands of the Quick View window.</summary>
public enum QuickViewFileCommand
{
    OpenInMavue,
    OpenWith,
    Copy,
    ToggleAnimation,
}

/// <summary>PDF commands of the Quick View window (with an argument where noted).</summary>
public enum QuickViewPdfCommand
{
    /// <summary>Open the find bar.</summary>
    Find,

    /// <summary>Next (+1) or previous (-1) result.</summary>
    FindNext,

    /// <summary>Show (1) or hide (0) the sidebar.</summary>
    Sidebar,

    /// <summary>Argument: a <see cref="PdfLayoutMode"/>.</summary>
    Layout,

    Presentation,

    /// <summary>Argument: zero-based page.</summary>
    GoToPage,

    FocusPageBox,
    SelectAll,
}
