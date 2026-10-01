namespace Mavue.Markup;

/// <summary>
/// Markup element kinds (SPEC §6, §11, §12). Lasso and Smart Lasso are excluded by SPEC §29.
/// </summary>
public enum MarkupKind
{
    Highlight,
    Underline,
    Strikethrough,
    TextBox,
    Note,
    SpeechBubble,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Freehand,
    Image,
    Stamp,
    Signature,
}
