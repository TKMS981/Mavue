using System.Diagnostics;
using Mavue.Pdf.Tests;

namespace Mavue.QuickView.Harness;

/// <summary>
/// PDF in Quick View with PDFium (the session shared with the main window): continuous scrolling, page keys from
/// Explorer, find and F3, text selection and copy, the page box, links, the outline in the sidebar, the two-page layout
/// with the cover alone, a Japanese PDF (made by Edge from HTML) and the presentation. Positions on pages come from the
/// host's "pdf-geometry" marks (window client, physical pixels).
/// </summary>
internal sealed partial class Runner
{
    private string HostSettingsPath => Path.Combine(options.WorkDirectory, "settings-e2e.json");

    /// <summary>The host's settings file for E2E (image scale from the options, optional PDF layout).</summary>
    private void WriteHostSettings(string? pdfLayout = null) => File.WriteAllText(
        HostSettingsPath,
        $"{{\"version\": 1, \"imageScale\": \"{(options.ScaleMode == "actual" ? "ActualSize" : "FitNoUpscale")}\"{(pdfLayout is null ? string.Empty : $", \"pdfLayout\": \"{pdfLayout}\"")}}}");

    private void RunQuickViewPdfScenario(List<TestAsset> assets)
    {
        string folder = Path.GetDirectoryName(assets[0].Path)! + " QV PDF";
        Directory.CreateDirectory(folder);
        string document = Path.Combine(folder, "01 文書.pdf");
        string japanese = Path.Combine(folder, "02 日本語.pdf");
        File.WriteAllBytes(document, TestPdf.Create(8));
        string japaneseNote = WriteJapanesePdf(japanese, folder);
        WriteHostSettings();
        Guarded("quickview-pdf", () => QuickViewPdf(document, File.Exists(japanese) ? japanese : null, japaneseNote),
            "Quick View, 8-page PDF (PDFium view): continuous layout, PageDown from Explorer turns pages, the wheel scrolls; after a click into Quick View: Ctrl+F finds the word on 8 pages and F3 moves on, Ctrl+G 1 Enter goes to page 1, dragging selects a line and Ctrl+C copies it, the internal link shows page 8, the web link is reported, the sidebar outline goes to Chapter 5, Ctrl+Shift+4 shows the cover alone and pages 2–3 side by side; ↓ to a Japanese PDF where \"東京\" is found twice and Ctrl+A, Ctrl+C copies its text; F5 presents (→ next page, Esc ends) and Quick View stays; Esc closes");
        WriteHostSettings();
    }

    /// <summary>A Japanese PDF printed by Edge (headless) from HTML: real embedded Japanese fonts and a link.</summary>
    private static string WriteJapanesePdf(string pdf, string folder)
    {
        if (File.Exists(pdf))
        {
            return "existing";
        }

        string edge = new[]
        {
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
        }.FirstOrDefault(File.Exists) ?? string.Empty;
        if (edge.Length == 0)
        {
            return "Edge not found";
        }

        string html = Path.Combine(folder, "japanese.html");
        File.WriteAllText(html, "<!doctype html><html lang=\"ja\"><meta charset=\"utf-8\"><body style=\"font-family:'Yu Gothic','Meiryo',sans-serif\">" +
            "<h1>日本語テスト文書</h1><p>これは Mavue の検索テストです。東京都の天気は晴れ。</p><p><a href=\"https://example.com/jp\">外部リンク</a></p>" +
            "<div style=\"page-break-before:always\"><h2>第二章</h2><p>二ページ目にも東京という語があります。</p></div></body></html>", new System.Text.UTF8Encoding(true));
        using Process? process = Process.Start(new ProcessStartInfo(edge, $"--headless=new --disable-gpu --no-pdf-header-footer --print-to-pdf=\"{pdf}\" \"{new Uri(html).AbsoluteUri}\"") { UseShellExecute = false, CreateNoWindow = true });
        process?.WaitForExit(30000);
        return File.Exists(pdf) ? "made by Edge" : "Edge did not write the PDF";
    }

    private (bool, string) QuickViewPdf(string document, string? japanese, string japaneseNote)
    {
        nint explorer = OpenExplorer(document);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Select(window, document, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, document, out string why))
        {
            return (false, why);
        }

        var observed = new List<string>();
        _log.Poll();
        Mark? first = _log.Marks.LastOrDefault(m => m.Name == "full-set");
        bool ok = first?.Text("decoder") == "pdfium-view" && first.Number("pageCount") == 8 && first.Text("layout") == "Continuous";
        observed.Add($"open: {first?.Text("decoder")} {first?.Text("layout")} {first?.Number("pageCount")} pages");

        // PageDown with Explorer in front (the hook) → page 2.
        long t = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkPageDown, explorer, out _);
        (Mark? nav, Mark? done) = WaitForNavigation(t, "pdf-page");
        Mark? full = nav is null ? null : _log.Find(nav.Request, "full-set");
        bool pageOk = done?.Name == "full-visible" && full?.Number("pageIndex") == 1 && ExplorerSelectionIs(explorer, document);
        observed.Add($"PageDown (Explorer) → page {full?.Number("pageIndex") + 1}, selection kept={ExplorerSelectionIs(explorer, document)}");
        ok &= pageOk;

        // The wheel scrolls the continuous view.
        nint host = HostWindow();
        if (!Native.GetWindowRect(host, out Native.RECT r))
        {
            return (false, "no Quick View window");
        }

        double offsetBefore = LastOffset();
        Native.SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        Thread.Sleep(150);
        for (int i = 0; i < 3; i++)
        {
            Native.MouseEvent(0x0800, 0, 0, unchecked((uint)-120), 0);
            Thread.Sleep(80);
        }

        bool scrolled = WaitFor(() => { _log.Poll(); return LastOffset() > offsetBefore + 50; }, 3000, null);
        observed.Add($"wheel scrolls {offsetBefore:0} → {LastOffset():0}");
        ok &= scrolled;

        // Click into Quick View (on the info bar): it becomes active.
        Click((r.Left + 60, r.Bottom - 30));
        bool active = WaitFor(() => Native.GetForegroundWindow() == host, 2000, null);
        observed.Add($"active={active}");
        ok &= active;

        // Find: Ctrl+F, the word, Enter; F3.
        t = Stopwatch.GetTimestamp();
        SendChordIfForeground(host, VkControlKey, 0x46);
        Thread.Sleep(400);
        foreach (char c in TestPdf.EveryPageWord.ToUpperInvariant())
        {
            SendKeyIfForeground(c, host, out _);
        }

        SendKeyIfForeground(0x0D, host, out _);
        Mark? search = WaitMark("search", t, 8000);
        long tStep = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkF3, host, out _);
        Mark? step = WaitMark("search-step", tStep, 3000);
        observed.Add($"find: {search?.Number("matches")} results, F3 → page {step?.Number("page") + 1}");
        ok &= search?.Number("matches") == 8 && step is not null;
        SendKeyIfForeground(Native.VK_ESCAPE, host, out _); // closes the find bar (not Quick View)
        Thread.Sleep(400);
        bool stillShown = Native.IsWindowVisible(host);
        observed.Add($"Esc in the find box keeps Quick View={stillShown}");
        ok &= stillShown;

        // Ctrl+G 1 Enter → page 1.
        t = Stopwatch.GetTimestamp();
        SendChordIfForeground(host, VkControlKey, 0x47);
        Thread.Sleep(300);
        SendKeyIfForeground(0x31, host, out _);
        SendKeyIfForeground(0x0D, host, out _);
        (Mark? toFirst, _) = WaitForNavigation(t, "pdf-page");
        Mark? firstPage = toFirst is null ? null : _log.Find(toFirst.Request, "full-set");
        Mark? geometry = WaitMarkWhere("pdf-geometry", t, 3000, m => m.Number("page") == 0);
        observed.Add($"Ctrl+G 1 → page {firstPage?.Number("pageIndex") + 1}");
        ok &= firstPage?.Number("pageIndex") == 0 && geometry is not null;
        if (geometry is null)
        {
            CloseQuickViewWindow(host);
            return (false, string.Join("; ", observed) + "; no page geometry");
        }

        double scale = geometry.Number("width")!.Value / 595.0;
        (int X, int Y) At(double x, double y) => ClientToScreen(host, (int)(geometry.Number("x")!.Value + (x * scale)), (int)(geometry.Number("y")!.Value + (y * scale)));

        // Select "The quick brown fox…" and copy.
        ClearClipboard();
        t = Stopwatch.GetTimestamp();
        Drag(At(58, 136), At(205, 136));
        Mark? selection = WaitMarkWhere("pdf-selection", t, 3000, m => m.Bool("active") == true);
        Thread.Sleep(300);
        SendChordIfForeground(host, VkControlKey, 0x43);
        string? text = WaitForClipboardText(3000);
        observed.Add($"drag + Ctrl+C → \"{text}\"");
        ok &= selection is not null && text?.StartsWith("The quick brown fox", StringComparison.Ordinal) == true;

        // Links: internal (→ page 8) and web (reported, not opened in tests).
        t = Stopwatch.GetTimestamp();
        Click(At(120, 236));
        Mark? link = WaitMark("pdf-goto", t, 3000);
        observed.Add($"link → page {link?.Number("page") + 1}");
        ok &= link?.Number("page") == 7;
        t = Stopwatch.GetTimestamp();
        SendChordIfForeground(host, VkControlKey, 0x47);
        Thread.Sleep(300);
        SendKeyIfForeground(0x31, host, out _);
        SendKeyIfForeground(0x0D, host, out _);
        geometry = WaitMarkWhere("pdf-geometry", t, 3000, m => m.Number("page") == 0) ?? geometry;
        scale = geometry.Number("width")!.Value / 595.0;
        t = Stopwatch.GetTimestamp();
        Click(At(120, 266));
        Mark? web = WaitMark("uri-opened", t, 3000);
        observed.Add($"web link → {web?.Text("uri")}");
        ok &= web?.Text("uri") == TestPdf.WebLink && web.Bool("opened") == false; // --no-launch: reported only

        // Outline: open the sidebar, the contents tab, "Chapter 5".
        t = Stopwatch.GetTimestamp();
        string uia = InvokeByUiAutomation(host, ["ページ・目次・検索結果|Pages, contents and results", "目次・しおり|Contents", "Chapter 5"]);
        Mark? chapter = WaitMarkWhere("pdf-goto", t, 4000, m => m.Number("page") == 4);
        observed.Add($"outline Chapter 5 → page {chapter?.Number("page") + 1} ({uia})");
        ok &= chapter is not null;

        // Two pages with the cover alone: Ctrl+Shift+4, then page 2 → pages 2 and 3 side by side.
        ForceForeground(host);
        t = Stopwatch.GetTimestamp();
        SendChordIfForeground(host, VkControlKey, VkShiftKey, 0x34);
        Mark? cover = WaitMarkWhere("pdf-layout", t, 3000, m => m.Text("mode") == "TwoPagesCover");
        SendChordIfForeground(host, VkControlKey, 0x47);
        Thread.Sleep(300);
        SendKeyIfForeground(0x32, host, out _);
        SendKeyIfForeground(0x0D, host, out _);
        Mark? left = WaitMarkWhere("pdf-geometry", t, 4000, m => m.Number("page") == 1);
        Mark? right = WaitMarkWhere("pdf-geometry", t, 4000, m => m.Number("page") == 2);
        bool spread = cover is not null && left is not null && right is not null && Math.Abs(left.Number("y")!.Value - right.Number("y")!.Value) <= 2 && right.Number("x") > left.Number("x");
        observed.Add($"cover alone: pages 2–3 side by side={spread}");
        ok &= spread;
        SendChordIfForeground(host, VkControlKey, VkShiftKey, 0x32); // continuous again

        // Japanese PDF: ↓ (the next file), find "東京" by pasting into the find box.
        if (japanese is not null)
        {
            t = Stopwatch.GetTimestamp();
            SendKeyIfForeground(Native.VK_DOWN, host, out _);
            (Mark? toJapanese, Mark? japaneseDone) = WaitForNavigation(t, "explorer-selection");
            Mark? japaneseFull = toJapanese is null ? null : _log.Find(toJapanese.Request, "full-set");
            bool japaneseShown = japaneseDone?.Name == "full-visible" && japaneseFull?.Text("decoder") == "pdfium-view" && ShowsFile(toJapanese!.Request, japanese);
            SetClipboardText("東京");
            ForceForeground(host);
            t = Stopwatch.GetTimestamp();
            SendChordIfForeground(host, VkControlKey, 0x46);
            Thread.Sleep(400);
            SendChordIfForeground(host, VkControlKey, 0x56);
            Thread.Sleep(200);
            SendKeyIfForeground(0x0D, host, out _);
            Mark? found = WaitMark("search", t, 6000);
            observed.Add($"Japanese PDF ({japaneseNote}) shown={japaneseShown}, 東京 → {found?.Number("matches")} results");
            ok &= japaneseShown && found?.Number("matches") == 2;
            SendKeyIfForeground(Native.VK_ESCAPE, host, out _);
            Thread.Sleep(300);

            // Japanese text: Ctrl+A selects the document's text, Ctrl+C copies it.
            ClearClipboard();
            SendChordIfForeground(host, VkControlKey, 0x41);
            Thread.Sleep(400);
            SendChordIfForeground(host, VkControlKey, 0x43);
            string? japaneseText = WaitForClipboardText(3000);
            bool japaneseCopied = japaneseText?.Contains("東京都の天気", StringComparison.Ordinal) == true && japaneseText.Contains("第二章", StringComparison.Ordinal);
            observed.Add($"Ctrl+A, Ctrl+C → Japanese text of both pages={japaneseCopied}");
            ok &= japaneseCopied;
        }
        else
        {
            observed.Add($"Japanese PDF skipped ({japaneseNote})");
        }

        // Presentation: F5, → , Esc; Quick View stays open.
        ForceForeground(host);
        t = Stopwatch.GetTimestamp();
        SendKeyIfForeground(0x74, host, out _);
        Mark? start = WaitMark("presentation-start", t, 3000);
        Thread.Sleep(800);
        nint presentation = Native.GetForegroundWindow();
        SendKeyIfForeground(VkRight, presentation, out _);
        Mark? next = WaitMarkWhere("presentation-page", t, 3000, m => m.Number("page") == (start?.Number("page") ?? 0) + 1);
        SendKeyIfForeground(Native.VK_ESCAPE, presentation, out _);
        Mark? end = WaitMark("presentation-end", t, 3000);
        Thread.Sleep(500);
        bool kept = Native.IsWindowVisible(host) && _log.HiddenSince(t) is null;
        observed.Add($"presentation: start={start is not null}, next page={next is not null}, end={end is not null}, Quick View kept={kept}");
        ok &= start is not null && next is not null && end is not null && kept;

        bool closed = CloseQuickViewWindow(host);
        observed.Add($"Esc closed={closed}");
        return (ok && closed, string.Join("; ", observed));
    }

    private double LastOffset()
    {
        _log.Poll();
        return _log.Marks.LastOrDefault(m => m.Name == "pdf-scrolled")?.Number("offset") ?? 0;
    }

    private Mark? WaitMark(string name, long since, int timeoutMs) => WaitMarkWhere(name, since, timeoutMs, _ => true);

    private Mark? WaitMarkWhere(string name, long since, int timeoutMs, Func<Mark, bool> match)
    {
        Mark? found = null;
        WaitFor(() => { _log.Poll(); found = _log.Marks.FirstOrDefault(m => m.Name == name && m.Qpc >= since && match(m)); return found is not null; }, timeoutMs, null);
        return found;
    }

    private bool CloseQuickViewWindow(nint host)
    {
        long t = Stopwatch.GetTimestamp();
        ForceForeground(host);
        SendKeyIfForeground(Native.VK_ESCAPE, host, out _);
        return WaitFor(() => { _log.Poll(); return _log.HiddenSince(t) is not null; }, 3000, null);
    }

    private static bool SendChordIfForeground(nint expected, ushort first, ushort second, ushort vk)
    {
        if (Native.GetForegroundWindow() != expected)
        {
            return false;
        }

        Native.INPUT[] inputs = [Key(first, false), Key(second, false), Key(vk, false), Key(vk, true), Key(second, true), Key(first, true)];
        return Native.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>()) == inputs.Length;
    }

    private static void SetClipboardText(string text)
    {
        var thread = new Thread(() =>
        {
            // Another process may hold the clipboard for a moment (CLIPBRD_E_CANT_OPEN): retry, never crash the run.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(text);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                    Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                    return;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    Thread.Sleep(100);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
