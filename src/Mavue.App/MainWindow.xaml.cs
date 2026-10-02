using System.Globalization;
using System.Runtime.InteropServices;
using Mavue.Core.Viewing;
using Mavue.Viewer;
using Mavue.Viewer.Controls;
using Mavue.Viewer.Playback;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace Mavue.App;

/// <summary>
/// The viewer window: opens files from the command line, the Open dialog or drag and drop, shows them with the
/// shared viewer (<see cref="DocumentViewer"/> on a <see cref="ViewerSurface"/>), and steps through the files with
/// ←/→ (the files opened together, or the viewable files of the opened file's folder).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A window is not disposed; the viewer and the trace file are released in the Closed handler.")]
public sealed partial class MainWindow : Window
{
    private static readonly ResourceLoader Resources = new();

    private readonly ViewerSurface _surface;
    private readonly DocumentViewer _viewer;
    private readonly TraceFile? _trace;
    private ViewerFileList _files = ViewerFileList.FromFiles([]);
    private int _listVersion;
    private bool _dragTraced;

    /// <param name="traceFile">Diagnostics: write viewer events to this JSON Lines file (tests).</param>
    public MainWindow(string? traceFile = null)
    {
        InitializeComponent();
        Title = Resources.GetString("AppDisplayName");
        ExtendsContentIntoTitleBar = false;

        _surface = new ViewerSurface { KeyboardFocusTarget = ViewerHost };
        ViewerHost.Children.Add(_surface);
        _viewer = new DocumentViewer(_surface, Text);
        _viewer.StateChanged += OnViewerStateChanged;
        if (traceFile is not null)
        {
            _trace = new TraceFile(traceFile);
            _viewer.Trace = _trace.Write;
        }

        // handledEventsToo: the surface's ScrollViewer marks wheel events handled even when it cannot scroll.
        ViewerHost.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), handledEventsToo: true);
        SetCommandText(OpenButton, "MainWindow_Open");
        SetCommandText(PreviousFileButton, "MainWindow_PreviousFile");
        SetCommandText(NextFileButton, "MainWindow_NextFile");
        SetCommandText(PreviousPageButton, "MainWindow_PreviousPage");
        SetCommandText(NextPageButton, "MainWindow_NextPage");

        Closed += (_, _) =>
        {
            // Stops playback and releases the open file before the process ends.
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
        Title = Format("MainWindow_TitleWithFile", name);
        UpdateFileControls();
        ViewerHost.Focus(FocusState.Programmatic);
        _trace?.Write("open", new Dictionary<string, object?> { ["path"] = path, ["index"] = _files.Index, ["count"] = _files.Count });
        await _viewer.OpenAsync(path);
    }

    private void OnViewerStateChanged(ViewerState state)
    {
        InfoText.Text = InfoFor(state);
        bool paged = state.Kind == ViewerContentKind.Pdf && state.PageCount > 1;
        Visibility pageVisibility = paged ? Visibility.Visible : Visibility.Collapsed;
        PageSeparator.Visibility = pageVisibility;
        PreviousPageButton.Visibility = pageVisibility;
        NextPageButton.Visibility = pageVisibility;
        PageNumberContainer.Visibility = pageVisibility;
        PageNumberText.Text = paged ? Format("MainWindow_PageNumber", state.PageIndex + 1, state.PageCount) : string.Empty;
        PreviousPageButton.IsEnabled = _viewer.CanStepPage(-1);
        NextPageButton.IsEnabled = _viewer.CanStepPage(+1);
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
            ViewerContentKind.Image or ViewerContentKind.AnimatedImage => Format("Viewer_InfoImage", facts.Format, state.SourceWidth, state.SourceHeight, size),
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

    private static string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Resources.GetString(key), values);

    private static void SetCommandText(AppBarButton button, string key)
    {
        string text = Resources.GetString(key);
        button.Label = text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
    }

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

    private void OnPreviousFileClick(object sender, RoutedEventArgs e) => StepFile(-1);

    private void OnNextFileClick(object sender, RoutedEventArgs e) => StepFile(+1);

    private void OnPreviousPageClick(object sender, RoutedEventArgs e) => _ = _viewer.StepPageAsync(-1);

    private void OnNextPageClick(object sender, RoutedEventArgs e) => _ = _viewer.StepPageAsync(+1);

    private void OnViewerPointerPressed(object sender, PointerRoutedEventArgs e) => ViewerHost.Focus(FocusState.Pointer);

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_viewer.HandleWheel(e.GetCurrentPoint(ViewerHost).Properties.MouseWheelDelta))
        {
            e.Handled = true;
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (_viewer.HasMedia && MediaShortcut(e.Key, control) is { } command)
        {
            e.Handled = true;
            if (command != MediaCommand.TogglePlay || !e.KeyStatus.WasKeyDown) // a held key toggles once
            {
                _viewer.Execute(command);
            }

            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Left when !control:
                e.Handled = true;
                StepFile(-1);
                break;
            case VirtualKey.Right when !control:
                e.Handled = true;
                StepFile(+1);
                break;
            case VirtualKey.PageUp:
                e.Handled = true;
                _ = _viewer.StepPageAsync(-1);
                break;
            case VirtualKey.PageDown:
                e.Handled = true;
                _ = _viewer.StepPageAsync(+1);
                break;
        }
    }

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
}
