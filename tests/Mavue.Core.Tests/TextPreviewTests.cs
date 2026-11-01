using System.Text;
using Mavue.Core.Formats;
using Mavue.Core.Settings;
using Mavue.Core.Text;
using Mavue.Core.Text.Syntax;
using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

/// <summary>Text, code, Markdown and HTML preview: encodings, the line index, reading lines, routing (ARCHITECTURE §4.7.12).</summary>
[Trait("Category", "QuickView")]
public sealed class TextPreviewTests : IDisposable
{
    private static readonly TextPreviewLimits Limits = TextPreviewLimits.High;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "text-" + Guid.NewGuid().ToString("N"));

    static TextPreviewTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TextPreviewTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // ------------------------------------------------------------------ encodings

    public static TheoryData<string, byte[]> EncodedSamples() => new()
    {
        { "UTF-8 BOM", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("日本語 text")] },
        { "UTF-16 LE", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("日本語 text")] },
        { "UTF-16 BE", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("日本語 text")] },
        { "UTF-8", Encoding.UTF8.GetBytes("日本語のテキスト、改行あり\nsecond line") },
        { "Shift_JIS", Encoding.GetEncoding(932).GetBytes("日本語のテキストです。漢字とかなを含みます。\r\n二行目") },
        { "EUC-JP", Encoding.GetEncoding(51932).GetBytes("日本語のテキストです。漢字とかなを含みます。\n二行目") },
        { "ISO-2022-JP", Encoding.GetEncoding(50220).GetBytes("日本語のテキスト\r\n") },
    };

    [Theory]
    [MemberData(nameof(EncodedSamples))]
    public void Encodings_AreDetected(string expected, byte[] bytes)
    {
        TextDetection detection = TextEncodingDetector.Detect(bytes, wholeFile: true, ansiCodePage: 932);
        Assert.True(detection.IsText);
        Assert.Equal(expected, detection.Encoding!.Name);
        string decoded = detection.Encoding.Encoding.GetString(bytes, detection.Encoding.PreambleLength, bytes.Length - detection.Encoding.PreambleLength);
        Assert.Contains("日本語", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void Utf16WithoutByteOrderMark_IsDetected()
    {
        TextDetection le = TextEncodingDetector.Detect(Encoding.Unicode.GetBytes("plain ASCII text in UTF-16\r\n"), true);
        TextDetection be = TextEncodingDetector.Detect(Encoding.BigEndianUnicode.GetBytes("plain ASCII text in UTF-16\r\n"), true);
        Assert.Equal(("UTF-16 LE", 2, false), (le.Encoding!.Name, le.Encoding.UnitSize, le.Encoding.BigEndian));
        Assert.Equal(("UTF-16 BE", 2, true), (be.Encoding!.Name, be.Encoding.UnitSize, be.Encoding.BigEndian));
    }

    [Fact]
    public void Utf8CutAtTheEndOfTheSample_IsStillUtf8()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string('あ', 100));
        TextDetection detection = TextEncodingDetector.Detect(bytes.AsSpan(0, bytes.Length - 1), wholeFile: false, ansiCodePage: 1252);
        Assert.Equal("UTF-8", detection.Encoding!.Name);
    }

    [Fact]
    public void Latin1Text_OnAWesternSystem_UsesTheAnsiCodePage()
    {
        byte[] bytes = Encoding.Latin1.GetBytes("Café crème brûlée, naïve façade.");
        TextDetection detection = TextEncodingDetector.Detect(bytes, true, ansiCodePage: 1252);
        Assert.Equal(1252, detection.Encoding!.Encoding.CodePage);
        Assert.Equal("Café crème brûlée, naïve façade.", detection.Encoding.Encoding.GetString(bytes));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D })]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00 })]
    public void BinaryContent_IsNotText(byte[] bytes)
    {
        Assert.False(TextEncodingDetector.Detect(bytes, true).IsText);
        Assert.False(TextEncodingDetector.LooksLikeText(bytes));
    }

    [Fact]
    public void ManyControlCharacters_AreBinary_ButEscapeSequencesAreText()
    {
        byte[] controls = Enumerable.Range(0, 200).Select(i => (byte)(i % 2 == 0 ? 0x01 : 'a')).ToArray();
        Assert.False(TextEncodingDetector.LooksLikeText(controls));
        Assert.True(TextEncodingDetector.LooksLikeText("\u001b[31mred\u001b[0m log line\r\n\tindented\f"u8));
        Assert.True(TextEncodingDetector.LooksLikeText([]));
    }

    // ------------------------------------------------------------------ line index and reading

    [Theory]
    [InlineData("a\nb\nc", new[] { "a", "b", "c" })]
    [InlineData("a\r\nb\r\nc\r\n", new[] { "a", "b", "c" })]
    [InlineData("a\rb\rc", new[] { "a", "b", "c" })]
    [InlineData("a\r\nb\nc\rd", new[] { "a", "b", "c", "d" })]
    [InlineData("\n\n", new[] { "", "" })]
    [InlineData("only", new[] { "only" })]
    [InlineData("", new[] { "" })]
    public void LineBreaks_LfCrLfAndCr_SplitLines(string text, string[] expected)
    {
        using TextDocument document = Open(Write("breaks.txt", Encoding.UTF8.GetBytes(text)));
        Assert.Equal(expected, document.ReadLines(0, 100, Ct).Select(l => l.Text).ToArray());
        Assert.Equal(expected.Length, document.Index.LineCount);
    }

    [Fact]
    public void LineBreakKinds_AreReported()
    {
        using TextDocument mixed = Open(Write("mixed.txt", "a\r\nb\nc"u8.ToArray()));
        Assert.Equal(LineBreakKinds.CrLf | LineBreakKinds.Lf, mixed.Index.LineBreaks);
        using TextDocument crlf = Open(Write("crlf.txt", "a\r\nb\r\n"u8.ToArray()));
        Assert.Equal(LineBreakKinds.CrLf, crlf.Index.LineBreaks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16Lines_SplitOnWholeCodeUnits(bool bigEndian)
    {
        // U+0A0D has the bytes of LF and CR: it must not break a line.
        Encoding encoding = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("first \u0A0D\u0D0A x\r\nsecond\nthird")];
        using TextDocument document = Open(Write("utf16.txt", bytes));
        string[] expected = ["first \u0A0D\u0D0A x", "second", "third"];
        Assert.Equal(expected, document.ReadLines(0, 10, Ct).Select(l => l.Text).ToArray());
    }

    [Fact]
    public void ShiftJisLines_AreDecoded()
    {
        byte[] bytes = Encoding.GetEncoding(932).GetBytes("一行目：漢字\r\n二行目：ソース\r\n三行目");
        using TextDocument document = Open(Write("sjis.txt", bytes));
        Assert.Equal("Shift_JIS", document.Encoding.Name);
        Assert.Equal("二行目：ソース", document.ReadLines(1, 1, Ct)[0].Text);
    }

    [Fact]
    public void AnyLineOfALargeFile_IsReadDirectly()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 200_000; i++)
        {
            builder.Append("line ").Append(i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)).Append(i % 3 == 0 ? "\r\n" : "\n");
        }

        using TextDocument document = Open(Write("large.txt", Encoding.ASCII.GetBytes(builder.ToString())));
        Assert.Equal(200_000, document.Index.LineCount);
        Assert.True(document.Index.IsComplete);
        Assert.False(document.Index.IsTruncated);
        Assert.True(document.Index.MemoryBytes < 200_000 * 2, $"index memory {document.Index.MemoryBytes}");
        IReadOnlyList<TextLine> lines = document.ReadLines(123_457, 3, Ct);
        string[] expected = ["line 123457", "line 123458", "line 123459"];
        Assert.Equal(expected, lines.Select(l => l.Text).ToArray());
        Assert.Equal("line 199999", document.ReadLines(199_999, 10, Ct).Single().Text);
    }

    [Fact]
    public void CrLfSplitAcrossIndexBlocks_IsOneBreak()
    {
        // The index reads 1 MiB blocks: put the CR as the last byte of the first block.
        byte[] bytes = new byte[(1 << 20) + 10];
        Array.Fill(bytes, (byte)'x');
        bytes[(1 << 20) - 1] = (byte)'\r';
        bytes[1 << 20] = (byte)'\n';
        using TextDocument document = Open(Write("split.txt", bytes));
        Assert.Equal(2, document.Index.LineCount);
        Assert.Equal(new string('x', 9), document.ReadLines(1, 1, Ct)[0].Text);
    }

    [Fact]
    public void VeryLongLines_AreCut_AndTheNextLineIsStillFound()
    {
        string longLine = new('a', 300_000);
        using TextDocument document = Open(Write("long.txt", Encoding.ASCII.GetBytes("short\n" + longLine + "\nafter\n")));
        IReadOnlyList<TextLine> lines = document.ReadLines(0, 3, Ct);
        Assert.Equal(3, lines.Count);
        Assert.False(lines[0].Cut);
        Assert.True(lines[1].Cut);
        Assert.Equal(Limits.MaxLineChars, lines[1].Text.Length);
        Assert.Equal("after", lines[2].Text);
        Assert.Equal("after", document.ReadLines(2, 1, Ct)[0].Text);
        Assert.True(document.Index.LongestLineBytes >= 300_000);
    }

    [Fact]
    public void Indexing_StopsAtTheByteLimit()
    {
        var limits = Limits with { MaxIndexedBytes = 64 * 1024 };
        byte[] bytes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 50_000).Select(i => $"row {i}\n")));
        using TextDocument document = Open(Write("bytes.txt", bytes), limits);
        Assert.True(document.Index.IsTruncated);
        Assert.True(document.Index.IndexedBytes <= 64 * 1024);
        Assert.InRange(document.Index.LineCount, 5_000, 12_000);
    }

    [Fact]
    public void Indexing_StopsAtTheCheckpointLimit()
    {
        var limits = Limits with { MaxCheckpoints = 10 };
        byte[] bytes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 1_000).Select(i => $"{i}\n")));
        using TextDocument document = Open(Write("checkpoints.txt", bytes), limits);
        Assert.True(document.Index.IsTruncated);
        Assert.Equal(10 * TextPreviewLimits.CheckpointLineStride, document.Index.LineCount);
    }

    [Fact]
    public void Indexing_CanBeCancelled()
    {
        string path = Write("cancel.txt", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("x\n", 2_000_000))));
        using TextDocument document = TextDocument.Open(path, Limits, null, Ct)!;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => document.BuildIndex(path, cancelled.Token));
        Assert.True(document.Index.IsComplete); // stopped, nothing more will come
    }

    [Fact]
    public void BinaryFile_IsNotOpenedAsText()
    {
        string path = Write("binary.txt", [0x00, 0x01, 0x02, 0x00, 0xFF, 0x00]);
        Assert.Null(TextDocument.Open(path, Limits, null, Ct));
    }

    [Fact]
    public void DisplayedText_ExpandsTabs_AndShowsControlAndBidiCharacters()
    {
        Assert.Equal("a   b", TextDocument.DisplayLine("a\tb"));
        Assert.Equal("\u241B[31m", TextDocument.DisplayLine("\u001b[31m"));
        Assert.Equal("photo<U+202E>gpj.exe", TextDocument.DisplayLine("photo\u202Egpj.exe")); // "Trojan Source"
        Assert.Same("plain", TextDocument.DisplayLine("plain"));
    }

    [Fact]
    public void BlockCommentsAcrossLines_KeepTheirColorWhenReadFromTheMiddle()
    {
        string code = "class A\n{\n/* start\n" + string.Concat(Enumerable.Range(0, 300).Select(i => $"inside {i}\n")) + "end */\nint x = 1;\n";
        using TextDocument document = Open(Write("comment.cs", Encoding.UTF8.GetBytes(code)), language: SyntaxLanguages.ForPath("a.cs"));
        _ = document.ReadLines(0, 200, Ct); // remembers highlighter states on the way
        TextLine inside = document.ReadLines(250, 1, Ct)[0];
        Assert.Equal(SyntaxTokenKind.Comment, Assert.Single(inside.Tokens!).Kind);
        TextLine after = document.ReadLines(304, 1, Ct)[0];
        Assert.Equal("int x = 1;", after.Text);
        Assert.Contains(after.Tokens!, t => t.Kind == SyntaxTokenKind.Keyword);
        Assert.DoesNotContain(after.Tokens!, t => t.Kind == SyntaxTokenKind.Comment);
    }

    // ------------------------------------------------------------------ syntax highlighting

    [Fact]
    public void CSharp_KeywordsStringsCommentsNumbersAndTypes()
    {
        SyntaxLanguage cs = SyntaxLanguages.ForPath("Program.cs")!;
        Assert.Equal("csharp", cs.Id);
        Assert.Equal(
            [("public", SyntaxTokenKind.Keyword), ("List", SyntaxTokenKind.Type), ("\"a\\\"b\"", SyntaxTokenKind.StringLiteral), ("42", SyntaxTokenKind.Number), ("// done", SyntaxTokenKind.Comment)],
            Tokens(cs, "public List x = \"a\\\"b\" + 42; // done"));
        Assert.Equal([("#if DEBUG", SyntaxTokenKind.Preprocessor)], Tokens(cs, "#if DEBUG"));
        Assert.Equal([("@\"C:\\path\"\"q\"", SyntaxTokenKind.StringLiteral)], Tokens(cs, "@\"C:\\path\"\"q\""));
    }

    [Fact]
    public void MultiLineStrings_AndComments_CarryState()
    {
        SyntaxLanguage py = SyntaxLanguages.ForPath("a.py")!;
        var tokens = new List<SyntaxToken>();
        int state = py.Tokenize("x = \"\"\"start", 0, tokens);
        Assert.NotEqual(0, state);
        tokens.Clear();
        state = py.Tokenize("middle", state, tokens);
        Assert.Equal(SyntaxTokenKind.StringLiteral, Assert.Single(tokens).Kind);
        tokens.Clear();
        state = py.Tokenize("end\"\"\" if True", state, tokens);
        Assert.Equal(0, state);
        Assert.Contains(tokens, t => t.Kind == SyntaxTokenKind.Keyword);
        Assert.Contains(tokens, t => t.Kind == SyntaxTokenKind.Literal);
    }

    [Fact]
    public void Json_KeysAreProperties()
    {
        SyntaxLanguage json = SyntaxLanguages.ForPath("package.json")!;
        Assert.Equal(
            [("\"name\"", SyntaxTokenKind.Property), ("\"mavue\"", SyntaxTokenKind.StringLiteral), ("\"n\"", SyntaxTokenKind.Property), ("1.5e3", SyntaxTokenKind.Number), ("true", SyntaxTokenKind.Literal)],
            Tokens(json, "{ \"name\" : \"mavue\", \"n\": 1.5e3, \"ok\": true }").Where(t => t.Text != "\"ok\"").ToArray());
    }

    [Fact]
    public void Markup_TagsAttributesValuesCommentsAndEntities()
    {
        SyntaxLanguage xml = SyntaxLanguages.ForPath("App.xaml")!;
        Assert.Equal(
            [("<Grid", SyntaxTokenKind.Tag), ("x:Name", SyntaxTokenKind.Attribute), ("\"Root\"", SyntaxTokenKind.StringLiteral), (">", SyntaxTokenKind.Tag), ("&amp;", SyntaxTokenKind.Literal), ("<!-- c -->", SyntaxTokenKind.Comment)],
            Tokens(xml, "<Grid x:Name=\"Root\">&amp;<!-- c -->"));
        var tokens = new List<SyntaxToken>();
        int state = xml.Tokenize("<Button", 0, tokens);
        tokens.Clear();
        xml.Tokenize("  Content=\"x\" />", state, tokens);
        Assert.Contains(tokens, t => t.Kind == SyntaxTokenKind.Attribute);
    }

    [Fact]
    public void DataAndDocumentFormats_AreColored()
    {
        Assert.Equal([("name", SyntaxTokenKind.Property), ("\"x\"", SyntaxTokenKind.StringLiteral), ("# c", SyntaxTokenKind.Comment)], Tokens(SyntaxLanguages.ForPath("a.yaml")!, "name: \"x\" # c"));
        Assert.Equal([("[core]", SyntaxTokenKind.Heading)], Tokens(SyntaxLanguages.ForPath(".gitconfig")!, "[core]"));
        Assert.Equal([("+added", SyntaxTokenKind.Inserted)], Tokens(SyntaxLanguages.ForPath("a.patch")!, "+added"));
        Assert.Equal([("2026-11-02", SyntaxTokenKind.Number), ("ERROR", SyntaxTokenKind.Error)], Tokens(SyntaxLanguages.ForPath("app.log")!, "2026-11-02 ERROR failed").Where(t => t.Text != "failed").ToArray());
        Assert.Equal([("# Title", SyntaxTokenKind.Heading)], Tokens(SyntaxLanguages.ForPath("README.md")!, "# Title"));
        Assert.Equal("css", SyntaxLanguages.ForPath("site.scss")!.Id);
        Assert.Contains(Tokens(SyntaxLanguages.ForPath("a.css")!, "a { color: #fff; }"), t => t.Kind == SyntaxTokenKind.Property && t.Text == "color");
    }

    [Theory]
    [InlineData("Makefile", "make")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData(".gitignore", "ignore")]
    [InlineData("CMakeLists.txt", "cmake")]
    [InlineData("build.ps1", "powershell")]
    [InlineData("run.cmd", "batch")]
    [InlineData("app.tsx", "typescript")]
    [InlineData("main.rs", "rust")]
    [InlineData("query.SQL", "sql")]
    [InlineData("Shader.hlsl", "shader")]
    [InlineData("App.csproj", "xml")]
    public void Languages_ByNameAndExtension(string name, string id)
    {
        Assert.Equal(id, SyntaxLanguages.ForPath(Path.Combine("C:\\src", name))?.Id);
    }

    [Fact]
    public void PlainText_HasNoLanguage()
    {
        Assert.Null(SyntaxLanguages.ForPath("notes.txt"));
        Assert.Null(SyntaxLanguages.ForPath("data.csv"));
    }

    [Fact]
    public void EveryLanguage_SurvivesHostileInput()
    {
        var random = new Random(7);
        string alphabet = "abcXYZ019 \t\"'`\\/*#-<>!?&;:=[]{}()@$%.,+~_\u3042\u202E";
        string[] names = [.. SyntaxLanguages.Extensions.Select(e => "x" + e), "Makefile", "Dockerfile"];
        foreach (SyntaxLanguage language in names.Select(SyntaxLanguages.ForPath).OfType<SyntaxLanguage>().Distinct())
        {
            int state = 0;
            var tokens = new List<SyntaxToken>();
            for (int line = 0; line < 300; line++)
            {
                string text = new([.. Enumerable.Range(0, random.Next(0, 120)).Select(_ => alphabet[random.Next(alphabet.Length)])]);
                tokens.Clear();
                state = language.Tokenize(text, state, tokens);
                int end = 0;
                foreach (SyntaxToken token in tokens)
                {
                    Assert.True(token.Start >= end && token.Length > 0 && token.Start + token.Length <= text.Length, $"{language.Id}: bad token {token} in \"{text}\"");
                    end = token.Start + token.Length;
                }
            }
        }
    }

    // ------------------------------------------------------------------ formats and routing

    [Theory]
    [InlineData("notes.txt", FileFormat.Text)]
    [InlineData("README.md", FileFormat.Markdown)]
    [InlineData("index.HTML", FileFormat.Html)]
    [InlineData("Program.cs", FileFormat.Text)]
    [InlineData("Makefile", FileFormat.Text)]
    [InlineData("LICENSE", FileFormat.Text)]
    [InlineData(".gitignore", FileFormat.Text)]
    [InlineData("data.csv", FileFormat.Text)]
    [InlineData("drawing.svg", FileFormat.Svg)]
    [InlineData("photo.jpg", FileFormat.Jpeg)] // not text by name: the extension decides as before
    public void TextFiles_AreRecognizedByName(string name, FileFormat expected)
    {
        string path = Write(name, Encoding.UTF8.GetBytes(name == "drawing.svg" ? "<svg xmlns=\"http://www.w3.org/2000/svg\"/>" : "some text\n"));
        Assert.Equal(expected, FileFormatDetector.Detect(path));
    }

    [Fact]
    public void TextContent_IsNotSniffedAsMediaSvgOrPdf()
    {
        // 'G' at 0 and 188 looks like an MPEG transport stream; a quoted "%PDF-" and inline <svg> are just text.
        string g = "G" + new string('x', 187) + "G and more text";
        Assert.Equal(FileFormat.Text, FileFormatDetector.Detect(Encoding.ASCII.GetBytes(g), "notes.txt"));
        Assert.Equal(FileFormat.Markdown, FileFormatDetector.Detect("Header: %PDF-1.7 explained"u8, "pdf.md"));
        Assert.Equal(FileFormat.Html, FileFormatDetector.Detect("<!doctype html><svg width=\"10\"></svg>"u8, "page.html"));
    }

    [Fact]
    public void TypeScript_And_TransportStreams_ShareTheTsExtension()
    {
        Assert.Equal(FileFormat.Text, FileFormatDetector.Detect("export const x: number = 1;\n"u8, "module.ts"));
        byte[] stream = new byte[400];
        stream[0] = 0x47;
        stream[188] = 0x47;
        Assert.Equal(FileFormat.MpegTransportStream, FileFormatDetector.Detect(stream, "clip.ts"));
        Assert.Equal(FileFormat.MpegTransportStream, FileFormatDetector.FromExtension(".ts")); // unread (cloud placeholder)
    }

    [Fact]
    public void BinarySignatures_WinOverTextNames()
    {
        byte[] png = Convert.FromHexString("89504E470D0A1A0A0000000D49484452");
        Assert.Equal(FileFormat.Png, FileFormatDetector.Detect(png, "image.txt"));
    }

    [Theory]
    [InlineData(FileFormat.Text, PreviewContentKind.Text)]
    [InlineData(FileFormat.Markdown, PreviewContentKind.Markdown)]
    [InlineData(FileFormat.Html, PreviewContentKind.Html)]
    public void TextFormats_RouteToTheirContents_ButCloudFilesToTheCard(FileFormat format, PreviewContentKind kind)
    {
        Assert.Equal(kind, PreviewContentRouting.Choose(new FileFacts(FileAttributes.Normal, 10, format, PreviewAccess.Allowed), true));
        Assert.Equal(PreviewContentKind.Universal, PreviewContentRouting.Choose(new FileFacts(FileAttributes.Normal, 10, format, PreviewAccess.CloudPlaceholder), true));
    }

    [Theory]
    [InlineData(".conf2", PreviewContentKind.Text)]   // unknown type, text inside
    [InlineData("", PreviewContentKind.Text)]
    [InlineData(".jpg", PreviewContentKind.Raster)]   // an image type: the decoder reports damage
    [InlineData(".docx", PreviewContentKind.Universal)]
    public void UnknownTypes_ThatLookLikeText_AreShownAsText(string extension, PreviewContentKind expected)
    {
        var facts = new FileFacts(FileAttributes.Normal, 10, FileFormat.Unknown, PreviewAccess.Allowed, LooksLikeText: true);
        Assert.Equal(expected, PreviewContentRouting.Choose(facts, true, extension));
    }

    [Fact]
    public void RealFiles_OfUnknownTypeWithText_AreShownAsText()
    {
        string path = Write("settings.custom", "key=value\n"u8.ToArray());
        FileFacts facts = FileFacts.Read(path);
        Assert.True(facts.LooksLikeText);
        Assert.Equal(PreviewContentKind.Text, PreviewContentRouting.Choose(facts, true, Path.GetExtension(path)));
        string binary = Write("blob.custom", [0x00, 0x10, 0x00, 0x20]);
        Assert.Equal(PreviewContentKind.Raster, PreviewContentRouting.Choose(FileFacts.Read(binary), true, ".custom"));
    }

    [Fact]
    public void TextFiles_KeepTheirCategoryAndMimeType()
    {
        Assert.Equal(FileKindCategory.Text, UniversalFileInfo.CategoryFor(".txt"));
        Assert.Equal(FileKindCategory.Code, UniversalFileInfo.CategoryFor(".cs"));
        Assert.Equal(FileKindCategory.Spreadsheet, UniversalFileInfo.CategoryFor(".csv"));
        Assert.Equal(FileKindCategory.Code, UniversalFileInfo.CategoryFor(".html"));
        Assert.Equal("text/markdown", UniversalFileInfo.MimeTypeFor(".md"));
    }

    [Fact]
    public void TextFormats_AreNotImagesOrTransparent()
    {
        foreach (FileFormat format in new[] { FileFormat.Text, FileFormat.Markdown, FileFormat.Html })
        {
            Assert.True(FileFormatKinds.IsText(format));
            Assert.False(FileFormatKinds.IsImage(format));
            Assert.False(FileFormatKinds.MayHaveTransparency(format));
        }

        Assert.DoesNotContain(FileFormat.Text, ViewerFormats.All); // no file association or shell registration
    }

    // ------------------------------------------------------------------ HTML charset, limits, settings

    [Fact]
    public void HtmlCharset_PrefersTheByteOrderMark_ThenTheMetaTag_ThenDetection()
    {
        TextEncodingInfo utf8 = TextEncodingDetector.DefaultUtf8;
        Assert.Equal("shift_jis", HtmlCharset.For("<html><head><meta charset=\"Shift_JIS\">"u8, utf8));
        Assert.Equal("euc-jp", HtmlCharset.For("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=EUC-JP\">"u8, utf8));
        Assert.Equal("utf-8", HtmlCharset.For("<p>no meta</p>"u8, utf8));
        Assert.Equal("utf-8", HtmlCharset.For("<meta charset=\"x-evil-unknown\">"u8, utf8));
        TextEncodingInfo bom = TextEncodingDetector.Detect([0xEF, 0xBB, 0xBF, (byte)'<'], true).Encoding!;
        Assert.Null(HtmlCharset.For([0xEF, 0xBB, 0xBF, (byte)'<'], bom));
    }

    [Fact]
    public void Limits_ShrinkWithThePerformanceTier()
    {
        Assert.True(TextPreviewLimits.Low.MaxIndexedBytes < TextPreviewLimits.Balanced.MaxIndexedBytes);
        Assert.True(TextPreviewLimits.Balanced.MaxIndexedBytes < TextPreviewLimits.High.MaxIndexedBytes);
        Assert.True(TextPreviewLimits.Low.MaxMarkdownBytes < TextPreviewLimits.High.MaxMarkdownBytes);
        Assert.Same(TextPreviewLimits.Low, TextPreviewLimits.For(PerformanceTier.Low));
        Assert.True(TextPreviewLimits.High.MaxCheckpoints * 12L <= 64L * 1024 * 1024, "index memory stays bounded");
    }

    [Fact]
    public async Task WrapSettings_DefaultAndRoundTrip()
    {
        var defaults = new QuickViewSettings();
        Assert.True(defaults.WrapPlainText);
        Assert.False(defaults.WrapCode);
        string path = Path.Combine(_folder, "settings.json");
        await (defaults with { WrapPlainText = false, WrapCode = true }).SaveAsync(path, Ct);
        QuickViewSettings loaded = QuickViewSettings.Load(path);
        Assert.False(loaded.WrapPlainText);
        Assert.True(loaded.WrapCode);
    }

    private static (string Text, SyntaxTokenKind Kind)[] Tokens(SyntaxLanguage language, string line)
    {
        var tokens = new List<SyntaxToken>();
        language.Tokenize(line, 0, tokens);
        return [.. tokens.Select(t => (line.Substring(t.Start, t.Length), t.Kind))];
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static TextDocument Open(string path, TextPreviewLimits? limits = null, SyntaxLanguage? language = null)
    {
        TextDocument document = TextDocument.Open(path, limits ?? Limits, language, Ct)!;
        document.BuildIndex(path, Ct);
        return document;
    }
}
