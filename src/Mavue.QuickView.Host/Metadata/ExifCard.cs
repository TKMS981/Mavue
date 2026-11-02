using System.Globalization;
using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Mavue.Metadata;
using Mavue.QuickView.Diagnostics;
using Mavue.Viewer.Metadata;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Mavue.QuickView.Host.Metadata;

/// <summary>
/// The EXIF card (SPEC §3.1 "EXIF inspector", ARCHITECTURE §4.7.13): the shot at a glance over the top-right corner of
/// the image — camera, lens, settings, time, and hints when the picture carries a location, people's names or device
/// serial numbers. "Details" opens the full list; "Check before sharing…" opens the share check. Its role is separate
/// from the pixel inspector (bottom corner, follows the pointer, pixel values): this card is about the file. Off by
/// default (E or the button; the choice is kept). Read only for the shown image, after it is on screen, off the UI
/// thread; nothing is read while the card is off. Values are shown only, never logged.
/// </summary>
internal sealed class ExifCard : IDisposable
{
    private const double CardWidth = 320;

    private readonly QuickViewWindow _window;
    private readonly ResourceLoader _strings;
    private readonly QuickViewTimeline _timeline;
    private readonly Func<long> _currentRequest;
    private CancellationTokenSource? _reading;
    private bool _enabled;
    private bool _expanded;
    private string? _path;
    private FileFacts? _facts;

    public ExifCard(QuickViewWindow window, ResourceLoader strings, QuickViewTimeline timeline, Func<long> currentRequest)
    {
        _window = window;
        _strings = strings;
        _timeline = timeline;
        _currentRequest = currentRequest;
        window.ThemeChanged += () =>
        {
            if (_enabled && _path is not null && _facts is not null)
            {
                OnItemShown(_path, _facts);
            }
        };
    }

    /// <summary>"Check before sharing…" in the card was clicked.</summary>
    public event Action? ShareCheckRequested;

    public bool Enabled => _enabled;

    public void SetEnabled(bool on)
    {
        _enabled = on;
        _window.SetExifOn(on);
        if (!on)
        {
            Clear();
        }
        else if (_path is not null && _facts is not null)
        {
            OnItemShown(_path, _facts);
        }
    }

    /// <summary>Another item is about to be shown: the card of the previous one goes at once.</summary>
    public void OnItemChanged()
    {
        _path = null;
        _facts = null;
        Clear();
    }

    /// <summary>The item is on screen: read its metadata when the card is on and it is an image.</summary>
    public void OnItemShown(string path, FileFacts facts)
    {
        _path = path;
        _facts = facts;
        if (!_enabled || !IsImage(facts))
        {
            Clear();
            return;
        }

        _reading?.Cancel();
        _reading?.Dispose();
        _reading = new CancellationTokenSource();
        _ = ReadAsync(path, facts, _currentRequest(), _reading.Token);
    }

    public void Dispose() => Clear();

    public void OnHidden()
    {
        _path = null;
        _facts = null;
        Clear();
    }

    /// <summary>The tools (EXIF, share check, similar images) apply to raster images that are local files.</summary>
    public static bool IsImage(FileFacts facts) =>
        facts.Access == PreviewAccess.Allowed && FileFormatKinds.IsImage(facts.Format) && facts.Format != FileFormat.Svg;

    private void Clear()
    {
        _reading?.Cancel();
        _reading?.Dispose();
        _reading = null;
        _window.ShowExif(null);
    }

    private async Task ReadAsync(string path, FileFacts facts, long requestId, CancellationToken cancellation)
    {
        long start = QuickViewTimeline.Now;
        ImageMetadata metadata;
        IReadOnlyList<FileInfoItem>? windows = null;
        try
        {
            metadata = await Task.Run(() => ImageMetadataReader.Read(path), cancellation);
            if (metadata.Container == MetadataContainer.Unsupported || metadata.Exif is null && metadata.Container == MetadataContainer.Tiff)
            {
                // HEIF, AVIF, JPEG XL, CR3…: what Windows knows (the same reading as the main app's information pane).
                windows = await FileInformation.ReadAsync(path, cancellation);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            metadata = ImageMetadata.Unsupported;
        }

        if (cancellation.IsCancellationRequested || !_enabled || _path != path)
        {
            return;
        }

        _window.ShowExif(Build(metadata, windows));
        _timeline.Mark(requestId, "exif-card", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["container"] = metadata.Container.ToString(),
            ["exif"] = metadata.Exif is not null,
            ["windows"] = windows is not null,
            ["ms"] = Math.Round((QuickViewTimeline.Now - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 1),
        });
    }

    private Border Build(ImageMetadata metadata, IReadOnlyList<FileInfoItem>? windows)
    {
        Brush text = _window.ThemeBrush("QvOverlayTextBrush");
        Brush secondary = _window.ThemeBrush("QvSecondaryTextBrush");
        var panel = new StackPanel { Spacing = 3 };
        ExifInfo? exif = metadata.Exif;
        CultureInfo culture = CultureInfo.CurrentCulture;

        string? camera = exif?.Camera ?? Value(windows, FileInfoField.Camera);
        string? lens = exif?.LensModel ?? Value(windows, FileInfoField.Lens);
        string? settings = ExifText.Settings(exif) ?? Join(Value(windows, FileInfoField.Aperture), Value(windows, FileInfoField.Exposure), Value(windows, FileInfoField.Iso), Value(windows, FileInfoField.FocalLength));
        string? taken = ExifText.Taken(exif, culture) ?? Value(windows, FileInfoField.DateTaken);

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock
        {
            Text = camera ?? _strings.GetString("QuickView_Exif_NoCamera"),
            FontWeight = FontWeights.SemiBold,
            Foreground = camera is null ? secondary : text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = CardWidth - 60,
        });
        panel.Children.Add(title);
        AddLine(panel, lens, secondary, 12);
        AddLine(panel, settings, text, 13, mono: true);
        AddLine(panel, taken, secondary, 12);

        // What the file would tell others (details in the share check).
        IReadOnlyList<PrivacyFinding> findings = PrivacyCheck.Review(metadata);
        bool location = findings.Any(f => f.Kind == PrivacyKind.Location) || Value(windows, FileInfoField.Location) is not null;
        bool person = findings.Any(f => f.Kind is PrivacyKind.AuthorCopyright or PrivacyKind.OwnerName);
        bool device = findings.Any(f => f.Kind == PrivacyKind.DeviceIdentity);
        if (location || person || device)
        {
            var hints = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 4, 0, 0) };
            if (location)
            {
                hints.Children.Add(Hint("", _strings.GetString("QuickView_Exif_HasLocation")));
            }

            if (person)
            {
                hints.Children.Add(Hint("", _strings.GetString("QuickView_Exif_HasPerson")));
            }

            if (device)
            {
                hints.Children.Add(Hint("", _strings.GetString("QuickView_Exif_HasSerial")));
            }

            panel.Children.Add(hints);
        }

        // Details (all fields) on request.
        StackPanel details = Details(metadata, windows, text, secondary);
        var scroller = new ScrollViewer { Content = details, MaxHeight = 360, Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(scroller);

        var actions = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var more = new HyperlinkButton
        {
            Content = _strings.GetString(_expanded ? "QuickView_Exif_Less" : "QuickView_Exif_More"),
            Padding = new Thickness(0),
            FontSize = 12,
            IsTabStop = false,
        };
        AutomationProperties.SetAutomationId(more, "QuickViewExifDetails");
        more.Click += (_, _) =>
        {
            _expanded = !_expanded;
            scroller.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
            more.Content = _strings.GetString(_expanded ? "QuickView_Exif_Less" : "QuickView_Exif_More");
            _window.FocusContent(); // Space and Esc stay with Quick View
        };
        actions.Children.Add(more);
        var share = new HyperlinkButton
        {
            Content = _strings.GetString("QuickView_Exif_ShareCheck"),
            Padding = new Thickness(0),
            FontSize = 12,
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetAutomationId(share, "QuickViewExifShareCheck");
        share.Click += (_, _) => ShareCheckRequested?.Invoke();
        Grid.SetColumn(share, 1);
        actions.Children.Add(share);
        panel.Children.Add(actions);

        var card = new Border
        {
            Width = CardWidth,
            Padding = new Thickness(12, 10, 12, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = _window.ThemeBrush("QvOverlayBrush"),
            BorderBrush = _window.ThemeBrush("QvOverlayBorderBrush"),
            Child = panel,
        };
        AutomationProperties.SetAutomationId(card, "QuickViewExifCard");
        AutomationProperties.SetName(card, _strings.GetString("QuickView_Exif"));
        return card;
    }

    private StackPanel Details(ImageMetadata metadata, IReadOnlyList<FileInfoItem>? windows, Brush text, Brush secondary)
    {
        var rows = new List<(string Label, string? Value)>();
        ExifInfo? exif = metadata.Exif;
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (exif is not null)
        {
            rows.Add(("Taken", ExifText.Taken(exif, culture)));
            rows.Add(("Camera", exif.Camera));
            rows.Add(("Lens", Join(exif.LensModel, exif.LensMake is { } maker && exif.LensModel?.Contains(maker, StringComparison.OrdinalIgnoreCase) != true ? maker : null)));
            rows.Add(("Aperture", ExifText.FNumber(exif.FNumber)));
            rows.Add(("Exposure", ExifText.Exposure(exif.ExposureTime)));
            rows.Add(("Iso", ExifText.Iso(exif.Iso)));
            rows.Add(("FocalLength", ExifText.FocalLength(exif.FocalLength, exif.FocalLength35mm)));
            rows.Add(("Bias", ExifText.ExposureBias(exif.ExposureBias)));
            rows.Add(("Flash", ExifText.FlashFired(exif.Flash) is { } fired ? _strings.GetString(fired ? "QuickView_Exif_FlashOn" : "QuickView_Exif_FlashOff") : null));
            rows.Add(("WhiteBalance", exif.WhiteBalance is { } wb ? _strings.GetString(wb == 1 ? "QuickView_Exif_WbManual" : "QuickView_Exif_WbAuto") : null));
            rows.Add(("Pixels", exif.PixelWidth is > 0 && exif.PixelHeight is > 0 ? string.Create(culture, $"{exif.PixelWidth} × {exif.PixelHeight}") : null));
            rows.Add(("Orientation", exif.Orientation is > 1 and <= 8 ? exif.Orientation.Value.ToString(culture) : null));
            rows.Add(("Location", ExifText.Position(exif.Gps)));
            rows.Add(("Software", exif.Software));
            rows.Add(("Author", Join(exif.Artist, exif.XpAuthor)));
            rows.Add(("Copyright", exif.Copyright));
            rows.Add(("Description", Join(exif.Description, exif.XpTitle, exif.XpComment, exif.UserComment)));
            rows.Add(("Serial", Join(exif.BodySerialNumber, exif.LensSerialNumber)));
            rows.Add(("Owner", exif.OwnerName));
        }
        else if (windows is not null)
        {
            foreach (FileInfoItem item in windows)
            {
                string? key = item.Field switch
                {
                    FileInfoField.DateTaken => "Taken",
                    FileInfoField.Camera => "Camera",
                    FileInfoField.Lens => "Lens",
                    FileInfoField.Aperture => "Aperture",
                    FileInfoField.Exposure => "Exposure",
                    FileInfoField.Iso => "Iso",
                    FileInfoField.FocalLength => "FocalLength",
                    FileInfoField.Dimensions => "Pixels",
                    FileInfoField.Location => "Location",
                    FileInfoField.Authors => "Author",
                    FileInfoField.Copyright => "Copyright",
                    FileInfoField.Title or FileInfoField.Comment => "Description",
                    _ => null,
                };
                if (key is not null)
                {
                    rows.Add((key, item.Value));
                }
            }
        }

        // Which metadata blocks the file has (sizes), e.g. "EXIF 12 KB · XMP 4 KB · ICC 3 KB".
        string blocks = string.Join(" · ", metadata.Blocks.GroupBy(b => b.Kind).Select(g => $"{BlockName(g.Key)} {ByteSizeText.Format(g.Sum(b => b.Length))}"));
        rows.Add(("Blocks", blocks.Length == 0 ? null : blocks));

        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        foreach ((string label, string? value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = _strings.GetString("QuickView_Exif_" + label), Foreground = secondary, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            var content = new TextBlock { Text = value, Foreground = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(name, row);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            grid.Children.Add(name);
            grid.Children.Add(content);
            row++;
        }

        var panel = new StackPanel();
        if (row == 0)
        {
            panel.Children.Add(new TextBlock { Text = _strings.GetString("QuickView_Exif_None"), Foreground = secondary, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }

        panel.Children.Add(grid);
        return panel;
    }

    private static string BlockName(MetadataBlockKind kind) => kind switch
    {
        MetadataBlockKind.Exif => "EXIF",
        MetadataBlockKind.Xmp => "XMP",
        MetadataBlockKind.Iptc => "IPTC",
        MetadataBlockKind.IccProfile => "ICC",
        MetadataBlockKind.Comment => "COM",
        MetadataBlockKind.Text => "tEXt",
        MetadataBlockKind.Time => "tIME",
        MetadataBlockKind.EmbeddedImage => "IMG",
        MetadataBlockKind.Trailer => "+DATA",
        _ => "APP",
    };

    private StackPanel Hint(string glyph, string label)
    {
        var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        Brush warning = _window.ThemeBrush("QvSyntaxWarningBrush");
        hint.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Foreground = warning });
        hint.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = warning });
        return hint;
    }

    private static void AddLine(StackPanel panel, string? value, Brush brush, double size, bool mono = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var block = new TextBlock { Text = value, Foreground = brush, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
        if (mono)
        {
            block.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        }

        panel.Children.Add(block);
    }

    private static string? Value(IReadOnlyList<FileInfoItem>? items, FileInfoField field) => items?.FirstOrDefault(i => i.Field == field)?.Value;

    private static string? Join(params string?[] parts)
    {
        string[] present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        return present.Length == 0 ? null : string.Join(" · ", present);
    }
}
