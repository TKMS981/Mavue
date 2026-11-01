using System.Buffers;
using System.Text;
using Mavue.Core.Text.Syntax;

namespace Mavue.Core.Text;

/// <summary>One displayed line: its text (tabs expanded, control characters made visible, cut when very long) and colors.</summary>
/// <param name="Text">The text to draw.</param>
/// <param name="Tokens">Colored spans (null for plain text).</param>
/// <param name="Cut">The line was longer than <see cref="TextPreviewLimits.MaxLineChars"/>.</param>
public sealed record TextLine(string Text, SyntaxToken[]? Tokens, bool Cut);

/// <summary>
/// A text file opened for preview (docs/ARCHITECTURE.md §4.7.12): the encoding detected from the first 64 KiB, a line
/// index built in the background (<see cref="BuildIndex"/>), and random access to any range of lines, read from
/// the file on demand and colored by a <see cref="SyntaxLanguage"/>. The file is opened read-only and shared (other
/// programs may keep writing it; what was indexed is shown). Never holds more than the requested lines in memory.
/// Thread-safe: the index may be built on one thread while lines are read on another.
/// </summary>
public sealed class TextDocument : IDisposable
{
    private const int TabSize = 4;
    private const int ReadBlock = 256 * 1024;

    /// <summary>States of the highlighter at line starts, remembered every this many lines while reading.</summary>
    private const int StateStride = 128;

    /// <summary>A state further back than this is not used: colors start fresh this many lines before the request.</summary>
    private const int MaxLexBack = 5_000;

    private readonly FileStream _stream;
    private readonly Lock _gate = new();
    private readonly SortedList<int, int> _states = [];

    private TextDocument(FileStream stream, TextEncodingInfo encoding, TextPreviewLimits limits, SyntaxLanguage? language, long length)
    {
        _stream = stream;
        Encoding = encoding;
        Limits = limits;
        Language = language;
        Length = length;
        Index = new TextLineIndex(encoding, limits);
    }

    public TextEncodingInfo Encoding { get; }

    public TextPreviewLimits Limits { get; }

    /// <summary>The highlighter, or null for plain text.</summary>
    public SyntaxLanguage? Language { get; }

    /// <summary>File size when opened.</summary>
    public long Length { get; }

    public TextLineIndex Index { get; }

    /// <summary>
    /// Opens <paramref name="path"/> and detects its encoding. Returns null when the content is binary.
    /// <paramref name="language"/> colors the lines (null: plain text).
    /// </summary>
    public static TextDocument? Open(string path, TextPreviewLimits limits, SyntaxLanguage? language, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(limits);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.RandomAccess);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            long length = stream.Length;
            byte[] sample = ArrayPool<byte>.Shared.Rent(TextPreviewLimits.SampleBytes);
            try
            {
                int read = stream.ReadAtLeast(sample.AsSpan(0, TextPreviewLimits.SampleBytes), TextPreviewLimits.SampleBytes, throwOnEndOfStream: false);
                TextDetection detection = TextEncodingDetector.Detect(sample.AsSpan(0, read), wholeFile: read >= length);
                if (!detection.IsText)
                {
                    stream.Dispose();
                    return null;
                }

                return new TextDocument(stream, detection.Encoding!, limits, language, length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sample);
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Builds the line index on the calling thread (a separate handle to the file; see <see cref="TextLineIndex.Build"/>).</summary>
    public void BuildIndex(string path, CancellationToken cancellation, Action? progress = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        Index.Build(stream, cancellation, progress);
    }

    /// <summary>
    /// Reads up to <paramref name="count"/> lines from <paramref name="first"/> (fewer at the end of what is indexed),
    /// decoded and colored. Lines before <paramref name="first"/> are tokenized from the nearest known highlighter
    /// state so multi-line comments and strings keep their colors.
    /// </summary>
    public IReadOnlyList<TextLine> ReadLines(int first, int count, CancellationToken cancellation)
    {
        int available = Index.LineCount;
        first = Math.Clamp(first, 0, Math.Max(0, available - 1));
        count = Math.Clamp(count, 0, available - first);
        if (count <= 0)
        {
            return [];
        }

        // Where the highlighter's state is known (or assumed fresh).
        int lexFrom = first;
        int state = 0;
        if (Language is not null)
        {
            lock (_gate)
            {
                (int line, int value)? known = NearestState(first);
                if (known is { } k && first - k.line <= MaxLexBack)
                {
                    (lexFrom, state) = (k.line, k.value);
                }
                else
                {
                    lexFrom = Math.Max(0, first - 200);
                }
            }
        }

        var lines = new List<TextLine>(count);
        var tokens = new List<SyntaxToken>();
        int lineNumber = lexFrom;
        foreach ((string text, bool cut) in ReadRaw(lexFrom, first + count - lexFrom, cancellation))
        {
            string shown = DisplayLine(text);
            SyntaxToken[]? colored = null;
            if (Language is not null)
            {
                if (lineNumber % StateStride == 0)
                {
                    RememberState(lineNumber, state);
                }

                tokens.Clear();
                state = Language.Tokenize(shown, state, tokens);
                colored = tokens.Count > 0 ? [.. tokens] : null;
            }

            if (lineNumber >= first)
            {
                lines.Add(new TextLine(shown, colored, cut));
            }

            lineNumber++;
        }

        return lines;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream.Dispose();
        }
    }

    /// <summary>Tabs to spaces (stops of 4), C0 control characters to their visible symbols (␛, ␀…), bidirectional
    /// override and isolate characters shown as their code points (they could reorder what is displayed).</summary>
    public static string DisplayLine(string text)
    {
        bool plain = true;
        foreach (char c in text)
        {
            if (c < 0x20 || c == 0x7F || IsBidiControl(c))
            {
                plain = false;
                break;
            }
        }

        if (plain)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            if (c == '\t')
            {
                builder.Append(' ', TabSize - (builder.Length % TabSize));
            }
            else if (c < 0x20)
            {
                builder.Append((char)(0x2400 + c));
            }
            else if (c == 0x7F)
            {
                builder.Append('␡');
            }
            else if (IsBidiControl(c))
            {
                builder.Append("<U+").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)).Append('>');
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool IsBidiControl(char c) => c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');

    private (int line, int value)? NearestState(int line)
    {
        IList<int> keys = _states.Keys;
        int low = 0, high = keys.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = (low + high) >> 1;
            if (keys[mid] <= line)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found < 0 ? (line > 0 ? null : (0, 0)) : (keys[found], _states.Values[found]);
    }

    private void RememberState(int line, int state)
    {
        lock (_gate)
        {
            if (_states.Count < 100_000)
            {
                _states[line] = state;
            }
        }
    }

    /// <summary>The raw text of <paramref name="count"/> lines from <paramref name="first"/> (line breaks removed).</summary>
    private List<(string Text, bool Cut)> ReadRaw(int first, int count, CancellationToken cancellation)
    {
        var result = new List<(string, bool)>(count);
        int unit = Encoding.UnitSize;
        long maxLineBytes = ((long)Limits.MaxLineChars * 4) + unit;
        (int line, long offset) = Index.CheckpointAtOrBefore(first);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBlock);
        Span<byte> next = stackalloc byte[4];
        try
        {
            long bufferStart = -1;
            int bufferLength = 0;
            var current = new List<byte>();
            while (result.Count < count)
            {
                cancellation.ThrowIfCancellationRequested();
                if (line >= Index.LineCount)
                {
                    break;
                }

                // One line from offset: up to its break, cut at maxLineBytes.
                int taken = 0;
                bool cut = false;
                long breakAt = -1;
                int breakLength = 0;
                current.Clear();
                while (true)
                {
                    if (bufferStart < 0 || offset < bufferStart || offset >= bufferStart + bufferLength)
                    {
                        bufferStart = offset;
                        bufferLength = ReadAt(offset, buffer);
                        bufferLength -= bufferLength % unit;
                        if (bufferLength <= 0)
                        {
                            break; // end of file
                        }
                    }

                    int at = (int)(offset - bufferStart);
                    ReadOnlySpan<byte> span = buffer.AsSpan(at, bufferLength - at);
                    int found = TextLineIndex.FindLineBreak(span, unit, Encoding.BigEndian);
                    int chunk = found < 0 ? span.Length : found;
                    int keep = (int)Math.Min(chunk, Math.Max(0, maxLineBytes - taken));
                    if (keep > 0)
                    {
                        current.AddRange(span[..keep]);
                        taken += keep;
                    }

                    if (chunk > keep)
                    {
                        cut = true;
                    }

                    if (found >= 0)
                    {
                        breakAt = offset + found;
                        bool cr = TextLineIndex.UnitIs(span, found, '\r', unit, Encoding.BigEndian);
                        breakLength = unit;
                        if (cr)
                        {
                            if (found + unit < span.Length)
                            {
                                breakLength = TextLineIndex.UnitIs(span, found + unit, '\n', unit, Encoding.BigEndian) ? 2 * unit : unit;
                            }
                            else
                            {
                                // CR at the end of the buffer: look at the next unit.
                                int got = ReadAt(breakAt + unit, next[..unit]);
                                breakLength = got == unit && TextLineIndex.UnitIs(next, 0, '\n', unit, Encoding.BigEndian) ? 2 * unit : unit;
                            }
                        }

                        break;
                    }

                    offset += span.Length;
                    if (cut)
                    {
                        // A very long line: its end is found through the index (the next line starts a checkpoint).
                        long? nextStart = Index.CheckpointOf(line + 1);
                        if (nextStart is not null)
                        {
                            breakAt = nextStart.Value;
                            breakLength = 0;
                            break;
                        }

                        if (line + 1 >= Index.LineCount)
                        {
                            break; // the last indexed line: nothing after it is shown yet
                        }
                    }
                }

                if (line >= first)
                {
                    string decoded = Decode(current, cut);
                    bool longer = decoded.Length > Limits.MaxLineChars;
                    result.Add((longer ? decoded[..Limits.MaxLineChars] : decoded, cut || longer));
                }

                line++;
                if (breakAt < 0)
                {
                    break; // end of the file
                }

                offset = breakAt + breakLength;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

    private int ReadAt(long offset, Span<byte> target)
    {
        lock (_gate)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            return _stream.ReadAtLeast(target, target.Length, throwOnEndOfStream: false);
        }
    }

    private string Decode(List<byte> bytes, bool cut)
    {
        if (bytes.Count == 0)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes);
        string text = Encoding.Encoding.GetString(span);
        return cut && text.Length > 0 && text[^1] == '\uFFFD' ? text[..^1] : text; // a character cut by the line limit
    }
}
