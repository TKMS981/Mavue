using System.Globalization;
using Mavue.Core.Viewing;
using Mavue.Image.Export;
using Mavue.Metadata;
using Mavue.QuickView.Diagnostics;
using Mavue.Shell;
using Mavue.Viewer.Metadata;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Mavue.QuickView.Host.Metadata;

/// <summary>Where a copy without private metadata goes.</summary>
internal enum CleanCopyDestination
{
    /// <summary>Next to the original, under a new name (the original is never changed).</summary>
    SameFolder,

    /// <summary>A private temporary folder; the copy is put on the clipboard (for pasting into mail or chat).</summary>
    Clipboard,
}

/// <summary>
/// The check before sharing (SPEC §3.1 "Privacy check before sharing", "Copy without private metadata";
/// ARCHITECTURE §4.7.13): a flyout listing what the image's metadata reveals (location, time, people, device serial
/// numbers, software and history, descriptions, hidden thumbnails and data after the image…), with the categories to
/// remove, and two ways to share a clean copy: on the clipboard (a private temporary copy) or saved next to the
/// original under a new name. The original is never changed; each copy is verified before it appears
/// (<see cref="CleanCopy"/>, shared with the main app's export). Nothing is uploaded or logged (the timing log gets
/// kinds and counts only).
/// </summary>
internal sealed class ShareCheck : IDisposable
{
    private readonly QuickViewWindow _window;
    private readonly ResourceLoader _strings;
    private readonly QuickViewTimeline _timeline;
    private readonly Func<long> _currentRequest;
    private readonly Func<PerformanceTier> _tier;
    private readonly CleanCopy _copies;
    private Flyout? _flyout;
    private CancellationTokenSource? _work;

    public ShareCheck(QuickViewWindow window, ResourceLoader strings, QuickViewTimeline timeline, Func<long> currentRequest, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Func<PerformanceTier> tier)
    {
        _tier = tier;
        _window = window;
        _strings = strings;
        _timeline = timeline;
        _currentRequest = currentRequest;
        _copies = new CleanCopy(TemporaryRoot, dispatcher);
    }

    /// <summary>Quick View's private folder for clipboard copies and the export worker's output.</summary>
    public static string TemporaryRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mavue", "QuickView", "Shared");

    /// <summary>Opens the check for <paramref name="path"/> under <paramref name="anchor"/>.</summary>
    public void Open(string path, FileFacts facts, FrameworkElement anchor)
    {
        Close();
        _work = new CancellationTokenSource();
        var content = new StackPanel { Width = 380, Spacing = 8 };
        AutomationProperties.SetAutomationId(content, "QuickViewShareCheck");
        content.Children.Add(new TextBlock { Text = _strings.GetString("QuickView_Share_Title"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = Path.GetFileName(path), Foreground = _window.ThemeBrush("QvSecondaryTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left });
        content.Children.Add(body);
        _flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        _flyout.Closed += (_, _) => _work?.Cancel();
        _flyout.ShowAt(anchor);
        _ = FillAsync(path, facts, body, _work.Token);
    }

    public void Dispose() => Close();

    public void Close()
    {
        _work?.Cancel();
        _work?.Dispose();
        _work = null;
        _flyout?.Hide();
        _flyout = null;
    }

    private async Task FillAsync(string path, FileFacts facts, StackPanel body, CancellationToken cancellation)
    {
        long requestId = _currentRequest();
        PrivacyReview review;
        try
        {
            review = await PrivacyReview.ReadAsync(path, FileInformationPrivacy.FindingsAsync, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        CleanExportPlan plan = review.Plan;
        bool canStrip = plan.CanKeepFormat;
        _timeline.Mark(requestId, "share-check", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["container"] = review.Metadata.Container.ToString(),
            ["findings"] = review.Findings.Count(f => f.Level != PrivacyLevel.Kept),
            ["kinds"] = string.Join(",", review.Findings.Select(f => f.Kind)),
            ["canStrip"] = canStrip,
            ["canDecode"] = review.CanDecode,
        });

        body.Children.Clear();
        Brush secondary = _window.ThemeBrush("QvSecondaryTextBrush");
        var list = new StackPanel { Spacing = 6 };
        foreach (PrivacyFinding finding in review.Findings)
        {
            list.Children.Add(Row(finding, secondary));
        }

        if (!review.HasPrivateData)
        {
            list.Children.Add(new TextBlock { Text = _strings.GetString("QuickView_Share_Nothing"), TextWrapping = TextWrapping.Wrap, Foreground = secondary });
        }

        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260 });

        // No copy is possible (a type that needs converting, and no codec): say why instead of offering buttons that fail.
        if (review.CannotCopy)
        {
            var reason = new TextBlock { Text = _strings.GetString("QuickView_Share_NoCodec"), TextWrapping = TextWrapping.Wrap, Foreground = secondary, FontSize = 12 };
            AutomationProperties.SetAutomationId(reason, "QuickViewShareStatus");
            body.Children.Add(reason);
            return;
        }

        // HEIF, AVIF, raw, TIFF…: the clean copy is a converted image (the type cannot carry a removal losslessly here).
        RadioButton? asJpeg = null, asPng = null;
        if (!canStrip)
        {
            body.Children.Add(new TextBlock { Text = _strings.GetString("QuickView_Share_Converts"), TextWrapping = TextWrapping.Wrap, Foreground = secondary, FontSize = 12 });
            var formats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            asJpeg = new RadioButton { Content = "JPEG", GroupName = "QvShareFormat", IsChecked = true, MinWidth = 0 };
            asPng = new RadioButton { Content = "PNG", GroupName = "QvShareFormat", MinWidth = 0 };
            AutomationProperties.SetAutomationId(asJpeg, "QuickViewShareJpeg");
            AutomationProperties.SetAutomationId(asPng, "QuickViewSharePng");
            formats.Children.Add(asJpeg);
            formats.Children.Add(asPng);
            body.Children.Add(formats);
        }

        // What to remove (always removed and always kept are stated, not offered).
        body.Children.Add(new Border { Height = 1, Background = _window.ThemeBrush("QvDividerBrush"), Margin = new Thickness(0, 2, 0, 2) });
        body.Children.Add(new TextBlock { Text = _strings.GetString("QuickView_Share_Remove"), FontWeight = FontWeights.SemiBold });
        CheckBox location = Option("QuickView_Share_Kind_Location", "QuickViewShareLocation");
        CheckBox time = Option("QuickView_Share_Kind_CaptureTime", "QuickViewShareTime");
        CheckBox camera = Option("QuickView_Share_Kind_CameraDetails", "QuickViewShareCamera");
        CheckBox author = Option("QuickView_Share_Kind_AuthorCopyright", "QuickViewShareAuthor");
        var options = new Grid { ColumnSpacing = 8 };
        options.ColumnDefinitions.Add(new ColumnDefinition());
        options.ColumnDefinitions.Add(new ColumnDefinition());
        options.RowDefinitions.Add(new RowDefinition());
        options.RowDefinitions.Add(new RowDefinition());
        Place(options, location, 0, 0);
        Place(options, time, 1, 0);
        Place(options, camera, 0, 1);
        Place(options, author, 1, 1);
        body.Children.Add(options);
        body.Children.Add(new TextBlock { Text = _strings.GetString("QuickView_Share_Always"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = secondary });

        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(status, "QuickViewShareStatus");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var toClipboard = new Button { Content = _strings.GetString("QuickView_Share_ToClipboard"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var save = new Button { Content = _strings.GetString("QuickView_Share_Save") };
        AutomationProperties.SetAutomationId(toClipboard, "QuickViewShareClipboard");
        AutomationProperties.SetAutomationId(save, "QuickViewShareSave");
        buttons.Children.Add(save);
        buttons.Children.Add(toClipboard);
        body.Children.Add(buttons);
        body.Children.Add(status);

        CleanExportOptions Chosen() => new(
            new StripOptions(location.IsChecked == true, time.IsChecked == true, camera.IsChecked == true, author.IsChecked == true),
            canStrip ? ExportFormat.Original : asPng?.IsChecked == true ? ExportFormat.Png : ExportFormat.Jpeg);
        toClipboard.Click += async (_, _) => await CopyAsync(path, facts, plan, CleanCopyDestination.Clipboard, Chosen(), status, [toClipboard, save]);
        save.Click += async (_, _) => await CopyAsync(path, facts, plan, CleanCopyDestination.SameFolder, Chosen(), status, [toClipboard, save]);
    }

    private async Task CopyAsync(string path, FileFacts facts, CleanExportPlan plan, CleanCopyDestination destination, CleanExportOptions options, TextBlock status, Button[] buttons)
    {
        foreach (Button button in buttons)
        {
            button.IsEnabled = false;
        }

        status.Text = _strings.GetString(plan.Converts(options.Format) ? "QuickView_Share_Converting" : "QuickView_Share_Working");
        string extension = plan.ExtensionFor(options.Format);
        long start = QuickViewTimeline.Now;
        bool fellBack = false;
        try
        {
            CleanCopyResult result;
            try
            {
                string where = destination == CleanCopyDestination.SameFolder
                    ? CleanCopyRules.NameNextTo(path, _strings.GetString("QuickView_Share_CopyName"), extension)
                    : _copies.TemporaryPathFor(path, extension);
                result = await _copies.CreateAsync(path, where, options, plan, _tier(), _work?.Token ?? CancellationToken.None);
            }
            catch (UnauthorizedAccessException) when (destination == CleanCopyDestination.SameFolder)
            {
                // The folder is read-only (a camera card, a protected folder): a private copy on the clipboard instead.
                fellBack = true;
                destination = CleanCopyDestination.Clipboard;
                result = await _copies.CreateAsync(path, _copies.TemporaryPathFor(path, extension), options, plan, _tier(), _work?.Token ?? CancellationToken.None);
            }

            if (destination == CleanCopyDestination.Clipboard)
            {
                await ShellActions.CopyToClipboardAsync(result.Path, result.Converted ? Core.Formats.FileFormatDetector.FromExtension(extension) : facts.Format);
            }

            status.Text = string.Format(
                CultureInfo.CurrentCulture,
                _strings.GetString(destination == CleanCopyDestination.Clipboard ? (fellBack ? "QuickView_Share_DoneFallback" : "QuickView_Share_DoneClipboard") : "QuickView_Share_DoneSaved"),
                Path.GetFileName(result.Path),
                ByteSizeText.Format(result.RemovedBytes));
            _timeline.Mark(_currentRequest(), "share-copy", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["destination"] = destination.ToString(),
                ["fellBack"] = fellBack,
                ["container"] = plan.Container.ToString(),
                ["converted"] = result.Converted,
                ["format"] = options.Format.ToString(),
                ["removedBytes"] = result.RemovedBytes,
                ["markOfTheWeb"] = result.KeptMarkOfTheWeb,
                ["verified"] = true,
                ["ms"] = Math.Round((QuickViewTimeline.Now - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 1),
            });
        }
        catch (OperationCanceledException)
        {
            status.Text = string.Empty;
        }
        catch (Exception ex) when (CleanCopyRules.Classify(ex) is { } failure)
        {
            status.Text = _strings.GetString(failure switch
            {
                CleanCopyFailure.NotVerified => "QuickView_Share_NotVerified",
                CleanCopyFailure.NoCodec => "QuickView_Share_NoCodec",
                CleanCopyFailure.TooLarge => "QuickView_Share_TooLarge",
                CleanCopyFailure.NotWritable => "QuickView_Share_NotWritable",
                _ => "QuickView_Share_Failed",
            });
            _timeline.Mark(_currentRequest(), "share-copy", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["destination"] = destination.ToString(),
                ["error"] = ex is CleanExportException ? "worker-" + ex.Message : ex.GetType().Name,
                ["failure"] = failure.ToString(),
                ["verified"] = false,
            });
        }
        finally
        {
            foreach (Button button in buttons)
            {
                button.IsEnabled = true;
            }
        }
    }

    private Grid Row(PrivacyFinding finding, Brush secondary)
    {
        string brush = finding.Level switch
        {
            PrivacyLevel.High => "QvSyntaxErrorBrush",
            PrivacyLevel.Medium => "QvSyntaxWarningBrush",
            PrivacyLevel.Kept => "QvSyntaxInsertedBrush",
            _ => "QvSecondaryTextBrush",
        };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon { Glyph = PrivacyReview.Glyph(finding.Level), FontSize = 14, Foreground = _window.ThemeBrush(brush), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = _strings.GetString("QuickView_Share_Kind_" + finding.Kind), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(finding.Detail))
        {
            text.Children.Add(new TextBlock { Text = finding.Detail, FontSize = 12, Foreground = secondary, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }

        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private CheckBox Option(string resource, string id)
    {
        var box = new CheckBox { Content = _strings.GetString(resource), IsChecked = true, MinWidth = 0 };
        AutomationProperties.SetAutomationId(box, id);
        return box;
    }

    private static void Place(Grid grid, UIElement element, int column, int row)
    {
        Grid.SetColumn((FrameworkElement)element, column);
        Grid.SetRow((FrameworkElement)element, row);
        grid.Children.Add(element);
    }
}
