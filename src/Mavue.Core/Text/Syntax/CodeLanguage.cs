namespace Mavue.Core.Text.Syntax;

/// <summary>A string literal form of a language.</summary>
/// <param name="Open">Opening delimiter ("\"", "@\"", "\"\"\"", "`").</param>
/// <param name="Close">Closing delimiter.</param>
/// <param name="Escape">Escape character inside the string ('\\'), or '\0' for none.</param>
/// <param name="Multiline">The string may continue on the next lines.</param>
/// <param name="DoubledClose">A doubled closing delimiter stands for itself (C# verbatim, SQL, VB, PowerShell '').</param>
public sealed record StringForm(string Open, string Close, char Escape = '\\', bool Multiline = false, bool DoubledClose = false);

/// <summary>
/// Definition of a C-family style language: comments, strings, numbers and word lists. Covers most programming and
/// scripting languages; markup, data and document formats have their own lexers.
/// </summary>
public sealed class CodeLanguageDefinition
{
    public string[] LineComments { get; init; } = [];

    public (string Open, string Close)[] BlockComments { get; init; } = [];

    /// <summary>Longest forms first where one starts with another ("\"\"\"" before "\"").</summary>
    public StringForm[] Strings { get; init; } = [];

    public string Keywords { get; init; } = string.Empty;

    public string Types { get; init; } = string.Empty;

    public string Literals { get; init; } = "true false null";

    public bool CaseInsensitive { get; init; }

    /// <summary>A line starting with '#' is a preprocessor line (C, C#).</summary>
    public bool HashPreprocessor { get; init; }

    /// <summary>'@name' is a decorator or annotation (Python, Java, Kotlin, TypeScript).</summary>
    public bool Decorators { get; init; }

    /// <summary>Characters that start a variable ("$" in shells, PHP, PowerShell; "%" in batch files).</summary>
    public string VariablePrefixes { get; init; } = string.Empty;

    /// <summary>Words starting with an upper-case letter are types (C#, Java, Kotlin, Swift, Rust, Go, TypeScript).</summary>
    public bool CapitalizedTypes { get; init; }

    /// <summary>A string or word directly followed by ':' is a key (JSON).</summary>
    public bool KeysBeforeColon { get; init; }

    /// <summary>Letters allowed directly before a string's opening quote (Python r"", f"", b""; C# $"").</summary>
    public string StringPrefixes { get; init; } = string.Empty;

    /// <summary>Extra characters inside words ("-" in CSS and Lisp, "?" and "!" in Ruby).</summary>
    public string WordCharacters { get; init; } = string.Empty;
}

/// <summary>The lexer for <see cref="CodeLanguageDefinition"/> languages.</summary>
/// <remarks>
/// State: 0 = normal; 1 + i = inside block comment i; 64 + j = inside multi-line string form j.
/// </remarks>
public sealed class CodeLanguage : SyntaxLanguage
{
    private const int StringState = 64;

    private readonly CodeLanguageDefinition _d;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _keywords;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _types;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _literals;
    private readonly StringComparison _comparison;
    private readonly HashSet<char> _stringStarts;

    public CodeLanguage(string id, string displayName, CodeLanguageDefinition definition)
        : base(id, displayName)
    {
        _d = definition;
        StringComparer comparer = definition.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _comparison = definition.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _keywords = Words(definition.Keywords, comparer);
        _types = Words(definition.Types, comparer);
        _literals = Words(definition.Literals, comparer);
        _stringStarts = [.. definition.Strings.Select(s => s.Open[0])];
    }

    public override int Tokenize(ReadOnlySpan<char> line, int state, List<SyntaxToken> tokens)
    {
        int n = line.Length;
        int i = 0;
        if (state is > 0 and < StringState && state - 1 < _d.BlockComments.Length)
        {
            (_, string close) = _d.BlockComments[state - 1];
            int end = line.IndexOf(close.AsSpan(), _comparison);
            if (end < 0)
            {
                Add(tokens, 0, n, SyntaxTokenKind.Comment);
                return state;
            }

            i = end + close.Length;
            Add(tokens, 0, i, SyntaxTokenKind.Comment);
        }
        else if (state >= StringState && state - StringState < _d.Strings.Length)
        {
            StringForm form = _d.Strings[state - StringState];
            int end = FindStringEnd(line, 0, form);
            if (end < 0)
            {
                Add(tokens, 0, n, SyntaxTokenKind.StringLiteral);
                return state;
            }

            i = end;
            Add(tokens, 0, i, SyntaxTokenKind.StringLiteral);
        }

        if (i == 0 && _d.HashPreprocessor)
        {
            int first = 0;
            while (first < n && char.IsWhiteSpace(line[first]))
            {
                first++;
            }

            if (first < n && line[first] == '#')
            {
                Add(tokens, first, n, SyntaxTokenKind.Preprocessor);
                return 0;
            }
        }

        while (i < n)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (StartsLineComment(line, i))
            {
                Add(tokens, i, n, SyntaxTokenKind.Comment);
                return 0;
            }

            int block = BlockCommentAt(line, i);
            if (block >= 0)
            {
                (string open, string close) = _d.BlockComments[block];
                int end = line[(i + open.Length)..].IndexOf(close.AsSpan(), _comparison);
                if (end < 0)
                {
                    Add(tokens, i, n, SyntaxTokenKind.Comment);
                    return 1 + block;
                }

                int stop = i + open.Length + end + close.Length;
                Add(tokens, i, stop, SyntaxTokenKind.Comment);
                i = stop;
                continue;
            }

            if (_stringStarts.Contains(c) && StringFormAt(line, i) is { } form)
            {
                int? next = ScanString(line, i, i, form.Index, tokens);
                if (next is null)
                {
                    return StringState + form.Index;
                }

                i = next.Value;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(line[i + 1])))
            {
                int end = ScanNumber(line, i);
                Add(tokens, i, end, SyntaxTokenKind.Number);
                i = end;
                continue;
            }

            if (_d.VariablePrefixes.Contains(c) && i + 1 < n && (IsIdentifierStart(line[i + 1]) || line[i + 1] == '{' || (c == '%' && char.IsAsciiDigit(line[i + 1]))))
            {
                int end = ScanVariable(line, i);
                Add(tokens, i, end, SyntaxTokenKind.Variable);
                i = end;
                continue;
            }

            if (_d.Decorators && c == '@' && i + 1 < n && IsIdentifierStart(line[i + 1]))
            {
                int end = i + 1;
                while (end < n && (IsIdentifierPart(line[end]) || line[end] == '.'))
                {
                    end++;
                }

                Add(tokens, i, end, SyntaxTokenKind.Preprocessor);
                i = end;
                continue;
            }

            if (IsIdentifierStart(c))
            {
                int end = i + 1;
                while (end < n && (IsIdentifierPart(line[end]) || _d.WordCharacters.Contains(line[end])))
                {
                    end++;
                }

                // A string prefix (r"…", f"…", $"…") belongs to the string.
                if (end < n && _stringStarts.Contains(line[end]) && end - i <= 3 && IsStringPrefix(line[i..end]) && StringFormAt(line, end) is { } prefixed)
                {
                    int? next = ScanString(line, i, end, prefixed.Index, tokens);
                    if (next is null)
                    {
                        return StringState + prefixed.Index;
                    }

                    i = next.Value;
                    continue;
                }

                Add(tokens, i, end, Classify(line[i..end], line, end));
                i = end;
                continue;
            }

            i++;
        }

        return 0;
    }

    private static HashSet<string>.AlternateLookup<ReadOnlySpan<char>> Words(string list, StringComparer comparer) =>
        new HashSet<string>(list.Split(' ', StringSplitOptions.RemoveEmptyEntries), comparer).GetAlternateLookup<ReadOnlySpan<char>>();

    private SyntaxTokenKind Classify(ReadOnlySpan<char> word, ReadOnlySpan<char> line, int end)
    {
        if (_d.KeysBeforeColon && NextNonSpace(line, end) == ':')
        {
            return SyntaxTokenKind.Property;
        }

        if (_keywords.Contains(word))
        {
            return SyntaxTokenKind.Keyword;
        }

        if (_literals.Contains(word))
        {
            return SyntaxTokenKind.Literal;
        }

        if (_types.Contains(word) || (_d.CapitalizedTypes && char.IsUpper(word[0]) && word.Length > 1 && HasLowercase(word)))
        {
            return SyntaxTokenKind.Type;
        }

        return SyntaxTokenKind.Text;
    }

    private static bool HasLowercase(ReadOnlySpan<char> word)
    {
        foreach (char c in word)
        {
            if (char.IsLower(c))
            {
                return true;
            }
        }

        return false; // ALL_CAPS words are usually constants, not types
    }

    private static char NextNonSpace(ReadOnlySpan<char> line, int i)
    {
        while (i < line.Length && line[i] is ' ' or '\t')
        {
            i++;
        }

        return i < line.Length ? line[i] : '\0';
    }

    private bool IsStringPrefix(ReadOnlySpan<char> word)
    {
        foreach (char c in word)
        {
            if (!_d.StringPrefixes.Contains(c))
            {
                return false;
            }
        }

        return true;
    }

    private bool StartsLineComment(ReadOnlySpan<char> line, int i)
    {
        foreach (string marker in _d.LineComments)
        {
            if (line[i..].StartsWith(marker, _comparison))
            {
                // Word-like markers (REM, ::) need a word boundary after them.
                if (char.IsLetter(marker[^1]) && i + marker.Length < line.Length && IsIdentifierPart(line[i + marker.Length]))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private int BlockCommentAt(ReadOnlySpan<char> line, int i)
    {
        for (int k = 0; k < _d.BlockComments.Length; k++)
        {
            if (line[i..].StartsWith(_d.BlockComments[k].Open, _comparison))
            {
                return k;
            }
        }

        return -1;
    }

    private (StringForm Form, int Index)? StringFormAt(ReadOnlySpan<char> line, int i)
    {
        for (int k = 0; k < _d.Strings.Length; k++)
        {
            if (line[i..].StartsWith(_d.Strings[k].Open, StringComparison.Ordinal))
            {
                return (_d.Strings[k], k);
            }
        }

        return null;
    }

    /// <summary>Emits a string starting at <paramref name="start"/> (its delimiter at <paramref name="open"/>); null when it continues on the next line.</summary>
    private int? ScanString(ReadOnlySpan<char> line, int start, int open, int formIndex, List<SyntaxToken> tokens)
    {
        StringForm form = _d.Strings[formIndex];
        int end = FindStringEnd(line, open + form.Open.Length, form);
        if (end < 0)
        {
            Add(tokens, start, line.Length, SyntaxTokenKind.StringLiteral);
            return form.Multiline ? null : line.Length;
        }

        SyntaxTokenKind kind = _d.KeysBeforeColon && NextNonSpace(line, end) == ':' ? SyntaxTokenKind.Property : SyntaxTokenKind.StringLiteral;
        Add(tokens, start, end, kind);
        return end;
    }

    /// <summary>Index just after the closing delimiter, or -1 when the line ends first.</summary>
    private static int FindStringEnd(ReadOnlySpan<char> line, int from, StringForm form)
    {
        int n = line.Length;
        int i = from;
        while (i < n)
        {
            char c = line[i];
            if (form.Escape != '\0' && c == form.Escape)
            {
                i += 2;
                continue;
            }

            if (line[i..].StartsWith(form.Close, StringComparison.Ordinal))
            {
                if (form.DoubledClose && line[(i + form.Close.Length)..].StartsWith(form.Close, StringComparison.Ordinal))
                {
                    i += 2 * form.Close.Length;
                    continue;
                }

                return i + form.Close.Length;
            }

            i++;
        }

        return -1;
    }

    private static int ScanVariable(ReadOnlySpan<char> line, int i)
    {
        int n = line.Length;
        char prefix = line[i];
        int end = i + 1;
        if (end < n && line[end] == '{')
        {
            int close = line[end..].IndexOf('}');
            return close < 0 ? n : end + close + 1;
        }

        while (end < n && (IsIdentifierPart(line[end]) || (prefix == '$' && line[end] == ':' && end + 1 < n && IsIdentifierStart(line[end + 1]))))
        {
            end++; // $env:PATH in PowerShell
        }

        if (prefix == '%' && end < n && line[end] == '%')
        {
            end++; // %VAR% in batch files
        }

        return end;
    }
}
