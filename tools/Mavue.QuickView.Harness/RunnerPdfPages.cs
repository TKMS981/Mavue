using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;

namespace Mavue.QuickView.Harness;

/// <summary>
/// PDF pages in Quick View: PageUp/PageDown (taken by the hook while Explorer has the keyboard) and the mouse
/// wheel turn pages without touching Explorer's selection; the arrow keys still move between files.
/// </summary>
internal sealed partial class Runner
{
    private const ushort VkPageUp = 0x21;
    private const ushort VkPageDown = 0x22;
    private const ushort VkUp = 0x26;

    private void RunPdfPageScenario(List<TestAsset> assets)
    {
        string folder = Path.GetDirectoryName(assets[0].Path)! + " PDF";
        Directory.CreateDirectory(folder);
        string pdf = Path.Combine(folder, "01 three pages.pdf");
        string image = Path.Combine(folder, "02 image.jpg");
        File.WriteAllBytes(pdf, TestAssets.MultiPagePdf(3));
        if (!File.Exists(image))
        {
            Task.Run(() => TestAssets.EncodeAsync(image, BitmapEncoder.JpegEncoderId, 640, 480, 0.9)).GetAwaiter().GetResult();
        }

        WriteHostSettings("SinglePage"); // page turns by the wheel are a single-page behavior (continuous: quickview-pdf)
        Guarded("pdf-pages", () => PdfPages(pdf, image),
            "3-page PDF: PageDown/PageUp (Explorer in front) and the mouse wheel turn pages, Explorer's selection is unchanged, nothing happens past the last page; ↓/↑ still move between files and a PDF opens at page 1 again");
        WriteHostSettings();
    }

    private (bool, string) PdfPages(string pdf, string image)
    {
        nint explorer = OpenExplorer(pdf);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Select(window, pdf, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, pdf, out string why))
        {
            return (false, why);
        }

        var observed = new List<string>();
        Mark? first = _log.Marks.LastOrDefault(m => m.Name == "full-set");
        bool ok = first?.Number("pageIndex") == 0 && first.Number("pageCount") == 3;
        observed.Add($"open: page {first?.Number("pageIndex") + 1}/{first?.Number("pageCount")}");

        // PageDown, PageDown, PageDown (past the end), PageUp — with Explorer keeping the keyboard.
        foreach ((ushort key, int expected) in new[] { (VkPageDown, 1), (VkPageDown, 2), (VkPageDown, -1), (VkPageUp, 1) })
        {
            ok &= PageStep(explorer, pdf, key, expected, "hook-page", observed);
        }

        // Mouse wheel (one notch down) over the Quick View window: page 2 → 3.
        nint host = HostWindow();
        if (options.ScaleMode == "actual")
        {
            observed.Add("wheel: skipped (actual size scrolls the page before turning it)");
        }
        else if (Native.GetWindowRect(host, out Native.RECT r))
        {
            long t = Stopwatch.GetTimestamp();
            Native.SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            Thread.Sleep(100);
            Native.MouseEvent(0x0800 /* MOUSEEVENTF_WHEEL */, 0, 0, unchecked((uint)-120), 0);
            (Mark? nav, Mark? done) = WaitForNavigation(t, "pdf-page");
            Mark? full = nav is null ? null : _log.Find(nav.Request, "full-set");
            bool wheelOk = done?.Name == "full-visible" && full?.Number("pageIndex") == 2 && ExplorerSelectionIs(explorer, pdf);
            ok &= wheelOk;
            observed.Add(wheelOk ? $"wheel: page 3 in {Ms(t, done!.Qpc):0} ms" : $"wheel: failed (nav={nav is not null}, page={full?.Number("pageIndex")})");
            ForceForeground(explorer);
            Thread.Sleep(200);
        }

        // ↓ moves Explorer's selection to the next file (the image); ↑ comes back and the PDF opens at page 1.
        long tDown = Stopwatch.GetTimestamp();
        SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
        (Mark? toImage, Mark? imageDone) = WaitForNavigation(tDown, "explorer-selection");
        bool movedToImage = imageDone?.Name == "full-visible" && toImage is not null && ShowsFile(toImage.Request, image) && ExplorerSelectionIs(explorer, image);
        observed.Add(movedToImage ? "↓ next file (image)" : "↓ failed");

        long tUp = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkUp, explorer, out _);
        (Mark? back, Mark? backDone) = WaitForNavigation(tUp, "explorer-selection");
        Mark? backFull = back is null ? null : _log.Find(back.Request, "full-set");
        bool backAtFirst = backDone?.Name == "full-visible" && backFull?.Number("pageIndex") == 0 && backFull.Number("pageCount") == 3;
        observed.Add(backAtFirst ? "↑ back to the PDF at page 1" : $"↑ failed (page={backFull?.Number("pageIndex")})");

        bool closed = CloseFromExplorer(explorer);
        ok &= movedToImage && backAtFirst && closed;
        return (ok, string.Join(", ", observed) + (closed ? string.Empty : "; Esc did not close"));
    }

    /// <summary>One page key; <paramref name="expected"/> is the zero-based page, or -1 for "no page change".</summary>
    private bool PageStep(nint explorer, string pdf, ushort key, int expected, string source, List<string> observed)
    {
        string name = key == VkPageDown ? "PageDown" : "PageUp";
        long t = Stopwatch.GetTimestamp();
        if (!SendKeyIfForeground(key, explorer, out _))
        {
            observed.Add($"{name}: foreground changed");
            return false;
        }

        if (expected < 0)
        {
            Thread.Sleep(700);
            _log.Poll();
            bool moved = _log.Marks.Any(m => m.Name == "nav" && m.Qpc >= t);
            bool edge = _log.Marks.Any(m => m.Name == "pdf-page-edge" && m.Qpc >= t);
            bool selectionKept = ExplorerSelectionIs(explorer, pdf);
            observed.Add($"{name} at the last page: {(moved ? "moved (unexpected)" : "no change")}, Explorer selection kept={selectionKept}");
            return !moved && edge && selectionKept;
        }

        (Mark? nav, Mark? done) = WaitForNavigation(t, "pdf-page");
        Mark? full = nav is null ? null : _log.Find(nav.Request, "full-set");
        Mark? page = nav is null ? null : _log.Find(nav.Request, "pdf-page");
        bool ok = done?.Name == "full-visible" && full?.Number("pageIndex") == expected && page?.Text("source") == source && ExplorerSelectionIs(explorer, pdf);
        observed.Add(ok ? $"{name}: page {expected + 1} in {Ms(t, done!.Qpc):0} ms" : $"{name}: failed (nav={nav is not null}, page={full?.Number("pageIndex")}, source={page?.Text("source")})");
        return ok;
    }
}

internal static partial class Native
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", EntryPoint = "mouse_event")]
    public static partial void MouseEvent(uint flags, int dx, int dy, uint data, nuint extraInfo);
}
