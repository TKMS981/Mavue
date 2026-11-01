using Mavue.Core.Formats;
using Mavue.Core.Text.Syntax;

namespace Mavue.Core.Text;

/// <summary>
/// Which files are text, Markdown or HTML by their name (docs/ARCHITECTURE.md §4.7.12): plain text and document
/// extensions, every extension with a syntax language, and well-known names without an extension (Makefile,
/// Dockerfile, README, LICENSE…). The content still decides: binary content behind a text extension is shown on the
/// information card, and a known binary signature (a PNG named .txt) wins over the extension.
/// </summary>
public static class TextFileTypes
{
    private static readonly HashSet<string> Markdown = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdwn", ".mdtxt", ".mdtext", ".rmarkdown",
    };

    private static readonly HashSet<string> Html = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".xht", ".shtml",
    };

    private static readonly HashSet<string> PlainText = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".csv", ".tsv", ".tab", ".psv", ".nfo", ".diz", ".srt", ".vtt", ".sbv", ".ass", ".ssa", ".lrc", ".rst", ".adoc",
        ".asciidoc", ".org", ".textile", ".wiki", ".mediawiki", ".creole", ".pod", ".me", ".readme", ".1st", ".lst", ".lock", ".sln", ".asc",
        ".pem", ".po", ".pot", ".strings", ".gitkeep", ".keep", ".todo", ".changelog", ".license", ".authors", ".info", ".mdx",
    };

    /// <summary>Names without an extension that are plain text.</summary>
    private static readonly HashSet<string> TextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "README", "LICENSE", "LICENCE", "COPYING", "COPYRIGHT", "AUTHORS", "CONTRIBUTORS", "CHANGELOG", "CHANGES", "HISTORY", "NEWS", "NOTICE",
        "CONTRIBUTING", "TODO", "INSTALL", "THANKS", "VERSION", "MAINTAINERS", "PATENTS", "UNLICENSE", "OWNERS",
    };

    /// <summary>Extensions that look like code but are usually binary or are shown by another content.</summary>
    private static readonly HashSet<string> NotText = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", // a drawing (SvgContent); its source is not the preview
    };

    /// <summary>The text format of <paramref name="path"/> from its name, or <see cref="FileFormat.Unknown"/>.</summary>
    public static FileFormat FormatForPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string name = Path.GetFileName(path);
        string extension = Path.GetExtension(name);
        if (Markdown.Contains(extension))
        {
            return FileFormat.Markdown;
        }

        if (Html.Contains(extension))
        {
            return FileFormat.Html;
        }

        if (extension.Length > 1 && NotText.Contains(extension))
        {
            return FileFormat.Unknown;
        }

        if (PlainText.Contains(extension) || SyntaxLanguages.HasKnownFileName(name) || TextNames.Contains(name) ||
            (extension.Length > 1 && SyntaxLanguages.Extensions.Contains(extension)) ||
            (TextNames.Contains(Path.GetFileNameWithoutExtension(name)) && extension is ".md" or ".txt"))
        {
            return FileFormat.Text;
        }

        return FileFormat.Unknown;
    }

    /// <summary>The extension (".ts", ".mts") is also an MPEG transport stream: the content decides.</summary>
    public static bool IsAmbiguousWithMedia(string? extension) =>
        extension is not null && (extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mts", StringComparison.OrdinalIgnoreCase));
}
