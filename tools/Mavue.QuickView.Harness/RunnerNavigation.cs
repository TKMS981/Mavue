using System.Diagnostics;

namespace Mavue.QuickView.Harness;

/// <summary>Quick View navigation (Explorer selection following) and coexistence scenarios.</summary>
internal sealed partial class Runner
{
    private const uint SvsiSelect = 0x1;
    private const uint SvsiDeselectOthers = 0x4;
    private const uint SvsiEnsureVisible = 0x8;
    private const uint SvsiFocused = 0x10;
    private const int FvmDetails = 4;

    /// <summary>Pause between navigation keys, roughly a person looking at each item (lets prefetch run).</summary>
    private const int HumanStepPauseMs = 300;

    /// <summary>(Re)starts QuickLook after the host so its hook is newer than Mavue's (the order that caused double previews).</summary>
    private void StartQuickLookAfterHost(int waitSeconds)
    {
        foreach (Process running in Process.GetProcessesByName("QuickLook"))
        {
            using (running)
            {
                running.Kill();
                running.WaitForExit(5000);
            }
        }

        Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\21090PaddyXu.QuickLook_egxr34yet59cg!Main") { UseShellExecute = false });
        WaitFor(() => Process.GetProcessesByName("QuickLook").Length > 0, 15000, null);
        Environment["quickLookStartedAfterHostWaitSeconds"] = waitSeconds;
        log($"QuickLook (re)started after the host; waiting {waitSeconds} s");
        Thread.Sleep(waitSeconds * 1000);
    }

    private void RunNavigationScenarios(List<TestAsset> assets)
    {
        string folder = Path.GetDirectoryName(assets[0].Path)!;

        // Details view sorted by name; the "01 ", "02 " … prefixes make the order unambiguous.
        string[] ordered = Directory.GetFiles(folder).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (ordered.Length < 4)
        {
            _scenarios.Add(new ScenarioResult("navigation", false, "needs at least 4 files", $"{ordered.Length} files"));
            return;
        }

        nint explorer = OpenExplorer(ordered[0]);
        dynamic? window = ShellWindowFor(explorer);
        if (window is null)
        {
            _scenarios.Add(new ScenarioResult("navigation", false, "Explorer automation window", "not found"));
            return;
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Thread.Sleep(500);

        Guarded("nav-single-follow", () => NavigateSingle(explorer, window, ordered),
            "Single selection: each ↓ in Explorer switches Quick View to the newly selected item (event-driven)");
        Guarded("nav-rapid", () => NavigateRapid(explorer, window, ordered),
            "Rapid ↓×4 then ↑×4 (40 ms apart, revisiting cached items): no errors, ends showing the first file");
        Guarded("nav-multi-step", () => NavigateMulti(explorer, window, ordered),
            "Multi-selection: →/← step inside the selection without changing it; ↓ collapses to one item and Quick View follows");
    }

    private (bool, string) NavigateSingle(nint explorer, dynamic window, string[] ordered)
    {
        Select(window, ordered[0], SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, ordered[0], out string why))
        {
            return (false, why);
        }

        var timings = new List<string>();
        for (int i = 1; i <= 3; i++)
        {
            if (!SendKeyIfForeground(Native.VK_DOWN, explorer, out long t))
            {
                return (false, $"step {i}: foreground changed ({Native.Describe(Native.GetForegroundWindow())})");
            }

            (Mark? nav, Mark? done) = WaitForNavigation(t, "explorer-selection");
            if (nav is null || done is null)
            {
                return (false, $"step {i}: no navigation after ↓ (nav={nav is not null}, done={done?.Name})");
            }

            if (done.Name != "full-visible" || !ShowsFile(nav.Request, ordered[i]) || !ExplorerSelectionIs(explorer, ordered[i]))
            {
                return (false, $"step {i}: expected {Path.GetFileName(ordered[i])}; {Describe(nav, explorer)}");
            }

            Thread.Sleep(HumanStepPauseMs);
            timings.Add($"↓{i}: show {Ms(t, _log.Find(nav.Request, "show-call")!.Qpc):0.0} ms / {done.Name} {Ms(t, done.Qpc):0.0} ms");
        }

        bool closed = CloseFromExplorer(explorer);
        return (closed, string.Join("; ", timings) + (closed ? "" : "; Esc did not close"));
    }

    private (bool, string) NavigateRapid(nint explorer, dynamic window, string[] ordered)
    {
        Select(window, ordered[0], SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, ordered[0], out string why))
        {
            return (false, why);
        }

        long start = Stopwatch.GetTimestamp();
        foreach (ushort key in new[] { Native.VK_DOWN, Native.VK_DOWN, Native.VK_DOWN, Native.VK_DOWN, (ushort)0x26, (ushort)0x26, (ushort)0x26, (ushort)0x26 })
        {
            if (!SendKeyIfForeground(key, explorer, out _))
            {
                return (false, "foreground changed during rapid navigation");
            }

            Thread.Sleep(40);
        }

        Thread.Sleep(1500);
        _log.Poll();
        var errors = _log.Marks.Where(m => m.Qpc >= start && m.Name is "error").ToList();
        Mark? last = _log.Marks.LastOrDefault(m => m.Qpc >= start && m.Name == "nav");
        bool lastVisible = last is not null && _log.Find(last.Request, "full-visible") is not null && ShowsFile(last.Request, ordered[0]);
        int navs = _log.Marks.Count(m => m.Qpc >= start && m.Name == "nav");
        int stale = _log.Marks.Count(m => m.Qpc >= start && m.Name == "stale-cleared");
        int fromCache = _log.Marks.Count(m => m.Qpc >= start && m.Name == "full-set" && m.Text("decoder") == "cache");
        bool closed = CloseFromExplorer(explorer);
        return (errors.Count == 0 && lastVisible && closed,
            $"{navs} navigations, {fromCache} from cache, {stale} stale images cleared, errors={errors.Count}, ends on {Path.GetFileName(ordered[0])}={lastVisible}");
    }

    private (bool, string) NavigateMulti(nint explorer, dynamic window, string[] ordered)
    {
        Select(window, ordered[0], SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        Select(window, ordered[2], SvsiSelect);
        Select(window, ordered[3], SvsiSelect);
        if (!OpenQuickView(explorer, ordered[0], out string why))
        {
            return (false, why);
        }

        Mark? start = _log.Marks.LastOrDefault(m => m.Name == "nav" && m.Text("source") == "space");
        if (start?.Text("mode") != "MultipleItems" || start.Number("count") != 3)
        {
            return (false, $"start: mode={start?.Text("mode")} count={start?.Number("count")}");
        }

        var observed = new List<string> { "start 1/3" };
        (ushort Key, int ExpectedIndex, string ExpectedFile)[] steps =
        [
            (Native.VK_RIGHT, 1, ordered[2]),
            (Native.VK_RIGHT, 2, ordered[3]),
        ];
        foreach ((ushort key, int index, string file) in steps)
        {
            SendKeyIfForeground(key, explorer, out long t);
            (Mark? nav, Mark? done) = WaitForNavigation(t, "hook-arrow");
            if (nav is null || done?.Name != "full-visible" || (int)(nav.Number("index") ?? -1) != index || !ShowsFile(nav.Request, file))
            {
                return (false, $"{string.Join(", ", observed)}; step to {index + 1}: {Describe(nav, explorer)}");
            }

            if (ExplorerSelectionCount(explorer) != 3)
            {
                return (false, $"Explorer selection changed to {ExplorerSelectionCount(explorer)} items after an intercepted arrow");
            }

            observed.Add($"→ {index + 1}/3 in {Ms(t, done?.Qpc ?? t):0.0} ms");
            Thread.Sleep(HumanStepPauseMs);
        }

        // At the last item → does nothing (no wrap-around).
        SendKeyIfForeground(Native.VK_RIGHT, explorer, out long tEnd);
        Thread.Sleep(600);
        _log.Poll();
        bool moved = _log.Marks.Any(m => m.Name == "nav" && m.Qpc >= tEnd);
        observed.Add(moved ? "→ at end moved (unexpected)" : "→ at end: no change");

        SendKeyIfForeground(Native.VK_LEFT, explorer, out long tLeft);
        (Mark? back, Mark? backDone) = WaitForNavigation(tLeft, "hook-arrow");
        bool backOk = back is not null && backDone?.Name == "full-visible" && (int)(back.Number("index") ?? -1) == 1;
        observed.Add(backOk ? "← 2/3" : $"← failed ({Describe(back, explorer)})");

        // ↓ goes to Explorer: the selection collapses to the item after the focused one (01 → 02).
        SendKeyIfForeground(Native.VK_DOWN, explorer, out long tDown);
        (Mark? single, Mark? singleDone) = WaitForNavigation(tDown, "explorer-selection");
        bool collapsed = single is not null && singleDone?.Name == "full-visible" && single.Text("mode") == "SingleItem" && ShowsFile(single.Request, ordered[1]) && ExplorerSelectionIs(explorer, ordered[1]);
        observed.Add(collapsed ? $"↓ single {Path.GetFileName(ordered[1])} in {Ms(tDown, singleDone?.Qpc ?? tDown):0.0} ms" : $"↓ failed ({Describe(single, explorer)})");

        bool closed = CloseFromExplorer(explorer);
        return (!moved && backOk && collapsed && closed, string.Join(", ", observed) + (closed ? "" : "; Esc did not close"));
    }

    private void Guarded(string name, Func<(bool Ok, string Observed)> body, string expected)
    {
        try
        {
            (bool ok, string observed) = body();
            _scenarios.Add(new ScenarioResult(name, ok, expected, observed));
            log($"scenario {name}: {(ok ? "PASS" : "FAIL")} {observed}");
        }
        catch (Exception ex)
        {
            _scenarios.Add(new ScenarioResult(name, false, expected, $"harness error {ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            EnsureHiddenNow();
        }
    }

    private bool OpenQuickView(nint explorer, string expectedFirst, out string why)
    {
        if (!PrepareExplorer(explorer, expectedFirst, out why))
        {
            return false;
        }

        Thread.Sleep(options.SettleMilliseconds);
        if (!SendKeyIfForeground(Native.VK_SPACE, explorer, out long t0))
        {
            why = "foreground changed before Space";
            return false;
        }

        long? request = WaitForRequest(t0);
        if (request is null || !WaitFor(() => { _log.Poll(); return _log.Find(request.Value, "full-visible") is not null; }, 10000, null))
        {
            why = "Quick View did not show the first item";
            return false;
        }

        Thread.Sleep(200);
        return true;
    }

    /// <summary>Waits for a "nav" mark from <paramref name="source"/> after <paramref name="qpc"/> and its completion mark.</summary>
    private (Mark? Nav, Mark? Done) WaitForNavigation(long qpc, string source)
    {
        Mark? nav = null;
        Mark? done = null;
        WaitFor(
            () =>
            {
                _log.Poll();
                nav ??= _log.Marks.FirstOrDefault(m => m.Name == "nav" && m.Qpc >= qpc && m.Text("source") == source);
                done = nav is null ? null : _log.ForRequest(nav.Request).FirstOrDefault(m => m.Name is "full-visible" or "full-skipped" or "error");
                return done is not null;
            },
            8000,
            null);
        return (nav, done);
    }

    private bool ShowsFile(long request, string path) =>
        _log.Find(request, "info") is { } info && (long)(info.Number("bytes") ?? -1) == new FileInfo(path).Length;

    private bool ExplorerSelectionIs(nint explorer, string path) =>
        _shell.EnumerateWindows().FirstOrDefault(s => s.TopLevelWindow == explorer) is { } s &&
        s.Paths.Count == 1 && string.Equals(s.Paths[0], path, StringComparison.OrdinalIgnoreCase);

    private int ExplorerSelectionCount(nint explorer) =>
        _shell.EnumerateWindows().FirstOrDefault(s => s.TopLevelWindow == explorer)?.Paths.Count ?? -1;

    private string Describe(Mark? nav, nint explorer)
    {
        var selection = _shell.EnumerateWindows().FirstOrDefault(s => s.TopLevelWindow == explorer);
        string shown = nav is null ? "none" : $"request {nav.Request} mode={nav.Text("mode")} index={nav.Number("index")} bytes={_log.Find(nav.Request, "info")?.Number("bytes")}";
        return $"nav: {shown}; Explorer selection: {string.Join(" | ", selection?.Paths.Select(Path.GetFileName) ?? [])}";
    }

    private bool CloseFromExplorer(nint explorer)
    {
        long before = Stopwatch.GetTimestamp();
        if (!SendKeyIfForeground(Native.VK_ESCAPE, explorer, out _))
        {
            return false;
        }

        return WaitFor(() => { _log.Poll(); return _log.HiddenSince(before) is not null; }, 3000, null);
    }

    private void EnsureHiddenNow()
    {
        nint host = HostWindow();
        if (host != 0 && Native.IsWindowVisible(host) && _log.Marks.LastOrDefault(m => m.Name is "hook" or "nav") is { } last)
        {
            nint explorer = _createdExplorers.LastOrDefault();
            if (explorer != 0)
            {
                ForceForeground(explorer);
                CloseFromExplorer(explorer);
            }

            _ = last;
        }
    }

    private static dynamic? ShellWindowFor(nint hwnd)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        foreach (dynamic w in shell.Windows())
        {
            if ((nint)(long)w.HWND == hwnd)
            {
                return w;
            }
        }

        return null;
    }

    private static void Select(dynamic window, string path, uint flags) =>
        window.Document.SelectItem(window.Document.Folder.ParseName(Path.GetFileName(path)), (int)flags);
}
