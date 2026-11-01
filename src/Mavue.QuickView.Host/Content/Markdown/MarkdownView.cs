using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MdBlock = Markdig.Syntax.Block;
using VirtualKey = Windows.System.VirtualKey;

namespace Mavue.QuickView.Host.Content.Markdown;

/// <summary>
/// The rendered Markdown document: a scrolling column (at most <see cref="MarkdownRenderer.ContentWidth"/> wide,
/// centered) whose top-level blocks are created by an <see cref="ItemsRepeater"/> only when they come into view and
/// dropped when they leave it, so very long documents cost memory for what is visible only.
/// </summary>
internal sealed partial class MarkdownView
{
    private readonly ScrollViewer _scroller;
    private readonly ItemsRepeater _repeater;
    private readonly Border _notice;
    private readonly TextBlock _noticeText;
    private readonly Grid _root;
    private readonly BlockFactory _factory = new();
    private RichTextBlock? _selected;

    public MarkdownView(QuickViewWindow window)
    {
        _repeater = new ItemsRepeater
        {
            Layout = new StackLayout { Spacing = 14 },
            ItemTemplate = _factory,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var column = new Grid
        {
            MaxWidth = MarkdownRenderer.ContentWidth + 64,
            Padding = new Thickness(32, 24, 32, 48),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        column.Children.Add(_repeater);
        _scroller = new ScrollViewer
        {
            Content = column,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
        };
        AutomationProperties.SetAutomationId(_scroller, "QuickViewMarkdown");

        _noticeText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var noticeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        noticeRow.Children.Add(new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        noticeRow.Children.Add(_noticeText);
        _notice = new Border { Padding = new Thickness(12, 6, 12, 6), Visibility = Visibility.Collapsed, Child = noticeRow };

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(_notice);
        Grid.SetRow(_scroller, 1);
        _root.Children.Add(_scroller);
        RefreshTheme(window);
    }

    public FrameworkElement Root => _root;

    /// <summary>Elements currently created (diagnostics: virtualization keeps this small).</summary>
    public int RealizedBlocks => _factory.Realized;

    /// <summary>Text selected in a block of the document, or empty.</summary>
    public string SelectedText => _selected?.SelectedText ?? string.Empty;

    public void RefreshTheme(QuickViewWindow window)
    {
        _root.Background = window.ThemeBrush("QvStageBrush");
        _notice.Background = window.ThemeBrush("QvNoticeBrush");
        _noticeText.Foreground = window.ThemeBrush("QvSecondaryTextBrush");
    }

    /// <summary>Shows <paramref name="blocks"/> (top-level) drawn by <paramref name="renderer"/>, from the top.</summary>
    public void Show(IReadOnlyList<MdBlock> blocks, MarkdownRenderer renderer, double verticalRatio = 0)
    {
        _selected = null;
        _factory.Renderer = renderer;
        _repeater.ItemsSource = null;
        _repeater.ItemsSource = blocks;
        _scroller.UpdateLayout();
        _scroller.ChangeView(null, verticalRatio * _scroller.ScrollableHeight, null, disableAnimation: true);
    }

    public void SetNotice(string? text)
    {
        _noticeText.Text = text ?? string.Empty;
        _notice.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Lets go of the document and every element.</summary>
    public void Clear()
    {
        _repeater.ItemsSource = null;
        _factory.Renderer = null;
        _selected = null;
    }

    /// <summary>Where the view is (0 = top, 1 = bottom), to keep the place when the document is drawn again.</summary>
    public double VerticalRatio => _scroller.ScrollableHeight > 0 ? _scroller.VerticalOffset / _scroller.ScrollableHeight : 0;

    /// <summary>A rich text block of the document took part in a selection.</summary>
    public void Track(RichTextBlock rich) => rich.SelectionChanged += (_, _) =>
    {
        if (rich.SelectedText.Length > 0)
        {
            _selected = rich;
        }
    };

    /// <summary>Brings top-level block <paramref name="index"/> to the top (a #heading link).</summary>
    public void ScrollToBlock(int index)
    {
        if (_repeater.ItemsSourceView is not { } view || index < 0 || index >= view.Count)
        {
            return;
        }

        UIElement element = _repeater.GetOrCreateElement(index);
        element.UpdateLayout();
        element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
    }

    public bool HandleKey(VirtualKey key)
    {
        double line = 48;
        double page = Math.Max(line, _scroller.ViewportHeight - line);
        double? target = key switch
        {
            VirtualKey.Up => _scroller.VerticalOffset - line,
            VirtualKey.Down => _scroller.VerticalOffset + line,
            VirtualKey.PageUp => _scroller.VerticalOffset - page,
            VirtualKey.PageDown => _scroller.VerticalOffset + page,
            VirtualKey.Home => 0,
            VirtualKey.End => _scroller.ScrollableHeight,
            _ => null,
        };
        if (target is not { } offset)
        {
            return false;
        }

        _scroller.ChangeView(null, Math.Clamp(offset, 0, _scroller.ScrollableHeight), null, disableAnimation: key is VirtualKey.Home or VirtualKey.End);
        return true;
    }

    /// <summary>Creates the element of a block when the repeater needs it; nothing is kept for recycling.</summary>
    private sealed partial class BlockFactory : IElementFactory
    {
        public MarkdownRenderer? Renderer { get; set; }

        public int Realized { get; private set; }

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            Realized++;
            return args.Data is MdBlock block && Renderer is { } renderer ? renderer.Render(block) : new Border();
        }

        public void RecycleElement(ElementFactoryRecycleArgs args) => Realized = Math.Max(0, Realized - 1);
    }
}
