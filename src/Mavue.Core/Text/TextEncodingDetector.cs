using System.Globalization;
using System.Text;

namespace Mavue.Core.Text;

/// <summary>How the bytes of a text file are read.</summary>
/// <param name="Encoding">Decoder for the file's bytes (with replacement characters for invalid sequences).</param>
/// <param name="Name">Short name shown to the user ("UTF-8", "Shift_JIS", …).</param>
/// <param name="PreambleLength">Bytes of the byte order mark at the start of the file (skipped).</param>
/// <param name="UnitSize">Bytes per code unit: 1 (UTF-8 and code pages), 2 (UTF-16) or 4 (UTF-32). Line breaks are
/// searched for in whole units, so a line break byte inside a UTF-16 character is never mistaken for one.</param>
/// <param name="BigEndian">UTF-16/UTF-32 big endian.</param>
public sealed record TextEncodingInfo(Encoding Encoding, string Name, int PreambleLength, int UnitSize, bool BigEndian);

/// <summary>Whether the sample is text, and in which encoding.</summary>
/// <param name="IsText">False for binary content (the information card is shown instead).</param>
/// <param name="Encoding">The encoding when <paramref name="IsText"/>.</param>
public sealed record TextDetection(bool IsText, TextEncodingInfo? Encoding);

/// <summary>
/// Detects the encoding of a text file from its first bytes: a byte order mark (UTF-8, UTF-16, UTF-32), UTF-16
/// without one (zero bytes on every other position), ISO-2022-JP (escape sequences), valid UTF-8, then the Japanese
/// code pages Shift_JIS (CP932) and EUC-JP (scored by how much of the decoded text is kana and kanji), and finally the
/// system's ANSI code page. Zero bytes elsewhere, or many control characters, mean the file is binary. Pure.
/// </summary>
public static class TextEncodingDetector
{
    private const int ShiftJis = 932;
    private const int EucJp = 51932;
    private const int Iso2022Jp = 50220;

    static TextEncodingDetector() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The UTF-8 encoding used when a file has no other clue (also for empty files).</summary>
    public static TextEncodingInfo DefaultUtf8 { get; } = new(new UTF8Encoding(false, false), "UTF-8", 0, 1, false);

    /// <param name="sample">The first bytes of the file (<see cref="TextPreviewLimits.SampleBytes"/>).</param>
    /// <param name="wholeFile">The sample is the entire file (a cut multi-byte character at its end is then invalid).</param>
    /// <param name="ansiCodePage">The system's ANSI code page (default: the current culture's).</param>
    public static TextDetection Detect(ReadOnlySpan<byte> sample, bool wholeFile, int? ansiCodePage = null)
    {
        if (DetectPreamble(sample) is { } bom)
        {
            return new TextDetection(true, bom);
        }

        if (sample.IsEmpty)
        {
            return new TextDetection(true, DefaultUtf8);
        }

        if (DetectUtf16WithoutBom(sample) is { } utf16)
        {
            return new TextDetection(true, utf16);
        }

        if (!LooksLikeText(sample))
        {
            return new TextDetection(false, null);
        }

        ReadOnlySpan<byte> complete = wholeFile ? sample : TrimCutUtf8Sequence(sample);
        if (IsIso2022Jp(sample))
        {
            return new TextDetection(true, new TextEncodingInfo(Encoding.GetEncoding(Iso2022Jp), "ISO-2022-JP", 0, 1, false));
        }

        if (System.Text.Unicode.Utf8.IsValid(complete))
        {
            return new TextDetection(true, DefaultUtf8);
        }

        int ansi = ansiCodePage ?? CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        ReadOnlySpan<byte> cut = wholeFile ? sample : sample[..Math.Max(0, sample.Length - 1)]; // a cut double-byte character
        double? sjis = JapaneseScore(cut, ShiftJis);
        double? euc = JapaneseScore(cut, EucJp);
        if (euc is { } e && (sjis is null || e > sjis.Value + 0.1) && e >= 0.5)
        {
            return new TextDetection(true, new TextEncodingInfo(Encoding.GetEncoding(EucJp), "EUC-JP", 0, 1, false));
        }

        if (sjis is { } s && (ansi == ShiftJis || s >= 0.6))
        {
            return new TextDetection(true, new TextEncodingInfo(Encoding.GetEncoding(ShiftJis), "Shift_JIS", 0, 1, false));
        }

        // Single-byte or other double-byte system code pages (Windows-1252, GBK, Big5, UHC…). Their line breaks are
        // never trail bytes, so lines can be split byte-wise.
        int fallback = ansi == ShiftJis ? 1252 : ansi;
        try
        {
            Encoding encoding = Encoding.GetEncoding(fallback);
            return new TextDetection(true, new TextEncodingInfo(encoding, encoding.WebName, 0, 1, false));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new TextDetection(true, new TextEncodingInfo(Encoding.Latin1, "ISO-8859-1", 0, 1, false));
        }
    }

    /// <summary>
    /// The sample has no zero bytes and few control characters (tab, line breaks, form feed and escape are text).
    /// Cheap check also used before routing a file of unknown type to the text preview.
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> sample)
    {
        if (sample.IsEmpty)
        {
            return true;
        }

        if (DetectPreamble(sample) is not null || DetectUtf16WithoutBom(sample) is not null)
        {
            return true;
        }

        int controls = 0;
        foreach (byte b in sample)
        {
            if (b == 0)
            {
                return false;
            }

            if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C or 0x0B or 0x1B or 0x08 or 0x1A))
            {
                controls++;
            }
        }

        return controls * 100 <= sample.Length * 2; // at most 2 %
    }

    private static TextEncodingInfo? DetectPreamble(ReadOnlySpan<byte> h)
    {
        if (h.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return new TextEncodingInfo(new UTF8Encoding(false, false), "UTF-8 BOM", 3, 1, false);
        }

        if (h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            return new TextEncodingInfo(new UTF32Encoding(false, false, false), "UTF-32 LE", 4, 4, false);
        }

        if (h.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            return new TextEncodingInfo(new UTF32Encoding(true, false, false), "UTF-32 BE", 4, 4, true);
        }

        if (h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return new TextEncodingInfo(new UnicodeEncoding(false, false, false), "UTF-16 LE", 2, 2, false);
        }

        if (h.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return new TextEncodingInfo(new UnicodeEncoding(true, false, false), "UTF-16 BE", 2, 2, true);
        }

        return null;
    }

    /// <summary>UTF-16 without a byte order mark: most code units of mostly-ASCII text have a zero high byte.</summary>
    private static TextEncodingInfo? DetectUtf16WithoutBom(ReadOnlySpan<byte> h)
    {
        int pairs = Math.Min(h.Length / 2, 4096);
        if (pairs < 2)
        {
            return null;
        }

        int zeroEven = 0, zeroOdd = 0;
        for (int i = 0; i < pairs; i++)
        {
            zeroEven += h[2 * i] == 0 ? 1 : 0;
            zeroOdd += h[(2 * i) + 1] == 0 ? 1 : 0;
        }

        if (zeroOdd * 10 >= pairs * 4 && zeroEven * 20 <= pairs && FewControlUnits(h, pairs, bigEndian: false))
        {
            return new TextEncodingInfo(new UnicodeEncoding(false, false, false), "UTF-16 LE", 0, 2, false);
        }

        if (zeroEven * 10 >= pairs * 4 && zeroOdd * 20 <= pairs && FewControlUnits(h, pairs, bigEndian: true))
        {
            return new TextEncodingInfo(new UnicodeEncoding(true, false, false), "UTF-16 BE", 0, 2, true);
        }

        return null;
    }

    /// <summary>Read as UTF-16, the units are mostly not control characters (binary data with many zero bytes is).</summary>
    private static bool FewControlUnits(ReadOnlySpan<byte> h, int pairs, bool bigEndian)
    {
        int controls = 0;
        for (int i = 0; i < pairs; i++)
        {
            int unit = bigEndian ? (h[2 * i] << 8) | h[(2 * i) + 1] : h[2 * i] | (h[(2 * i) + 1] << 8);
            if (unit < 0x20 && unit is not ('\t' or '\n' or '\r' or 0x0C or 0x1B))
            {
                controls++;
            }
        }

        return controls * 100 <= pairs * 2;
    }

    private static bool IsIso2022Jp(ReadOnlySpan<byte> h)
    {
        foreach (byte b in h)
        {
            if (b >= 0x80)
            {
                return false;
            }
        }

        return h.IndexOf("\u001B$B"u8) >= 0 || h.IndexOf("\u001B$@"u8) >= 0;
    }

    /// <summary>Drops a multi-byte UTF-8 sequence cut by the end of the sample.</summary>
    private static ReadOnlySpan<byte> TrimCutUtf8Sequence(ReadOnlySpan<byte> h)
    {
        for (int back = 1; back <= Math.Min(3, h.Length); back++)
        {
            byte b = h[^back];
            if ((b & 0xC0) == 0x80)
            {
                continue; // continuation byte: look further back for the lead byte
            }

            int needed = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return needed > back ? h[..^back] : h;
        }

        return h;
    }

    /// <summary>
    /// Null when the bytes are not valid in <paramref name="codePage"/>; otherwise the share of non-ASCII characters
    /// that are kana, kanji or full-width punctuation (half-width katakana — what EUC-JP text looks like when read as
    /// Shift_JIS — does not count).
    /// </summary>
    private static double? JapaneseScore(ReadOnlySpan<byte> bytes, int codePage)
    {
        Encoding strict = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        string text;
        try
        {
            text = strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        int nonAscii = 0, japanese = 0;
        foreach (char c in text)
        {
            if (c < 0x80)
            {
                continue;
            }

            nonAscii++;
            if (c is (>= '　' and <= 'ヿ') or (>= '一' and <= '鿿') or (>= '！' and <= '～'))
            {
                japanese++;
            }
        }

        return nonAscii == 0 ? 0 : (double)japanese / nonAscii;
    }
}
