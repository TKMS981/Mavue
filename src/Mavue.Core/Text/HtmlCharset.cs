using System.Text;
using System.Text.RegularExpressions;

namespace Mavue.Core.Text;

/// <summary>
/// The character encoding to declare when an HTML file is shown (docs/ARCHITECTURE.md §4.7.12). A byte order mark
/// needs no declaration; otherwise the document's own &lt;meta charset&gt; (in its first 1024 bytes, as browsers look
/// for it) wins, then the encoding detected from the bytes. Only well-known names are passed on. Pure.
/// </summary>
public static partial class HtmlCharset
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "utf-8", "utf8", "shift_jis", "shift-jis", "sjis", "windows-31j", "cp932", "euc-jp", "iso-2022-jp", "windows-1252", "iso-8859-1",
        "latin1", "us-ascii", "ascii", "gbk", "gb2312", "gb18030", "big5", "euc-kr", "ks_c_5601-1987", "windows-1250", "windows-1251",
        "windows-1253", "windows-1254", "windows-1255", "windows-1256", "windows-1257", "windows-1258", "koi8-r", "iso-8859-2",
        "iso-8859-5", "iso-8859-7", "iso-8859-15", "utf-16le", "utf-16be", "utf-16",
    };

    /// <summary>The charset to declare for <paramref name="content"/>, or null when it starts with a byte order mark.</summary>
    public static string? For(ReadOnlySpan<byte> content, TextEncodingInfo detected)
    {
        ArgumentNullException.ThrowIfNull(detected);
        if (detected.PreambleLength > 0)
        {
            return null;
        }

        if (detected.UnitSize == 1 && FromMeta(content) is { } declared)
        {
            return declared;
        }

        return detected.UnitSize switch
        {
            2 => detected.BigEndian ? "utf-16be" : "utf-16le",
            4 => detected.BigEndian ? "utf-32be" : "utf-32le",
            _ => detected.Encoding.WebName,
        };
    }

    /// <summary>The charset named by a &lt;meta&gt; in the first 1024 bytes, if it is a well-known one.</summary>
    public static string? FromMeta(ReadOnlySpan<byte> content)
    {
        string head = Encoding.Latin1.GetString(content[..Math.Min(content.Length, 1024)]);
        Match match = MetaCharset().Match(head);
        if (!match.Success)
        {
            return null;
        }

        string name = match.Groups[1].Value.Trim();
        return Known.Contains(name) ? name.ToLowerInvariant() : null;
    }

    [GeneratedRegex("<meta[^>]+charset\\s*=\\s*[\"']?\\s*([A-Za-z0-9_:.\\-]+)", RegexOptions.IgnoreCase, 50)]
    private static partial Regex MetaCharset();
}
