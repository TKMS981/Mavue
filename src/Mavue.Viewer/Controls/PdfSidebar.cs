using System.Globalization;
using System.Runtime.InteropServices;
using Mavue.Pdf;
using Mavue.Viewer.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using XamlImage = Microsoft.UI.Xaml.Controls.Image;

namespace Mavue.Viewer.Controls;

/// <summary>
/// The sidebar of a PDF (shared by the main window and Quick View): page thumbnails, the outline (table of contents /
/// bookmarks) and the search results, on three tabs. It follows its <see cref="Session"/>: a new document refreshes the
/// thumbnails and the outline, the current page stays selected, and new search results fill the results tab.
/// <para>
/// Thumbnails are drawn only for items scrolled into view, in the background; at most <see cref="ThumbnailCacheLimit"/>
/// are kept. Items navigate on click only — selection notifications of a list that also follows the current page
/// arrive late and turned the page back (measured).
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A control lives as long as its window; the token source only cancels filling the results list.")]
public sealed partial class PdfSidebar : UserControl
{
    /// <summary>Page thumbnails kept (the farthest from the page in view are forgotten first).</summary>
    public const int ThumbnailCacheLimit = 240;

    private const string ThumbnailTemplate =
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
        "<StackPanel Padding=\"0,6\" Spacing=\"4\" HorizontalAlignment=\"Center\">" +
        "<Border Width=\"128\" Height=\"160\" Background=\"#14808080\"><Image Stretch=\"Uniform\" /></Border>" +
        "<TextBlock HorizontalAlignment=\"Center\" FontSize=\"12\" />" +
        "</StackPanel></DataTemplate>";

    private readonly Func<string, string> _text;
    private readonly SelectorBar _tabs;
    private readonly SelectorBarItem _pagesTab;
    private readonly SelectorBarItem _outlineTab;
    private readonly SelectorBarItem _resultsTab;
    private readonly ListView _thumbnails;
    private readonly Grid _outlinePane;
    private readonly TreeView _outline;
    private readonly TextBlock _outlineEmpty;
    private readonly ListView _results;
    private readonly Dictionary<int, ImageSource> _thumbnailCache = [];
    private readonly Dictionary<TreeViewNode, PdfDestination> _outlineTargets = [];
    private PdfSession? _session;
    private int _documentVersion;
    private CancellationTokenSource? _resultsFill;

    /// <param name="text">Texts for <see cref="PdfUiText"/> keys.</param>
    public PdfSidebar(Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
        _pagesTab = new SelectorBarItem { Text = text(PdfUiText.TabPages), IsSelected = true };
        _outlineTab = new SelectorBarItem { Text = text(PdfUiText.TabOutline) };
        _resultsTab = new SelectorBarItem { Text = text(PdfUiText.TabResults) };
        _tabs = new SelectorBar();
        _tabs.Items.Add(_pagesTab);
        _tabs.Items.Add(_outlineTab);
        _tabs.Items.Add(_resultsTab);
        _tabs.SelectionChanged += (_, _) => ShowSelectedTab();

        _thumbnails = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            Padding = new Thickness(0, 4, 0, 4),
            ItemTemplate = (DataTemplate)XamlReader.Load(ThumbnailTemplate),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_thumbnails, "ThumbnailList");
        _thumbnails.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is int page)
            {
                _session?.GoToPage(page);
            }
        };
        _thumbnails.ContainerContentChanging += OnThumbnailContainerContentChanging;

        _outline = new TreeView { SelectionMode = TreeViewSelectionMode.None };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_outline, "OutlineTree");
        _outline.ItemInvoked += OnOutlineItemInvoked;
        _outlineEmpty = new TextBlock { Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Text = text(PdfUiText.NoOutline), Visibility = Visibility.Collapsed };
        _outlinePane = new Grid { Visibility = Visibility.Collapsed };
        _outlinePane.Children.Add(_outline);
        _outlinePane.Children.Add(_outlineEmpty);

        _results = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_results, "ResultsList");
        _results.ItemClick += (_, e) =>
        {
            int index = _results.Items.IndexOf(e.ClickedItem);
            if (_session is { } session && index >= 0 && index < session.Matches.Count)
            {
                _ = session.ShowMatchAsync(index);
            }
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(_tabs);
        foreach (FrameworkElement pane in new FrameworkElement[] { _thumbnails, _outlinePane, _results })
        {
            Grid.SetRow(pane, 1);
            root.Children.Add(pane);
        }

        Content = root;
    }

    /// <summary>Diagnostics (tests): "thumbnails", "outline", "outline-invoked".</summary>
    public PlaybackTrace? Trace { get; set; }

    /// <summary>The session shown (set by the host once).</summary>
    public PdfSession? Session
    {
        get => _session;
        set
        {
            if (_session is { } old)
            {
                old.Opened -= OnOpened;
                old.Closed -= OnClosed;
                old.ViewChanged -= SyncCurrentPage;
                old.SearchChanged -= OnSearchChanged;
            }

            _session = value;
            if (value is not null)
            {
                value.Opened += OnOpened;
                value.Closed += OnClosed;
                value.ViewChanged += SyncCurrentPage;
                value.SearchChanged += OnSearchChanged;
            }
        }
    }

    private void ShowSelectedTab()
    {
        SelectorBarItem? selected = _tabs.SelectedItem;
        _thumbnails.Visibility = selected == _pagesTab ? Visibility.Visible : Visibility.Collapsed;
        _outlinePane.Visibility = selected == _outlineTab ? Visibility.Visible : Visibility.Collapsed;
        _results.Visibility = selected == _resultsTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpened()
    {
        if (_session is not { } session)
        {
            return;
        }

        int version = ++_documentVersion;
        ResetThumbnails();
        _thumbnails.ItemsSource = Enumerable.Range(0, session.PageCount).Cast<object>().ToList();
        Trace?.Invoke("thumbnails", new Dictionary<string, object?> { ["count"] = session.PageCount });
        ResetResults();
        _ = LoadOutlineAsync(session, version);
        SyncCurrentPage();
    }

    private void OnClosed()
    {
        _documentVersion++;
        ResetThumbnails();
        ResetOutline();
        ResetResults();
    }

    private void SyncCurrentPage()
    {
        if (_session is not { IsOpen: true } session || _thumbnails.Items.Count != session.PageCount || _thumbnails.SelectedIndex == session.CurrentPage)
        {
            return;
        }

        _thumbnails.SelectedIndex = session.CurrentPage;
        if (_thumbnails.Visibility == Visibility.Visible && Visibility == Visibility.Visible)
        {
            _thumbnails.ScrollIntoView(_thumbnails.SelectedItem);
        }
    }

    private void ResetThumbnails()
    {
        _thumbnails.ItemsSource = null;

        // Only forgotten, never disposed while an item may still show them: drawing a disposed source crashes XAML
        // (RO_E_CLOSED, measured after jumping across a 600-page document). The collector frees them.
        _thumbnailCache.Clear();
    }

    private void OnThumbnailContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not StackPanel { Children: [Border { Child: XamlImage image }, TextBlock label] } || args.Item is not int page)
        {
            return;
        }

        (args.ItemContainer.Tag as CancellationTokenSource)?.Cancel();
        if (args.InRecycleQueue)
        {
            image.Source = null;
            return;
        }

        label.Text = (page + 1).ToString(CultureInfo.CurrentCulture);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, string.Format(CultureInfo.CurrentCulture, _text(PdfUiText.PageItem), page + 1));
        if (_thumbnailCache.TryGetValue(page, out ImageSource? cached))
        {
            image.Source = cached;
            return;
        }

        image.Source = null;
        var cancellation = new CancellationTokenSource();
        args.ItemContainer.Tag = cancellation;
        _ = LoadThumbnailAsync(page, image, _documentVersion, cancellation.Token);
    }

    private async Task LoadThumbnailAsync(int page, XamlImage image, int version, CancellationToken cancellation)
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            await Task.Delay(30, cancellation); // skip items that only fly past while scrolling
            double scale = XamlRoot?.RasterizationScale ?? 1;

            // Not disposed: the source may still draw from it (thumbnails are small; the collector frees them).
            SoftwareBitmap? bitmap = await session.RenderThumbnailAsync(page, (uint)(128 * scale), (uint)(160 * scale), cancellation);
            if (bitmap is null || cancellation.IsCancellationRequested || version != _documentVersion)
            {
                return;
            }

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            if (cancellation.IsCancellationRequested || version != _documentVersion)
            {
                return;
            }

            if (_thumbnailCache.Count >= ThumbnailCacheLimit)
            {
                foreach (int far in _thumbnailCache.Keys.OrderByDescending(p => Math.Abs(p - page)).Take(_thumbnailCache.Count - ThumbnailCacheLimit + 1).ToList())
                {
                    _thumbnailCache.Remove(far);
                }
            }

            _thumbnailCache[page] = source;
            image.Source = source;
        }
        catch (OperationCanceledException)
        {
            // Recycled or another document.
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or ArgumentException)
        {
            Trace?.Invoke("thumbnail-error", new Dictionary<string, object?> { ["page"] = page, ["type"] = ex.GetType().Name });
        }
    }

    private async Task LoadOutlineAsync(PdfSession session, int version)
    {
        ResetOutline();
        IReadOnlyList<PdfOutlineItem> outline = await session.GetOutlineAsync();
        if (version != _documentVersion)
        {
            return;
        }

        ResetOutline();
        AddOutlineNodes(_outline.RootNodes, outline, depth: 0);
        _outlineEmpty.Visibility = outline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Trace?.Invoke("outline", new Dictionary<string, object?> { ["items"] = outline.Count, ["titles"] = string.Join('|', outline.Take(5).Select(o => o.Title)) });
    }

    private void AddOutlineNodes(IList<TreeViewNode> target, IReadOnlyList<PdfOutlineItem> items, int depth)
    {
        foreach (PdfOutlineItem item in items)
        {
            var node = new TreeViewNode { Content = item.Title, IsExpanded = depth == 0 && items.Count < 30 };
            if (item.Destination is { } destination)
            {
                _outlineTargets[node] = destination;
            }

            AddOutlineNodes(node.Children, item.Children, depth + 1);
            target.Add(node);
        }
    }

    private void ResetOutline()
    {
        _outline.RootNodes.Clear();
        _outlineTargets.Clear();
        _outlineEmpty.Visibility = Visibility.Collapsed;
    }

    private void OnOutlineItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node && _outlineTargets.TryGetValue(node, out PdfDestination? destination) && _session is { } session)
        {
            Trace?.Invoke("outline-invoked", new Dictionary<string, object?> { ["title"] = node.Content as string, ["page"] = destination.PageIndex });
            _ = session.GoToDestinationAsync(destination);
        }
    }

    /// <summary>New results fill the results tab (and show it when the sidebar is open); a step selects the current one.</summary>
    private void OnSearchChanged()
    {
        if (_session is not { } session)
        {
            return;
        }

        if (_results.Items.Count != session.Matches.Count || session.Matches.Count == 0)
        {
            _ = FillResultsAsync(session);
            return;
        }

        if (session.CurrentMatch >= 0 && _results.SelectedIndex != session.CurrentMatch)
        {
            _results.SelectedIndex = session.CurrentMatch;
            _results.ScrollIntoView(_results.SelectedItem);
        }
    }

    private async Task FillResultsAsync(PdfSession session)
    {
        _resultsFill?.Cancel();
        _resultsFill = new CancellationTokenSource();
        CancellationToken cancellation = _resultsFill.Token;
        _results.Items.Clear();
        IReadOnlyList<PdfTextMatch> matches = session.Matches;
        var items = new List<TextBlock>(matches.Count);
        foreach (PdfTextMatch match in matches)
        {
            var item = new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, _text(PdfUiText.ResultPage), match.PageIndex + 1), TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 4) };
            items.Add(item);
            _results.Items.Add(item);
        }

        if (matches.Count == 0)
        {
            return;
        }

        _results.SelectedIndex = Math.Max(0, session.CurrentMatch);
        if (Visibility == Visibility.Visible)
        {
            _tabs.SelectedItem = _resultsTab;
        }

        for (int i = 0; i < Math.Min(500, matches.Count) && !cancellation.IsCancellationRequested; i++)
        {
            string context = await session.MatchContextAsync(matches[i]);
            if (!cancellation.IsCancellationRequested && i < items.Count)
            {
                items[i].Text = string.Format(CultureInfo.CurrentCulture, _text(PdfUiText.ResultPage), matches[i].PageIndex + 1) + "  " + context;
            }
        }
    }

    private void ResetResults()
    {
        _resultsFill?.Cancel();
        _results.Items.Clear();
    }
}
