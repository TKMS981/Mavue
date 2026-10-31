using System.Globalization;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Shell;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// Universal Preview (docs/ARCHITECTURE.md §4.7.8): every file shows something at once. Files no other content can
/// show — folders, missing items, cloud-only media, and files that turned out not to be images — get an information
/// card: the type's icon (or Windows' cached thumbnail), name, type, size, dates, MIME type, attributes, location and
/// Mark-of-the-Web, plus the reason when there is one. Only the file system is read (never the file's contents);
/// nothing is launched or executed. A folder's contents are counted afterwards, bounded and cancellable.
/// </summary>
internal sealed class UniversalContent : IPreviewContent
{
    /// <summary>A folder's entries are counted up to this many ("more than …").</summary>
    private const int MaxCountedEntries = 10_000;

    private readonly PreviewServices _s;
    private readonly IPreviewHost _host;
    private CancellationTokenSource? _work;
    private UIElement? _card;

    public UniversalContent(PreviewServices services, IPreviewHost host)
    {
        _s = services;
        _host = host;
    }

    public PreviewContentKind Kind => PreviewContentKind.Universal;

    /// <summary>The icon glyph (Segoe Fluent Icons) of a file category (also used by the archive listing).</summary>
    internal static string IconGlyph(FileKindCategory category) => InfoCard.GlyphFor(category);

    public Task ShowAsync(PreviewItem item, CancellationToken cancellation)
    {
        string? message = item.Facts.Access switch
        {
            PreviewAccess.CloudPlaceholder => "QuickView_Error_CloudPlaceholder", // playing a cloud placeholder would download it
            _ => null,
        };
        return ShowCardAsync(item, message, cancellation);
    }

    /// <summary>The card for <paramref name="item"/>, with an optional message (a string resource).</summary>
    public async Task ShowCardAsync(PreviewItem item, string? messageResource, CancellationToken cancellation)
    {
        CancelWork();
        string? message = messageResource is null ? null : _s.Strings.GetString(messageResource);

        // The message (if any) is on screen at once, in place of the previous item's picture.
        _s.Window.Surface.ShowStatusOnly(message ?? string.Empty);
        if (message is not null)
        {
            _host.Display.MarkMessage(item.RequestId); // no late thumbnail or "loading" replaces it
        }

        _s.ImageView.ReleaseDisplayedBitmap();
        _s.ImageView.RecordShown(SurfaceImageKind.None, default, 0, _host.DisplayScale());

        (UniversalFileInfo info, string? typeName, string? mime) = await Task.Run(
            () =>
            {
                UniversalFileInfo read = UniversalFileInfo.Read(item.Path, item.Facts.Format);
                string? type = read.IsFolder ? null : NativeMethods.AssociationString(read.Extension, NativeMethods.ASSOCSTR_FRIENDLYDOCNAME);
                string? contentType = read.MimeType ?? (read.IsFolder ? null : NativeMethods.AssociationString(read.Extension, NativeMethods.ASSOCSTR_CONTENTTYPE));
                return (read, type, contentType);
            },
            cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (!info.Exists && message is null)
        {
            message = _s.Strings.GetString("QuickView_Card_NotFound");
        }

        var card = new InfoCard(_s.Strings, info, typeName, mime, message);
        _s.Window.Surface.ShowStatusOnly(string.Empty); // the card carries the message now
        _s.Window.ShowCard(card.Root);
        _card = card.Root;
        if (message is null)
        {
            _host.Display.MarkContent(item.RequestId);
        }
        else
        {
            _host.Display.MarkMessage(item.RequestId);
        }

        _host.SetInfo(string.Join(" · ", new[] { info.IsFolder ? _s.Strings.GetString("QuickView_Card_Folder") : typeName, info.Length is { } l ? ByteSizeText.Format(l) : null }.Where(s => !string.IsNullOrEmpty(s))));
        _s.Timeline.Mark(item.RequestId, "info-card", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["category"] = info.Category.ToString(),
            ["exists"] = info.Exists,
            ["message"] = messageResource,
            ["motw"] = info.Origin?.ZoneId,
        });

        // Afterwards, off the UI thread: Windows' cached thumbnail (never extracted) and a folder's entry count.
        var work = new CancellationTokenSource();
        _work = work;
        CancellationToken token = work.Token;
        if (info.Exists)
        {
            _ = LoadThumbnailAsync(card, item.Path, token);
        }

        if (info.IsFolder)
        {
            _ = CountAsync(card, item.Path, token);
        }
    }

    public void Reset(ContentReset scope, string reason)
    {
        CancelWork();
        if (_card is not null && ReferenceEquals(_s.Window.CardContent, _card))
        {
            _s.Window.ShowCard(null); // another content's card (an archive listing) is its own business
        }

        _card = null;
    }

    /// <summary>The card lays itself out.</summary>
    public AreaResponse OnAreaChanged() => AreaResponse.Handled;

    public void Dispose() => CancelWork();

    private void CancelWork()
    {
        _work?.Cancel();
        _work?.Dispose();
        _work = null;
    }

    private async Task LoadThumbnailAsync(InfoCard card, string path, CancellationToken cancellation)
    {
        try
        {
            BgraImage? thumbnail = await _s.ShellThread.InvokeAsync(() => ShellThumbnail.TryGetCached(path, 256));
            if (thumbnail is not null && !cancellation.IsCancellationRequested)
            {
                card.SetThumbnail(thumbnail);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OverflowException or ArgumentException)
        {
            // Keep the type icon.
        }
    }

    private static async Task CountAsync(InfoCard card, string folder, CancellationToken cancellation)
    {
        try
        {
            int? count = await Task.Run(() => UniversalFileInfo.CountEntries(folder, MaxCountedEntries, cancellation), cancellation);
            if (!cancellation.IsCancellationRequested)
            {
                card.SetItemCount(count, MaxCountedEntries);
            }
        }
        catch (OperationCanceledException)
        {
            // Another item.
        }
    }

    /// <summary>The card's elements (built in code; one per shown item).</summary>
    private sealed class InfoCard
    {
        private readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader _strings;
        private readonly Grid _iconHost;
        private TextBlock? _itemsValue;

        public InfoCard(Microsoft.Windows.ApplicationModel.Resources.ResourceLoader strings, UniversalFileInfo info, string? typeName, string? mime, string? message)
        {
            _strings = strings;
            var panel = new StackPanel { Spacing = 14, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
            _iconHost = new Grid { Width = 128, Height = 128, HorizontalAlignment = HorizontalAlignment.Center };
            _iconHost.Children.Add(new FontIcon { Glyph = GlyphFor(info.Category), FontSize = 88, FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"), Opacity = 0.85 });
            panel.Children.Add(_iconHost);

            var name = new TextBlock { Text = info.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            name.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.HeadingLevelProperty, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
            panel.Children.Add(name);
            if (message is not null)
            {
                panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.9 });
            }

            var rows = new Grid { ColumnSpacing = 16, RowSpacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (info.Exists)
            {
                AddRow(rows, "QuickView_Card_Type", info.IsFolder ? strings.GetString("QuickView_Card_Folder") : typeName ?? info.Extension.TrimStart('.').ToUpperInvariant());
                if (info.Length is { } length)
                {
                    AddRow(rows, "QuickView_Card_Size", string.Format(CultureInfo.CurrentCulture, strings.GetString("QuickView_Card_SizeValue"), ByteSizeText.Format(length), length.ToString("N0", CultureInfo.CurrentCulture)));
                }

                if (info.IsFolder)
                {
                    _itemsValue = AddRow(rows, "QuickView_Card_Items", strings.GetString("QuickView_Card_ItemsCounting"));
                }

                AddRow(rows, "QuickView_Card_Modified", Date(info.ModifiedUtc));
                AddRow(rows, "QuickView_Card_Created", Date(info.CreatedUtc));
                AddRow(rows, "QuickView_Card_Mime", mime);
                AddRow(rows, "QuickView_Card_Attributes", Attributes(info.Attributes));
                if (info.Origin is { } origin)
                {
                    AddRow(rows, "QuickView_Card_Origin", origin.IsFromInternet
                        ? origin.SourceHost is { } host
                            ? string.Format(CultureInfo.CurrentCulture, strings.GetString("QuickView_Card_FromInternet"), host)
                            : strings.GetString("QuickView_Card_FromInternetNoHost")
                        : string.Format(CultureInfo.CurrentCulture, strings.GetString("QuickView_Card_Zone"), origin.ZoneId));
                }
            }

            panel.Children.Add(rows);
            Root = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsTabStop = false };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(Root, "QuickViewInfoCard");
        }

        public FrameworkElement Root { get; }

        public void SetThumbnail(BgraImage thumbnail)
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(thumbnail.Width, thumbnail.Height);
            using (Stream stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bitmap.PixelBuffer))
            {
                stream.Write(thumbnail.Pixels, 0, thumbnail.Pixels.Length);
            }

            bitmap.Invalidate();
            _iconHost.Children.Clear();
            _iconHost.Children.Add(new Microsoft.UI.Xaml.Controls.Image { Source = bitmap, Stretch = Stretch.Uniform });
        }

        public void SetItemCount(int? count, int limit)
        {
            if (_itemsValue is null)
            {
                return;
            }

            _itemsValue.Text = count is not { } n
                ? "—"
                : n >= limit
                    ? string.Format(CultureInfo.CurrentCulture, _strings.GetString("QuickView_Card_ItemsMore"), limit.ToString("N0", CultureInfo.CurrentCulture))
                    : string.Format(CultureInfo.CurrentCulture, _strings.GetString("QuickView_Card_ItemsCount"), n.ToString("N0", CultureInfo.CurrentCulture));

            // Screen readers read the row's name, which was set while counting.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_itemsValue, _strings.GetString("QuickView_Card_Items") + ": " + _itemsValue.Text);
        }

        private TextBlock? AddRow(Grid rows, string labelResource, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            int row = rows.RowDefinitions.Count;
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = _strings.GetString(labelResource), Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right };
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(text, label.Text + ": " + value);
            Grid.SetRow(label, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            rows.Children.Add(label);
            rows.Children.Add(text);
            return text;
        }

        private string? Attributes(FileAttributes attributes)
        {
            var parts = new List<string>();
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                parts.Add(_strings.GetString("QuickView_Attr_ReadOnly"));
            }

            if (attributes.HasFlag(FileAttributes.Hidden))
            {
                parts.Add(_strings.GetString("QuickView_Attr_Hidden"));
            }

            if (attributes.HasFlag(FileAttributes.System))
            {
                parts.Add(_strings.GetString("QuickView_Attr_System"));
            }

            if (attributes.HasFlag(FileAttributes.Encrypted))
            {
                parts.Add(_strings.GetString("QuickView_Attr_Encrypted"));
            }

            if (attributes.HasFlag(FileAttributes.Offline) || ((int)attributes & 0x00400000) != 0) // RECALL_ON_DATA_ACCESS
            {
                parts.Add(_strings.GetString("QuickView_Attr_Online"));
            }

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        private static string? Date(DateTime? utc) => utc is { } d ? d.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : null;

        internal static string GlyphFor(FileKindCategory category) => category switch
        {
            FileKindCategory.Folder => "",
            FileKindCategory.Image => "",
            FileKindCategory.Video => "",
            FileKindCategory.Audio => "",
            FileKindCategory.Pdf => "",
            FileKindCategory.Document => "",
            FileKindCategory.Spreadsheet => "",
            FileKindCategory.Presentation => "",
            FileKindCategory.Text => "",
            FileKindCategory.Code => "",
            FileKindCategory.Archive => "",
            FileKindCategory.Application => "",
            FileKindCategory.Shortcut => "",
            FileKindCategory.Font => "",
            FileKindCategory.DiskImage => "",
            _ => "",
        };
    }
}
