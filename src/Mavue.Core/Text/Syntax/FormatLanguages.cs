namespace Mavue.Core.Text.Syntax;

/// <summary>
/// XML, HTML, XAML and similar markup: tags, attributes, attribute values, comments, CDATA and entities.
/// State: 0 = text, 1 = comment, 2 = inside a tag, 3 = attribute value in "…", 4 = in '…', 5 = CDATA,
/// 6 = processing instruction or declaration (&lt;? … ?&gt;, &lt;! … &gt;).
/// </summary>
public sealed class MarkupLanguage : SyntaxLanguage
{
    public MarkupLanguage(string id, string displayName)
        : base(id, displayName)
    {
    }

    public override int Tokenize(ReadOnlySpan<char> line, int state, List<SyntaxToken> tokens)
    {
        int n = line.Length;
        int i = 0;
        while (i < n)
        {
            switch (state)
            {
                case 1:
                    i = Until(line, i, "-->", SyntaxTokenKind.Comment, tokens, ref state);
                    continue;
                case 5:
                    i = Until(line, i, "]]>", SyntaxTokenKind.StringLiteral, tokens, ref state);
                    continue;
                case 6:
                {
                    int end = line[i..].IndexOf('>');
                    int stop = end < 0 ? n : i + end + 1;
                    Add(tokens, i, stop, SyntaxTokenKind.Preprocessor);
                    state = end < 0 ? 6 : 0;
                    i = stop;
                    continue;
                }

                case 3 or 4:
                {
                    char quote = state == 3 ? '"' : '\'';
                    int end = line[i..].IndexOf(quote);
                    int stop = end < 0 ? n : i + end + 1;
                    Add(tokens, i, stop, SyntaxTokenKind.StringLiteral);
                    state = end < 0 ? state : 2;
                    i = stop;
                    continue;
                }

                case 2:
                    i = InTag(line, i, tokens, ref state);
                    continue;
            }

            // Text between tags.
            char c = line[i];
            if (c == '<')
            {
                ReadOnlySpan<char> rest = line[i..];
                if (rest.StartsWith("<!--"))
                {
                    int close = line[(i + 4)..].IndexOf("-->");
                    int stop = close < 0 ? n : i + 4 + close + 3;
                    Add(tokens, i, stop, SyntaxTokenKind.Comment);
                    state = close < 0 ? 1 : 0;
                    i = stop;
                    continue;
                }

                if (rest.StartsWith("<![CDATA["))
                {
                    state = 5;
                    Add(tokens, i, i + 9, SyntaxTokenKind.Preprocessor);
                    i += 9;
                    continue;
                }

                if (rest.Length > 1 && rest[1] is '?' or '!')
                {
                    state = 6;
                    continue;
                }

                int nameStart = i + 1 + (rest.Length > 1 && rest[1] == '/' ? 1 : 0);
                int nameEnd = nameStart;
                while (nameEnd < n && (char.IsLetterOrDigit(line[nameEnd]) || line[nameEnd] is ':' or '-' or '_' or '.'))
                {
                    nameEnd++;
                }

                if (nameEnd > nameStart)
                {
                    Add(tokens, i, nameEnd, SyntaxTokenKind.Tag);
                    state = 2;
                    i = nameEnd;
                    continue;
                }

                i++;
                continue;
            }

            if (c == '&')
            {
                int semi = line[i..].IndexOf(';');
                if (semi is > 1 and <= 10)
                {
                    Add(tokens, i, i + semi + 1, SyntaxTokenKind.Literal);
                    i += semi + 1;
                    continue;
                }
            }

            i++;
        }

        return state;
    }

    private static int Until(ReadOnlySpan<char> line, int i, string close, SyntaxTokenKind kind, List<SyntaxToken> tokens, ref int state)
    {
        int end = line[i..].IndexOf(close.AsSpan());
        int stop = end < 0 ? line.Length : i + end + close.Length;
        Add(tokens, i, stop, kind);
        state = end < 0 ? state : 0;
        return stop;
    }

    private static int InTag(ReadOnlySpan<char> line, int i, List<SyntaxToken> tokens, ref int state)
    {
        int n = line.Length;
        char c = line[i];
        if (char.IsWhiteSpace(c) || c == '=')
        {
            return i + 1;
        }

        if (c == '>' || (c == '/' && i + 1 < n && line[i + 1] == '>'))
        {
            int stop = c == '>' ? i + 1 : i + 2;
            Add(tokens, i, stop, SyntaxTokenKind.Tag);
            state = 0;
            return stop;
        }

        if (c is '"' or '\'')
        {
            int end = line[(i + 1)..].IndexOf(c);
            int stop = end < 0 ? n : i + 1 + end + 1;
            Add(tokens, i, stop, SyntaxTokenKind.StringLiteral);
            state = end < 0 ? (c == '"' ? 3 : 4) : 2;
            return stop;
        }

        int nameEnd = i;
        while (nameEnd < n && !char.IsWhiteSpace(line[nameEnd]) && line[nameEnd] is not ('=' or '>' or '/' or '"' or '\''))
        {
            nameEnd++;
        }

        if (nameEnd == i)
        {
            return i + 1;
        }

        Add(tokens, i, nameEnd, SyntaxTokenKind.Attribute);
        return nameEnd;
    }
}

/// <summary>
/// CSS, SCSS and Less: comments, strings, numbers with units, at-rules, colors, and property names inside braces.
/// State: bit 0 = inside a comment; bits 1+ = brace depth (capped).
/// </summary>
public sealed class CssLanguage : SyntaxLanguage
{
    public CssLanguage(string id, string displayName)
        : base(id, displayName)
    {
    }

    public override int Tokenize(ReadOnlySpan<char> line, int state, List<SyntaxToken> tokens)
    {
        int n = line.Length;
        bool comment = (state & 1) != 0;
        int depth = state >> 1;
        int i = 0;
        while (i < n)
        {
            if (comment)
            {
                int end = line[i..].IndexOf("*/");
                int stop = end < 0 ? n : i + end + 2;
                Add(tokens, i, stop, SyntaxTokenKind.Comment);
                comment = end < 0;
                i = stop;
                continue;
            }

            char c = line[i];
            if (c == '/' && i + 1 < n && line[i + 1] == '*')
            {
                comment = true;
                continue;
            }

            if (c == '/' && i + 1 < n && line[i + 1] == '/')
            {
                Add(tokens, i, n, SyntaxTokenKind.Comment); // SCSS/Less line comment
                break;
            }

            if (c is '"' or '\'')
            {
                int end = line[(i + 1)..].IndexOf(c);
                int stop = end < 0 ? n : i + end + 2;
                Add(tokens, i, stop, SyntaxTokenKind.StringLiteral);
                i = stop;
                continue;
            }

            if (c == '{')
            {
                depth = Math.Min(depth + 1, 1000);
                i++;
                continue;
            }

            if (c == '}')
            {
                depth = Math.Max(depth - 1, 0);
                i++;
                continue;
            }

            if (c == '@' || (c == '$' && i + 1 < n && IsIdentifierStart(line[i + 1])))
            {
                int end = Word(line, i + 1);
                Add(tokens, i, end, c == '@' ? SyntaxTokenKind.Keyword : SyntaxTokenKind.Variable);
                i = end;
                continue;
            }

            if (c == '#' && depth > 0 && i + 1 < n && char.IsAsciiHexDigit(line[i + 1]))
            {
                int end = Word(line, i + 1);
                Add(tokens, i, end, SyntaxTokenKind.Number);
                i = end;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c is '.' or '-' && i + 1 < n && char.IsAsciiDigit(line[i + 1])))
            {
                int end = ScanNumber(line, c == '-' || c == '.' ? i + 1 : i);
                if (end < n && line[end] == '%')
                {
                    end++;
                }

                Add(tokens, i, end, SyntaxTokenKind.Number);
                i = end;
                continue;
            }

            if (IsIdentifierStart(c) || c == '-')
            {
                int end = Word(line, i);
                int after = end;
                while (after < n && line[after] is ' ' or '\t')
                {
                    after++;
                }

                bool property = depth > 0 && after < n && line[after] == ':' && !(after + 1 < n && line[after + 1] == ':');
                SyntaxTokenKind kind = property ? SyntaxTokenKind.Property : depth == 0 ? SyntaxTokenKind.Tag : SyntaxTokenKind.Text;
                if (!property && end < n && line[end] == '(')
                {
                    kind = SyntaxTokenKind.Keyword; // url(), rgb(), var()
                }

                Add(tokens, i, end, kind);
                i = end;
                continue;
            }

            i++;
        }

        return (comment ? 1 : 0) | (depth << 1);
    }

    private static int Word(ReadOnlySpan<char> line, int i)
    {
        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '-' or '_'))
        {
            i++;
        }

        return i;
    }
}

/// <summary>YAML: keys, comments, strings, numbers, literals, anchors, tags and document markers. No state.</summary>
public sealed class YamlLanguage : SyntaxLanguage
{
    private static readonly HashSet<string> Literals = new(["true", "false", "null", "yes", "no", "on", "off", "~", "True", "False", "Null", "TRUE", "FALSE", "NULL"], StringComparer.Ordinal);

    public YamlLanguage()
        : base("yaml", "YAML")
    {
    }

    public override int Tokenize(ReadOnlySpan<char> line, int state, List<SyntaxToken> tokens)
    {
        int n = line.Length;
        int i = 0;
        while (i < n && line[i] == ' ')
        {
            i++;
        }

        if (line[i..].StartsWith("---") || line[i..].StartsWith("..."))
        {
            Add(tokens, i, n, SyntaxTokenKind.Preprocessor);
            return 0;
        }

        while (i + 1 < n && line[i] == '-' && line[i + 1] == ' ')
        {
            Add(tokens, i, i + 1, SyntaxTokenKind.Keyword);
            i += 2;
            while (i < n && line[i] == ' ')
            {
                i++;
            }
        }

        // key: value
        int colon = KeyEnd(line, i);
        if (colon > i)
        {
            Add(tokens, i, colon, SyntaxTokenKind.Property);
            i = colon + 1;
        }

        while (i < n)
        {
            char c = line[i];
            if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                Add(tokens, i, n, SyntaxTokenKind.Comment);
                break;
            }

            if (c is '"' or '\'')
            {
                int end = line[(i + 1)..].IndexOf(c);
                int stop = end < 0 ? n : i + end + 2;
                Add(tokens, i, stop, SyntaxTokenKind.StringLiteral);
                i = stop;
                continue;
            }

            if (c is '&' or '*' && i + 1 < n && !char.IsWhiteSpace(line[i + 1]))
            {
                int end = i + 1;
                while (end < n && !char.IsWhiteSpace(line[end]))
                {
                    end++;
                }

                Add(tokens, i, end, SyntaxTokenKind.Variable);
                i = end;
                continue;
            }

            if (c == '!' && i + 1 < n && line[i + 1] != ' ')
            {
                int end = i + 1;
                while (end < n && !char.IsWhiteSpace(line[end]))
                {
                    end++;
                }

                Add(tokens, i, end, SyntaxTokenKind.Type);
                i = end;
                continue;
            }

            if (char.IsWhiteSpace(c) || c is ',' or '[' or ']' or '{' or '}')
            {
                i++;
                continue;
            }

            int wordEnd = i;
            while (wordEnd < n && !char.IsWhiteSpace(line[wordEnd]) && line[wordEnd] is not (',' or ']' or '}'))
            {
                wordEnd++;
            }

            ReadOnlySpan<char> word = line[i..wordEnd];
            if (Literals.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word))
            {
                Add(tokens, i, wordEnd, SyntaxTokenKind.Literal);
            }
            else if (double.TryParse(word, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                Add(tokens, i, wordEnd, SyntaxTokenKind.Number);
            }

            i = wordEnd;
        }

        return 0;
    }

    /// <summary>End of a plain or quoted key followed by ": " (or ':' at the end of the line), or -1.</summary>
    private static int KeyEnd(ReadOnlySpan<char> line, int i)
    {
        int n = line.Length;
        if (i >= n || line[i] is '#' or '"' or '\'' or '[' or '{' or '|' or '>')
        {
            return -1;
        }

        for (int j = i; j < n; j++)
        {
            char c = line[j];
            if (c == ':' && (j + 1 == n || line[j + 1] == ' '))
            {
                return j;
            }

            if (c == '#' && j > i && line[j - 1] == ' ')
            {
                return -1;
            }
        }

        return -1;
    }
}

/// <summary>
/// INI, TOML, .properties, .env and registry files: [sections], keys, comments, strings and values.
/// State: 1 = inside a TOML multi-line string ("""…""").
/// </summary>
public sealed class IniLanguage : SyntaxLanguage
{
    private readonly bool _toml;

    public IniLanguage(string id, string displayName, bool toml = false)
        : base(id, displayName)
    {
        _toml = toml;
    }

    public override int Tokenize(ReadOnlySpan<char> line, int state, List<SyntaxToken> tokens)
    {
        int n = line.Length;
        int i = 0;
        if (state == 1)
        {
            int end = line.IndexOf("\"\"\"");
            if (end < 0)
            {
                Add(tokens, 0, n, SyntaxTokenKind.StringLiteral);
                return 1;
            }

            Add(tokens, 0, end + 3, SyntaxTokenKind.StringLiteral);
            i = end + 3;
        }

        while (i < n && char.IsWhiteSpace(line[i]))
        {
            i++;
        }

        if (i >= n)
        {
            return 0;
        }

        if (line[i] is ';' or '#' || (line[i] == '!' && !_toml))
        {
            Add(tokens, i, n, SyntaxTokenKind.Comment);
            return 0;
        }

        if (line[i] == '[')
        {
            int end = line[i..].IndexOf(']');
            Add(tokens, i, end < 0 ? n : i + end + 1, SyntaxTokenKind.Heading);
            return 0;
        }

        int separator = line[i..].IndexOfAny('=', ':');
        if (separator > 0)
        {
            Add(tokens, i, i + separator, SyntaxTokenKind.Property);
            i += separator + 1;
        }

        while (i < n)
        {
            char c = line[i];
            if (c == '#' && _toml)
            {
                Add(tokens, i, n, SyntaxTokenKind.Comment);
                break;
            }

            if (_toml && line[i..].StartsWith("\"\"\""))
            {
                int end = line[(i + 3)..].IndexOf("\"\"\"");
                if (end < 0)
                {
                    Add(tokens, i, n, SyntaxTokenKind.StringLiteral);
                    return 1;
                }

                Add(tokens, i, i + 3 + end + 3, SyntaxTokenKind.StringLiteral);
                i += 3 + end + 3;
                continue;
            }

            if (c is '"' or '\'')
            {
                int end = line[(i + 1)..].IndexOf(c);
                int stop = end < 0 ? n : i + end + 2;
                Add(tokens, i, stop, SyntaxTokenKind.StringLiteral);
                i = stop;
                continue;
            }

            if (_toml && (char.IsAsciiDigit(c) || (c is '-' or '+' && i + 1 < n && char.IsAsciiDigit(line[i + 1]))))
            {
                int end = ScanNumber(line, char.IsAsciiDigit(c) ? i : i + 1);
                Add(tokens, i, end, SyntaxTokenKind.Number);
                i = end;
                continue;
            }

            if (_toml && IsIdentifierStart(c))
            {
                int end = i;
                while (end < n && IsIdentifierPart(line[end]))
                {
                    end++;
                }

                if (line[i..end] is "true" or "false")
                {
                    Add(tokens, i, end, SyntaxTokenKind.Literal);
                }

                i = end;
                continue;
            }

            i++;
        }

        return 0;
    }
}

/// <summary>
/// Markdown source (the "source" view of a Markdown file): headings, fenced code, quotes, list markers, inline code,
/// links and emphasis markers. State: 1 = inside a fenced code block opened with ```, 2 = with ~~~.
/// </summary>
public sealed class MarkdownSourceLanguage : SyntaxLanguage
{
    public MarkdownSourceLanguage()
        : base("markdown", "Markdown")
    {
    }

    public override int Tokenize(ReadOnlySpan<char> line, int state, List<SyntaxToken> tokens)
    {
        int n = line.Length;
        int i = 0;
        while (i < n && i < 4 && line[i] == ' ')
        {
            i++;
        }

        ReadOnlySpan<char> rest = line[i..];
        bool backticks = rest.StartsWith("```");
        bool tildes = rest.StartsWith("~~~");
        if (state != 0)
        {
            if ((state == 1 && backticks) || (state == 2 && tildes))
            {
                Add(tokens, 0, n, SyntaxTokenKind.Preprocessor);
                return 0;
            }

            Add(tokens, 0, n, SyntaxTokenKind.StringLiteral);
            return state;
        }

        if (backticks || tildes)
        {
            Add(tokens, 0, n, SyntaxTokenKind.Preprocessor);
            return backticks ? 1 : 2;
        }

        if (rest.StartsWith("#"))
        {
            Add(tokens, 0, n, SyntaxTokenKind.Heading);
            return 0;
        }

        if (rest.StartsWith(">"))
        {
            Add(tokens, 0, n, SyntaxTokenKind.Comment);
            return 0;
        }

        if (rest.Length >= 2 && rest[0] is '-' or '*' or '+' && rest[1] == ' ')
        {
            Add(tokens, i, i + 1, SyntaxTokenKind.Keyword);
            i += 2;
        }
        else
        {
            int digits = 0;
            while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            {
                digits++;
            }

            if (digits > 0 && digits + 1 < rest.Length && rest[digits] is '.' or ')' && rest[digits + 1] == ' ')
            {
                Add(tokens, i, i + digits + 1, SyntaxTokenKind.Keyword);
                i += digits + 2;
            }
        }

        while (i < n)
        {
            char c = line[i];
            if (c == '`')
            {
                int end = line[(i + 1)..].IndexOf('`');
                int stop = end < 0 ? n : i + end + 2;
                Add(tokens, i, stop, SyntaxTokenKind.StringLiteral);
                i = stop;
                continue;
            }

            if (c == ']' && i + 1 < n && line[i + 1] == '(')
            {
                int end = line[(i + 2)..].IndexOf(')');
                int stop = end < 0 ? n : i + 2 + end + 1;
                Add(tokens, i + 1, stop, SyntaxTokenKind.Attribute);
                i = stop;
                continue;
            }

            if (c is '*' or '_' && i + 1 < n && line[i + 1] == c)
            {
                Add(tokens, i, i + 2, SyntaxTokenKind.Keyword);
                i += 2;
                continue;
            }

            i++;
        }

        return 0;
    }
}

/// <summary>Unified diffs and patches: added, removed, hunk and header lines. No state.</summary>
public sealed class DiffLanguage : SyntaxLanguage
{
    public DiffLanguage()
        : base("diff", "Diff")
    {
    }

    public override int Tokenize(ReadOnlySpan<char> line, int state, List<SyntaxToken> tokens)
    {
        int n = line.Length;
        if (n == 0)
        {
            return 0;
        }

        SyntaxTokenKind kind = line switch
        {
            _ when line.StartsWith("+++") || line.StartsWith("---") || line.StartsWith("diff ") || line.StartsWith("index ") => SyntaxTokenKind.Preprocessor,
            _ when line.StartsWith("@@") => SyntaxTokenKind.Heading,
            _ when line[0] == '+' || line[0] == '>' => SyntaxTokenKind.Inserted,
            _ when line[0] == '-' || line[0] == '<' => SyntaxTokenKind.Deleted,
            _ => SyntaxTokenKind.Text,
        };
        Add(tokens, 0, n, kind);
        return 0;
    }
}

/// <summary>Log files: error and warning levels, quoted strings and numbers (dates, times, durations). No state.</summary>
public sealed class LogLanguage : SyntaxLanguage
{
    private static readonly HashSet<string> Errors = new(["ERROR", "ERR", "FATAL", "CRITICAL", "CRIT", "FAIL", "FAILED", "FAILURE", "EXCEPTION", "PANIC", "SEVERE", "ALERT", "EMERG"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Warnings = new(["WARN", "WARNING", "WRN"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Levels = new(["INFO", "INF", "DEBUG", "DBG", "TRACE", "TRC", "VERBOSE", "NOTICE"], StringComparer.OrdinalIgnoreCase);

    public LogLanguage()
        : base("log", "Log")
    {
    }

    public override int Tokenize(ReadOnlySpan<char> line, int state, List<SyntaxToken> tokens)
    {
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> errors = Errors.GetAlternateLookup<ReadOnlySpan<char>>();
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> warnings = Warnings.GetAlternateLookup<ReadOnlySpan<char>>();
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> levels = Levels.GetAlternateLookup<ReadOnlySpan<char>>();
        int n = line.Length;
        int i = 0;
        while (i < n)
        {
            char c = line[i];
            if (c == '"')
            {
                int end = line[(i + 1)..].IndexOf('"');
                int stop = end < 0 ? n : i + end + 2;
                Add(tokens, i, stop, SyntaxTokenKind.StringLiteral);
                i = stop;
                continue;
            }

            if (char.IsAsciiDigit(c) && (i == 0 || !char.IsLetter(line[i - 1])))
            {
                int end = i;
                while (end < n && (char.IsAsciiDigit(line[end]) || line[end] is '-' or ':' or '.' or '/' or ',' or 'T' or 'Z'))
                {
                    end++;
                }

                Add(tokens, i, end, SyntaxTokenKind.Number);
                i = end;
                continue;
            }

            if (char.IsLetter(c))
            {
                int end = i;
                while (end < n && char.IsLetter(line[end]))
                {
                    end++;
                }

                ReadOnlySpan<char> word = line[i..end];
                SyntaxTokenKind kind = errors.Contains(word) ? SyntaxTokenKind.Error
                    : warnings.Contains(word) ? SyntaxTokenKind.Warning
                    : levels.Contains(word) ? SyntaxTokenKind.Keyword
                    : SyntaxTokenKind.Text;
                Add(tokens, i, end, kind);
                i = end;
                continue;
            }

            i++;
        }

        return 0;
    }
}
