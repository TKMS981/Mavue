using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Mavue.Core.Text;
using Mavue.Core.Text.Syntax;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using XamlBlock = Microsoft.UI.Xaml.Documents.Block;
using XamlImage = Microsoft.UI.Xaml.Controls.Image;
using XamlInline = Microsoft.UI.Xaml.Documents.Inline;
using XamlParagraph = Microsoft.UI.Xaml.Documents.Paragraph;

namespace Mavue.QuickView.Host.Content.Markdown;

/// <summary>Colors and sizes the Markdown view draws with (the window's theme dictionaries).</summary>
internal sealed record MarkdownTheme(
    Brush Text,
    Brush Secondary,
    Brush Divider,
    Brush CodeBackground,
    Brush Link,
    Brush InlineCode,
    IReadOnlyDictionary<SyntaxTokenKind, Brush> Syntax,
    Brush Note,
    Brush Tip,
    Brush Important,
    Brush Warning,
    Brush Caution);

/// <summary>
/// Draws Markdown (parsed by Markdig) as native XAML (docs/ARCHITECTURE.md §4.7.12): headings, paragraphs, emphasis,
/// lists and task lists, quotes and GitHub alerts, code blocks colored by <see cref="SyntaxLanguage"/>, tables,
/// footnotes, definition lists, front matter and images. Nothing is executed and no HTML engine is involved: raw HTML
/// in the document is not rendered (its text is kept, &lt;br&gt; breaks lines and &lt;img&gt; shows the image), math
/// is shown as source. Links run nothing by themselves: a click asks the content (web addresses open in the browser,
/// #anchors scroll). One top-level block becomes one element; the view creates them only when they scroll into view.
/// </summary>
internal sealed partial class MarkdownRenderer
{
    private const double MaxContentWidth = 880;
    private const int MaxDepth = 24;
    private const int MaxCodeLines = 3_000;
    private const int MaxTableRows = 1_000;

    private readonly MarkdownTheme _theme;
    private readonly double _size;
    private readonly MarkdownImages _images;
    private readonly Func<string, string> _strings;
    private readonly Action<string> _link;
    private readonly Action<RichTextBlock> _selectable;
    private readonly double _displayScale;
    private readonly CancellationToken _cancellation;
    private readonly Action<MarkdownImageOrigin, bool>? _imageReport;

    /// <param name="imageReport">Each image's origin and whether it was drawn (diagnostics; no addresses).</param>
    public MarkdownRenderer(MarkdownTheme theme, double fontSize, MarkdownImages images, Func<string, string> strings, Action<string> link, Action<RichTextBlock> selectable, double displayScale, CancellationToken cancellation, Action<MarkdownImageOrigin, bool>? imageReport = null)
    {
        _imageReport = imageReport;
        _theme = theme;
        _size = fontSize;
        _images = images;
        _strings = strings;
        _link = link;
        _selectable = selectable;
        _displayScale = displayScale;
        _cancellation = cancellation;
    }

    public static double ContentWidth => MaxContentWidth;

    /// <summary>The element for one top-level block.</summary>
    public UIElement Render(MdBlock block)
    {
        try
        {
            return RenderBlock(block, 0) ?? new Border();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A construct the renderer does not handle must not take the document down: show its text.
            return Paragraph(block.ToString() ?? string.Empty, _size, _theme.Secondary);
        }
    }

    private UIElement? RenderBlock(MdBlock block, int depth)
    {
        if (depth > MaxDepth)
        {
            return Paragraph(PlainText(block), _size, _theme.Secondary);
        }

        switch (block)
        {
            case HeadingBlock heading:
                return Heading(heading);
            case YamlFrontMatterBlock front:
                return Code(front.Lines.ToString(), "yaml", dim: true);
            case MathBlock math:
                return Code(math.Lines.ToString(), null, dim: false);
            case FencedCodeBlock fenced:
                return Code(fenced.Lines.ToString(), fenced.Info, dim: false);
            case CodeBlock code:
                return Code(code.Lines.ToString(), null, dim: false);
            case HtmlBlock html:
                return Html(html);
            case ThematicBreakBlock:
                return new Border { Height = 1, Background = _theme.Divider, Margin = new Thickness(0, 8, 0, 8) };
            case ParagraphBlock paragraph:
                return ParagraphOf(paragraph.Inline, _size, _theme.Text);
            case Table table:
                return Table(table, depth);
            case ListBlock list:
                return List(list, depth);
            case AlertBlock alert:
                return Alert(alert, depth);
            case QuoteBlock quote:
                return Quote(quote, depth);
            case FootnoteGroup footnotes:
                return Footnotes(footnotes, depth);
            case DefinitionList definitions:
                return Definitions(definitions, depth);
            case LinkReferenceDefinitionGroup or LinkReferenceDefinition:
                return null;
            case ContainerBlock container:
                return Stack(container, depth + 1, 8);
            case LeafBlock leaf when leaf.Inline is not null:
                return ParagraphOf(leaf.Inline, _size, _theme.Text);
            case LeafBlock leaf:
                return Paragraph(leaf.Lines.ToString(), _size, _theme.Text);
            default:
                return null;
        }
    }

    private StackPanel Stack(IEnumerable<MdBlock> blocks, int depth, double spacing)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (MdBlock child in blocks)
        {
            if (RenderBlock(child, depth) is { } element)
            {
                panel.Children.Add(element);
            }
        }

        return panel;
    }

    private UIElement Heading(HeadingBlock heading)
    {
        double factor = heading.Level switch { 1 => 2.0, 2 => 1.5, 3 => 1.25, 4 => 1.0, 5 => 0.875, _ => 0.85 };
        RichTextBlock text = ParagraphOf(heading.Inline, _size * factor, heading.Level >= 6 ? _theme.Secondary : _theme.Text);
        text.FontWeight = FontWeights.SemiBold;
        text.Margin = new Thickness(0, heading.Level <= 2 ? 12 : 6, 0, 0);
        AutomationProperties.SetHeadingLevel(text, (AutomationHeadingLevel)Math.Clamp(heading.Level, 1, 9));
        if (heading.Level > 2)
        {
            return text;
        }

        // Levels 1 and 2 have a rule under them (as on GitHub).
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(text);
        panel.Children.Add(new Border { Height = 1, Background = _theme.Divider });
        return panel;
    }

    private RichTextBlock ParagraphOf(ContainerInline? inlines, double size, Brush foreground)
    {
        var paragraph = new XamlParagraph();
        if (inlines is not null)
        {
            AddInlines(paragraph.Inlines, inlines, size, 0);
        }

        return Rich(paragraph, size, foreground);
    }

    private RichTextBlock Paragraph(string text, double size, Brush foreground)
    {
        var paragraph = new XamlParagraph();
        paragraph.Inlines.Add(new Run { Text = text });
        return Rich(paragraph, size, foreground);
    }

    private RichTextBlock Rich(XamlBlock block, double size, Brush foreground)
    {
        var rich = new RichTextBlock
        {
            FontSize = size,
            LineHeight = Math.Round(size * 1.55),
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        rich.Blocks.Add(block);
        _selectable(rich);
        return rich;
    }

    private void AddInlines(InlineCollection target, ContainerInline container, double size, int depth)
    {
        foreach (MdInline inline in container)
        {
            if (_cancellation.IsCancellationRequested)
            {
                return;
            }

            if (depth > MaxDepth)
            {
                target.Add(new Run { Text = inline.ToString() });
                continue;
            }

            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run { Text = literal.Content.ToString() });
                    break;
                case CodeInline code:
                    target.Add(new Run { Text = code.Content, FontFamily = CodeFont, Foreground = _theme.InlineCode });
                    break;
                case LineBreakInline lineBreak:
                    target.Add(lineBreak.IsHard ? new LineBreak() : new Run { Text = " " });
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run { Text = entity.Transcoded.ToString() });
                    break;
                case HtmlInline html:
                    if (BreakTag().IsMatch(html.Tag ?? string.Empty))
                    {
                        target.Add(new LineBreak());
                    }
                    else if (ImageTag().Match(html.Tag ?? string.Empty) is { Success: true } img)
                    {
                        target.Add(Image(Attribute(img.Value, "src"), Attribute(img.Value, "alt"), WidthAttribute(img.Value)));
                    }

                    break; // other tags are not rendered (their text between them still is)
                case TaskList task:
                    target.Add(new Run { Text = task.Checked ? "\u2611 " : "\u2610 ", FontFamily = new FontFamily("Segoe UI Symbol") });
                    break;
                case MathInline math:
                    target.Add(new Run { Text = math.Content.ToString(), FontFamily = CodeFont, Foreground = _theme.InlineCode });
                    break;
                case FootnoteLink footnote:
                    target.Add(new Run { Text = footnote.IsBackLink ? "\u21A9" : $"[{footnote.Index}]", FontSize = size * 0.8, Foreground = _theme.Link });
                    break;
                case LinkInline { IsImage: true } image:
                    target.Add(Image(image.Url, PlainText(image), null));
                    break;
                case LinkInline link:
                    target.Add(Hyperlink(link.Url, link, size, depth));
                    break;
                case AutolinkInline autolink:
                {
                    string url = autolink.IsEmail && !autolink.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? "mailto:" + autolink.Url : autolink.Url;
                    var hyperlink = NewHyperlink(url);
                    hyperlink.Inlines.Add(new Run { Text = autolink.Url });
                    target.Add(hyperlink);
                    break;
                }

                case EmphasisInline emphasis:
                    target.Add(Emphasis(emphasis, size, depth));
                    break;
                case ContainerInline nested:
                {
                    var span = new Span();
                    AddInlines(span.Inlines, nested, size, depth + 1);
                    target.Add(span);
                    break;
                }

                default:
                    if (inline.ToString() is { Length: > 0 } text && inline is not DelimiterInline)
                    {
                        target.Add(new Run { Text = text });
                    }

                    break;
            }
        }
    }

    private Span Emphasis(EmphasisInline emphasis, double size, int depth)
    {
        Span span = (emphasis.DelimiterChar, emphasis.DelimiterCount) switch
        {
            ('*' or '_', >= 2) => new Bold(),
            ('*' or '_', _) => new Italic(),
            _ => new Span(),
        };
        if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2)
        {
            span.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
        }
        else if (emphasis.DelimiterChar == '+' && emphasis.DelimiterCount >= 2)
        {
            span.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        }

        AddInlines(span.Inlines, emphasis, size, depth + 1);
        return span;
    }

    private Hyperlink Hyperlink(string? url, ContainerInline label, double size, int depth)
    {
        Hyperlink hyperlink = NewHyperlink(url ?? string.Empty);
        AddInlines(hyperlink.Inlines, label, size, depth + 1);
        RemoveNested(hyperlink.Inlines); // a hyperlink cannot contain another one or an inline container
        return hyperlink;
    }

    /// <summary>No NavigateUri: XAML would launch it by itself. The click goes to the content, which decides.</summary>
    private Hyperlink NewHyperlink(string url)
    {
        var hyperlink = new Hyperlink { Foreground = _theme.Link, UnderlineStyle = UnderlineStyle.Single };
        hyperlink.Click += (_, _) => _link(url);
        if (url.Length > 0)
        {
            ToolTipService.SetToolTip(hyperlink, url.Length > 300 ? url[..300] + "…" : url);
        }

        return hyperlink;
    }

    private static void RemoveNested(InlineCollection inlines)
    {
        for (int i = inlines.Count - 1; i >= 0; i--)
        {
            switch (inlines[i])
            {
                case Hyperlink nested:
                {
                    string text = string.Concat(nested.Inlines.OfType<Run>().Select(r => r.Text));
                    inlines.RemoveAt(i);
                    inlines.Insert(i, new Run { Text = text });
                    break;
                }

                case InlineUIContainer:
                    inlines.RemoveAt(i); // an image inside a link: shown without the link
                    break;
                case Span span:
                    RemoveNested(span.Inlines);
                    break;
            }
        }
    }

    private InlineUIContainer Image(string? url, string? alt, double? width)
    {
        (MarkdownImageOrigin origin, _) = _images.Classify(url);
        if (origin is MarkdownImageOrigin.Remote or MarkdownImageOrigin.Invalid)
        {
            _imageReport?.Invoke(origin, false); // never requested
            return new InlineUIContainer { Child = ImagePlaceholder(url, alt, origin == MarkdownImageOrigin.Remote) };
        }

        var image = new XamlImage { Stretch = Stretch.Uniform, MaxWidth = MaxContentWidth, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(image, alt ?? string.Empty);
        var host = new Grid { MinHeight = 24, MinWidth = 24 };
        host.Children.Add(image);
        _ = LoadImageAsync(image, host, url!, alt, width, origin);
        return new InlineUIContainer { Child = host };
    }

    private async Task LoadImageAsync(XamlImage image, Grid host, string url, string? alt, double? width, MarkdownImageOrigin origin)
    {
        double maxPixels = MaxContentWidth * _displayScale;
        (Microsoft.UI.Xaml.Media.ImageSource Source, double Width, double Height)? loaded = await _images.LoadAsync(url, maxPixels, _cancellation);
        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        _imageReport?.Invoke(origin, loaded is not null);
        if (loaded is not { } result)
        {
            host.Children.Clear();
            host.Children.Add(ImagePlaceholder(url, alt, remote: false));
            return;
        }

        // Image pixels are CSS pixels (device-independent), as in a browser; never wider than the column.
        double shown = Math.Min(width ?? result.Width, MaxContentWidth);
        image.Width = shown;
        image.Height = shown * result.Height / Math.Max(1, result.Width);
        image.Source = result.Source;
    }

    private Border ImagePlaceholder(string? url, string? alt, bool remote)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon { Glyph = remote ? "\uE774" : "\uE91B", FontSize = 12, Foreground = _theme.Secondary });
        row.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(alt) ? _strings("QuickView_Markdown_Image") : alt, FontSize = _size * 0.85, Foreground = _theme.Secondary });
        var border = new Border
        {
            Child = row,
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = _theme.Divider,
        };
        string reason = _strings(remote ? "QuickView_Markdown_RemoteImage" : "QuickView_Markdown_ImageUnavailable");
        ToolTipService.SetToolTip(border, string.IsNullOrEmpty(url) ? reason : reason + "\n" + (url.Length > 300 ? url[..300] + "…" : url));
        return border;
    }

    private Border Code(string code, string? info, bool dim)
    {
        SyntaxLanguage? language = LanguageFor(info);
        var text = new TextBlock
        {
            FontFamily = CodeFont,
            FontSize = _size * 0.9,
            LineHeight = Math.Round(_size * 0.9 * 1.5),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = dim ? _theme.Secondary : _theme.Text,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
        };
        string[] lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        int shown = Math.Min(lines.Length, MaxCodeLines);
        var tokens = new List<SyntaxToken>();
        int state = 0;
        for (int i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                text.Inlines.Add(new LineBreak());
            }

            string line = TextDocument.DisplayLine(lines[i]);
            if (language is null || dim)
            {
                text.Inlines.Add(new Run { Text = line });
                continue;
            }

            tokens.Clear();
            state = language.Tokenize(line, state, tokens);
            int at = 0;
            foreach (SyntaxToken token in tokens)
            {
                if (token.Start > at)
                {
                    text.Inlines.Add(new Run { Text = line[at..token.Start] });
                }

                var run = new Run { Text = line.Substring(token.Start, token.Length) };
                if (_theme.Syntax.TryGetValue(token.Kind, out Brush? brush))
                {
                    run.Foreground = brush;
                }

                text.Inlines.Add(run);
                at = token.Start + token.Length;
            }

            if (at < line.Length)
            {
                text.Inlines.Add(new Run { Text = line[at..] });
            }
        }

        if (lines.Length > shown)
        {
            text.Inlines.Add(new LineBreak());
            text.Inlines.Add(new Run
            {
                Text = string.Format(CultureInfo.CurrentCulture, _strings("QuickView_Markdown_MoreLines"), (lines.Length - shown).ToString("N0", CultureInfo.CurrentCulture)),
                Foreground = _theme.Secondary,
            });
        }

        var scroller = new ScrollViewer
        {
            Content = text,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Padding = new Thickness(0, 0, 0, 4),
        };
        var border = new Border
        {
            Background = _theme.CodeBackground,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Child = scroller,
        };
        if (language is not null)
        {
            AutomationProperties.SetName(border, language.DisplayName);
        }

        return border;
    }

    /// <summary>The language of a fenced code block's info string ("csharp", "js", "c++", "shell"…).</summary>
    internal static SyntaxLanguage? LanguageFor(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return null;
        }

        string id = info.Trim().Split(' ', '{', ',')[0].ToLowerInvariant();
        string alias = id switch
        {
            "c#" or "cs" or "csharp" => ".cs",
            "c++" or "cpp" or "cxx" => ".cpp",
            "js" or "javascript" or "node" => ".js",
            "ts" or "typescript" => ".tsx",
            "py" or "python" or "python3" => ".py",
            "sh" or "bash" or "shell" or "zsh" or "console" or "shellsession" => ".sh",
            "ps" or "ps1" or "powershell" or "pwsh" => ".ps1",
            "bat" or "batch" or "cmd" => ".bat",
            "yml" or "yaml" => ".yaml",
            "md" or "markdown" => ".md",
            "html" or "htm" or "xhtml" or "vue" => ".html",
            "xml" or "xaml" or "svg" or "plist" => ".xml",
            "rs" or "rust" => ".rs",
            "rb" or "ruby" => ".rb",
            "kt" or "kotlin" => ".kt",
            "golang" or "go" => ".go",
            "docker" or "dockerfile" => ".dockerfile",
            "make" or "makefile" => ".mk",
            "objc" or "objective-c" => ".m",
            "fs" or "fsharp" or "f#" => ".fs",
            "vb" or "vbnet" or "vb.net" => ".vb",
            "jsonc" or "json5" or "json" => ".json",
            "text" or "txt" or "plain" or "plaintext" or "mermaid" => string.Empty,
            _ => "." + id,
        };
        return alias.Length == 0 ? null : SyntaxLanguages.ForPath("code" + alias);
    }

    private UIElement Html(HtmlBlock html)
    {
        string raw = html.Lines.ToString();
        if (raw.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
        {
            return new Border(); // a comment
        }

        var paragraph = new XamlParagraph();
        foreach (Match img in ImageTag().Matches(raw))
        {
            paragraph.Inlines.Add(Image(Attribute(img.Value, "src"), Attribute(img.Value, "alt"), WidthAttribute(img.Value)));
            paragraph.Inlines.Add(new Run { Text = " " });
        }

        string text = System.Net.WebUtility.HtmlDecode(AnyTag().Replace(BreakTag().Replace(raw, "\n"), string.Empty)).Trim();
        if (text.Length > 0)
        {
            if (paragraph.Inlines.Count > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            paragraph.Inlines.Add(new Run { Text = text });
        }

        return paragraph.Inlines.Count == 0 ? new Border() : Rich(paragraph, _size, _theme.Text);
    }

    private StackPanel List(ListBlock list, int depth)
    {
        var panel = new StackPanel { Spacing = list.IsLoose ? 8 : 2 };
        int number = list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) ? start : 1;
        string bullet = (depth % 3) switch { 0 => "\u2022", 1 => "\u25E6", _ => "\u25AA" };
        foreach (MdBlock child in list)
        {
            if (child is not ListItemBlock item)
            {
                continue;
            }

            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(list.IsOrdered ? 30 : 18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bool task = item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList };
            var marker = new TextBlock
            {
                Text = task ? string.Empty : list.IsOrdered ? number.ToString(CultureInfo.CurrentCulture) + (list.OrderedDelimiter == ')' ? ")" : ".") : bullet,
                FontSize = _size,
                LineHeight = Math.Round(_size * 1.55),
                Foreground = _theme.Secondary,
                HorizontalAlignment = list.IsOrdered ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            };
            row.Children.Add(marker);
            StackPanel content = Stack(item, depth + 1, list.IsLoose ? 8 : 2);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
            number++;
        }

        return panel;
    }

    private Border Quote(QuoteBlock quote, int depth)
    {
        StackPanel content = Stack(quote, depth + 1, 8);
        return new Border
        {
            BorderBrush = _theme.Divider,
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(14, 2, 0, 2),
            Child = Recolor(content, _theme.Secondary),
        };
    }

    private Border Alert(AlertBlock alert, int depth)
    {
        string kind = alert.Kind.ToString().ToUpperInvariant();
        (Brush brush, string glyph) = kind switch
        {
            "TIP" => (_theme.Tip, "\uEA80"),
            "IMPORTANT" => (_theme.Important, "\uE8BD"),
            "WARNING" => (_theme.Warning, "\uE7BA"),
            "CAUTION" => (_theme.Caution, "\uEA39"),
            _ => (_theme.Note, "\uE946"),
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new FontIcon { Glyph = glyph, FontSize = _size, Foreground = brush });
        string name = _strings("QuickView_Markdown_Alert_" + kind);
        title.Children.Add(new TextBlock { Text = name.Length > 0 ? name : kind, FontSize = _size, FontWeight = FontWeights.SemiBold, Foreground = brush });
        StackPanel content = Stack(alert, depth + 1, 8);
        content.Children.Insert(0, title);
        return new Border { BorderBrush = brush, BorderThickness = new Thickness(4, 0, 0, 0), Padding = new Thickness(14, 4, 0, 4), Child = content };
    }

    private StackPanel Footnotes(FootnoteGroup group, int depth)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new Border { Height = 1, Background = _theme.Divider, Margin = new Thickness(0, 8, 0, 4) });
        foreach (MdBlock child in group)
        {
            if (child is not Footnote footnote)
            {
                continue;
            }

            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = footnote.Order.ToString(CultureInfo.CurrentCulture) + ".", FontSize = _size * 0.85, Foreground = _theme.Secondary, HorizontalAlignment = HorizontalAlignment.Right });
            StackPanel content = Stack(footnote, depth + 1, 4);
            Grid.SetColumn(content, 1);
            row.Children.Add(Recolor(content, _theme.Secondary));
            panel.Children.Add(row);
        }

        return panel;
    }

    private StackPanel Definitions(DefinitionList list, int depth)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (MdBlock child in list)
        {
            if (child is not DefinitionItem item)
            {
                continue;
            }

            foreach (MdBlock part in item)
            {
                if (part is DefinitionTerm term)
                {
                    RichTextBlock text = ParagraphOf(term.Inline, _size, _theme.Text);
                    text.FontWeight = FontWeights.SemiBold;
                    panel.Children.Add(text);
                }
                else if (RenderBlock(part, depth + 1) is FrameworkElement definition)
                {
                    definition.Margin = new Thickness(24, 0, 0, 4);
                    panel.Children.Add(definition);
                }
            }
        }

        return panel;
    }

    private ScrollViewer Table(Table table, int depth)
    {
        var grid = new Grid();
        int columns = Math.Max(1, table.ColumnDefinitions.Count);
        foreach (MdBlock rowBlock in table)
        {
            if (rowBlock is TableRow candidate)
            {
                columns = Math.Max(columns, candidate.Count);
            }
        }

        for (int c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 480 });
        }

        int r = 0;
        foreach (MdBlock rowBlock in table)
        {
            if (rowBlock is not TableRow row)
            {
                continue;
            }

            if (r >= MaxTableRows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var more = new TextBlock
                {
                    Text = string.Format(CultureInfo.CurrentCulture, _strings("QuickView_Markdown_MoreRows"), (table.Count - r).ToString("N0", CultureInfo.CurrentCulture)),
                    Foreground = _theme.Secondary,
                    Margin = new Thickness(8, 6, 8, 6),
                };
                Grid.SetRow(more, r);
                Grid.SetColumnSpan(more, columns);
                grid.Children.Add(more);
                break;
            }

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int c = 0;
            foreach (MdBlock cellBlock in row)
            {
                if (cellBlock is not TableCell cell || c >= columns)
                {
                    continue;
                }

                var content = Stack(cell, depth + 1, 4);
                TableColumnAlign? align = c < table.ColumnDefinitions.Count ? table.ColumnDefinitions[c].Alignment : null;
                content.HorizontalAlignment = align switch
                {
                    TableColumnAlign.Center => HorizontalAlignment.Center,
                    TableColumnAlign.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Left,
                };
                if (row.IsHeader)
                {
                    foreach (UIElement element in content.Children)
                    {
                        if (element is RichTextBlock header)
                        {
                            header.FontWeight = FontWeights.SemiBold;
                        }
                    }
                }

                var border = new Border
                {
                    BorderBrush = _theme.Divider,
                    BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    Padding = new Thickness(10, 5, 10, 5),
                    Background = row.IsHeader ? _theme.CodeBackground : null,
                    Child = content,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                Grid.SetColumnSpan(border, Math.Max(1, Math.Min(cell.ColumnSpan, columns - c)));
                grid.Children.Add(border);
                c += Math.Max(1, cell.ColumnSpan);
            }

            r++;
        }

        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };
    }

    /// <summary>Quotes and footnotes are drawn in the secondary text color.</summary>
    private static T Recolor<T>(T panel, Brush brush)
        where T : Panel
    {
        foreach (UIElement child in panel.Children)
        {
            if (child is RichTextBlock rich)
            {
                rich.Foreground = brush;
            }
        }

        return panel;
    }

    private static string PlainText(MarkdownObject node)
    {
        var builder = new StringBuilder();
        void Walk(MarkdownObject n)
        {
            switch (n)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case ContainerInline container:
                    foreach (MdInline child in container)
                    {
                        Walk(child);
                    }

                    break;
                case LeafBlock leaf when leaf.Inline is not null:
                    Walk(leaf.Inline);
                    builder.Append(' ');
                    break;
                case LeafBlock leaf:
                    builder.Append(leaf.Lines.ToString());
                    break;
                case ContainerBlock blocks:
                    foreach (MdBlock child in blocks)
                    {
                        Walk(child);
                    }

                    break;
            }
        }

        Walk(node);
        return builder.ToString().Trim();
    }

    private static string? Attribute(string tag, string name)
    {
        Match match = Regex.Match(tag, "\\b" + name + "\\s*=\\s*(\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        if (!match.Success)
        {
            return null;
        }

        string value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
        return System.Net.WebUtility.HtmlDecode(value);
    }

    private static double? WidthAttribute(string tag) =>
        double.TryParse(Attribute(tag, "width")?.TrimEnd('p', 'x'), NumberStyles.Float, CultureInfo.InvariantCulture, out double width) && width is > 0 and < 10_000 ? width : null;

    private static FontFamily CodeFont { get; } = new("Cascadia Mono, Consolas, Courier New");

    [GeneratedRegex("^<br\\s*/?>$|<br\\s*/?>", RegexOptions.IgnoreCase, 50)]
    private static partial Regex BreakTag();

    [GeneratedRegex("<img\\b[^>]*>", RegexOptions.IgnoreCase, 50)]
    private static partial Regex ImageTag();

    [GeneratedRegex("<[^>]*>", RegexOptions.None, 50)]
    private static partial Regex AnyTag();
}
