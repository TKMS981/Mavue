namespace Mavue.Core.Text.Syntax;

/// <summary>What a span of source text is (the palette decides the colors per theme).</summary>
public enum SyntaxTokenKind : byte
{
    Text = 0,
    Keyword,
    Type,

    /// <summary>true, false, null and the like; entities in markup.</summary>
    Literal,
    StringLiteral,
    Number,
    Comment,

    /// <summary>Preprocessor lines, decorators/annotations, document markers.</summary>
    Preprocessor,

    /// <summary>Markup tag names.</summary>
    Tag,

    /// <summary>Markup attribute names.</summary>
    Attribute,

    /// <summary>Keys of JSON, YAML, INI, CSS properties.</summary>
    Property,

    /// <summary>Headings and sections (Markdown, INI, diff hunks).</summary>
    Heading,

    /// <summary>Added lines of a diff.</summary>
    Inserted,

    /// <summary>Removed lines of a diff.</summary>
    Deleted,

    /// <summary>Shell and PowerShell variables, YAML anchors.</summary>
    Variable,

    /// <summary>Error levels in logs.</summary>
    Error,

    /// <summary>Warning levels in logs.</summary>
    Warning,
}

/// <summary>A colored span of one line (start and length in UTF-16 code units of the displayed line).</summary>
public readonly record struct SyntaxToken(int Start, int Length, SyntaxTokenKind Kind);

/// <summary>
/// A syntax highlighter for one language (docs/ARCHITECTURE.md §4.7.12). Lines are colored one at a time: the state
/// at the end of a line (inside a block comment, a multi-line string, a markup tag…) is passed to the next line, so
/// only visible lines (and those from the nearest known state before them) are ever tokenized. Implementations are
/// small hand-written lexers, not full parsers: fast, allocation-free per character, and robust against any input
/// (the worst result of unusual code is a wrong color). Pure and thread-safe.
/// </summary>
public abstract class SyntaxLanguage
{
    protected SyntaxLanguage(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    /// <summary>Stable identifier ("csharp").</summary>
    public string Id { get; }

    /// <summary>Name shown to the user ("C#").</summary>
    public string DisplayName { get; }

    /// <summary>
    /// Appends the non-plain tokens of <paramref name="line"/> (in order, not overlapping) and returns the state at
    /// its end. State 0 is "nothing open".
    /// </summary>
    public abstract int Tokenize(ReadOnlySpan<char> line, int state, List<SyntaxToken> tokens);

    public override string ToString() => DisplayName;

    /// <summary>Adds a token unless it is empty.</summary>
    protected static void Add(List<SyntaxToken> tokens, int start, int end, SyntaxTokenKind kind)
    {
        if (end > start && kind != SyntaxTokenKind.Text)
        {
            tokens.Add(new SyntaxToken(start, end - start, kind));
        }
    }

    protected static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    protected static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>End of a number starting at <paramref name="i"/> (hex, binary, digit separators, exponents, suffixes).</summary>
    protected static int ScanNumber(ReadOnlySpan<char> s, int i)
    {
        int n = s.Length;
        if (i + 1 < n && s[i] == '0' && (s[i + 1] | 0x20) is 'x' or 'b' or 'o')
        {
            i += 2;
            while (i < n && (char.IsAsciiHexDigit(s[i]) || s[i] == '_'))
            {
                i++;
            }
        }
        else
        {
            while (i < n && (char.IsAsciiDigit(s[i]) || s[i] == '_'))
            {
                i++;
            }

            if (i + 1 < n && s[i] == '.' && char.IsAsciiDigit(s[i + 1]))
            {
                i++;
                while (i < n && (char.IsAsciiDigit(s[i]) || s[i] == '_'))
                {
                    i++;
                }
            }

            if (i < n && (s[i] | 0x20) == 'e')
            {
                int j = i + 1;
                if (j < n && s[j] is '+' or '-')
                {
                    j++;
                }

                if (j < n && char.IsAsciiDigit(s[j]))
                {
                    i = j;
                    while (i < n && char.IsAsciiDigit(s[i]))
                    {
                        i++;
                    }
                }
            }
        }

        while (i < n && char.IsAsciiLetter(s[i]))
        {
            i++; // suffixes: 10u, 1.5f, 100L, 5px, 2em
        }

        return i;
    }
}
