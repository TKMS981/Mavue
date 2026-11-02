using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.Core.Similarity;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.Viewer.Workers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Mavue.QuickView.Host.Similar;

/// <summary>
/// Similar images (SPEC §3.1 "Similar image search", ARCHITECTURE §4.7.14): started only by the user (the button),
/// for the shown image's folder. The work runs in the worker process (<see cref="SimilarSearchSession"/>); this row
/// under the image shows the stages, a cancel button and the results (best first, with their similarity). A result
/// opens inside Quick View (pinned, so Explorer's selection stays where it was). Closing the row, closing Quick View or
/// starting another search ends the worker; the results' thumbnails are released with the row. The settings menu (gear)
/// chooses the sensitivity, subfolders and turned/mirrored copies (kept in the settings, shared with the main
/// application's settings) and deletes the signature cache.
/// </summary>
internal sealed class SimilarPanel : IDisposable
{
    private const int MaxResults = 60;

    private readonly QuickViewWindow _window;
    private readonly ResourceLoader _strings;
    private readonly QuickViewTimeline _timeline;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<PreviewResourcePolicy> _policy;
    private readonly Func<double> _displayScale;
    private readonly QuickViewSettingsStore _settings;
    private Grid? _root;
    private TextBlock? _status;
    private ProgressBar? _progress;
    private StackPanel? _results;
    private Button? _stop;
    private DropDownButton? _options;
    private WorkerSession? _session;
    private string? _target;
    private long _started;
    private int _found;

    public SimilarPanel(QuickViewWindow window, ResourceLoader strings, QuickViewTimeline timeline, DispatcherQueue dispatcher, Func<PreviewResourcePolicy> policy, Func<double> displayScale, QuickViewSettingsStore settings)
    {
        _settings = settings;
        _window = window;
        _strings = strings;
        _timeline = timeline;
        _dispatcher = dispatcher;
        _policy = policy;
        _displayScale = displayScale;
    }

    /// <summary>A result was clicked.</summary>
    public event Action<string>? ItemInvoked;

    public bool IsOpen => _root is not null;

    public bool IsSearching => _session is not null;

    /// <summary>Searches the folder of <paramref name="target"/> (replacing any search still running).</summary>
    public void Start(string target, long requestId)
    {
        StopSession();
        _target = target;
        EnsureRow();
        _results!.Children.Clear();
        _found = 0;
        bool subfolders = _settings.SimilarIncludeSubfolders;
        bool rotations = _settings.SimilarDetectRotations;
        int threshold = SimilarSearchSettings.Threshold(_settings.SimilarSensitivity);
        PreviewResourcePolicy policy = _policy();
        (long memory, int cpu, int parallel, int maxFiles) = policy.Tier switch
        {
            PerformanceTier.Low => (512L << 20, 15, 1, 5_000),
            PerformanceTier.Balanced => (768L << 20, 20, 2, 10_000),
            _ => (1024L << 20, 25, 2, 20_000),
        };
        var request = new SimilarSearchRequest(
            target,
            Path.GetDirectoryName(target)!,
            subfolders,
            maxFiles,
            threshold,
            MaxResults,
            ThumbnailSize: (int)Math.Clamp(Math.Round(88 * _displayScale()), 64, 192),
            CachePath: SimilarSearchSettings.CachePath,
            Parallelism: parallel,
            DetectRotations: rotations);
        var session = new WorkerSession(_dispatcher, memory, cpu);
        session.Line += line =>
        {
            if (ReferenceEquals(_session, session) && SimilarSearchMessage.FromJson(line) is { } message)
            {
                OnMessage(message); // a replaced or cancelled search says nothing more
            }
        };
        session.Ended += code => OnEnded(session, code);
        _session = session;
        _started = QuickViewTimeline.Now;
        SetStatus(_strings.GetString("QuickView_Similar_Listing"), indeterminate: true);
        _stop!.Visibility = Visibility.Visible;
        _timeline.Mark(requestId, "similar-start", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["subfolders"] = subfolders,
            ["rotations"] = rotations,
            ["threshold"] = threshold,
            ["maxFiles"] = maxFiles,
            ["memoryLimitMb"] = memory >> 20,
            ["cpuPercent"] = cpu,
            ["parallel"] = parallel,
        });
        if (!session.Start(SimilarWorker.Argument, System.Text.Json.JsonSerializer.Serialize(request, SimilarSearchJson.Default.SimilarSearchRequest)))
        {
            _session = null;
            session.Dispose();
            SetStatus(_strings.GetString("QuickView_Similar_StartFailed"), indeterminate: false);
            _stop.Visibility = Visibility.Collapsed;
            _timeline.Mark(requestId, "similar-error", QuickViewTimeline.Now, new Dictionary<string, object?> { ["code"] = "start-failed" });
        }
    }

    /// <summary>Stops the search (the results so far stay).</summary>
    public void Cancel()
    {
        if (_session is { } session)
        {
            session.Cancel();
            _timeline.Mark(0, "similar-cancel", QuickViewTimeline.Now);
            SetStatus(_strings.GetString("QuickView_Similar_Cancelled"), indeterminate: false);
            _session = null;
            _stop!.Visibility = Visibility.Collapsed;
        }
    }

    public void Dispose() => Close();

    /// <summary>Closes the row: the worker ends and the thumbnails are released.</summary>
    public void Close()
    {
        StopSession();
        if (_root is not null)
        {
            _results?.Children.Clear();
            _window.ShowSimilar(null);
            _root = null;
            _results = null;
            _status = null;
            _progress = null;
            _stop = null;
            _options = null;
        }
    }

    private void StopSession()
    {
        _session?.Dispose();
        _session = null;
    }

    private void EnsureRow()
    {
        if (_root is not null)
        {
            return;
        }

        _root = new Grid { Padding = new Thickness(12, 6, 12, 8), RowSpacing = 4 };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetAutomationId(_root, "QuickViewSimilar");

        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = _strings.GetString("QuickView_Similar_Title"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        _status = new TextBlock { Foreground = _window.ThemeBrush("QvSecondaryTextBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        AutomationProperties.SetAutomationId(_status, "QuickViewSimilarStatus");
        Grid.SetColumn(_status, 1);
        header.Children.Add(_status);
        _progress = new ProgressBar { VerticalAlignment = VerticalAlignment.Center, Maximum = 1 };
        Grid.SetColumn(_progress, 2);
        header.Children.Add(_progress);
        _options = new DropDownButton
        {
            Content = new FontIcon { Glyph = "\uE713", FontSize = 14 },
            Flyout = OptionsMenu(),
            IsTabStop = false,
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        string optionsText = _strings.GetString("QuickView_Similar_Options");
        AutomationProperties.SetName(_options, optionsText);
        AutomationProperties.SetAutomationId(_options, "QuickViewSimilarOptions");
        ToolTipService.SetToolTip(_options, optionsText);
        Grid.SetColumn(_options, 3);
        header.Children.Add(_options);
        _stop = new Button { Content = _strings.GetString("QuickView_Similar_Stop"), IsTabStop = false, Padding = new Thickness(10, 3, 10, 4) };
        AutomationProperties.SetAutomationId(_stop, "QuickViewSimilarStop");
        _stop.Click += (_, _) =>
        {
            Cancel();
            _window.FocusContent();
        };
        Grid.SetColumn(_stop, 4);
        header.Children.Add(_stop);
        var close = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, IsTabStop = false, Padding = new Thickness(8, 5, 8, 5) };
        string closeText = _strings.GetString("QuickView_Similar_Close");
        AutomationProperties.SetName(close, closeText);
        AutomationProperties.SetAutomationId(close, "QuickViewSimilarClose");
        ToolTipService.SetToolTip(close, closeText);
        close.Click += (_, _) =>
        {
            Close();
            _window.FocusContent();
        };
        Grid.SetColumn(close, 5);
        header.Children.Add(close);
        _root.Children.Add(header);

        _results = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var scroller = new ScrollViewer
        {
            Content = _results,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        AutomationProperties.SetAutomationId(scroller, "QuickViewSimilarResults");
        Grid.SetRow(scroller, 1);
        _root.Children.Add(scroller);
        _window.ShowSimilar(_root);
    }

    private void OnMessage(SimilarSearchMessage message)
    {
        if (_root is null)
        {
            return;
        }

        switch (message.Type)
        {
            case SimilarSearchMessage.ProgressType:
            {
                string format = message.Stage switch
                {
                    SimilarSearchStage.Coarse => "QuickView_Similar_Coarse",
                    SimilarSearchStage.Fine => "QuickView_Similar_Fine",
                    SimilarSearchStage.Thumbnails => "QuickView_Similar_Thumbnails",
                    _ => "QuickView_Similar_Listing",
                };
                int total = Math.Max(1, message.Total ?? 1);
                _status!.Text = string.Format(CultureInfo.CurrentCulture, _strings.GetString(format),
                    (message.Done ?? 0).ToString("N0", CultureInfo.CurrentCulture), total.ToString("N0", CultureInfo.CurrentCulture), (message.Candidates ?? 0).ToString("N0", CultureInfo.CurrentCulture));
                _progress!.IsIndeterminate = message.Stage == SimilarSearchStage.Listing;
                _progress.Value = Math.Clamp((message.Done ?? 0) / (double)total, 0, 1);
                break;
            }

            case SimilarSearchMessage.ResultType when message.Path is { } path && _found < MaxResults:
                _results!.Children.Add(Result(path, message));
                _found++;
                break;
            case SimilarSearchMessage.DoneType:
            {
                double seconds = (QuickViewTimeline.Now - _started) / (double)System.Diagnostics.Stopwatch.Frequency;
                string text = string.Format(
                    CultureInfo.CurrentCulture,
                    _strings.GetString((message.Matches ?? 0) == 0 ? "QuickView_Similar_NoneFound" : "QuickView_Similar_Done"),
                    (message.Matches ?? 0).ToString("N0", CultureInfo.CurrentCulture),
                    (message.Examined ?? 0).ToString("N0", CultureInfo.CurrentCulture),
                    seconds.ToString("0.0", CultureInfo.CurrentCulture));
                if (message.Truncated == true)
                {
                    text += " " + _strings.GetString("QuickView_Similar_Truncated");
                }

                SetStatus(text, indeterminate: false);
                _progress!.Value = 1;
                _stop!.Visibility = Visibility.Collapsed;
                _timeline.Mark(0, "similar-done", QuickViewTimeline.Now, new Dictionary<string, object?>
                {
                    ["examined"] = message.Examined,
                    ["skipped"] = message.Skipped,
                    ["matches"] = message.Matches,
                    ["shown"] = _found,
                    ["cacheHits"] = message.CacheHits,
                    ["workerMs"] = message.Milliseconds,
                    ["truncated"] = message.Truncated,
                    ["ms"] = Math.Round(seconds * 1000, 1),
                });
                break;
            }

            case SimilarSearchMessage.ErrorType:
                SetStatus(_strings.GetString(message.Code == "target-unreadable" ? "QuickView_Similar_TargetUnreadable" : "QuickView_Similar_Failed"), indeterminate: false);
                _stop!.Visibility = Visibility.Collapsed;
                _timeline.Mark(0, "similar-error", QuickViewTimeline.Now, new Dictionary<string, object?> { ["code"] = message.Code });
                break;
        }
    }

    /// <summary>The settings menu: sensitivity, subfolders, turned/mirrored copies, and deleting the cache.</summary>
    private MenuFlyout OptionsMenu()
    {
        var menu = new MenuFlyout();
        var sensitivities = new List<(RadioMenuFlyoutItem Item, SimilarSensitivity Value)>();
        foreach ((SimilarSensitivity value, string resource) in new[]
        {
            (SimilarSensitivity.Loose, "QuickView_Similar_Loose"),
            (SimilarSensitivity.Normal, "QuickView_Similar_Normal"),
            (SimilarSensitivity.Strict, "QuickView_Similar_Strict"),
        })
        {
            var item = new RadioMenuFlyoutItem { Text = _strings.GetString(resource), GroupName = "QvSimilarSensitivity", IsChecked = _settings.SimilarSensitivity == value };
            AutomationProperties.SetAutomationId(item, "QuickViewSimilar" + value);
            // The checked state, not Click: UI Automation's Select (Narrator) checks the item without a click.
            item.RegisterPropertyChangedCallback(RadioMenuFlyoutItem.IsCheckedProperty, (_, _) =>
            {
                if (item.IsChecked && _settings.SimilarSensitivity != value)
                {
                    Change(s => s with { SimilarSensitivity = value }, () => _settings.SimilarSensitivity = value);
                }
            });
            menu.Items.Add(item);
            sensitivities.Add((item, value));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var subfolders = new ToggleMenuFlyoutItem { Text = _strings.GetString("QuickView_Similar_Subfolders"), IsChecked = _settings.SimilarIncludeSubfolders };
        AutomationProperties.SetAutomationId(subfolders, "QuickViewSimilarSubfolders");
        subfolders.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, (_, _) =>
        {
            bool on = subfolders.IsChecked;
            if (_settings.SimilarIncludeSubfolders != on)
            {
                Change(s => s with { SimilarIncludeSubfolders = on }, () => _settings.SimilarIncludeSubfolders = on);
            }
        });
        menu.Items.Add(subfolders);
        var rotations = new ToggleMenuFlyoutItem { Text = _strings.GetString("QuickView_Similar_Rotations"), IsChecked = _settings.SimilarDetectRotations };
        AutomationProperties.SetAutomationId(rotations, "QuickViewSimilarRotations");
        rotations.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, (_, _) =>
        {
            bool on = rotations.IsChecked;
            if (_settings.SimilarDetectRotations != on)
            {
                Change(s => s with { SimilarDetectRotations = on }, () => _settings.SimilarDetectRotations = on);
            }
        });
        menu.Items.Add(rotations);
        menu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem();
        AutomationProperties.SetAutomationId(clear, "QuickViewSimilarClearCache");
        clear.Click += (_, _) => ClearCache();
        menu.Items.Add(clear);
        menu.Opening += (_, _) =>
        {
            // The main app's settings page may have changed them meanwhile (the callbacks see equal values and do nothing).
            foreach ((RadioMenuFlyoutItem item, SimilarSensitivity value) in sensitivities)
            {
                item.IsChecked = _settings.SimilarSensitivity == value;
            }

            subfolders.IsChecked = _settings.SimilarIncludeSubfolders;
            rotations.IsChecked = _settings.SimilarDetectRotations;
            long bytes = SimilarSearchSettings.CacheBytes();
            clear.Text = string.Format(CultureInfo.CurrentCulture, _strings.GetString("QuickView_Similar_ClearCache"), ByteSizeText.Format(bytes));
            clear.IsEnabled = bytes > 0;
        };
        return menu;
    }

    /// <summary>A setting changed: remembered, and the shown image is searched again with it (the cache makes it quick).</summary>
    private void Change(Func<Core.Settings.QuickViewSettings, Core.Settings.QuickViewSettings> change, Action apply)
    {
        apply();
        _settings.Save(change);
        _window.FocusContent();
        if (_target is { } target)
        {
            Start(target, 0);
        }
    }

    /// <summary>Deletes the signature cache (a running search is stopped first; it would save it again).</summary>
    private void ClearCache()
    {
        Cancel();
        long bytes = SimilarSearchSettings.CacheBytes();
        bool deleted = SimilarSearchSettings.DeleteCache();
        if (!deleted)
        {
            // The stopped worker may still be closing the file: once more shortly.
            _ = Task.Delay(800).ContinueWith(_ => _dispatcher.TryEnqueue(() => ReportCleared(bytes, SimilarSearchSettings.DeleteCache())), TaskScheduler.Default);
            return;
        }

        ReportCleared(bytes, true);
    }

    private void ReportCleared(long bytes, bool deleted)
    {
        SetStatus(deleted
            ? string.Format(CultureInfo.CurrentCulture, _strings.GetString("QuickView_Similar_CacheCleared"), ByteSizeText.Format(bytes))
            : _strings.GetString("QuickView_Similar_CacheBusy"), indeterminate: false);
        _timeline.Mark(0, "similar-cache-cleared", QuickViewTimeline.Now, new Dictionary<string, object?> { ["deleted"] = deleted, ["bytes"] = bytes });
        _window.FocusContent();
    }

    private void OnEnded(WorkerSession session, int? code)
    {
        session.Dispose(); // the job and its handles go with the worker
        if (ReferenceEquals(_session, session))
        {
            _session = null;
            if (_stop is not null)
            {
                _stop.Visibility = Visibility.Collapsed;
            }

            if (code is not 0 && _status is not null && _status.Text == _strings.GetString("QuickView_Similar_Listing"))
            {
                SetStatus(_strings.GetString("QuickView_Similar_Failed"), indeterminate: false);
            }
        }

        _timeline.Mark(0, "similar-worker-ended", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["exitCode"] = code,
            ["peakMemoryMb"] = Math.Round(session.PeakMemoryBytes / (1024.0 * 1024), 1), // the whole job, from Windows
        });
    }

    private Button Result(string path, SimilarSearchMessage message)
    {
        var image = new Microsoft.UI.Xaml.Controls.Image { Width = 88, Height = 72, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        if (message.Thumbnail is { } data && message.ThumbnailWidth is > 0 and var width && message.ThumbnailHeight is > 0 and var height)
        {
            try
            {
                byte[] pixels = Convert.FromBase64String(data);
                if (pixels.Length == width * height * 4)
                {
                    var bitmap = new WriteableBitmap(width, height);
                    using (Stream stream = bitmap.PixelBuffer.AsStream())
                    {
                        stream.Write(pixels, 0, pixels.Length);
                    }

                    bitmap.Invalidate();
                    image.Source = bitmap;
                }
            }
            catch (FormatException)
            {
                // No picture: the name and similarity still show.
            }
        }

        var label = new TextBlock
        {
            Text = string.Format(CultureInfo.CurrentCulture, message.Rotation is > 0 || message.Mirrored == true ? "{0}% \u21BB" : "{0}%", message.Similarity ?? 0),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = _window.ThemeBrush("QvSecondaryTextBrush"),
        };
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(image);
        stack.Children.Add(label);
        var button = new Button
        {
            Content = stack,
            Padding = new Thickness(3),
            IsTabStop = false,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        string name = Path.GetFileName(path);
        string tip = string.Format(CultureInfo.CurrentCulture, _strings.GetString("QuickView_Similar_ResultTip"), name, message.Similarity ?? 0, message.Width ?? 0, message.Height ?? 0);
        if (message.Rotation is > 0 || message.Mirrored == true)
        {
            tip += "\n" + string.Format(CultureInfo.CurrentCulture, _strings.GetString(message.Mirrored == true ? "QuickView_Similar_MatchedMirrored" : "QuickView_Similar_MatchedRotated"), message.Rotation ?? 0);
        }
        ToolTipService.SetToolTip(button, tip);
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => ItemInvoked?.Invoke(path);
        return button;
    }

    private void SetStatus(string text, bool indeterminate)
    {
        if (_status is not null)
        {
            _status.Text = text;
        }

        if (_progress is not null)
        {
            _progress.IsIndeterminate = indeterminate;
        }
    }
}
