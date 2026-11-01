using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Windows.Graphics.Imaging;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Quick Preview Phase 4 (docs/ARCHITECTURE.md §4.7.12): text and code (encodings, syntax colors, scrolling, a very
/// large file), routing of text files (no extension, unknown extension, TypeScript .ts, binary content behind .txt),
/// Markdown drawn as XAML (local and remote images, source view, size limit) and HTML in the locked-down WebView2
/// (no scripts, no requests, no navigation; the browser ends when Quick View closes).
/// </summary>
internal sealed partial class Runner
{
    private const ushort VkU = 0x55;
    private const ushort VkW = 0x57;

    private void RunPhase4Scenarios(List<TestAsset> assets)
    {
        string root = Path.GetDirectoryName(assets[0].Path)! + " Phase4";
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var listener = new ConnectionCounter();
        Phase4Files files;
        try
        {
            files = Task.Run(() => WritePhase4FilesAsync(root, listener.Port)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Guarded("text-plain", () => (false, $"could not generate files: {ex.GetType().Name} {ex.Message}"), "generated files");
            return;
        }

        WriteHostSettings();
        Guarded("text-plain", () => TextPlainScenario(files.ShiftJis),
            "A Japanese plain text file in Shift_JIS (CP932): shown as text at once (encoding detected, CRLF), wrapped (plain text wraps by default), no line numbers; the text is on screen");
        Guarded("text-code", () => TextCodeScenario(files.Code),
            "A 2,000-line C# file: colored (language csharp), line numbers; the wheel scrolls 3 lines a notch; with Quick View active End shows the last line, Home the first; W switches wrapping (saved)");
        Guarded("text-large", () => TextLargeScenario(files.Large),
            "A 2,000,000-line, ~70 MB log: the first screen shows without waiting for the index (well under a second), the index then counts all lines in the background with bounded memory; End shows the last line; the host's memory stays bounded and returns after closing");
        Guarded("text-routing", () => TextRoutingScenario(files),
            "Routing: README (no extension) and a file of unknown type with text are shown as text, module.ts is TypeScript (not a video), binary bytes behind .txt get the information card with a message");
        Guarded("markdown-render", () => MarkdownScenario(files.Markdown, listener),
            "README.md drawn as XAML: headings, table, task list, code block, alert; the local image is drawn, the remote image (a local test server) is never requested; Ctrl+U shows the colored source and back");
        Guarded("markdown-large", () => MarkdownLargeScenario(files.LargeMarkdown),
            "A Markdown file over the size limit is shown as source text with a notice (not parsed)");
        Guarded("html-safe", () => HtmlScenario(files.Html, listener),
            "Hostile HTML: the page is drawn (data: image, inline style) but its script does not run; images, styles, frames, prefetch, refresh, forms and links to a local test server are refused (zero connections; blocked requests counted); Ctrl+U shows the source; after closing Quick View no WebView2 process of Quick View remains");
        WriteHostSettings();
    }

    private sealed record Phase4Files(string ShiftJis, string Code, string Large, string Readme, string Unknown, string TypeScript, string Binary, string Markdown, string LargeMarkdown, string Html);

    private static async Task<Phase4Files> WritePhase4FilesAsync(string root, int port)
    {
        string P(string name) => Path.Combine(root, name);

        await File.WriteAllBytesAsync(P("01 日本語.txt"), Encoding.GetEncoding(932).GetBytes(
            "日本語のテキストファイル（Shift_JIS）です。\r\n" + string.Concat(Enumerable.Range(1, 40).Select(i => $"{i} 行目：とても長い行は折り返して表示されます。" + new string('長', 60) + "\r\n"))));

        var code = new StringBuilder("using System;\n\nnamespace Demo;\n\n/* A block comment\n   over two lines */\npublic sealed class Program\n{\n");
        for (int i = 4; i <= 1997; i++)
        {
            code.Append("    // line ").Append(i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)).Append(i % 5 == 0 ? " string s = \"text\"; int n = 42;\n" : "\n");
        }

        code.Append("}\n// line 1999\n// line 2000\n");
        await File.WriteAllTextAsync(P("02 Program.cs"), code.ToString(), new UTF8Encoding(false));

        await using (var large = new StreamWriter(P("03 big.log"), false, new UTF8Encoding(false), 1 << 20))
        {
            for (int i = 0; i < 2_000_000; i++)
            {
                await large.WriteAsync($"2026-11-02T10:00:00Z INFO row {i:D7} the quick brown fox jumps\n");
            }
        }

        string readme = P("README");
        await File.WriteAllTextAsync(readme, "Plain README without an extension.\n");
        string unknown = P("04 settings.weird");
        await File.WriteAllTextAsync(unknown, "key=value\nother=1\n");
        string typescript = P("05 module.ts");
        await File.WriteAllTextAsync(typescript, "export interface Item { id: number }\nexport const items: Item[] = [];\n");
        string binary = P("06 blob.txt");
        byte[] noise = new byte[4096];
        new Random(3).NextBytes(noise);
        noise[10] = 0;
        await File.WriteAllBytesAsync(binary, noise);

        string logo = P("logo.png");
        await TestAssets.EncodeAsync(logo, BitmapEncoder.PngEncoderId, 320, 120, null);
        string markdown = P("07 README.md");
        await File.WriteAllTextAsync(markdown, $$"""
            ---
            title: front matter
            ---
            # Mavue Markdown test

            Intro with **bold**, *italic*, `code`, ~~strike~~ and a [web link](https://example.com/page) and an [anchor](#second-section).

            ![local logo](logo.png)
            ![remote image](http://127.0.0.1:{{port}}/remote.png)
            <img src="http://127.0.0.1:{{port}}/html-img.png" alt="html remote">

            > [!WARNING]
            > An alert block.

            - [x] done task
            - [ ] open task
              1. nested ordered

            | 列 A | 列 B |
            |:-----|-----:|
            | セル | 1 |

            ```csharp
            public static int Main() => 42; // comment
            ```

            ## Second section

            Footnote reference[^1].

            [^1]: The footnote.
            """, new UTF8Encoding(false));

        string largeMarkdown = P("08 large.md");
        await using (var writer = new StreamWriter(largeMarkdown, false, new UTF8Encoding(false)))
        {
            for (int i = 0; i < 90_000; i++)
            {
                await writer.WriteAsync($"## Heading {i}\n\nParagraph {i} with *emphasis*.\n\n");
            }
        }

        string html = P("09 page.html");
        string dataPng = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(logo));
        string server = $"http://127.0.0.1:{port}";
        await File.WriteAllTextAsync(html, $$"""
            <!doctype html>
            <html><head>
            <meta charset="utf-8">
            <title>Safe HTML</title>
            <meta http-equiv="refresh" content="0;url={{server}}/refresh">
            <link rel="stylesheet" href="{{server}}/style.css">
            <link rel="prefetch" href="{{server}}/prefetch">
            <link rel="preconnect" href="{{server}}">
            <link rel="icon" href="{{server}}/favicon.ico">
            <style>@import url("{{server}}/import.css"); body { font-family: sans-serif; background: url("{{server}}/bg.png"); } h1 { color: #0a6; }</style>
            <script>document.addEventListener('DOMContentLoaded', () => { document.body.innerHTML = '<p>SCRIPT RAN</p>'; fetch('{{server}}/fetch'); });</script>
            <script src="{{server}}/external.js"></script>
            </head><body>
            <h1>Safe HTML test</h1>
            <p>日本語の段落と <a href="{{server}}/link" id="link">a link</a>.</p>
            <img src="{{server}}/image.png" alt="remote">
            <img src="logo.png" alt="relative local">
            <img src="file:///C:/Windows/Web/Wallpaper/Windows/img0.jpg" alt="file url">
            <img src="{{dataPng}}" alt="data image" width="160">
            <iframe src="{{server}}/frame.html"></iframe>
            <iframe srcdoc="<p>inline frame</p>"></iframe>
            <object data="{{server}}/object.pdf"></object>
            <embed src="{{server}}/embed.swf">
            <video src="{{server}}/video.mp4" autoplay></video>
            <audio src="{{server}}/audio.mp3" autoplay></audio>
            <form action="{{server}}/form" method="post"><input name="q" value="x" autofocus></form>
            <a href="{{server}}/download.zip" download>download</a>
            </body></html>
            """, new UTF8Encoding(false));

        return new Phase4Files(P("01 日本語.txt"), P("02 Program.cs"), P("03 big.log"), readme, unknown, typescript, binary, markdown, largeMarkdown, html);
    }

    // ------------------------------------------------------------------ text

    private (bool, string) TextPlainScenario(string path)
    {
        if (!ShowInQuickView(path, out long t0, out string why))
        {
            return (false, why);
        }

        Mark? set = WaitForMark(t0, "full-set", m => m.Text("decoder") == "text");
        Mark? visible = set is null ? null : WaitForRequestMark(set.Request, "full-visible");
        Mark? info = set is null ? null : _log.Find(set.Request, "info");
        string all = string.Join(" | ", HostTexts());
        bool japanese = all.Contains("日本語のテキストファイル", StringComparison.Ordinal);
        bool view = UiaRectById(HostWindow(), "QuickViewTextLines") != "missing";
        bool ok = set?.Text("encoding") == "Shift_JIS" && set.Text("language") == "plain" && set.Bool("wrap") == true && visible is not null && japanese && view;
        bool closed = CloseHostOrExplorer();
        return (ok && closed, $"decoder={set?.Text("decoder")} encoding={set?.Text("encoding")} language={set?.Text("language")} wrap={set?.Bool("wrap")} lines={set?.Number("lines")}; shown {(info is null || visible is null ? double.NaN : Ms(info.Qpc, visible.Qpc)):0} ms after the file facts; Japanese on screen={japanese}, view={view}");
    }

    private (bool, string) TextCodeScenario(string path)
    {
        if (!ShowInQuickView(path, out long t0, out string why))
        {
            return (false, why);
        }

        var observed = new List<string>();
        Mark? set = WaitForMark(t0, "full-set", m => m.Text("decoder") == "text");
        bool ok = set?.Text("language") == "csharp" && set.Bool("wrap") == false && set.Number("lines") == 2005;
        observed.Add($"language={set?.Text("language")} wrap={set?.Bool("wrap")} lines={set?.Number("lines")}");
        string[] first = HostTexts();
        bool numbers = first.Contains("1") && first.Contains("12");
        observed.Add($"line numbers={numbers}");
        ok &= numbers;

        nint host = HostWindow();
        if (Native.GetWindowRect(host, out Native.RECT r))
        {
            Native.SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            Thread.Sleep(100);
            for (int i = 0; i < 10; i++)
            {
                Native.MouseEvent(0x0800 /* MOUSEEVENTF_WHEEL */, 0, 0, unchecked((uint)-120), 0);
                Thread.Sleep(30);
            }

            Thread.Sleep(400);
            string after = string.Join(" | ", HostTexts());
            bool scrolled = after.Contains("// line 0040", StringComparison.Ordinal) && !after.Contains("using System;", StringComparison.Ordinal);
            observed.Add($"wheel ×10: scrolled 30 lines={scrolled}");
            ok &= scrolled;
        }

        bool active = ForceForeground(host);
        Thread.Sleep(200);
        SendKeyIfForeground(VkEnd, host, out _);
        Thread.Sleep(500);
        bool end = string.Join(" | ", HostTexts()).Contains("// line 2000", StringComparison.Ordinal);
        SendKeyIfForeground(VkHome, host, out _);
        Thread.Sleep(500);
        bool home = string.Join(" | ", HostTexts()).Contains("using System;", StringComparison.Ordinal);
        long tWrap = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkW, host, out _);
        Mark? wrap = WaitForMark(tWrap, "text-wrap", _ => true);
        SendKeyIfForeground(VkW, host, out _); // back to the default
        Thread.Sleep(300);
        observed.Add($"End={end}, Home={home}, W: wrap={wrap?.Bool("wrap")} (code={wrap?.Bool("code")})");
        ok &= active && end && home && wrap?.Bool("wrap") == true;
        ok &= CloseHostOrExplorer();
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) TextLargeScenario(string path)
    {
        _host?.Refresh();
        long before = _host?.WorkingSet64 ?? 0;
        if (!ShowInQuickView(path, out long t0, out string why))
        {
            return (false, why);
        }

        var observed = new List<string>();
        Mark? set = WaitForMark(t0, "full-set", m => m.Text("decoder") == "text");
        Mark? visible = set is null ? null : WaitForRequestMark(set.Request, "full-visible");
        Mark? info = set is null ? null : _log.Find(set.Request, "info");
        double firstScreen = info is null || visible is null ? double.NaN : Ms(info.Qpc, visible.Qpc);
        observed.Add($"first screen {firstScreen:0} ms after the file facts (lines known then: {set?.Number("lines")})");
        bool ok = firstScreen < 1000 && set?.Text("language") == "log";

        Mark? index = set is null ? null : WaitForRequestMark(set.Request, "text-index", 60_000);
        observed.Add($"index: {index?.Number("lines")} lines, {index?.Number("bytes") / (1024 * 1024):0} MB in {index?.Number("ms"):0} ms, memory {index?.Number("indexMemory") / 1024:0} KB, truncated={index?.Bool("truncated")}");
        ok &= index?.Number("lines") == 2_000_000 && index.Bool("truncated") == false && index.Number("indexMemory") < 4 * 1024 * 1024;

        nint host = HostWindow();
        ForceForeground(host);
        Thread.Sleep(200);
        SendKeyIfForeground(VkEnd, host, out _);
        Thread.Sleep(700);
        bool last = string.Join(" | ", HostTexts()).Contains("row 1999999", StringComparison.Ordinal);
        _host?.Refresh();
        long during = _host?.WorkingSet64 ?? 0;
        observed.Add($"End: last row on screen={last}; host working set {before / (1024 * 1024)} → {during / (1024 * 1024)} MB");
        ok &= last && during - before < 200L * 1024 * 1024;
        ok &= CloseHostOrExplorer();
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) TextRoutingScenario(Phase4Files files)
    {
        var observed = new List<string>();
        bool ok = true;
        foreach ((string path, string? language) in new[] { (files.Readme, "plain"), (files.Unknown, "plain"), (files.TypeScript, "typescript") })
        {
            if (!ShowInQuickView(path, out long t0, out string why))
            {
                observed.Add($"{Path.GetFileName(path)}: {why}");
                ok = false;
                continue;
            }

            Mark? set = WaitForMark(t0, "full-set", _ => true);
            bool right = set?.Text("decoder") == "text" && set.Text("language") == language;
            observed.Add($"{Path.GetFileName(path)}: {set?.Text("decoder")} {set?.Text("language")}");
            ok &= right;
            ok &= CloseHostOrExplorer();
        }

        // The information card is not "full-visible": wait for the skip instead of the shown image.
        if (ShowInQuickView(files.Binary, out long tb, out string whyBinary) || whyBinary.Contains("first item", StringComparison.Ordinal))
        {
            Mark? skipped = WaitForMark(tb, "full-skipped", _ => true);
            Thread.Sleep(300);
            string all = string.Join(" | ", HostTexts());
            bool message = all.Contains("表示できるテキストが含まれていません", StringComparison.Ordinal) || all.Contains("does not contain text", StringComparison.Ordinal);
            observed.Add($"{Path.GetFileName(files.Binary)}: {skipped?.Text("reason")}, message={message}");
            ok &= skipped?.Text("reason") == "text-binary" && message;
            ok &= CloseHostOrExplorer();
        }
        else
        {
            observed.Add($"binary: {whyBinary}");
            ok = false;
        }

        return (ok, string.Join("; ", observed));
    }

    // ------------------------------------------------------------------ Markdown

    private (bool, string) MarkdownScenario(string path, ConnectionCounter listener)
    {
        int connections = listener.Count;
        if (!ShowInQuickView(path, out long t0, out string why))
        {
            return (false, why);
        }

        var observed = new List<string>();
        Mark? set = WaitForMark(t0, "full-set", m => m.Text("decoder") == "markdown");
        Mark? visible = set is null ? null : WaitForRequestMark(set.Request, "full-visible");
        Thread.Sleep(1500); // images load after the text
        string all = string.Join(" | ", HostTexts());
        bool heading = all.Contains("Mavue Markdown test", StringComparison.Ordinal);
        bool table = all.Contains("セル", StringComparison.Ordinal);
        bool alert = all.Contains("警告", StringComparison.Ordinal) || all.Contains("Warning", StringComparison.Ordinal);
        _log.Poll();
        List<Mark> images = set is null ? [] : _log.ForRequest(set.Request).Where(m => m.Name == "markdown-image").ToList();
        bool image = images.Any(m => m.Text("origin") == "Local" && m.Bool("drawn") == true);
        int remote = images.Count(m => m.Text("origin") == "Remote" && m.Bool("drawn") == false);
        int requested = listener.Count - connections;
        observed.Add($"blocks={set?.Number("blocks")} parse {set?.Number("parseMs")} ms, realized {visible?.Number("realizedBlocks")}; heading={heading}, table={table}, alert={alert}, local image drawn={image}, remote images not loaded={remote}, connections to the test server={requested}");
        bool ok = set is not null && visible is not null && heading && table && alert && image && remote >= 2 && requested == 0;

        nint host = HostWindow();
        ForceForeground(host);
        Thread.Sleep(200);
        long tSource = Stopwatch.GetTimestamp();
        SendChordIfForeground(host, VkControl, VkU);
        (Mark? nav, Mark? done) = WaitForNavigation(tSource, "source-toggle");
        Mark? source = nav is null ? null : _log.Find(nav.Request, "full-set");
        bool sourceShown = done?.Name == "full-visible" && source?.Text("decoder") == "text" && source.Text("language") == "markdown";
        long tBack = Stopwatch.GetTimestamp();
        SendChordIfForeground(host, VkControl, VkU);
        (Mark? navBack, _) = WaitForNavigation(tBack, "source-toggle");
        bool back = navBack is not null && _log.Find(navBack.Request, "full-set")?.Text("decoder") == "markdown";
        observed.Add($"Ctrl+U: source={sourceShown}, back to rendered={back}");
        ok &= sourceShown && back && CloseHostOrExplorer();
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) MarkdownLargeScenario(string path)
    {
        if (!ShowInQuickView(path, out long t0, out string why))
        {
            return (false, why);
        }

        Mark? set = WaitForMark(t0, "full-set", _ => true);
        bool notice = UiaRectById(HostWindow(), "QuickViewTextNotice") != "missing";
        bool ok = set?.Text("decoder") == "text" && set.Text("language") == "markdown" && notice;
        ok &= CloseHostOrExplorer();
        return (ok, $"{new FileInfo(path).Length / (1024 * 1024)} MB: decoder={set?.Text("decoder")} language={set?.Text("language")}, notice={notice}");
    }

    // ------------------------------------------------------------------ HTML

    private (bool, string) HtmlScenario(string path, ConnectionCounter listener)
    {
        int connections = listener.Count;
        if (!ShowInQuickView(path, out long t0, out string why))
        {
            return (false, why);
        }

        var observed = new List<string>();
        Mark? set = WaitForMark(t0, "full-set", m => m.Text("decoder") == "html", 20_000);
        Thread.Sleep(2500); // anything the page could still try
        string all = string.Join(" | ", HostTexts().Concat(HostTexts())); // the first query turns on the page's accessibility tree
        // The heading is green (#00AA66) by an inline style; a script that ran would have replaced the whole body.
        int green = GreenHeadingPixels(HostWindow());
        bool drawn = all.Contains("Safe HTML test", StringComparison.Ordinal) || green > 1500;
        bool script = all.Contains("SCRIPT RAN", StringComparison.Ordinal);
        int requested = listener.Count - connections;
        _log.Poll();
        int blockedMarks = _log.Marks.Count(m => m.Name == "html-blocked" && m.Qpc >= t0);
        bool fetched = _log.Marks.Any(m => m.Name == "html-blocked" && m.Qpc >= t0 && (m.Text("detail") ?? string.Empty).StartsWith("Fetch", StringComparison.Ordinal));
        script |= fetched; // the script would have called fetch()
        observed.Add($"loaded={set?.Bool("loaded")} in {set?.Number("ms"):0} ms (runtime {set?.Text("runtime")}), charset={set?.Text("charset")}; page drawn={drawn} (green heading pixels {green}), script ran={script}; connections to the test server={requested}; blocked events so far={blockedMarks}");
        bool ok = set?.Bool("loaded") == true && drawn && !script && requested == 0;

        nint host = HostWindow();
        ForceForeground(host);
        Thread.Sleep(200);
        long tSource = Stopwatch.GetTimestamp();
        SendChordIfForeground(host, VkControl, VkU);
        (Mark? nav, Mark? done) = WaitForNavigation(tSource, "source-toggle");
        Mark? source = nav is null ? null : _log.Find(nav.Request, "full-set");
        bool sourceShown = done?.Name == "full-visible" && source?.Text("decoder") == "text" && source.Text("language") == "html";
        Mark? total = _log.Marks.LastOrDefault(m => m.Name == "html-blocked-total" && m.Qpc >= t0);
        observed.Add($"blocked: requests={total?.Number("requests")} navigations={total?.Number("navigations")} downloads={total?.Number("downloads")}; Ctrl+U source={sourceShown}");
        // Content-Security-Policy stops the sub-resources before they become requests; the frames are cancelled as navigations.
        ok &= sourceShown && total is not null && total.Number("navigations") >= 2 && total.Number("downloads") == 0;
        SendChordIfForeground(host, VkControl, VkU); // back to the page for the next run
        Thread.Sleep(800);
        ok &= CloseHostOrExplorer();

        bool ended = WaitFor(() => QuickViewBrowserProcesses() == 0, 8000, null);
        observed.Add($"WebView2 processes of Quick View after closing: {QuickViewBrowserProcesses()} (ended={ended}); connections in total={listener.Count - connections}");
        ok &= ended && listener.Count == connections;
        return (ok, string.Join("; ", observed));
    }

    /// <summary>WebView2 processes started for Quick View's HTML preview (its own user data folder).</summary>
    private static int QuickViewBrowserProcesses()
    {
        string script = "(@(Get-CimInstance Win32_Process -Filter \"Name='msedgewebview2.exe'\" | Where-Object { $_.CommandLine -like '*Mavue\\QuickView\\WebView2*' })).Count";
        using Process ps = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"", StringComparison.Ordinal)}\"")
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        string output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(20000);
        return int.TryParse(output.Trim(), out int count) ? count : -1;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Pixels of the page's green heading color in the Quick View window.</summary>
    private static int GreenHeadingPixels(nint window)
    {
        if (!Native.GetWindowRect(window, out Native.RECT r) || r.Right <= r.Left || r.Bottom <= r.Top)
        {
            return 0;
        }

        byte[] pixels = Native.CaptureScreen(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        SaveCapture(Path.Combine(Path.GetTempPath(), "Mavue.QuickView.E2E", "html-capture.bmp"), pixels, r.Right - r.Left, r.Bottom - r.Top);
        int count = 0;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            // BGRA. The display's color management shifts #00AA66 (measured: R 79, G 166, B 108), so test the hue.
            int b = pixels[i], g = pixels[i + 1], red = pixels[i + 2];
            if (g > 130 && g > red + 60 && g > b + 35 && Math.Abs(red - 79) < 45 && Math.Abs(b - 108) < 40)
            {
                count++;
            }
        }

        return count;
    }

    private bool ShowInQuickView(string path, out long t0, out string why)
    {
        nint explorer = OpenExplorer(path);
        dynamic? window = ShellWindowFor(explorer);
        t0 = Stopwatch.GetTimestamp();
        if (explorer == 0 || window is null)
        {
            why = "Explorer window not found";
            return false;
        }

        if (!OpenQuickView(explorer, path, out why))
        {
            return false;
        }

        _lastPhase4Explorer = explorer;
        return true;
    }

    private nint _lastPhase4Explorer;

    private bool CloseHostOrExplorer() =>
        (_lastPhase4Explorer != 0 && Native.GetForegroundWindow() == _lastPhase4Explorer && CloseFromExplorer(_lastPhase4Explorer)) || CloseHostWindow();

    private Mark? WaitForMark(long since, string name, Func<Mark, bool> match, int timeoutMs = 10_000)
    {
        Mark? found = null;
        WaitFor(
            () =>
            {
                _log.Poll();
                found = _log.Marks.LastOrDefault(m => m.Name == name && m.Qpc >= since && match(m));
                return found is not null;
            },
            timeoutMs,
            null);
        return found;
    }

    private Mark? WaitForRequestMark(long request, string name, int timeoutMs = 10_000)
    {
        Mark? found = null;
        WaitFor(
            () =>
            {
                _log.Poll();
                found = _log.Find(request, name);
                return found is not null;
            },
            timeoutMs,
            null);
        return found;
    }

    /// <summary>A local TCP server that only counts connections (what a page or image must never open).</summary>
    private sealed class ConnectionCounter : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _count;

        public ConnectionCounter()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        public int Port { get; }

        public int Count => Volatile.Read(ref _count);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _count);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
            }
        }
    }
}
