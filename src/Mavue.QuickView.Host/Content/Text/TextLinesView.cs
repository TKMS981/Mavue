using System.Globalization;
using Mavue.Core.Text;
using Mavue.Core.Text.Syntax;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace Mavue.QuickView.Host.Content.Text;

/// <summary>Lines for <see cref="TextLinesView"/>, read on demand (all members on the UI thread).</summary>
internal interface ITextLineSource
{
    /// <summary>Lines known so far (grows while the file is indexed).</summary>
    int LineCount { get; }

    /// <summary>The line when it has been read; null while it is being read.</summary>
    TextLine? TryGet(int line);

    /// <summary>Asks for lines <paramref name="first"/>..<paramref name="last"/> to be read (asynchronously).</summary>
    void Request(int first, int last);

    /// <summary>Lines were read, or more lines became known.</summary>
    event Action? Changed;
}

/// <summary>
/// A virtualized view of text lines (docs/ARCHITECTURE.md §4.7.12): only the visible lines exist as elements, and
/// only they are read from the file and colored. Scrolling is anchored to a line (the top line and a pixel offset
/// into it), not to a pixel position over the whole file, so files of millions of lines scroll precisely and lines
/// may wrap (each visible line is measured; lines never seen have the height of one row). The wheel scrolls (Shift or
/// a tilt wheel: sideways), Ctrl+wheel is the window's zoom (text size), touch pans, the mouse selects text inside a
/// line. Keys come from the content: ↑/↓, PageUp/PageDown, Home/End.
/// </summary>
internal sealed partial class TextLinesView
{
    private const double Padding = 12;
    private const double GutterPadding = 16;
    private const int WheelLines = 3;
    private const int RequestMargin = 60;
    private const int MaxPool = 160;

    private readonly QuickViewWindow _window;
    private readonly Grid _root;
    private readonly Border _notice;
    private readonly TextBlock _noticeText;
    private readonly Canvas _gutter;
    private readonly Canvas _text;
    private readonly ScrollBar _vertical;
    private readonly ScrollBar _horizontal;
    private readonly TextBlock _empty;
    private readonly RectangleGeometry _textClip = new();
    private readonly RectangleGeometry _gutterClip = new();
    private readonly Dictionary<int, LineVisual> _realized = [];
    private readonly Stack<LineVisual> _pool = new();
    private readonly Dictionary<SyntaxTokenKind, Brush> _palette = [];
    private Brush _textBrush = null!;
    private Brush _numberBrush = null!;
    private Brush _secondaryBrush = null!;
    private ITextLineSource? _source;
    private int _top;
    private double _topOffset;
    private double _left;
    private double _maxWidth;
    private double _fontSize = 13;
    private double _lineHeight;
    private double _gutterWidth;
    private bool _wrap;
    private bool _lineNumbers = true;
    private int _version;
    private bool _updatingBars;
    private bool _layingOut;
    private double _lastTextWidth;

    public TextLinesView(QuickViewWindow window)
    {
        _window = window;
        _root = new TextLinesRoot { Background = window.ThemeBrush("QvStageBrush") };
        AutomationProperties.SetAutomationId(_root, "QuickViewTextLines");
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _noticeText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var noticeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        noticeRow.Children.Add(new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        noticeRow.Children.Add(_noticeText);
        _notice = new Border { Padding = new Thickness(12, 6, 12, 6), Visibility = Visibility.Collapsed, Child = noticeRow };
        AutomationProperties.SetAutomationId(_noticeText, "QuickViewTextNotice");
        Grid.SetColumnSpan(_notice, 3);
        _root.Children.Add(_notice);

        _gutter = new Canvas { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Clip = _gutterClip, IsHitTestVisible = false };
        Grid.SetRow(_gutter, 1);
        _root.Children.Add(_gutter);

        _text = new Canvas
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // hit-testable: wheel and touch anywhere
            Clip = _textClip,
            ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.TranslateInertia,
        };
        Grid.SetRow(_text, 1);
        Grid.SetColumn(_text, 1);
        _root.Children.Add(_text);

        _empty = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        Grid.SetRow(_empty, 1);
        Grid.SetColumn(_empty, 1);
        _root.Children.Add(_empty);

        _vertical = new ScrollBar { Orientation = Orientation.Vertical, IndicatorMode = ScrollingIndicatorMode.MouseIndicator, IsTabStop = false, SmallChange = 1 };
        Grid.SetRow(_vertical, 1);
        Grid.SetColumn(_vertical, 2);
        _root.Children.Add(_vertical);
        _horizontal = new ScrollBar { Orientation = Orientation.Horizontal, IndicatorMode = ScrollingIndicatorMode.MouseIndicator, IsTabStop = false, Visibility = Visibility.Collapsed };
        Grid.SetRow(_horizontal, 2);
        Grid.SetColumn(_horizontal, 1);
        _root.Children.Add(_horizontal);

        _vertical.ValueChanged += OnVerticalBar;
        _horizontal.ValueChanged += OnHorizontalBar;
        _text.SizeChanged += (_, _) => Layout();
        _text.PointerWheelChanged += OnWheel;
        _text.ManipulationStarted += (_, e) =>
        {
            if (e.PointerDeviceType == PointerDeviceType.Mouse)
            {
                e.Complete(); // the mouse selects text; touch and pen pan
            }
        };
        _text.ManipulationDelta += (_, e) =>
        {
            ScrollBy(-e.Delta.Translation.Y);
            ScrollSideways(-e.Delta.Translation.X);
        };
        RefreshTheme();
        SetFontSize(_fontSize);
    }

    /// <summary>The element to put on the stage.</summary>
    public FrameworkElement Root => _root;

    /// <summary>The first visible line (diagnostics and tests).</summary>
    public int TopLine => _top;

    /// <summary>Lines currently shown (at least partly).</summary>
    public int VisibleLines { get; private set; }

    public bool Wrap => _wrap;

    public double FontSize => _fontSize;

    /// <summary>Text selected inside a line (the mouse selects within one line), or empty.</summary>
    public string SelectedText
    {
        get
        {
            foreach (LineVisual visual in _realized.Values)
            {
                if (visual.Text.SelectedText is { Length: > 0 } selected)
                {
                    return selected;
                }
            }

            return string.Empty;
        }
    }

    /// <summary>Shows <paramref name="source"/> from its first line.</summary>
    /// <param name="name">Accessible name (the file name).</param>
    public void Attach(ITextLineSource source, string name, bool wrap, bool lineNumbers)
    {
        Detach();
        _source = source;
        _source.Changed += OnSourceChanged;
        _wrap = wrap;
        _lineNumbers = lineNumbers;
        _top = 0;
        _topOffset = 0;
        _left = 0;
        _maxWidth = 0;
        _version++;
        AutomationProperties.SetName(_root, name);
        SetNotice(null);
        Layout();
    }

    /// <summary>Lets go of the source and every line element (memory is returned when Quick View closes).</summary>
    public void Detach()
    {
        if (_source is not null)
        {
            _source.Changed -= OnSourceChanged;
            _source = null;
        }

        foreach (LineVisual visual in _realized.Values)
        {
            Remove(visual);
        }

        _realized.Clear();
        while (_pool.TryPop(out LineVisual? pooled))
        {
            Remove(pooled);
        }

        _gutter.Children.Clear();
        _text.Children.Clear();
        _empty.Visibility = Visibility.Collapsed;
    }

    /// <summary>A note above the text (limits reached, fallbacks), or none.</summary>
    public void SetNotice(string? text)
    {
        _noticeText.Text = text ?? string.Empty;
        _notice.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A message in place of the lines (an empty file).</summary>
    public void SetEmptyMessage(string? text)
    {
        _empty.Text = text ?? string.Empty;
        _empty.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetWrap(bool wrap)
    {
        if (_wrap == wrap)
        {
            return;
        }

        _wrap = wrap;
        _left = 0;
        _topOffset = 0;
        _version++;
        Layout();
    }

    /// <summary>Text size in device-independent pixels (the window's zoom).</summary>
    public void SetFontSize(double size)
    {
        _fontSize = Math.Clamp(size, 6, 72);
        _lineHeight = Math.Ceiling(_fontSize * 1.5);
        _topOffset = 0;
        _maxWidth = 0;
        _version++;
        Layout();
    }

    /// <summary>Colors for the current theme (syntax palette, text, line numbers).</summary>
    public void RefreshTheme()
    {
        _root.Background = _window.ThemeBrush("QvStageBrush");
        _textBrush = _window.ThemeBrush("QvTextBrush");
        _numberBrush = _window.ThemeBrush("QvLineNumberBrush");
        _secondaryBrush = _window.ThemeBrush("QvSecondaryTextBrush");
        _notice.Background = _window.ThemeBrush("QvNoticeBrush");
        _noticeText.Foreground = _secondaryBrush;
        _empty.Foreground = _secondaryBrush;
        _palette.Clear();
        foreach (SyntaxTokenKind kind in Enum.GetValues<SyntaxTokenKind>())
        {
            if (kind != SyntaxTokenKind.Text)
            {
                _palette[kind] = _window.ThemeBrush("QvSyntax" + (kind == SyntaxTokenKind.StringLiteral ? "String" : kind.ToString()) + "Brush");
            }
        }

        _version++;
        Layout();
    }

    /// <summary>Scrolling keys (Quick View has the keyboard). Returns true when used.</summary>
    public bool HandleKey(VirtualKey key)
    {
        if (_source is null)
        {
            return false;
        }

        double page = Math.Max(_lineHeight, _text.ActualHeight - _lineHeight);
        switch (key)
        {
            case VirtualKey.Up:
                ScrollBy(-_lineHeight);
                return true;
            case VirtualKey.Down:
                ScrollBy(_lineHeight);
                return true;
            case VirtualKey.PageUp:
                ScrollBy(-page);
                return true;
            case VirtualKey.PageDown:
                ScrollBy(page);
                return true;
            case VirtualKey.Home:
                ScrollToLine(0);
                return true;
            case VirtualKey.End:
                ScrollToLine(Math.Max(0, _source.LineCount - 1)); // the layout settles with the last line at the bottom
                return true;
            default:
                return false;
        }
    }

    public void ScrollToLine(int line)
    {
        if (_source is null)
        {
            return;
        }

        _top = Math.Clamp(line, 0, Math.Max(0, _source.LineCount - 1));
        _topOffset = 0;
        Layout();
    }

    /// <summary>Moves the view down (positive) or up by <paramref name="pixels"/>.</summary>
    public void ScrollBy(double pixels)
    {
        if (_source is null || pixels == 0 || double.IsNaN(pixels))
        {
            return;
        }

        int count = _source.LineCount;
        _topOffset += pixels;
        while (_topOffset < 0 && _top > 0)
        {
            _top--;
            _topOffset += HeightOf(_top);
        }

        if (_topOffset < 0)
        {
            _topOffset = 0;
        }

        int guard = 0;
        while (_top < count - 1 && _topOffset >= HeightOf(_top) && guard++ < 1_000_000)
        {
            _topOffset -= HeightOf(_top);
            _top++;
        }

        if (_top >= count - 1)
        {
            _top = Math.Max(0, count - 1);
            _topOffset = Math.Min(_topOffset, Math.Max(0, HeightOf(_top) - _lineHeight));
        }

        Layout();
    }

    private void ScrollSideways(double pixels)
    {
        if (_wrap || pixels == 0)
        {
            return;
        }

        _left = Math.Clamp(_left + pixels, 0, MaxLeft());
        Layout();
    }

    private double MaxLeft() => Math.Max(0, _maxWidth + Padding - Math.Max(0, _text.ActualWidth - Padding));

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            return; // the window zooms (text size)
        }

        PointerPointProperties properties = e.GetCurrentPoint(_text).Properties;
        double steps = properties.MouseWheelDelta / 120.0;
        if (properties.IsHorizontalMouseWheel || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift))
        {
            ScrollSideways((properties.IsHorizontalMouseWheel ? steps : -steps) * WheelLines * _lineHeight * 2);
        }
        else
        {
            ScrollBy(-steps * WheelLines * _lineHeight);
        }

        e.Handled = true;
    }

    private void OnSourceChanged() => Layout();

    private void OnVerticalBar(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingBars || _source is null)
        {
            return;
        }

        int line = (int)Math.Floor(e.NewValue);
        _top = Math.Clamp(line, 0, Math.Max(0, _source.LineCount - 1));
        _topOffset = (e.NewValue - line) * HeightOf(_top);
        Layout();
    }

    private void OnHorizontalBar(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingBars)
        {
            return;
        }

        _left = Math.Clamp(e.NewValue, 0, MaxLeft());
        Layout();
    }

    /// <summary>Height of a line: measured when it is shown (wrapping), otherwise one row.</summary>
    private double HeightOf(int line)
    {
        if (!_wrap)
        {
            return _lineHeight;
        }

        if (_realized.TryGetValue(line, out LineVisual? visual) && visual.Version == _version && visual.Data is not null)
        {
            return visual.Height;
        }

        return _lineHeight;
    }

    private void Layout()
    {
        if (_layingOut)
        {
            return;
        }

        _layingOut = true;
        try
        {
            LayoutCore(allowSettle: true);
        }
        finally
        {
            _layingOut = false;
        }
    }

    private void LayoutCore(bool allowSettle)
    {
        double height = _text.ActualHeight;
        double width = _text.ActualWidth;
        if (_source is null || height <= 0 || width <= 0)
        {
            return;
        }

        int count = _source.LineCount;
        UpdateGutter(count);
        double textWidth = Math.Max(40, width - (2 * Padding));
        if (_wrap && Math.Abs(textWidth - _lastTextWidth) > 0.5)
        {
            _version++; // wrapped heights depend on the width
        }

        _lastTextWidth = textWidth;
        _textClip.Rect = new Rect(0, 0, width, height);
        _gutterClip.Rect = new Rect(0, 0, _gutterWidth, height);
        _top = Math.Clamp(_top, 0, Math.Max(0, count - 1));

        var used = new HashSet<int>();
        double y = -_topOffset;
        int line = _top;
        bool missing = false;
        while (y < height && line < count)
        {
            TextLine? data = _source.TryGet(line);
            missing |= data is null;
            if (!_realized.TryGetValue(line, out LineVisual? visual))
            {
                visual = Rent();
                _realized[line] = visual;
                visual.Line = line;
                visual.Data = null;
                visual.Version = -1;
            }

            if (visual.Version != _version || (visual.Data is null && data is not null) || !ReferenceEquals(visual.Data, data))
            {
                Fill(visual, data, textWidth);
            }

            Canvas.SetTop(visual.Text, y);
            Canvas.SetLeft(visual.Text, Padding - (_wrap ? 0 : _left));
            if (visual.Number is { } number)
            {
                number.Visibility = _lineNumbers ? Visibility.Visible : Visibility.Collapsed;
                if (_lineNumbers)
                {
                    number.Text = (line + 1).ToString(CultureInfo.CurrentCulture);
                    number.Width = Math.Max(0, _gutterWidth - GutterPadding);
                    Canvas.SetTop(number, y);
                }
            }

            used.Add(line);
            y += visual.Height;
            line++;
        }

        // Past the end with space left: settle so the last line sits at the bottom.
        if (allowSettle && line >= count && y < height && (_top > 0 || _topOffset > 0))
        {
            double gap = height - y;
            _topOffset -= gap;
            while (_topOffset < 0 && _top > 0)
            {
                _top--;
                _topOffset += HeightOf(_top);
            }

            _topOffset = Math.Max(0, _topOffset);
            Recycle(used);
            LayoutCore(allowSettle: false);
            return;
        }

        Recycle(used);
        VisibleLines = used.Count;
        if (missing || NeedsMargin())
        {
            _source.Request(Math.Max(0, _top - RequestMargin), Math.Min(count - 1, line + RequestMargin));
        }

        UpdateBars(count, height);
    }

    private bool NeedsMargin()
    {
        int last = _top + VisibleLines;
        return _source is not null &&
            ((last + (RequestMargin / 2) < _source.LineCount && _source.TryGet(last + (RequestMargin / 2)) is null) ||
             (_top - (RequestMargin / 2) >= 0 && _source.TryGet(_top - (RequestMargin / 2)) is null));
    }

    private void Fill(LineVisual visual, TextLine? data, double textWidth)
    {
        visual.Data = data;
        visual.Version = _version;
        TextBlock block = visual.Text;
        block.FontSize = _fontSize;
        block.LineHeight = _lineHeight;
        block.TextWrapping = _wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        block.Foreground = _textBrush;
        block.Inlines.Clear();
        if (data is not null)
        {
            AddRuns(block.Inlines, data);
        }

        if (visual.Number is { } number)
        {
            number.FontSize = _fontSize;
            number.LineHeight = _lineHeight;
            number.Foreground = _numberBrush;
        }

        if (_wrap)
        {
            block.Width = textWidth;
            block.Measure(new Size(textWidth, double.PositiveInfinity));
            visual.Height = Math.Max(_lineHeight, Math.Ceiling(block.DesiredSize.Height));
        }
        else
        {
            block.Width = double.NaN;
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            visual.Height = _lineHeight;
            if (block.DesiredSize.Width > _maxWidth)
            {
                _maxWidth = block.DesiredSize.Width;
            }
        }
    }

    private void AddRuns(InlineCollection inlines, TextLine data)
    {
        string text = data.Text;
        int at = 0;
        if (data.Tokens is { } tokens)
        {
            foreach (SyntaxToken token in tokens)
            {
                int start = Math.Clamp(token.Start, at, text.Length);
                int end = Math.Clamp(token.Start + token.Length, start, text.Length);
                if (start > at)
                {
                    inlines.Add(new Run { Text = text[at..start] });
                }

                if (end > start)
                {
                    var run = new Run { Text = text[start..end] };
                    if (_palette.TryGetValue(token.Kind, out Brush? brush))
                    {
                        run.Foreground = brush;
                    }

                    if (token.Kind == SyntaxTokenKind.Heading)
                    {
                        run.FontWeight = FontWeights.SemiBold;
                    }
                    else if (token.Kind == SyntaxTokenKind.Comment)
                    {
                        run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                    }

                    inlines.Add(run);
                }

                at = end;
            }
        }

        if (at < text.Length)
        {
            inlines.Add(new Run { Text = text[at..] });
        }

        if (data.Cut)
        {
            inlines.Add(new Run { Text = " …", Foreground = _secondaryBrush });
        }
    }

    private void UpdateGutter(int count)
    {
        double width = 0;
        if (_lineNumbers)
        {
            int digits = Math.Max(3, count.ToString(CultureInfo.InvariantCulture).Length);
            width = Math.Ceiling((digits * _fontSize * 0.62) + GutterPadding + 8);
        }

        if (Math.Abs(width - _gutterWidth) > 0.5)
        {
            _gutterWidth = width;
            _root.ColumnDefinitions[0].Width = new GridLength(width);
        }
    }

    private void UpdateBars(int count, double height)
    {
        _updatingBars = true;
        try
        {
            double visible = Math.Max(1, height / _lineHeight);
            _vertical.Minimum = 0;
            _vertical.Maximum = Math.Max(0, count - 1);
            _vertical.ViewportSize = visible;
            _vertical.LargeChange = Math.Max(1, visible - 1);
            double fraction = HeightOf(_top) > 0 ? _topOffset / HeightOf(_top) : 0;
            _vertical.Value = Math.Clamp(_top + fraction, 0, _vertical.Maximum);
            _vertical.Visibility = count > visible || _top > 0 ? Visibility.Visible : Visibility.Collapsed;

            double maxLeft = MaxLeft();
            _left = Math.Min(_left, maxLeft);
            bool sideways = !_wrap && maxLeft > 0.5;
            _horizontal.Visibility = sideways ? Visibility.Visible : Visibility.Collapsed;
            if (sideways)
            {
                _horizontal.Minimum = 0;
                _horizontal.Maximum = maxLeft;
                _horizontal.ViewportSize = _text.ActualWidth;
                _horizontal.LargeChange = Math.Max(1, _text.ActualWidth - 40);
                _horizontal.SmallChange = _lineHeight * 2;
                _horizontal.Value = _left;
            }
        }
        finally
        {
            _updatingBars = false;
        }
    }

    private LineVisual Rent()
    {
        if (_pool.TryPop(out LineVisual? visual))
        {
            visual.Text.Visibility = Visibility.Visible;
            if (visual.Number is not null)
            {
                visual.Number.Visibility = _lineNumbers ? Visibility.Visible : Visibility.Collapsed;
            }

            return visual;
        }

        var text = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            IsTextSelectionEnabled = true,
            TextTrimming = TextTrimming.None,
        };
        var number = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Right,
        };
        _text.Children.Add(text);
        _gutter.Children.Add(number);
        return new LineVisual(text, number);
    }

    private void Recycle(HashSet<int> used)
    {
        List<int>? gone = null;
        foreach (int line in _realized.Keys)
        {
            if (!used.Contains(line))
            {
                (gone ??= []).Add(line);
            }
        }

        if (gone is null)
        {
            return;
        }

        foreach (int line in gone)
        {
            LineVisual visual = _realized[line];
            _realized.Remove(line);
            if (_pool.Count < MaxPool)
            {
                visual.Text.Visibility = Visibility.Collapsed;
                visual.Text.Inlines.Clear();
                visual.Data = null;
                if (visual.Number is not null)
                {
                    visual.Number.Visibility = Visibility.Collapsed;
                }

                _pool.Push(visual);
            }
            else
            {
                Remove(visual);
            }
        }
    }

    private void Remove(LineVisual visual)
    {
        _text.Children.Remove(visual.Text);
        if (visual.Number is not null)
        {
            _gutter.Children.Remove(visual.Number);
        }
    }

    /// <summary>The view's root: a "document" for UI Automation (screen readers read the visible lines inside it).</summary>
    private sealed partial class TextLinesRoot : Grid
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new DocumentPeer(this);
    }

    private sealed partial class DocumentPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

        protected override string GetClassNameCore() => "QuickViewTextLines";
    }

    private sealed class LineVisual(TextBlock text, TextBlock? number)
    {
        public TextBlock Text { get; } = text;

        public TextBlock? Number { get; } = number;

        public int Line { get; set; }

        public TextLine? Data { get; set; }

        public int Version { get; set; } = -1;

        public double Height { get; set; }
    }
}
