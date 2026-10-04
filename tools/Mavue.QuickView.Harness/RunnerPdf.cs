using System.Diagnostics;
using System.Runtime.InteropServices;
using Mavue.Pdf.Tests;

namespace Mavue.QuickView.Harness;

/// <summary>
/// The PDF viewer of the main window (PDFium): search and results, text selection and copy, links, outline, and the
/// continuous / two-page / single-page layouts, including a long document whose pages must not all stay in memory.
/// Positions on pages come from the app's "pdf-geometry" trace (window client, physical pixels).
/// </summary>
internal sealed partial class Runner
{
    private const ushort VkShiftKey = 0x10;
    private const ushort VkF3 = 0x72;

    private void RunPdfViewerScenarios()
    {
        string folder = Path.Combine(options.WorkDirectory, "Mavue PDF ビューア");
        Directory.CreateDirectory(folder);
        string document = Path.Combine(folder, "01 文書.pdf");
        string longDocument = Path.Combine(folder, "02 long.pdf");
        File.WriteAllBytes(document, TestPdf.Create(8));
        File.WriteAllBytes(longDocument, TestPdf.Create(600));

        File.Delete(AppSettingsPath);
        Guarded("app-pdf-text", () => AppPdfText(document),
            "8-page PDF (PDFium): Ctrl+F + word + Enter finds it on all 8 pages, F3 moves to the next result (page 2); dragging over a line selects it and Ctrl+C copies the text; the link \"Go to the last page\" shows page 8; the web link is reported (not opened in tests); the outline lists 8 chapters and \"Chapter 5\" shows page 5");
        string encrypted = Path.Combine(folder, "03 保護.pdf");
        File.WriteAllBytes(encrypted, TestPdf.CreateEncrypted("mavue"));
        File.Delete(AppSettingsPath);
        Guarded("app-pdf-password", () => AppPdfPassword(encrypted),
            "Encrypted PDF: the password dialog opens; a wrong password asks again (\"incorrect\"); the right one shows the page with its text; a second time Cancel shows the \"password-protected\" message and the app keeps working");
        File.Delete(AppSettingsPath);
        Guarded("app-pdf-layouts", () => AppPdfLayouts(longDocument),
            "600-page PDF: continuous scrolling (End → page 600, PageUp → 599) keeps only the pages near the screen (≤ 8 page elements, private memory stays bounded); Ctrl+Shift+3 shows two pages side by side; Ctrl+Shift+1 shows a single page; Ctrl+Shift+2 back to continuous; Ctrl+Shift+4 shows the cover alone and pages 2–3 side by side; F5 presents full screen (→ next page, Esc ends, the window stays)");
        File.Delete(AppSettingsPath);
    }

    private (bool, string) AppPdfText(string document)
    {
        using AppSession app = LaunchApp(document);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        var observed = new List<string>();
        AppEvent? opened = app.WaitFor(e => e.Name == "pdf-opened", 15000);
        AppEvent? geometry = app.WaitFor(e => e.Name == "pdf-geometry" && e.Number("page") == 0, 10000);
        bool ok = opened?.Number("pages") == 8 && geometry is not null;
        observed.Add($"opened {opened?.Number("pages")} pages ({opened?.Text("engine")}, {opened?.Text("layout")})");
        if (geometry is null)
        {
            return (false, string.Join("; ", observed) + "; no page geometry");
        }

        // Search: Ctrl+F, type, Enter; F3.
        int mark = app.Events.Count;
        app.SendChord(VkControlKey, 0x46);
        Thread.Sleep(300);
        foreach (char c in TestPdf.EveryPageWord.ToUpperInvariant())
        {
            app.SendKey(c);
        }

        app.SendKey(0x0D);
        AppEvent? search = app.WaitFor(e => e.Name == "search", 8000, mark);
        mark = app.Events.Count;
        app.SendKey(VkF3);
        AppEvent? step = app.WaitFor(e => e.Name == "search-step", 3000, mark);
        bool searchOk = search?.Number("matches") == 8 && step?.Number("page") == 1;
        observed.Add($"search: {search?.Number("matches")} results, F3 → page {step?.Number("page") + 1}");
        ok &= searchOk;
        app.SendKey(Native.VK_ESCAPE); // close the search bar
        Thread.Sleep(300);
        mark = app.Events.Count;
        app.SendKey(VkHome);
        geometry = app.WaitFor(e => e.Name == "pdf-geometry" && e.Number("page") == 0, 3000, mark) ?? geometry; // where page 1 is now
        if (!WaitFor(() => app.Events.Any(e => e.Name == "state" && e.Text("kind") == "Pdf" && e.Number("pageIndex") == 0), 2000, null))
        {
            observed.Add("Home did not return to page 1");
        }

        double scale = geometry.Number("width")!.Value / 595.0;
        (int X, int Y) At(double x, double y) => ClientToScreen(app.Window, (int)(geometry.Number("x")!.Value + (x * scale)), (int)(geometry.Number("y")!.Value + (y * scale)));

        // Select "The quick brown fox" by dragging over the line (60, 142 pt baseline), copy with Ctrl+C.
        ClearClipboard();
        mark = app.Events.Count;
        Drag(At(58, 136), At(205, 136));
        AppEvent? selection = app.WaitFor(e => e.Name == "pdf-selection" && e.Bool("active") == true, 3000, mark);
        Thread.Sleep(300);
        app.SendChord(VkControlKey, 0x43);
        AppEvent? copied = app.WaitFor(e => e.Name is "text-copied" or "copied", 3000, mark);
        string? text = WaitForClipboardText(3000);
        bool selectOk = selection is not null && copied?.Name == "text-copied" && text?.StartsWith("The quick brown fox", StringComparison.Ordinal) == true;
        observed.Add($"drag-select + Ctrl+C → \"{text}\"");
        ok &= selectOk;
        app.SendKey(Native.VK_ESCAPE); // clear the selection

        // Links: "Go to the last page" (55..240 × 224..247 pt) and "Open the web site" (55..220 × 254..277 pt).
        mark = app.Events.Count;
        Click(At(120, 236));
        AppEvent? link = app.WaitFor(e => e.Name == "pdf-goto", 3000, mark);
        AppEvent? onLast = app.WaitForState(s => s.Text("kind") == "Pdf" && s.Number("pageIndex") == 7, 3000, mark);
        observed.Add($"link → page {link?.Number("page") + 1} (shown {onLast?.Number("pageIndex") + 1})");
        ok &= link?.Number("page") == 7 && onLast is not null;

        mark = app.Events.Count;
        app.SendKey(VkHome);
        geometry = app.WaitFor(e => e.Name == "pdf-geometry" && e.Number("page") == 0, 3000, mark) ?? geometry;
        scale = geometry.Number("width")!.Value / 595.0;
        mark = app.Events.Count;
        Click(At(120, 266));
        AppEvent? web = app.WaitFor(e => e.Name == "uri-opened", 3000, mark);
        observed.Add($"web link → {web?.Text("uri")} (opened in test: {web?.Bool("opened")})");
        ok &= web?.Text("uri") == TestPdf.WebLink && web.Bool("opened") == false;

        // Outline: 8 entries; invoke "Chapter 5" through UI Automation (Contents tab, then the tree item).
        AppEvent? outline = app.WaitFor(e => e.Name == "outline", 3000);
        mark = app.Events.Count;
        string uia = InvokeByUiAutomation(app.Process.Id, ["目次・しおり|Contents", "Chapter 5"]);
        AppEvent? invoked = app.WaitFor(e => e.Name == "outline-invoked", 4000, mark);
        AppEvent? chapter = app.WaitForState(s => s.Text("kind") == "Pdf" && s.Number("pageIndex") == 4, 3000, mark);
        observed.Add($"outline {outline?.Number("items")} items; Chapter 5 → page {chapter?.Number("pageIndex") + 1} ({uia})");
        ok &= outline?.Number("items") == 8 && invoked is not null && chapter is not null;

        (bool closed, _) = app.Close(expectPlayerStop: false);
        ok &= closed;
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) AppPdfPassword(string encrypted)
    {
        var observed = new List<string>();
        bool ok;
        using (AppSession app = LaunchApp(encrypted))
        {
            if (app.Window == 0)
            {
                return (false, "no window");
            }

            AppEvent? first = app.WaitFor(e => e.Name == "password-dialog", 15000);
            Thread.Sleep(600);
            int mark = app.Events.Count;
            foreach (char c in "WRONG")
            {
                app.SendKey(c);
            }

            app.SendKey(0x0D);
            AppEvent? again = app.WaitFor(e => e.Name == "password-dialog" && e.Bool("wrong") == true, 8000, mark);
            Thread.Sleep(600);
            mark = app.Events.Count;
            foreach (char c in "MAVUE")
            {
                app.SendKey(c);
            }

            app.SendKey(0x0D);
            AppEvent? opened = app.WaitFor(e => e.Name == "pdf-opened", 10000, mark);
            AppEvent? state = app.WaitForState(s => s.Text("kind") == "Pdf" && s.Number("pageCount") == 1, 5000, mark);
            observed.Add($"dialog={first is not null}, wrong password → asked again={again is not null}, right password → opened {opened?.Number("pages")} page(s), state Pdf={state is not null}");
            ok = first is not null && again is not null && opened?.Number("pages") == 1 && state is not null;
            (bool closed, _) = app.Close(expectPlayerStop: false);
            ok &= closed;
        }

        using (AppSession app = LaunchApp(encrypted))
        {
            AppEvent? dialog = app.WaitFor(e => e.Name == "password-dialog", 15000);
            Thread.Sleep(600);
            int mark = app.Events.Count;
            app.SendKey(Native.VK_ESCAPE); // Cancel
            AppEvent? message = app.WaitForState(s => s.Text("kind") == "Message", 5000, mark);
            observed.Add($"Cancel → message={message?.Text("message") ?? message?.Text("kind")}");
            ok &= dialog is not null && message is not null;
            (bool closed, _) = app.Close(expectPlayerStop: false);
            ok &= closed;
        }

        return (ok, string.Join("; ", observed));
    }

    private (bool, string) AppPdfLayouts(string longDocument)
    {
        using AppSession app = LaunchApp(longDocument);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        var observed = new List<string>();
        AppEvent? opened = app.WaitFor(e => e.Name == "pdf-opened", 20000);
        app.WaitFor(e => e.Name == "pdf-rendered", 10000);
        bool ok = opened?.Number("pages") == 600 && opened.Text("layout") == "Continuous";
        long privateStart = PrivateMb(app.Process);

        int mark = app.Events.Count;
        app.SendKey(VkEnd);
        AppEvent? end = app.WaitForState(s => s.Text("kind") == "Pdf" && s.Number("pageIndex") == 599, 5000, mark);
        AppEvent? endRendered = app.WaitFor(e => e.Name == "pdf-rendered" && e.Number("page") == 599, 8000, mark);
        mark = app.Events.Count;
        app.SendKey(VkPageUp);
        AppEvent? up = app.WaitForState(s => s.Text("kind") == "Pdf" && s.Number("pageIndex") == 598, 4000, mark);

        // Scroll through the middle of the document with PageUp, then check what stays loaded.
        for (int i = 0; i < 30; i++)
        {
            app.SendKey(VkPageUp);
            Thread.Sleep(40);
        }

        Thread.Sleep(1500);
        int realized = (int)(app.Events.LastOrDefault(e => e.Name == "pdf-rendered")?.Number("realized") ?? -1);
        long privateAfter = PrivateMb(app.Process);
        bool memoryOk = realized is > 0 and <= 8 && privateAfter - privateStart < 300;
        observed.Add($"continuous: End → page {end?.Number("pageIndex") + 1} (drawn={endRendered is not null}), PageUp → {up?.Number("pageIndex") + 1}; after scrolling {realized} page elements, private {privateStart} → {privateAfter} MB");
        ok &= end is not null && endRendered is not null && up is not null && memoryOk;

        // Two pages: Ctrl+Shift+3 — a spread has two pages on the same row.
        mark = app.Events.Count;
        app.SendKey(VkHome);
        Thread.Sleep(800);
        mark = app.Events.Count;
        app.SendChordWith([VkControlKey, VkShiftKey], 0x33);
        AppEvent? two = app.WaitForState(s => s.Text("pdfLayout") == "TwoPages", 3000, mark);
        AppEvent? left = app.WaitFor(e => e.Name == "pdf-geometry" && e.Number("page") == 0, 5000, mark);
        AppEvent? right = app.WaitFor(e => e.Name == "pdf-geometry" && e.Number("page") == 1, 5000, mark);
        bool spreadOk = two is not null && left is not null && right is not null &&
            Math.Abs(left.Number("y")!.Value - right.Number("y")!.Value) <= 2 && right.Number("x") > left.Number("x") + left.Number("width") - 2;
        observed.Add($"two pages: pages 1–2 side by side={spreadOk}");
        ok &= spreadOk;

        // PageDown in two pages moves by a spread (page 3).
        mark = app.Events.Count;
        app.SendKey(VkPageDown);
        AppEvent? spreadStep = app.WaitForState(s => s.Text("kind") == "Pdf" && s.Number("pageIndex") == 2, 3000, mark);
        observed.Add($"PageDown → page {spreadStep?.Number("pageIndex") + 1}");
        ok &= spreadStep is not null;

        // Single page: Ctrl+Shift+1; continuous again: Ctrl+Shift+2.
        mark = app.Events.Count;
        app.SendChordWith([VkControlKey, VkShiftKey], 0x31);
        AppEvent? single = app.WaitForState(s => s.Text("pdfLayout") == "SinglePage", 3000, mark);
        mark = app.Events.Count;
        app.SendKey(VkPageDown);
        AppEvent? singleStep = app.WaitForState(s => s.Text("pdfLayout") == "SinglePage" && s.Number("pageIndex") == 3, 3000, mark);
        mark = app.Events.Count;
        app.SendChordWith([VkControlKey, VkShiftKey], 0x32);
        AppEvent? continuous = app.WaitForState(s => s.Text("pdfLayout") == "Continuous", 3000, mark);
        observed.Add($"single page={single is not null} (PageDown → page {singleStep?.Number("pageIndex") + 1}), continuous again={continuous is not null}");
        ok &= single is not null && singleStep is not null && continuous is not null;

        // Two pages with the cover alone: Ctrl+Shift+4 — page 1 on its own row, pages 2–3 side by side.
        app.SendKey(VkHome);
        Thread.Sleep(800);
        mark = app.Events.Count;
        app.SendChordWith([VkControlKey, VkShiftKey], 0x34);
        AppEvent? cover = app.WaitForState(s => s.Text("pdfLayout") == "TwoPagesCover", 3000, mark);
        AppEvent? coverPage = app.WaitFor(e => e.Name == "pdf-geometry" && e.Number("page") == 0, 5000, mark);
        AppEvent? second = app.WaitFor(e => e.Name == "pdf-geometry" && e.Number("page") == 1, 5000, mark);
        AppEvent? third = app.WaitFor(e => e.Name == "pdf-geometry" && e.Number("page") == 2, 5000, mark);
        bool coverOk = cover is not null && coverPage is not null && second is not null && third is not null &&
            second.Number("y") > coverPage.Number("y") + 2 &&
            Math.Abs(second.Number("y")!.Value - third.Number("y")!.Value) <= 2 && third.Number("x") > second.Number("x") + second.Number("width") - 2;
        observed.Add($"cover alone: page 1 alone, pages 2–3 side by side={coverOk}");
        ok &= coverOk;

        // Presentation: F5 from the current page, → next page, Esc ends; the window stays.
        mark = app.Events.Count;
        app.SendKey(0x74);
        AppEvent? start = app.WaitFor(e => e.Name == "presentation-start", 3000, mark);
        Thread.Sleep(800);
        nint presentation = Native.GetForegroundWindow();
        bool fullScreen = presentation != app.Window && Native.GetWindowRect(presentation, out Native.RECT pr) &&
            Native.Monitors().Any(m => pr.Left <= m.Bounds.Left && pr.Top <= m.Bounds.Top && pr.Right >= m.Bounds.Right && pr.Bottom >= m.Bounds.Bottom);
        SendKeyIfForeground(VkRight, presentation, out _);
        AppEvent? next = app.WaitFor(e => e.Name == "presentation-page" && e.Number("page") == (start?.Number("page") ?? 0) + 1, 3000, mark);
        SendKeyIfForeground(Native.VK_ESCAPE, presentation, out _);
        AppEvent? ended = app.WaitFor(e => e.Name == "presentation-end", 3000, mark);
        Thread.Sleep(500);
        bool back = Native.IsWindowVisible(app.Window) && !app.Process.HasExited;
        observed.Add($"presentation: start={start is not null} (page {start?.Number("page") + 1}), full screen={fullScreen}, → page {next?.Number("page") + 1}, Esc end={ended is not null}, window kept={back}");
        ok &= start is not null && fullScreen && next is not null && ended is not null && back;
        app.SendChordWith([VkControlKey, VkShiftKey], 0x32);

        (bool closed, _) = app.Close(expectPlayerStop: false);
        ok &= closed;
        return (ok, string.Join("; ", observed));
    }

    private static long PrivateMb(Process process)
    {
        process.Refresh();
        return process.PrivateMemorySize64 / (1024 * 1024);
    }

    private static (int X, int Y) ClientToScreen(nint window, int x, int y)
    {
        var point = new Native.POINT { X = x, Y = y };
        Native.ClientToScreen(window, ref point);
        return (point.X, point.Y);
    }

    /// <summary>Absolute mouse moves (SetCursorPos alone does not produce the moves a drag needs).</summary>
    private static void MoveMouse((int X, int Y) point)
    {
        int left = Native.GetSystemMetrics(76), top = Native.GetSystemMetrics(77);
        int width = Native.GetSystemMetrics(78), height = Native.GetSystemMetrics(79);
        int nx = (int)((point.X - left) * 65535L / Math.Max(1, width - 1));
        int ny = (int)((point.Y - top) * 65535L / Math.Max(1, height - 1));
        Native.MouseEvent(0x8001 | 0x4000 /* MOVE | ABSOLUTE | VIRTUALDESK */, nx, ny, 0, 0);
    }

    private static void Click((int X, int Y) point)
    {
        MoveMouse(point);
        Thread.Sleep(120);
        Native.MouseEvent(0x0002, 0, 0, 0, 0);
        Thread.Sleep(60);
        Native.MouseEvent(0x0004, 0, 0, 0, 0);
        Thread.Sleep(300);
    }

    private static void Drag((int X, int Y) from, (int X, int Y) to)
    {
        MoveMouse(from);
        Thread.Sleep(120);
        Native.MouseEvent(0x0002, 0, 0, 0, 0);
        for (int i = 1; i <= 12; i++)
        {
            MoveMouse((from.X + ((to.X - from.X) * i / 12), from.Y + ((to.Y - from.Y) * i / 12)));
            Thread.Sleep(40);
        }

        Thread.Sleep(250);
        Native.MouseEvent(0x0004, 0, 0, 0, 0);
        Thread.Sleep(300);
    }

    private static string? WaitForClipboardText(int timeoutMs)
    {
        string? text = null;
        WaitFor(() => (text = ClipboardText()) is not null, timeoutMs, null);
        return text;
    }

    private static string? ClipboardText()
    {
        if (!Native.IsClipboardFormatAvailable(13 /* CF_UNICODETEXT */) || !Native.OpenClipboard(0))
        {
            return null;
        }

        try
        {
            nint handle = Native.GetClipboardData(13);
            nint data = handle == 0 ? 0 : Native.GlobalLock(handle);
            try
            {
                return data == 0 ? null : Marshal.PtrToStringUni(data);
            }
            finally
            {
                if (data != 0)
                {
                    Native.GlobalUnlock(handle);
                }
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    /// <summary>
    /// Selects/invokes elements by name with UI Automation (in a PowerShell child process: the harness has no UIA
    /// client of its own). Each step is "name|alternative name"; tabs are selected, other elements invoked.
    /// </summary>
    private static string InvokeByUiAutomation(int processId, string[] steps) => InvokeByUiAutomation(
        $"$A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, {processId})))", steps);

    /// <summary>The same in one window (a process with several top-level windows, such as the Quick View host).</summary>
    private static string InvokeByUiAutomation(nint window, string[] steps) => InvokeByUiAutomation($"$A::FromHandle([IntPtr]{window})", steps);

    private static string InvokeByUiAutomation(string rootExpression, string[] steps)
    {
        string names = string.Join(';', steps.Select(s => s.Replace("'", "''", StringComparison.Ordinal)));
        string script = $$"""
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
            $A = [System.Windows.Automation.AutomationElement]
            $win = {{rootExpression}}
            foreach ($step in '{{names}}'.Split(';')) {
              $found = $null
              foreach ($name in $step.Split('|')) { if (-not $found) { $found = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name))) } }
              if (-not $found) { Write-Output "missing:$step"; exit 1 }
              $pattern = $null
              if ($found.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
              elseif ($found.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
              elseif ($found.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle() }
              Start-Sleep -Milliseconds 600
            }
            Write-Output "uia ok"
            """;
        string file = Path.Combine(Path.GetTempPath(), $"mavue-uia-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            using Process ps = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"") { UseShellExecute = false, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8, CreateNoWindow = true })!;
            string output = ps.StandardOutput.ReadToEnd().Trim();
            ps.WaitForExit(20000);
            return output;
        }
        finally
        {
            File.Delete(file);
        }
    }
}

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint window, ref POINT point);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    public static partial nint GetClipboardData(uint format);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint memory);
}
