using System.Globalization;
using System.Runtime.InteropServices;
using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Mavue.Image.Export;
using Mavue.Metadata;
using Mavue.Shell;
using Mavue.Viewer.Metadata;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Mavue.App;

/// <summary>
/// "Export without metadata…" in the main app (SPEC §7 "metadata removal", §8 "EXIF removal"; ARCHITECTURE §4.7.15).
/// The same steps, words and safety as Quick View's check before sharing: <see cref="PrivacyReview"/> lists what the
/// image reveals; the categories to remove are chosen; <see cref="CleanCopy"/> writes the copy (without re-encoding for
/// JPEG, PNG, WebP and GIF; converted to JPEG or PNG by the memory-limited export worker otherwise) through
/// <see cref="Core.IO.SafeFileWriter"/>, verifies it before it gets its name, and carries the Mark-of-the-Web over. The
/// original is only read and can never be the destination. The copy goes next to the original ("Save a copy", as in
/// Quick View), on the clipboard, or where the save dialog says. The main app also offers converting any image and the
/// JPEG quality. Types without a way to remove metadata (SVG, a missing codec, a cloud-only file) are explained instead.
/// </summary>
public sealed partial class MainWindow
{
    private CleanCopy? _cleanCopies;

    private async void OnExportCleanClick(object sender, RoutedEventArgs e)
    {
        if (CurrentFile() is { } path && _viewer.State.Facts is { } facts)
        {
            await ExportCleanAsync(path, facts);
        }
    }

    /// <summary>Images: the command is offered (types it cannot handle get an explanation, not a disabled button).</summary>
    private static bool CanExportClean(FileFacts? facts) => facts is not null && FileFormatKinds.IsImage(facts.Format);

    /// <summary>The main app's private folder for clipboard copies and the export worker's output.</summary>
    private CleanCopy CleanCopies => _cleanCopies ??= new CleanCopy(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mavue", "Shared"),
        DispatcherQueue);

    /// <summary>The machine's class (the export worker's memory limit), as Quick View chooses it.</summary>
    private static PerformanceTier ExportTier() => PreviewResourcePolicy.SelectTier(new SystemResourceSnapshot(
        Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        EnergySaver: false,
        RemoteSession: false));

    private async Task ExportCleanAsync(string source, FileFacts facts)
    {
        // Not possible at all: explained before anything is read.
        if (facts.Format == FileFormat.Svg || facts.Access == PreviewAccess.CloudPlaceholder)
        {
            _trace?.Write("export-clean-check", new Dictionary<string, object?> { ["format"] = facts.Format.ToString(), ["refused"] = facts.Format == FileFormat.Svg ? "svg" : "cloud" });
            await MessageAsync("Export_Title", Resources.GetString(facts.Format == FileFormat.Svg ? "Export_Svg" : "Export_Cloud"));
            return;
        }

        using var cancel = new CancellationTokenSource();
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left });
        var panel = new StackPanel { Spacing = 10, MinWidth = 440 };
        AutomationProperties.SetAutomationId(panel, "ExportClean");
        panel.Children.Add(new TextBlock { Text = Path.GetFileName(source), Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(body);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = Resources.GetString("Export_Title"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 620 },
            CloseButtonText = Resources.GetString("Dialog_Close"),
            DefaultButton = ContentDialogButton.Close,
        };

        // Closing the dialog cancels whatever is running (reading, converting, writing): nothing half-written remains.
        dialog.Closing += (_, _) => cancel.Cancel();
        Task shown = ShowDialogAsync(dialog);
        Task running = FillExportDialogAsync(source, facts, body, cancel.Token);
        await shown;
        cancel.Cancel();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
            // Closed while working.
        }
    }

    private async Task FillExportDialogAsync(string source, FileFacts facts, StackPanel body, CancellationToken cancellation)
    {
        PrivacyReview review = await PrivacyReview.ReadAsync(source, FileInformationPrivacy.FindingsAsync, cancellation);
        cancellation.ThrowIfCancellationRequested();
        CleanExportPlan plan = review.Plan;
        _trace?.Write("export-clean-check", new Dictionary<string, object?>
        {
            ["container"] = review.Metadata.Container.ToString(),
            ["findings"] = review.Findings.Count(f => f.Level != PrivacyLevel.Kept),
            ["kinds"] = string.Join(",", review.Findings.Select(f => f.Kind)),
            ["canStrip"] = plan.CanKeepFormat,
            ["canDecode"] = review.CanDecode,
        });

        body.Children.Clear();
        var list = new StackPanel { Spacing = 6 };
        foreach (PrivacyFinding finding in review.Findings)
        {
            list.Children.Add(FindingRow(finding));
        }

        if (!review.HasPrivateData)
        {
            list.Children.Add(new TextBlock { Text = Resources.GetString("Export_Nothing"), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        }

        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220 });

        // A type that needs converting, and Windows cannot decode it: the reason instead of buttons that would fail.
        if (review.CannotCopy)
        {
            body.Children.Add(Status(Resources.GetString("Export_NoCodec")));
            return;
        }

        // The type: the original's (no re-encoding) when possible, otherwise (or by choice) a converted JPEG or PNG.
        var format = new ComboBox { Header = Resources.GetString("Export_Format"), MinWidth = 260 };
        AutomationProperties.SetAutomationId(format, "ExportFormat");
        var formats = new List<ExportFormat>();
        if (plan.CanKeepFormat)
        {
            format.Items.Add(string.Format(CultureInfo.CurrentCulture, Resources.GetString("Export_FormatOriginal"), plan.OriginalExtension.TrimStart('.').ToUpperInvariant()));
            formats.Add(ExportFormat.Original);
        }

        if (review.CanDecode)
        {
            format.Items.Add(Resources.GetString("Export_FormatJpeg"));
            formats.Add(ExportFormat.Jpeg);
            format.Items.Add(Resources.GetString("Export_FormatPng"));
            formats.Add(ExportFormat.Png);
        }

        format.SelectedIndex = 0;
        var quality = new Slider { Header = Resources.GetString("Export_Quality"), Minimum = 50, Maximum = 100, Value = 92, StepFrequency = 1, MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(quality, "ExportQuality");
        var converts = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
        ExportFormat Chosen() => formats[Math.Max(0, format.SelectedIndex)];
        void Update()
        {
            bool convert = plan.Converts(Chosen());
            bool animated = plan.Container is MetadataContainer.Gif or MetadataContainer.WebP;
            converts.Text = Resources.GetString("Export_ConvertsNote") + (animated ? " " + Resources.GetString("Export_FirstFrame") : string.Empty);
            converts.Visibility = convert ? Visibility.Visible : Visibility.Collapsed;
            quality.Visibility = convert && Chosen() == ExportFormat.Jpeg ? Visibility.Visible : Visibility.Collapsed;
        }

        format.SelectionChanged += (_, _) => Update();
        Update();
        body.Children.Add(format);
        body.Children.Add(quality);
        body.Children.Add(converts);

        // What to remove (always removed and always kept are stated, not offered) — as in Quick View.
        body.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], Margin = new Thickness(0, 2, 0, 2) });
        body.Children.Add(new TextBlock { Text = Resources.GetString("Export_Remove"), FontWeight = FontWeights.SemiBold });
        CheckBox location = Check("Export_Kind_Location", "ExportLocation");
        CheckBox time = Check("Export_Kind_CaptureTime", "ExportTime");
        CheckBox camera = Check("Export_Kind_CameraDetails", "ExportCamera");
        CheckBox author = Check("Export_Kind_AuthorCopyright", "ExportAuthor");
        var checks = new Grid { ColumnSpacing = 12 };
        checks.ColumnDefinitions.Add(new ColumnDefinition());
        checks.ColumnDefinitions.Add(new ColumnDefinition());
        checks.RowDefinitions.Add(new RowDefinition());
        checks.RowDefinitions.Add(new RowDefinition());
        foreach ((CheckBox box, int c, int r) in new[] { (location, 0, 0), (time, 1, 0), (camera, 0, 1), (author, 1, 1) })
        {
            Grid.SetColumn(box, c);
            Grid.SetRow(box, r);
            checks.Children.Add(box);
        }

        body.Children.Add(checks);
        body.Children.Add(new TextBlock { Text = Resources.GetString("Export_Always"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 });

        // Where: next to the original or on the clipboard (as in Quick View), or a place chosen in the save dialog.
        var choose = new Button { Content = Resources.GetString("Export_Choose"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var save = new Button { Content = Resources.GetString("Export_SaveNext") };
        var clipboard = new Button { Content = Resources.GetString("Export_ToClipboard") };
        AutomationProperties.SetAutomationId(choose, "ExportChoose");
        AutomationProperties.SetAutomationId(save, "ExportSaveNext");
        AutomationProperties.SetAutomationId(clipboard, "ExportClipboard");
        ToolTipService.SetToolTip(save, Resources.GetString("Export_SaveNextTip"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(clipboard);
        buttons.Children.Add(save);
        buttons.Children.Add(choose);
        body.Children.Add(buttons);
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        TextBlock status = Status(string.Empty);
        var showInFolder = new HyperlinkButton { Content = Resources.GetString("Export_ShowInFolder"), Visibility = Visibility.Collapsed, Padding = new Thickness(0) };
        AutomationProperties.SetAutomationId(showInFolder, "ExportShowInFolder");
        string? lastSaved = null;
        showInFolder.Click += (_, _) =>
        {
            if (lastSaved is not null)
            {
                ShellActions.ShowInFolder(lastSaved);
            }
        };
        body.Children.Add(progress);
        body.Children.Add(status);
        body.Children.Add(showInFolder);

        CleanExportOptions Options() => new(
            new StripOptions(location.IsChecked == true, time.IsChecked == true, camera.IsChecked == true, author.IsChecked == true),
            Chosen(),
            (int)quality.Value);

        async Task RunAsync(ExportDestination destination)
        {
            CleanExportOptions options = Options();
            string extension = plan.ExtensionFor(options.Format);
            string? target = destination switch
            {
                ExportDestination.SameFolder => CleanCopyRules.NameNextTo(source, Resources.GetString("Export_FileName"), extension),
                ExportDestination.Clipboard => CleanCopies.TemporaryPathFor(source, extension),
                _ => (await PickExportTargetAsync(source, extension))?.Path,
            };
            if (target is null)
            {
                return; // the save dialog was cancelled
            }

            if (CleanCopyRules.IsOriginal(source, target))
            {
                status.Text = Resources.GetString("Export_NotOriginal");
                return;
            }

            foreach (Control control in new Control[] { choose, save, clipboard, format, quality, location, time, camera, author })
            {
                control.IsEnabled = false;
            }

            showInFolder.Visibility = Visibility.Collapsed;
            progress.Visibility = Visibility.Visible;
            status.Text = Resources.GetString(plan.Converts(options.Format) ? "Export_Converting" : "Export_Working");
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            bool fellBack = false;
            try
            {
                CleanCopyResult result;
                try
                {
                    result = await CleanCopies.CreateAsync(source, target, options, plan, ExportTier(), cancellation);
                }
                catch (UnauthorizedAccessException) when (destination == ExportDestination.SameFolder)
                {
                    // A read-only folder (a camera card, a protected folder): a private copy on the clipboard instead, as in Quick View.
                    DeleteIfEmpty(target);
                    fellBack = true;
                    destination = ExportDestination.Clipboard;
                    result = await CleanCopies.CreateAsync(source, CleanCopies.TemporaryPathFor(source, extension), options, plan, ExportTier(), cancellation);
                }

                if (destination == ExportDestination.Clipboard)
                {
                    await ShellActions.CopyToClipboardAsync(result.Path, result.Converted ? FileFormatDetector.FromExtension(extension) : facts.Format);
                }
                else
                {
                    lastSaved = result.Path;
                    showInFolder.Visibility = Visibility.Visible;
                }

                string done = destination switch
                {
                    ExportDestination.Clipboard => fellBack ? "Export_DoneFallback" : "Export_DoneClipboard",
                    ExportDestination.SameFolder => "Export_DoneSaved",
                    _ => result.Converted ? "Export_DoneConverted" : "Export_Done",
                };
                status.Text = string.Format(CultureInfo.CurrentCulture, Resources.GetString(done), Path.GetFileName(result.Path), ByteSizeText.Format(result.RemovedBytes));
                _trace?.Write("export-clean", new Dictionary<string, object?>
                {
                    ["destination"] = destination.ToString(),
                    ["fellBack"] = fellBack,
                    ["container"] = plan.Container.ToString(),
                    ["converted"] = result.Converted,
                    ["format"] = options.Format.ToString(),
                    ["removedBytes"] = result.RemovedBytes,
                    ["markOfTheWeb"] = result.KeptMarkOfTheWeb,
                    ["verified"] = true,
                    ["ms"] = Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1),
                });
            }
            catch (OperationCanceledException)
            {
                DeleteIfEmpty(target);
                status.Text = string.Empty;
            }
            catch (Exception ex) when (CleanCopyRules.Classify(ex) is { } failure)
            {
                DeleteIfEmpty(target); // the save dialog created an empty file
                status.Text = Resources.GetString(failure switch
                {
                    CleanCopyFailure.NotVerified => "Export_NotVerified",
                    CleanCopyFailure.NoCodec => "Export_NoCodec",
                    CleanCopyFailure.TooLarge => "Export_TooLarge",
                    CleanCopyFailure.NotWritable => "Export_NotWritable",
                    _ => "Export_Failed",
                });
                _trace?.Write("export-clean", new Dictionary<string, object?>
                {
                    ["destination"] = destination.ToString(),
                    ["error"] = ex is CleanExportException ? "worker-" + ex.Message : ex.GetType().Name,
                    ["failure"] = failure.ToString(),
                    ["verified"] = false,
                });
            }
            finally
            {
                progress.Visibility = Visibility.Collapsed;
                foreach (Control control in new Control[] { choose, save, clipboard, format, quality, location, time, camera, author })
                {
                    control.IsEnabled = true;
                }
            }
        }

        choose.Click += async (_, _) => await RunAsync(ExportDestination.Chosen);
        save.Click += async (_, _) => await RunAsync(ExportDestination.SameFolder);
        clipboard.Click += async (_, _) => await RunAsync(ExportDestination.Clipboard);
    }

    private enum ExportDestination
    {
        Chosen,
        SameFolder,
        Clipboard,
    }

    private static TextBlock Status(string text)
    {
        var status = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(status, "ExportStatus");
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        return status;
    }

    /// <summary>A finding as in Quick View: the level's icon and colour, the kind, and the value (shown, never logged).</summary>
    private static Grid FindingRow(PrivacyFinding finding)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon { Glyph = PrivacyReview.Glyph(finding.Level), FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        string? brush = finding.Level switch
        {
            PrivacyLevel.High => "SystemFillColorCriticalBrush",
            PrivacyLevel.Medium => "SystemFillColorCautionBrush",
            PrivacyLevel.Kept => "SystemFillColorSuccessBrush",
            _ => null,
        };
        if (brush is not null && Application.Current.Resources.TryGetValue(brush, out object value) && value is Brush color)
        {
            icon.Foreground = color;
        }
        else
        {
            icon.Opacity = 0.75;
        }

        row.Children.Add(icon);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = Resources.GetString("Export_Kind_" + finding.Kind), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(finding.Detail))
        {
            text.Children.Add(new TextBlock { Text = finding.Detail, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }

        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private static CheckBox Check(string key, string id)
    {
        var box = new CheckBox { Content = Resources.GetString(key), IsChecked = true, MinWidth = 0 };
        AutomationProperties.SetAutomationId(box, id);
        return box;
    }

    private async Task<StorageFile?> PickExportTargetAsync(string source, string extension)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = string.Format(CultureInfo.CurrentCulture, Resources.GetString("Export_FileName"), Path.GetFileNameWithoutExtension(source)),
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        try
        {
            return await picker.PickSaveFileAsync();
        }
        catch (COMException)
        {
            return null;
        }
    }

    private async Task MessageAsync(string titleKey, string text)
    {
        await ShowDialogAsync(new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = Resources.GetString(titleKey),
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = Resources.GetString("Dialog_Close"),
            DefaultButton = ContentDialogButton.Close,
        });
    }

    /// <summary>Removes the empty file the save dialog created when nothing was written to it.</summary>
    private static void DeleteIfEmpty(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length == 0)
            {
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left as it is.
        }
    }
}
