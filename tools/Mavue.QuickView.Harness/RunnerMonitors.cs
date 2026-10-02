using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Window-size scenarios: Quick View opened on every monitor (each with its own scale), moved to another
/// monitor while shown, and resized while shown. In each case the image must be planned for the window's
/// real image area (the area the controller computed must match the XAML layout) and re-decoded for it.
/// </summary>
internal sealed partial class Runner
{
    private void RunMonitorScenarios(List<TestAsset> assets)
    {
        TestAsset? large = assets.FirstOrDefault(a => a.Case == "large-jpeg");
        if (large is null)
        {
            return;
        }

        TestAsset? pdf = assets.FirstOrDefault(a => a.Case == "pdf");

        List<Native.MonitorInfo> monitors = Native.Monitors();
        Environment["monitors"] = string.Join("; ", monitors.Select(m => $"{m.Width}x{m.Height}@{m.Dpi}dpi"));
        Guarded("monitor-open-each", () => OpenOnEachMonitor(large, pdf, monitors),
            "Opened from an Explorer window on each monitor: Quick View is placed on that monitor at its DPI and the image (and the PDF page) is planned for the real image area");
        Guarded("monitor-move-while-shown", () => MoveWhileShown(large, monitors),
            "Quick View moved to another monitor while shown: when the image area changes, the current image is re-planned for the new area and DPI");
        Guarded("resize-while-shown", () => ResizeWhileShown(large),
            "Quick View resized while shown (smaller, then larger): the current image is re-planned for each new image area");
    }

    /// <summary>Empty when the full-set mark is consistent with the window; otherwise what is wrong.</summary>
    private string PlanProblem(Mark? full)
    {
        if (full is null)
        {
            return "no full-set mark";
        }

        double aw = full.Number("areaWidth") ?? 0, ah = full.Number("areaHeight") ?? 0;
        double lw = full.Number("layoutAreaWidth") ?? 0, lh = full.Number("layoutAreaHeight") ?? 0;
        double dw = full.Number("decodedWidth") ?? 0, dh = full.Number("decodedHeight") ?? 0;
        double sw = full.Number("sourceWidth") ?? 0, sh = full.Number("sourceHeight") ?? 0;
        if (Math.Abs(aw - lw) > 2 || Math.Abs(ah - lh) > 2)
        {
            return $"planned area {aw}x{ah} != layout {lw}x{lh}";
        }

        if (full.Text("decoder") == "windows-data-pdf")
        {
            // sourceWidth/Height of a PDF page are device-independent pixels; actual size is 100 % at the window's DPI.
            double scale = (full.Number("dpi") ?? 96) / 96;
            if (options.ScaleMode == "actual")
            {
                return Math.Abs(dw - (sw * scale)) <= 3 && Math.Abs(dh - (sh * scale)) <= 3 ? string.Empty : $"PDF at 100 % expected about {sw * scale:0}x{sh * scale:0}, decoded {dw}x{dh}";
            }

            sw = sh = double.MaxValue; // a page is fitted to the area, never shown "at its own size"
        }

        if (options.ScaleMode == "actual")
        {
            return dw == sw && dh == sh ? string.Empty : $"actual size expected {sw}x{sh}, decoded {dw}x{dh}";
        }

        bool fits = dw <= aw && dh <= ah;
        bool fills = dw >= aw - 2 || dh >= ah - 2 || (dw == sw && dh == sh);
        return fits && fills ? string.Empty : $"decoded {dw}x{dh} does not fit area {aw}x{ah}";
    }

    private static string DescribePlan(Mark? full) =>
        full is null ? "?" : $"area {full.Number("areaWidth")}x{full.Number("areaHeight")} decoded {full.Number("decodedWidth")}x{full.Number("decodedHeight")} dpi {full.Number("dpi")}";

    private static bool Inside(Native.RECT window, Native.MonitorInfo monitor)
    {
        int x = (window.Left + window.Right) / 2, y = (window.Top + window.Bottom) / 2;
        return x >= monitor.Bounds.Left && x < monitor.Bounds.Right && y >= monitor.Bounds.Top && y < monitor.Bounds.Bottom;
    }

    private static void PutOn(nint window, Native.MonitorInfo monitor, double widthFraction, double heightFraction)
    {
        Native.RECT work = monitor.Work;
        int w = (int)((work.Right - work.Left) * widthFraction), h = (int)((work.Bottom - work.Top) * heightFraction);
        int x = work.Left + ((work.Right - work.Left - w) / 2), y = work.Top + ((work.Bottom - work.Top - h) / 2);
        Native.SetWindowPos(window, 0, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private (bool, string) OpenOnEachMonitor(TestAsset large, TestAsset? pdf, List<Native.MonitorInfo> monitors)
    {
        nint explorer = OpenExplorer(large.Path);
        if (explorer == 0)
        {
            return (false, "Explorer window not found");
        }

        Native.ShowWindow(explorer, Native.SW_RESTORE);
        var observed = new List<string>();
        bool ok = true;
        foreach (Native.MonitorInfo monitor in monitors)
        {
            PutOn(explorer, monitor, 0.5, 0.6);
            Thread.Sleep(500);
            if (!OpenQuickView(explorer, large.Path, out string why))
            {
                observed.Add($"{monitor.Dpi}dpi: {why}");
                ok = false;
                continue;
            }

            Mark? placed = _log.Marks.LastOrDefault(m => m.Name == "placed");
            Mark? full = _log.Marks.LastOrDefault(m => m.Name == "full-set");
            bool onMonitor = Native.GetWindowRect(HostWindow(), out Native.RECT r) && Inside(r, monitor);
            string problem = PlanProblem(full);
            bool good = onMonitor && placed?.Number("dpi") == monitor.Dpi && full?.Number("dpi") == monitor.Dpi && problem.Length == 0;
            ok &= good;
            observed.Add($"{monitor.Width}x{monitor.Height}@{monitor.Dpi}: placed {placed?.Number("width")}x{placed?.Number("height")} onMonitor={onMonitor} {DescribePlan(full)} {problem}".TrimEnd());
            ok &= CloseFromExplorer(explorer);
            Thread.Sleep(300);
            if (pdf is not null && OpenExplorer(pdf.Path) == explorer)
            {
                if (!OpenQuickView(explorer, pdf.Path, out why))
                {
                    observed.Add($"  pdf: {why}");
                    ok = false;
                    continue;
                }

                Mark? page = _log.Marks.LastOrDefault(m => m.Name == "full-set");
                string pageProblem = PlanProblem(page);
                ok &= page?.Number("dpi") == monitor.Dpi && pageProblem.Length == 0;
                observed.Add($"  pdf: {DescribePlan(page)} {pageProblem}".TrimEnd());
                ok &= CloseFromExplorer(explorer);
                Thread.Sleep(300);
                OpenExplorer(large.Path); // select the image again for the next monitor
            }
        }

        return (ok && monitors.Count > 1, string.Join(" | ", observed) + (monitors.Count > 1 ? string.Empty : " (only one monitor)"));
    }

    private (bool, string) MoveWhileShown(TestAsset large, List<Native.MonitorInfo> monitors)
    {
        if (monitors.Count < 2)
        {
            return (false, "only one monitor");
        }

        nint explorer = OpenExplorer(large.Path);
        if (explorer == 0)
        {
            return (false, "Explorer window not found");
        }

        Native.ShowWindow(explorer, Native.SW_RESTORE);
        PutOn(explorer, monitors[0], 0.5, 0.6);
        Thread.Sleep(500);
        if (!OpenQuickView(explorer, large.Path, out string why))
        {
            return (false, why);
        }

        var observed = new List<string> { $"start {DescribePlan(_log.Marks.LastOrDefault(m => m.Name == "full-set"))}" };
        bool ok = true;
        uint previousDpi = monitors[0].Dpi;
        foreach (Native.MonitorInfo monitor in monitors.Skip(1).Append(monitors[0]))
        {
            nint host = HostWindow();
            long before = Stopwatch.GetTimestamp();
            Native.GetWindowRect(host, out Native.RECT r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            Native.RECT work = monitor.Work;
            Native.SetWindowPos(host, 0, work.Left + Math.Max(0, (work.Right - work.Left - w) / 2), work.Top + Math.Max(0, (work.Bottom - work.Top - h) / 2), w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            (Mark? nav, Mark? done) = WaitForNavigation(before, "area-changed");
            bool onMonitor = Native.GetWindowRect(host, out Native.RECT moved) && Inside(moved, monitor);
            if (nav is null)
            {
                // Same DPI and the same physical size: the image area did not change, nothing to re-plan.
                bool expected = monitor.Dpi == previousDpi;
                ok &= expected && onMonitor && Native.IsWindowVisible(host);
                observed.Add($"→ {monitor.Width}x{monitor.Height}@{monitor.Dpi}: no re-plan (same dpi: {expected}) onMonitor={onMonitor}");
            }
            else
            {
                Mark? full = _log.Find(nav.Request, "full-set");
                string problem = PlanProblem(full);
                bool good = done?.Name == "full-visible" && onMonitor && full?.Number("dpi") == monitor.Dpi && problem.Length == 0;
                ok &= good;
                observed.Add($"→ {monitor.Width}x{monitor.Height}@{monitor.Dpi}: re-planned {DescribePlan(full)} onMonitor={onMonitor} {problem}".TrimEnd());
            }

            previousDpi = monitor.Dpi;
            Thread.Sleep(300);
        }

        ok &= CloseFromExplorer(explorer);
        return (ok, string.Join(" ", observed));
    }

    private (bool, string) ResizeWhileShown(TestAsset large)
    {
        nint explorer = OpenExplorer(large.Path);
        if (explorer == 0)
        {
            return (false, "Explorer window not found");
        }

        if (!OpenQuickView(explorer, large.Path, out string why))
        {
            return (false, why);
        }

        Mark? start = _log.Marks.LastOrDefault(m => m.Name == "full-set");
        nint host = HostWindow();
        Native.GetWindowRect(host, out Native.RECT r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var observed = new List<string> { $"start {DescribePlan(start)}" };
        bool ok = true;
        foreach ((double fw, double fh) in new[] { (0.6, 0.5), (1.0, 1.0) })
        {
            long before = Stopwatch.GetTimestamp();
            Native.SetWindowPos(host, 0, r.Left, r.Top, (int)(w * fw), (int)(h * fh), Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            (Mark? nav, Mark? done) = WaitForNavigation(before, "area-changed");
            Mark? full = nav is null ? null : _log.Find(nav.Request, "full-set");
            string problem = PlanProblem(full);
            ok &= done?.Name == "full-visible" && problem.Length == 0;
            observed.Add($"→ {(int)(w * fw)}x{(int)(h * fh)}: {(nav is null ? "no re-plan" : DescribePlan(full))} {problem}".TrimEnd());
            Thread.Sleep(300);
        }

        Mark? end = _log.Marks.LastOrDefault(m => m.Name == "full-set");
        ok &= end?.Number("decodedWidth") == start?.Number("decodedWidth") && end?.Number("decodedHeight") == start?.Number("decodedHeight");
        ok &= CloseFromExplorer(explorer);
        return (ok, string.Join(" ", observed));
    }
}

internal static partial class Native
{
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const int SW_RESTORE = 9;

    internal sealed record MonitorInfo(RECT Bounds, RECT Work, uint Dpi)
    {
        public int Width => Bounds.Right - Bounds.Left;

        public int Height => Bounds.Bottom - Bounds.Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    private static unsafe partial int EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, nint, nint, int> callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    private static List<nint>? s_monitors;

    [UnmanagedCallersOnly]
    private static int MonitorCallback(nint monitor, nint hdc, nint rect, nint data)
    {
        s_monitors!.Add(monitor);
        return 1;
    }

    /// <summary>All monitors in physical pixels with their effective DPI, primary first.</summary>
    public static unsafe List<MonitorInfo> Monitors()
    {
        s_monitors = [];
        EnumDisplayMonitors(0, 0, &MonitorCallback, 0);
        var result = new List<MonitorInfo>();
        foreach (nint monitor in s_monitors)
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info) && GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0)
            {
                result.Add(new MonitorInfo(info.rcMonitor, info.rcWork, dpi));
            }
        }

        s_monitors = null;
        return result.OrderByDescending(m => m.Bounds.Left == 0 && m.Bounds.Top == 0).ToList();
    }
}
