using System.Globalization;
using System.Runtime.InteropServices;
using Mavue.Core.Formats;
using Mavue.Core.Settings;
using Mavue.Core.Viewing;
using Mavue.Pdf;
using Mavue.Shell;
using Mavue.Viewer;
using Mavue.Viewer.Controls;
using Mavue.Viewer.Metadata;
using Mavue.Viewer.Playback;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using XamlImage = Microsoft.UI.Xaml.Controls.Image;

namespace Mavue.App;

/// <summary>
/// The viewer window: opens files from the command line, the Open dialog, recent files, the clipboard or drag and drop,
/// shows them with the shared viewer (<see cref="DocumentViewer"/> on a <see cref="ViewerSurface"/>), and steps
/// through the files with ←/→ (the files opened together, or the viewable files of the opened file's folder).
/// Zoom, rotation, PDF pages and thumbnails, file information and the file commands (copy, open with, show in folder,
/// properties) are on the toolbar and the keyboard.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A window is not disposed; the viewer and the trace file are released in the Closed handler.")]
public sealed partial class MainWindow : Window
{
    private static readonly ResourceLoader Resources = new();


    private readonly ViewerSurface _surface;
    private readonly DocumentViewer _viewer;
    private readonly TraceFile? _trace;
    private readonly string _settingsPath;
    private AppSettings _settings;
    private ViewerFileList _files = ViewerFileList.FromFiles([]);
    private int _listVersion;
    private int _infoVersion;
    private bool _dragTraced;
    private string? _infoFor;
    private string? _recentAdded;
    private readonly bool _noLaunch;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);

    // The shared PDF controls (Mavue.Viewer), working on the viewer's PDF session.
    private readonly PdfSearchBar _searchBar;
    private readonly PdfSidebar _sidebar;

    /// <param name="traceFile">Diagnostics: write viewer events to this JSON Lines file (tests).</param>
    /// <param name="settingsPath">Settings file (tests use their own).</param>
    /// <param name="noLaunch">Tests: web links are traced, not opened.</param>
    public MainWindow(string? traceFile = null, string? settingsPath = null, bool noLaunch = false)
    {
        _noLaunch = noLaunch;
        InitializeComponent();
        Title = Resources.GetString("AppDisplayName");
        ExtendsContentIntoTitleBar = false;
        _settingsPath = settingsPath ?? AppSettings.DefaultPath;
        _settings = AppSettings.Load(_settingsPath);

        _surface = new ViewerSurface { KeyboardFocusTarget = ViewerHost };
        ViewerHost.Children.Add(_surface);
        _viewer = new DocumentViewer(_surface, Text) { ScaleMode = _settings.DefaultScale };
        _viewer.StateChanged += OnViewerStateChanged;
        _viewer.PdfLayout = _settings.PdfLayout;
        _searchBar = new PdfSearchBar(Text) { Session = _viewer.Pdf };
        _searchBar.Closed += () => ViewerHost.Focus(FocusState.Programmatic);
        SearchHost.Children.Add(_searchBar);
        _sidebar = new PdfSidebar(Text) { Session = _viewer.Pdf };
        Sidebar.Children.Add(_sidebar);
        _viewer.UriRequested += OnUriRequested;
        _viewer.PasswordProvider = AskPdfPasswordAsync;
        if (traceFile is not null)
        {
            _trace = new TraceFile(traceFile);
            _searchBar.Trace = _trace.Write;
            _sidebar.Trace = _trace.Write;
            _viewer.Trace = (name, detail) =>
            {
                _trace.Write(name, detail);
                // Tests click links and drag selections at real screen positions: where the pages are once drawn or scrolled.
                if (name is "pdf-rendered" or "pdf-scrolled" && detail.TryGetValue("page", out object? page) && page is int index)
                {
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        foreach (int p in name == "pdf-scrolled" ? new[] { index, index + 1, index + 2 } : [index]) // a spread with the cover alone: 1 | 2–3
                        {
                            if (_viewer.Pdf.PageRectInWindow(p) is { } rect)
                            {
                                _trace.Write("pdf-geometry", new Dictionary<string, object?> { ["page"] = p, ["x"] = Math.Round(rect.X), ["y"] = Math.Round(rect.Y), ["width"] = Math.Round(rect.Width), ["height"] = Math.Round(rect.Height) });
                            }
                        }
                    });
                }
            };
        }

        // handledEventsToo: the surface's ScrollViewer marks wheel events handled even when it cannot scroll.
        ViewerHost.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), handledEventsToo: true);
        LocalizeCommands();
        ApplyTheme(_settings.Theme);
        InfoButton.IsChecked = _settings.ShowInfoPane;
        InfoPane.Visibility = _settings.ShowInfoPane ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailsButton.IsChecked = _settings.ShowPageThumbnails;
        RestorePlacement(_settings.Window);
        BuildRecentList();

        Closed += (_, _) =>
        {
            // Stops playback and releases the open file before the process ends.
            SavePlacement();
            _viewer.Dispose();
            _trace?.Write("closed", new Dictionary<string, object?>());
            _trace?.Dispose();
        };
    }

    /// <summary>
    /// Opens <paramref name="paths"/>: several files are stepped through in the given order; a single file is
    /// stepped through together with the viewable files of its folder (listed in the background).
    /// </summary>
    public void OpenFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return;
        }

        int version = ++_listVersion;
        _files = ViewerFileList.FromFiles(paths);
        _ = ShowCurrentAsync();
        if (paths.Count == 1)
        {
            _ = ListFolderAsync(paths[0], version);
        }
    }

    /// <summary>Waits for the first rendered frame, checks resources, then exits (see App.SmokeTestSwitch).</summary>
    internal void RunSmokeTestAndExit()
    {
        void OnRendering(object? sender, object e)
        {
            CompositionTarget.Rendering -= OnRendering;
            bool ok = !string.IsNullOrEmpty(Title) && !string.IsNullOrEmpty(WelcomeText.Text);
            Environment.ExitCode = ok ? 0 : 1;
            Close();
            Application.Current.Exit();
        }

        CompositionTarget.Rendering += OnRendering;
    }

    private async Task ListFolderAsync(string file, int version)
    {
        List<string>? folder = await Task.Run(() =>
        {
            try
            {
                return Path.GetDirectoryName(file) is { } directory ? Directory.EnumerateFiles(directory).ToList() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null; // no folder navigation; the file itself is still shown
            }
        });
        if (folder is null || version != _listVersion)
        {
            return;
        }

        _files = ViewerFileList.FromFolder(file, folder, ViewerFormats.HasViewableExtension);
        _trace?.Write("file-list", new Dictionary<string, object?> { ["count"] = _files.Count, ["index"] = _files.Index });
        UpdateFileControls();
    }

    private void StepFile(int delta)
    {
        if (_files.TryStep(delta))
        {
            _ = ShowCurrentAsync();
        }
    }

    private void GoToFile(int index)
    {
        if (_files.Count > 0 && _files.TryStep(Math.Clamp(index, 0, _files.Count - 1) - _files.Index))
        {
            _ = ShowCurrentAsync();
        }
    }

    private async Task ShowCurrentAsync()
    {
        if (_files.Current is not { } path)
        {
            return;
        }

        string name = Path.GetFileName(path);
        EmptyState.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Visible;
        NameText.Text = name;
        InfoText.Text = string.Empty;
        ZoomText.Text = string.Empty;
        Title = Format("MainWindow_TitleWithFile", name);
        UpdateFileControls();
        ViewerHost.Focus(FocusState.Programmatic);
        _trace?.Write("open", new Dictionary<string, object?> { ["path"] = path, ["index"] = _files.Index, ["count"] = _files.Count });
        await _viewer.OpenAsync(path);
    }

    private void OnViewerStateChanged(ViewerState state)
    {
        InfoText.Text = InfoFor(state);
        bool pdf = state.Kind == ViewerContentKind.Pdf;
        bool paged = pdf && state.PageCount > 1;
        Visibility pageVisibility = paged ? Visibility.Visible : Visibility.Collapsed;
        PageSeparator.Visibility = pageVisibility;
        PreviousPageButton.Visibility = pageVisibility;
        NextPageButton.Visibility = pageVisibility;
        PageNumberContainer.Visibility = pageVisibility;
        ThumbnailsButton.Visibility = pdf ? Visibility.Visible : Visibility.Collapsed;
        bool text = pdf && _viewer.HasPdfText;
        SearchButton.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        LayoutContainer.Visibility = text && paged ? Visibility.Visible : Visibility.Collapsed;
        LayoutSingleItem.IsChecked = state.PdfLayout == PdfLayoutMode.SinglePage;
        LayoutContinuousItem.IsChecked = state.PdfLayout == PdfLayoutMode.Continuous;
        LayoutTwoPagesItem.IsChecked = state.PdfLayout == PdfLayoutMode.TwoPages;
        LayoutTwoPagesCoverItem.IsChecked = state.PdfLayout == PdfLayoutMode.TwoPagesCover;
        if (paged && PageBox.FocusState == FocusState.Unfocused)
        {
            PageBox.Text = (state.PageIndex + 1).ToString(CultureInfo.CurrentCulture);
        }

        PageCountText.Text = paged ? Format("MainWindow_PageCount", state.PageCount) : string.Empty;
        PreviousPageButton.IsEnabled = _viewer.CanStepPage(-1);
        NextPageButton.IsEnabled = _viewer.CanStepPage(+1);

        bool zoom = state.CanZoom;
        ZoomInButton.IsEnabled = zoom;
        ZoomOutButton.IsEnabled = zoom;
        ZoomMenuButton.IsEnabled = zoom;
        ZoomMenuButton.Content = zoom ? ViewerZoom.Percent(state.Zoom) : "–";
        ZoomText.Text = zoom ? ViewerZoom.Percent(state.Zoom) : string.Empty;
        ZoomFitWidthItem.Visibility = pdf ? Visibility.Visible : Visibility.Collapsed;
        RotateLeftButton.IsEnabled = state.CanRotate;
        RotateRightButton.IsEnabled = state.CanRotate;
        FlipHorizontalButton.IsEnabled = state.CanRotate && !pdf;
        FlipVerticalButton.IsEnabled = state.CanRotate && !pdf;

        bool file = state.Path is not null && state.Kind is not (ViewerContentKind.None or ViewerContentKind.Loading) && state.Message != ViewerMessage.NotAFile;
        CopyButton.IsEnabled = file;
        OpenWithButton.IsEnabled = file;
        ShowInFolderButton.IsEnabled = file;
        PropertiesButton.IsEnabled = file;

        Sidebar.Visibility = pdf && state.PageCount > 0 && ThumbnailsButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (file && state.Path is { } path)
        {
            RememberRecent(path);
            if (InfoPane.Visibility == Visibility.Visible && !string.Equals(_infoFor, path, StringComparison.OrdinalIgnoreCase))
            {
                _ = ShowInfoAsync(path);
            }
        }

        _trace?.Write("state", new Dictionary<string, object?>
        {
            ["kind"] = state.Kind.ToString(),
            ["format"] = state.Facts?.Format.ToString(),
            ["width"] = state.SourceWidth,
            ["height"] = state.SourceHeight,
            ["pageIndex"] = state.PageIndex,
            ["pageCount"] = state.PageCount,
            ["durationMs"] = Math.Round(state.Duration.TotalMilliseconds),
            ["message"] = state.Message?.ToString(),
            ["info"] = InfoText.Text,
            ["zoom"] = Math.Round(state.Zoom, 4),
            ["zoomMode"] = state.ZoomMode.ToString(),
            ["quarterTurns"] = state.Orientation.QuarterTurns,
            ["mirrored"] = state.Orientation.Mirrored,
            ["paused"] = state.IsPaused,
            ["frame"] = state.Frame,
            ["pdfLayout"] = state.PdfLayout.ToString(),
            ["elementWidth"] = Math.Round(_surface.ImageElementSize.Width, 1),
            ["elementHeight"] = Math.Round(_surface.ImageElementSize.Height, 1),
        });
    }

    private void UpdateFileControls()
    {
        PreviousFileButton.IsEnabled = _files.CanStep(-1);
        NextFileButton.IsEnabled = _files.CanStep(+1);
        PositionText.Text = _files.Count > 1 ? Format("MainWindow_Position", _files.Index + 1, _files.Count) : string.Empty;
    }

    private static string InfoFor(ViewerState state)
    {
        if (state.Facts is not { } facts)
        {
            return string.Empty;
        }

        string size = ByteSizeText.Format(facts.Length);
        return state.Kind switch
        {
            ViewerContentKind.Image => Format("Viewer_InfoImage", facts.Format, state.SourceWidth, state.SourceHeight, size),
            ViewerContentKind.AnimatedImage => Format("Viewer_InfoAnimation", facts.Format, state.SourceWidth, state.SourceHeight, size, state.Frame + 1, state.FrameCount)
                + (state.IsPaused ? " · " + Resources.GetString("Viewer_AnimationPaused") : string.Empty),
            ViewerContentKind.Pdf => Format("Viewer_InfoPdf", state.PageCount, size),
            ViewerContentKind.Video => Format("Viewer_InfoVideo", facts.Format, state.SourceWidth, state.SourceHeight, MediaControlMath.FormatDuration(state.Duration), size),
            ViewerContentKind.Audio => Format("Viewer_InfoAudio", facts.Format, MediaControlMath.FormatDuration(state.Duration), size),
            _ => Format("Viewer_InfoBasic", facts.Format, size),
        };
    }

    private static string Text(ViewerMessage message) => Resources.GetString(message switch
    {
        ViewerMessage.Loading => "Viewer_Loading",
        ViewerMessage.NotAFile => "Viewer_Error_NotFile",
        ViewerMessage.Unsupported => "Viewer_Error_Unsupported",
        ViewerMessage.TooLarge => "Viewer_Error_TooLarge",
        ViewerMessage.PdfPassword => "Viewer_Error_PdfPassword",
        ViewerMessage.MediaFailed => "Viewer_Error_Media",
        ViewerMessage.MediaPlaying => "Viewer_MediaPlaying",
        ViewerMessage.MediaPaused => "Viewer_MediaPaused",
        ViewerMessage.MediaEnded => "Viewer_MediaEnded",
        _ => "Viewer_Error_Failed",
    });

    internal static string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Resources.GetString(key), values);

    internal static string Text(string key) => Resources.GetString(key);

    private void LocalizeCommands()
    {
        SetCommandText(OpenButton, "MainWindow_Open", "Ctrl+O");
        SetCommandText(RecentButton, "MainWindow_Recent");
        SetCommandText(PreviousFileButton, "MainWindow_PreviousFile", "←");
        SetCommandText(NextFileButton, "MainWindow_NextFile", "→");
        SetCommandText(ThumbnailsButton, "MainWindow_Sidebar");
        SetCommandText(SearchButton, "MainWindow_Search", "Ctrl+F");
        ToolTipService.SetToolTip(LayoutMenuButton, Resources.GetString("MainWindow_Layout"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LayoutMenuButton, Resources.GetString("MainWindow_Layout"));
        LayoutSingleItem.Text = Resources.GetString("MainWindow_LayoutSingle");
        LayoutSingleItem.KeyboardAcceleratorTextOverride = "Ctrl+Shift+1";
        LayoutContinuousItem.Text = Resources.GetString("MainWindow_LayoutContinuous");
        LayoutContinuousItem.KeyboardAcceleratorTextOverride = "Ctrl+Shift+2";
        LayoutTwoPagesItem.Text = Resources.GetString("MainWindow_LayoutTwoPages");
        LayoutTwoPagesItem.KeyboardAcceleratorTextOverride = "Ctrl+Shift+3";
        LayoutTwoPagesCoverItem.Text = Resources.GetString("MainWindow_LayoutTwoPagesCover");
        LayoutTwoPagesCoverItem.KeyboardAcceleratorTextOverride = "Ctrl+Shift+4";
        PresentationItem.Text = Resources.GetString("MainWindow_Presentation");
        PresentationItem.KeyboardAcceleratorTextOverride = "F5";
        SetCommandText(PreviousPageButton, "MainWindow_PreviousPage", "PageUp");
        SetCommandText(NextPageButton, "MainWindow_NextPage", "PageDown");
        SetCommandText(ZoomOutButton, "MainWindow_ZoomOut", "Ctrl+-");
        SetCommandText(ZoomInButton, "MainWindow_ZoomIn", "Ctrl++");
        SetCommandText(RotateLeftButton, "MainWindow_RotateLeft", "Ctrl+Shift+R");
        SetCommandText(RotateRightButton, "MainWindow_RotateRight", "Ctrl+R");
        SetCommandText(InfoButton, "MainWindow_Info", "Ctrl+I");
        SetCommandText(FlipHorizontalButton, "MainWindow_FlipHorizontal");
        SetCommandText(FlipVerticalButton, "MainWindow_FlipVertical");
        SetCommandText(CopyButton, "MainWindow_Copy", "Ctrl+C");
        SetCommandText(OpenWithButton, "MainWindow_OpenWith");
        SetCommandText(ShowInFolderButton, "MainWindow_ShowInFolder");
        SetCommandText(PropertiesButton, "MainWindow_Properties", "Alt+Enter");
        SetCommandText(NewWindowButton, "MainWindow_NewWindow", "Ctrl+N");
        SetCommandText(SettingsButton, "MainWindow_Settings", "Ctrl+,");
        SetCommandText(ShortcutsButton, "MainWindow_Shortcuts", "F1");
        ZoomFitItem.Text = Resources.GetString("MainWindow_ZoomFit");
        ZoomFitItem.KeyboardAcceleratorTextOverride = "Ctrl+0";
        ZoomFitWidthItem.Text = Resources.GetString("MainWindow_ZoomFitWidth");
        ZoomFitWidthItem.KeyboardAcceleratorTextOverride = "Ctrl+2";
        ZoomActualItem.Text = Resources.GetString("MainWindow_ZoomActual");
        ZoomActualItem.KeyboardAcceleratorTextOverride = "Ctrl+1";
        ToolTipService.SetToolTip(ZoomMenuButton, Resources.GetString("MainWindow_Zoom"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ZoomMenuButton, Resources.GetString("MainWindow_Zoom"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PageBox, Resources.GetString("MainWindow_PageBox"));
        ToolTipService.SetToolTip(PageBox, Resources.GetString("MainWindow_PageBox") + " (Ctrl+G)");
        WelcomeOpenButton.Content = Resources.GetString("MainWindow_OpenEllipsis");
        RecentHeader.Text = Resources.GetString("MainWindow_RecentFiles");
        InfoTitle.Text = Resources.GetString("MainWindow_Info");
    }

    private static void SetCommandText(AppBarButton button, string key, string? shortcut = null)
    {
        string text = Resources.GetString(key);
        button.Label = text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, shortcut is null ? text : $"{text} ({shortcut})");
        if (shortcut is not null)
        {
            button.KeyboardAcceleratorTextOverride = shortcut;
        }
    }

    private static void SetCommandText(AppBarToggleButton button, string key, string? shortcut = null)
    {
        string text = Resources.GetString(key);
        button.Label = text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, shortcut is null ? text : $"{text} ({shortcut})");
    }

    // ---------------------------------------------------------------- opening

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail };
        foreach (string extension in ViewerFormats.Extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        // An unpackaged desktop app must tell the picker which window owns it.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        IReadOnlyList<StorageFile> picked;
        try
        {
            picked = await picker.PickMultipleFilesAsync();
        }
        catch (COMException ex)
        {
            _trace?.Write("picker-error", new Dictionary<string, object?> { ["hresult"] = ex.HResult });
            return;
        }

        OpenFiles(picked.Select(file => file.Path).Where(path => !string.IsNullOrEmpty(path)).ToList());
    }

    private void OnRecentMenuOpening(object sender, object e)
    {
        RecentMenu.Items.Clear();
        _settings = AppSettings.Load(_settingsPath);
        foreach (string path in _settings.RecentFiles)
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(path), Tag = path };
            ToolTipService.SetToolTip(item, path);
            item.Click += (_, _) => OpenFiles([path]);
            RecentMenu.Items.Add(item);
        }

        if (RecentMenu.Items.Count == 0)
        {
            RecentMenu.Items.Add(new MenuFlyoutItem { Text = Resources.GetString("MainWindow_NoRecentFiles"), IsEnabled = false });
            return;
        }

        RecentMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = Resources.GetString("MainWindow_ClearRecent") };
        clear.Click += async (_, _) => await ClearRecentAsync();
        RecentMenu.Items.Add(clear);
    }

    private void BuildRecentList()
    {
        RecentList.Children.Clear();
        foreach (string path in _settings.RecentFiles.Take(8))
        {
            var button = new HyperlinkButton
            {
                Content = new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            ToolTipService.SetToolTip(button, path);
            button.Click += (_, _) => OpenFiles([path]);
            RecentList.Children.Add(button);
        }

        RecentHeader.Visibility = RecentList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Adds an opened file to the recent files (once per file shown, in the background).</summary>
    private void RememberRecent(string path)
    {
        if (string.Equals(_recentAdded, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _recentAdded = path;
        _ = UpdateSettingsAsync(s => s.WithRecentFile(path));
    }

    private async Task ClearRecentAsync()
    {
        await UpdateSettingsAsync(s => s with { RecentFiles = [] });
        BuildRecentList();
    }

    /// <summary>Re-reads, changes and saves the settings (several windows may share the file).</summary>
    /// <remarks>Changes from this window run one after another; concurrent read-change-write lost changes (measured).</remarks>
    private async Task UpdateSettingsAsync(Func<AppSettings, AppSettings> change)
    {
        await _settingsGate.WaitAsync();
        try
        {
            _settings = await Task.Run(() => AppSettings.Update(_settingsPath, change));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _trace?.Write("settings-error", new Dictionary<string, object?> { ["type"] = ex.GetType().Name });
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    // ---------------------------------------------------------------- toolbar

    private void OnPreviousFileClick(object sender, RoutedEventArgs e) => StepFile(-1);

    private void OnNextFileClick(object sender, RoutedEventArgs e) => StepFile(+1);

    private void OnPreviousPageClick(object sender, RoutedEventArgs e) => _ = _viewer.StepPageAsync(-1);

    private void OnNextPageClick(object sender, RoutedEventArgs e) => _ = _viewer.StepPageAsync(+1);

    private void OnZoomInClick(object sender, RoutedEventArgs e) => _viewer.ZoomIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => _viewer.ZoomOut();

    private void OnZoomFitClick(object sender, RoutedEventArgs e) => _viewer.SetZoom(ZoomSetting.Fit);

    private void OnZoomFitWidthClick(object sender, RoutedEventArgs e) => _viewer.SetZoom(ZoomSetting.FitWidth);

    private void OnZoomActualClick(object sender, RoutedEventArgs e) => _viewer.SetZoom(ZoomSetting.ActualSize);

    private void OnZoomPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor))
        {
            _viewer.ZoomTo(factor);
        }
    }

    private void OnRotateLeftClick(object sender, RoutedEventArgs e) => _viewer.Rotate(-1);

    private void OnRotateRightClick(object sender, RoutedEventArgs e) => _viewer.Rotate(+1);

    private void OnFlipHorizontalClick(object sender, RoutedEventArgs e) => _viewer.FlipHorizontal();

    private void OnFlipVerticalClick(object sender, RoutedEventArgs e) => _viewer.FlipVertical();

    private async void OnCopyClick(object sender, RoutedEventArgs e) => await CopyAsync();

    private async void OnOpenWithClick(object sender, RoutedEventArgs e)
    {
        if (CurrentFile() is { } path)
        {
            _viewer.PauseMedia(); // a playing file would keep sounding behind the other app
            await ShellActions.OpenWithAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), path);
        }
    }

    private void OnShowInFolderClick(object sender, RoutedEventArgs e)
    {
        if (CurrentFile() is { } path)
        {
            ShellActions.ShowInFolder(path);
        }
    }

    private void OnPropertiesClick(object sender, RoutedEventArgs e) => ShowProperties();

    private void OnNewWindowClick(object sender, RoutedEventArgs e) => OpenNewWindow();

    private async void OnSettingsClick(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private async void OnShortcutsClick(object sender, RoutedEventArgs e) => await ShowShortcutsAsync();

    private void OnInfoClick(object sender, RoutedEventArgs e) => SetInfoPane(InfoButton.IsChecked == true);

    private void OnThumbnailsClick(object sender, RoutedEventArgs e)
    {
        bool show = ThumbnailsButton.IsChecked == true;
        _ = UpdateSettingsAsync(s => s with { ShowPageThumbnails = show });
        _settings = _settings with { ShowPageThumbnails = show };
        Sidebar.Visibility = _viewer.State.Kind == ViewerContentKind.Pdf && show ? Visibility.Visible : Visibility.Collapsed;
    }

    private string? CurrentFile() => _viewer.State.Path is { } path && File.Exists(path) ? path : null;

    private async Task CopyAsync()
    {
        if (CurrentFile() is not { } path)
        {
            return;
        }

        try
        {
            await ShellActions.CopyToClipboardAsync(path, _viewer.State.Facts?.Format ?? FileFormat.Unknown);
            _trace?.Write("copied", new Dictionary<string, object?> { ["path"] = path });
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException)
        {
            _trace?.Write("copy-error", new Dictionary<string, object?> { ["type"] = ex.GetType().Name, ["hresult"] = ex.HResult });
        }
    }

    private async Task PasteAsync()
    {
        try
        {
            IReadOnlyList<string> files = await ShellActions.FilesOnClipboardAsync();
            List<string> viewable = files.Where(File.Exists).ToList();
            _trace?.Write("paste", new Dictionary<string, object?> { ["count"] = viewable.Count });
            OpenFiles(viewable);
        }
        catch (COMException ex)
        {
            _trace?.Write("paste-error", new Dictionary<string, object?> { ["hresult"] = ex.HResult });
        }
    }

    private void ShowProperties()
    {
        if (CurrentFile() is { } path)
        {
            ShellActions.ShowProperties(WinRT.Interop.WindowNative.GetWindowHandle(this), path);
        }
    }

    private void OpenNewWindow()
    {
        if (Environment.ProcessPath is { } self)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(self) { UseShellExecute = false });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _trace?.Write("new-window-error", new Dictionary<string, object?> { ["error"] = ex.NativeErrorCode });
            }
        }
    }

    // ---------------------------------------------------------------- PDF pages

    private void OnPageBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (int.TryParse(PageBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int page))
            {
                _ = _viewer.GoToPageAsync(Math.Clamp(page, 1, Math.Max(1, _viewer.State.PageCount)) - 1);
            }

            ViewerHost.Focus(FocusState.Programmatic);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            PageBox.Text = (_viewer.State.PageIndex + 1).ToString(CultureInfo.CurrentCulture);
            ViewerHost.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>"Go to page" when the toolbar's page box is not on screen: a small box over the document.</summary>
    private void ShowGoToPageFlyout()
    {
        var box = new TextBox { Width = 120, InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } }, Text = (_viewer.State.PageIndex + 1).ToString(CultureInfo.CurrentCulture) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, Resources.GetString("MainWindow_PageBox"));
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = Format("MainWindow_PageCount", _viewer.State.PageCount), VerticalAlignment = VerticalAlignment.Center });
        var flyout = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int page))
                {
                    _ = _viewer.GoToPageAsync(Math.Clamp(page, 1, Math.Max(1, _viewer.State.PageCount)) - 1);
                }

                flyout.Hide();
            }
        };
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Keyboard);
            box.SelectAll();
        };
        flyout.Closed += (_, _) => ViewerHost.Focus(FocusState.Programmatic);
        flyout.ShowAt(ViewerHost);
    }

    private void OnPageBoxLostFocus(object sender, RoutedEventArgs e) =>
        PageBox.Text = _viewer.State.PageCount > 0 ? (_viewer.State.PageIndex + 1).ToString(CultureInfo.CurrentCulture) : string.Empty;

    // ---------------------------------------------------------------- PDF: sidebar tabs, outline, search, layout

    private void OnSearchClick(object sender, RoutedEventArgs e) => _searchBar.Open();

    private void OnPresentationClick(object sender, RoutedEventArgs e) => StartPresentation();

    /// <summary>Presents the PDF full screen from the current page (F5).</summary>
    private void StartPresentation()
    {
        if (_viewer.HasPdfText)
        {
            PdfPresentationWindow.Start(_viewer.Pdf, WinRT.Interop.WindowNative.GetWindowHandle(this), Resources.GetString("Pdf_PresentationHint"), _trace is null ? null : _trace.Write);
        }
    }

    private void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out PdfLayoutMode mode))
        {
            SetPdfLayout(mode);
        }
    }

    private void SetPdfLayout(PdfLayoutMode mode)
    {
        _viewer.PdfLayout = mode;
        _ = UpdateSettingsAsync(s => s with { PdfLayout = mode });
        _trace?.Write("pdf-layout", new Dictionary<string, object?> { ["mode"] = mode.ToString() });
    }

    /// <summary>A link in a PDF to a web address: opened by the default browser / mail app (http, https and mailto only).</summary>
    private void OnUriRequested(string uri)
    {
        bool opened = !_noLaunch && ShellActions.OpenUri(uri);
        _trace?.Write("uri-opened", new Dictionary<string, object?> { ["uri"] = uri, ["opened"] = opened });
    }

    // ---------------------------------------------------------------- information pane

    private void SetInfoPane(bool show)
    {
        InfoButton.IsChecked = show;
        InfoPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _ = UpdateSettingsAsync(s => s with { ShowInfoPane = show });
        if (show && CurrentFile() is { } path)
        {
            _ = ShowInfoAsync(path);
        }
    }

    private async Task ShowInfoAsync(string path)
    {
        int version = ++_infoVersion;
        _infoFor = path;
        IReadOnlyList<FileInfoItem> items;
        try
        {
            items = await FileInformation.ReadAsync(path, CancellationToken.None);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException or IOException)
        {
            items = [new(FileInfoField.Name, Path.GetFileName(path)), new(FileInfoField.Folder, Path.GetDirectoryName(path) ?? string.Empty)];
        }

        if (version != _infoVersion)
        {
            return;
        }

        InfoRows.Children.Clear();
        ViewerState state = _viewer.State;
        if (state.Kind == ViewerContentKind.Pdf && state.PageCount > 0 && !items.Any(i => i.Field == FileInfoField.Pages))
        {
            items = [.. items, new(FileInfoField.Pages, state.PageCount.ToString(CultureInfo.CurrentCulture))];
        }

        foreach (FileInfoItem item in items)
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock { Text = Resources.GetString("Info_" + item.Field), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Opacity = 0.7 });
            row.Children.Add(new TextBlock { Text = item.Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            InfoRows.Children.Add(row);
        }

        _trace?.Write("info-pane", new Dictionary<string, object?> { ["rows"] = items.Count, ["fields"] = string.Join(',', items.Select(i => i.Field)) });
    }

    // ---------------------------------------------------------------- keyboard and mouse

    private void OnViewerPointerPressed(object sender, PointerRoutedEventArgs e) => ViewerHost.Focus(FocusState.Pointer);

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(_surface);
        int delta = point.Properties.MouseWheelDelta;
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            if (_viewer.State.CanZoom)
            {
                _viewer.ZoomWheel(delta, point.Position);
            }

            e.Handled = true;
            return;
        }

        if (_viewer.HandleWheel(delta))
        {
            e.Handled = true;
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            _trace?.Write("key-handled", new Dictionary<string, object?> { ["key"] = e.Key.ToString(), ["source"] = e.OriginalSource?.GetType().Name });
            return;
        }

        bool control = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        bool alt = IsDown(VirtualKey.Menu);
        object? focused = Content.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        bool typing = focused is TextBox;
        _trace?.Write("key", new Dictionary<string, object?> { ["key"] = e.Key.ToString(), ["control"] = control, ["focus"] = focused?.GetType().Name });
        if (typing)
        {
            return; // the page box has the keyboard
        }

        if (_viewer.HasMedia && MediaShortcut(e.Key, control) is { } command)
        {
            e.Handled = true;
            if (command != MediaCommand.TogglePlay || !e.KeyStatus.WasKeyDown) // a held key toggles once
            {
                _viewer.Execute(command);
            }

            return;
        }

        if (control && HandleControlKey(e.Key, shift))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        switch (e.Key)
        {
            case VirtualKey.Left when !control && !alt:
                StepFile(-1);
                break;
            case VirtualKey.Right when !control && !alt:
                StepFile(+1);
                break;
            case VirtualKey.Up when !control:
                if (!_viewer.Scroll(0, -ScrollStep()) && _viewer.State.Kind == ViewerContentKind.Pdf)
                {
                    _ = _viewer.StepPageAsync(-1);
                }

                break;
            case VirtualKey.Down when !control:
                if (!_viewer.Scroll(0, ScrollStep()) && _viewer.State.Kind == ViewerContentKind.Pdf)
                {
                    _ = _viewer.StepPageAsync(+1);
                }

                break;
            case VirtualKey.PageUp:
                _ = _viewer.StepPageAsync(-1);
                break;
            case VirtualKey.PageDown:
                _ = _viewer.StepPageAsync(+1);
                break;
            case VirtualKey.Home:
                if (_viewer.State.Kind == ViewerContentKind.Pdf)
                {
                    _ = _viewer.GoToPageAsync(0);
                }
                else
                {
                    GoToFile(0);
                }

                break;
            case VirtualKey.End:
                if (_viewer.State.Kind == ViewerContentKind.Pdf)
                {
                    _ = _viewer.GoToPageAsync(_viewer.State.PageCount - 1);
                }
                else
                {
                    GoToFile(_files.Count - 1);
                }

                break;
            case VirtualKey.Space or VirtualKey.Enter when !alt && _viewer.HasAnimation && !e.KeyStatus.WasKeyDown:
                _viewer.ToggleAnimation();
                break;
            case (VirtualKey)188 when _viewer.HasAnimation: // ,
                _viewer.StepFrame(-1);
                break;
            case (VirtualKey)190 when _viewer.HasAnimation: // .
                _viewer.StepFrame(+1);
                break;
            case VirtualKey.Enter when alt:
                ShowProperties();
                break;
            case VirtualKey.F3 when _searchBar.HasResults:
                _ = _searchBar.FindAsync(IsDown(VirtualKey.Shift) ? -1 : +1);
                break;
            case VirtualKey.F5 when _viewer.HasPdfText:
                StartPresentation();
                break;
            case VirtualKey.Escape when _viewer.HasTextSelection:
                _viewer.Pdf.ClearSelection();
                break;
            case VirtualKey.Escape when _searchBar.IsOpen:
                _searchBar.Close();
                break;
            case VirtualKey.F1:
                _ = ShowShortcutsAsync();
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    /// <summary>Ctrl shortcuts; returns false for keys it does not use.</summary>
    private bool HandleControlKey(VirtualKey key, bool shift)
    {
        if (shift && _viewer.HasPdfText && key is VirtualKey.Number1 or VirtualKey.Number2 or VirtualKey.Number3 or VirtualKey.Number4)
        {
            SetPdfLayout(key switch
            {
                VirtualKey.Number1 => PdfLayoutMode.SinglePage,
                VirtualKey.Number3 => PdfLayoutMode.TwoPages,
                VirtualKey.Number4 => PdfLayoutMode.TwoPagesCover,
                _ => PdfLayoutMode.Continuous,
            });
            return true;
        }

        switch (key)
        {
            case VirtualKey.F:
                _searchBar.Open();
                return true;
            case VirtualKey.A when _viewer.HasPdfText:
                _ = _viewer.Pdf.SelectAllAsync();
                return true;
            case VirtualKey.C when _viewer.HasTextSelection:
                _ = _viewer.Pdf.CopySelectionAsync();
                return true;
            case (VirtualKey)187 or VirtualKey.Add: // = / + (OEM plus) and the numeric keypad
                _viewer.ZoomIn();
                return true;
            case (VirtualKey)189 or VirtualKey.Subtract: // - (OEM minus)
                _viewer.ZoomOut();
                return true;
            case VirtualKey.Number0 or VirtualKey.NumberPad0:
                _viewer.SetZoom(ZoomSetting.Fit);
                return true;
            case VirtualKey.Number1 or VirtualKey.NumberPad1:
                _viewer.SetZoom(ZoomSetting.ActualSize);
                return true;
            case VirtualKey.Number2 or VirtualKey.NumberPad2:
                _viewer.SetZoom(ZoomSetting.FitWidth);
                return true;
            case VirtualKey.R:
                _viewer.Rotate(shift ? -1 : +1);
                return true;
            case VirtualKey.C:
                _ = CopyAsync();
                return true;
            case VirtualKey.V:
                _ = PasteAsync();
                return true;
            case VirtualKey.I:
                SetInfoPane(InfoPane.Visibility != Visibility.Visible);
                return true;
            case VirtualKey.G when PageNumberContainer.Visibility == Visibility.Visible:
                if (PageBox.Focus(FocusState.Keyboard))
                {
                    PageBox.SelectAll();
                }
                else
                {
                    ShowGoToPageFlyout(); // the page box is in the toolbar's overflow (narrow window)
                }

                return true;
            case VirtualKey.N:
                OpenNewWindow();
                return true;
            case VirtualKey.W:
                Close();
                return true;
            case (VirtualKey)188: // ,
                _ = ShowSettingsAsync();
                return true;
            default:
                return false;
        }
    }

    private double ScrollStep() => Math.Max(48, _surface.ActualHeight / 8);

    private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Space/Enter play and pause; Ctrl+←/→ seek 10 s; Ctrl+↑/↓ change the volume (same as Quick View, plus Space).</summary>
    private static MediaCommand? MediaShortcut(VirtualKey key, bool control) => (key, control) switch
    {
        (VirtualKey.Space or VirtualKey.Enter, false) => MediaCommand.TogglePlay,
        (VirtualKey.Left, true) => MediaCommand.SeekBackward,
        (VirtualKey.Right, true) => MediaCommand.SeekForward,
        (VirtualKey.Up, true) => MediaCommand.VolumeUp,
        (VirtualKey.Down, true) => MediaCommand.VolumeDown,
        _ => null,
    };

    // ---------------------------------------------------------------- drag and drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = e.DataView.Contains(StandardDataFormats.StorageItems);
        if (files)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }

        if (!_dragTraced)
        {
            _dragTraced = true;
            _trace?.Write("drag-over", new Dictionary<string, object?> { ["files"] = files });
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        _dragTraced = false;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            OpenFiles(items.OfType<IStorageFile>().Select(file => file.Path).Where(path => !string.IsNullOrEmpty(path)).ToList());
        }
        catch (COMException ex)
        {
            _trace?.Write("drop-error", new Dictionary<string, object?> { ["hresult"] = ex.HResult });
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---------------------------------------------------------------- window, theme, dialogs

    private void ApplyTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    /// <summary>Puts the window where it was last time, if that place is still on a monitor.</summary>
    private void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null)
        {
            return;
        }

        var rect = new RectInt32(placement.X, placement.Y, Math.Max(480, placement.Width), Math.Max(360, placement.Height));
        DisplayArea area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
        if (area is null)
        {
            return; // that monitor is gone: let Windows choose
        }

        RectInt32 work = area.WorkArea;
        rect.Width = Math.Min(rect.Width, work.Width);
        rect.Height = Math.Min(rect.Height, work.Height);
        rect.X = Math.Clamp(rect.X, work.X, work.X + work.Width - rect.Width);
        rect.Y = Math.Clamp(rect.Y, work.Y, work.Y + work.Height - rect.Height);
        AppWindow.MoveAndResize(rect);
        if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private void SavePlacement()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized)
        {
            return;
        }

        bool maximized = presenter.State == OverlappedPresenterState.Maximized;
        WindowPlacement? previous = _settings.Window;
        WindowPlacement placement = maximized && previous is not null
            ? previous with { Maximized = true } // keep the restored size for later
            : new WindowPlacement(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, maximized);
        bool entered = _settingsGate.Wait(TimeSpan.FromSeconds(2)); // let a pending change finish first
        try
        {
            AppSettings.Update(_settingsPath, s => s with { Window = placement }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next start uses Windows' default placement.
        }
        finally
        {
            if (entered)
            {
                _settingsGate.Release();
            }
        }
    }

    private async Task ShowShortcutsAsync()
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (string Keys, string Action)[] rows =
        [
            ("Ctrl+O", "Shortcut_Open"), ("← / →", "Shortcut_Files"), ("Home / End", "Shortcut_FirstLast"),
            ("PageUp / PageDown", "Shortcut_Pages"), ("Ctrl+G", "Shortcut_GoToPage"),
            ("Ctrl++ / Ctrl+- / Ctrl+wheel", "Shortcut_Zoom"), ("Ctrl+0", "Shortcut_Fit"), ("Ctrl+1", "Shortcut_ActualSize"), ("Ctrl+2", "Shortcut_FitWidth"),
            ("↑ / ↓ / drag", "Shortcut_Pan"), ("Ctrl+R / Ctrl+Shift+R", "Shortcut_Rotate"),
            ("Space / Enter", "Shortcut_Play"), (", / .", "Shortcut_Frames"), ("Ctrl+← / Ctrl+→", "Shortcut_Seek"), ("Ctrl+↑ / Ctrl+↓", "Shortcut_Volume"),
            ("Ctrl+F / F3 / Shift+F3", "Shortcut_Find"), ("Ctrl+A", "Shortcut_SelectAll"), ("Ctrl+Shift+1 / 2 / 3 / 4", "Shortcut_Layout"), ("F5", "Shortcut_Presentation"),
            ("Ctrl+C", "Shortcut_Copy"), ("Ctrl+V", "Shortcut_Paste"), ("Ctrl+I", "Shortcut_Info"), ("Alt+Enter", "Shortcut_Properties"),
            ("Ctrl+N", "Shortcut_NewWindow"), ("Ctrl+W", "Shortcut_Close"), ("Ctrl+,", "Shortcut_Settings"), ("F1", "Shortcut_Help"),
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = rows[i].Keys, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var action = new TextBlock { Text = Resources.GetString(rows[i].Action), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = Resources.GetString("MainWindow_Shortcuts"),
            Content = new ScrollViewer { Content = grid, MaxHeight = 520 },
            CloseButtonText = Resources.GetString("Dialog_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowDialogAsync(dialog);
    }

    private async Task ShowSettingsAsync()
    {
        var page = new SettingsPanel(_settings, _settingsPath);
        page.ThemeChanged += theme =>
        {
            ApplyTheme(theme);
            _settings = _settings with { Theme = theme };
        };
        page.DefaultScaleChanged += scale =>
        {
            _settings = _settings with { DefaultScale = scale };
            _viewer.ScaleMode = scale;
        };
        page.RecentCleared += BuildRecentList;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = Resources.GetString("MainWindow_Settings"),
            Content = page,
            CloseButtonText = Resources.GetString("Dialog_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowDialogAsync(dialog);
        _settings = AppSettings.Load(_settingsPath);
    }

    /// <summary>The password of an encrypted PDF (null when cancelled). Nothing is remembered.</summary>
    private async Task<string?> AskPdfPasswordAsync(string fileName, bool wrong)
    {
        var box = new PasswordBox
        {
            Header = string.Format(System.Globalization.CultureInfo.CurrentCulture, Resources.GetString("Password_Prompt"), fileName),
            PlaceholderText = Resources.GetString("Password_Placeholder"),
            MinWidth = 320,
        };
        AutomationProperties.SetName(box, Resources.GetString("Password_Placeholder"));
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(box);
        if (wrong)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Resources.GetString("Password_Wrong"),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = Resources.GetString("Password_Title"),
            Content = panel,
            PrimaryButtonText = Resources.GetString("Password_Open"),
            CloseButtonText = Resources.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                dialog.Hide();
                box.Tag = "enter";
            }
        };
        _trace?.Write("password-dialog", new Dictionary<string, object?> { ["wrong"] = wrong });
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (COMException)
        {
            return null; // another dialog is open
        }

        return result == ContentDialogResult.Primary || box.Tag is "enter" ? box.Password : null;
    }

    private static async Task ShowDialogAsync(ContentDialog dialog)
    {
        try
        {
            await dialog.ShowAsync();
        }
        catch (COMException)
        {
            // Another dialog is open (only one ContentDialog can be shown at a time).
        }
    }
}
