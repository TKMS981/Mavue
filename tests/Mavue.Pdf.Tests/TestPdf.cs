using System.Globalization;
using System.Text;

namespace Mavue.Pdf.Tests;

/// <summary>
/// Writes small, valid PDFs for tests (also compiled into the E2E harness): every page has its number and searchable
/// text, an outline ("Chapter n" → page n), link annotations on page 1 (to the last page, to a web address, and a
/// javascript: link that must be ignored), and optionally one page with /Rotate 90.
/// </summary>
internal static class TestPdf
{
    /// <summary>The word that appears exactly once on every page (for search tests).</summary>
    public const string EveryPageWord = "Mavuesearch";

    /// <summary>A phrase only on the last page.</summary>
    public const string LastPagePhrase = "The final page target";

    public const string WebLink = "https://example.com/mavue";

    /// <param name="pages">Number of pages (at least 1).</param>
    /// <param name="rotatedPage">Zero-based page with /Rotate 90, or -1.</param>
    public static byte[] Create(int pages, int rotatedPage = -1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pages, 1);

        // 1 catalog, 2 page tree, 3 font, 4 outline root, then per page: page, content; then outline items; then annotations.
        int pageBase = 5;
        int outlineBase = pageBase + (2 * pages);
        int annotBase = outlineBase + pages;
        var objects = new string[annotBase + 3 - 1];
        objects[0] = "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R /PageMode /UseOutlines >>";
        objects[2] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
        objects[3] = Invariant($"<< /Type /Outlines /First {outlineBase} 0 R /Last {outlineBase + pages - 1} 0 R /Count {pages} >>");
        var kids = new List<string>();
        for (int i = 0; i < pages; i++)
        {
            int pageObject = pageBase + (2 * i);
            int contentObject = pageObject + 1;
            double r = 0.85 + (0.1 * i / Math.Max(1, pages - 1));
            var content = new StringBuilder();
            content.Append(Invariant($"{r:0.00} 0.92 0.98 rg 40 40 515 762 re f\n0 0 0 rg\n"));
            content.Append(Invariant($"BT /F1 36 Tf 60 760 Td (Page {i + 1}) Tj ET\n"));
            content.Append(Invariant($"BT /F1 14 Tf 60 700 Td (The quick brown fox jumps over the lazy dog on page {i + 1}.) Tj ET\n"));
            content.Append(Invariant($"BT /F1 14 Tf 60 670 Td ({EveryPageWord} appears once here.) Tj ET\n"));
            if (i == 0)
            {
                content.Append("0 0 1 rg BT /F1 14 Tf 60 600 Td (Go to the last page) Tj ET\n");
                content.Append("BT /F1 14 Tf 60 570 Td (Open the web site) Tj ET\n0 0 0 rg\n");
            }

            if (i == pages - 1)
            {
                content.Append(Invariant($"BT /F1 14 Tf 60 400 Td ({LastPagePhrase}) Tj ET\n"));
            }

            string body = content.ToString();
            string rotate = i == rotatedPage ? " /Rotate 90" : string.Empty;
            string annots = i == 0 ? Invariant($" /Annots [{annotBase} 0 R {annotBase + 1} 0 R {annotBase + 2} 0 R]") : string.Empty;
            objects[pageObject - 1] = Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842]{rotate} /Resources << /Font << /F1 3 0 R >> >> /Contents {contentObject} 0 R{annots} >>");
            objects[contentObject - 1] = Invariant($"<< /Length {Encoding.ASCII.GetByteCount(body)} >>\nstream\n{body}endstream");
            kids.Add(Invariant($"{pageObject} 0 R"));

            int item = outlineBase + i;
            string prev = i > 0 ? Invariant($" /Prev {item - 1} 0 R") : string.Empty;
            string next = i < pages - 1 ? Invariant($" /Next {item + 1} 0 R") : string.Empty;
            objects[item - 1] = Invariant($"<< /Title (Chapter {i + 1}) /Parent 4 0 R{prev}{next} /Dest [{pageObject} 0 R /XYZ 0 700 0] >>");
        }

        objects[1] = Invariant($"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {pages} >>");
        int lastPage = pageBase + (2 * (pages - 1));
        objects[annotBase - 1] = Invariant($"<< /Type /Annot /Subtype /Link /Rect [55 595 240 618] /Border [0 0 0] /Dest [{lastPage} 0 R /XYZ 0 420 0] >>");
        objects[annotBase] = Invariant($"<< /Type /Annot /Subtype /Link /Rect [55 565 220 588] /Border [0 0 0] /A << /S /URI /URI ({WebLink}) >> >>");
        objects[annotBase + 1] = "<< /Type /Annot /Subtype /Link /Rect [300 565 400 588] /Border [0 0 0] /A << /S /URI /URI (javascript:alert\\(1\\)) >> >>";
        return Build(objects);
    }

    /// <summary>Text on the single page of <see cref="CreateEncrypted"/>.</summary>
    public const string SecretPhrase = "Secret page";

    /// <summary>
    /// A one-page PDF encrypted with the standard security handler, revision 2 (40-bit RC4, ISO 32000-1 §7.6.3,
    /// algorithms 2–4): opening needs <paramref name="userPassword"/> (or the owner password). Written by hand so tests
    /// need no PDF library; only for tests (RC4-40 is not a protection worth anything).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do not use broken cryptographic algorithms", Justification = "MD5/RC4 are what this PDF encryption revision is defined with; test data only.")]
    public static byte[] CreateEncrypted(string userPassword, string ownerPassword = "owner-secret")
    {
        byte[] id = System.Security.Cryptography.MD5.HashData(Encoding.ASCII.GetBytes("Mavue encrypted test " + userPassword));
        const int Permissions = -4; // everything allowed
        byte[] owner = Rc4(System.Security.Cryptography.MD5.HashData(Pad(ownerPassword))[..5], Pad(userPassword));
        byte[] keyInput = [.. Pad(userPassword), .. owner, .. BitConverter.GetBytes(Permissions), .. id];
        byte[] key = System.Security.Cryptography.MD5.HashData(keyInput)[..5];
        byte[] user = Rc4(key, PasswordPadding);

        byte[] content = Encoding.ASCII.GetBytes(Invariant($"BT /F1 28 Tf 72 720 Td ({SecretPhrase}) Tj ET\n"));
        byte[] encrypted = Rc4(System.Security.Cryptography.MD5.HashData([.. key, 4, 0, 0, 0, 0])[..10], content); // object 4, generation 0

        var parts = new List<byte[]>();
        var offsets = new List<int>();
        int length = 0;
        void Add(byte[] bytes)
        {
            parts.Add(bytes);
            length += bytes.Length;
        }

        void Object(int number, string body, byte[]? stream = null)
        {
            offsets.Add(length);
            Add(Encoding.ASCII.GetBytes(Invariant($"{number} 0 obj\n{body}\n")));
            if (stream is not null)
            {
                Add(Encoding.ASCII.GetBytes("stream\n"));
                Add(stream);
                Add(Encoding.ASCII.GetBytes("\nendstream\n"));
            }

            Add(Encoding.ASCII.GetBytes("endobj\n"));
        }

        Add(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        Object(4, Invariant($"<< /Length {encrypted.Length} >>"), encrypted);
        Object(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Object(6, Invariant($"<< /Filter /Standard /V 1 /R 2 /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(user)}> /P {Permissions} >>"));
        int xref = length;
        var tail = new StringBuilder("xref\n0 7\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            tail.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        string hexId = Convert.ToHexString(id);
        tail.Append(Invariant($"trailer\n<< /Size 7 /Root 1 0 R /Encrypt 6 0 R /ID [<{hexId}> <{hexId}>] >>\nstartxref\n{xref}\n%%EOF\n"));
        Add(Encoding.ASCII.GetBytes(tail.ToString()));
        return parts.SelectMany(p => p).ToArray();
    }

    private static readonly byte[] PasswordPadding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private static byte[] Pad(string password)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(password);
        return [.. bytes.Take(32), .. PasswordPadding.Take(32 - Math.Min(32, bytes.Length))];
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        byte[] s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        byte[] output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return output;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static byte[] Build(string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
