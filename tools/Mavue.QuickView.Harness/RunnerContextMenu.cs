using System.Diagnostics;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Context-menu path: <c>Mavue.QuickView.Host.exe --quickview</c> forwards to the resident process through the
/// named pipe, which then shows (and follows) the Explorer selection. The verb is registered under a test
/// name and removed afterwards, so the user's own registration is never touched.
/// </summary>
internal sealed partial class Runner
{
    private const string TestVerb = "Mavue.QuickView.E2E";

    private void RunContextMenuScenarios(List<TestAsset> assets)
    {
        if (options.AttachHostLog is not null)
        {
            return; // needs the host this run started (its timing log)
        }

        string folder = Path.GetDirectoryName(assets[0].Path)!;
        string[] ordered = Directory.GetFiles(folder).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        nint explorer = OpenExplorer(ordered[0]);
        dynamic? window = ShellWindowFor(explorer);
        if (window is null)
        {
            _scenarios.Add(new ScenarioResult("context-menu", false, "Explorer automation window", "not found"));
            return;
        }

        Guarded("cli-quickview", () => CliQuickView(explorer, window, ordered),
            "`--quickview <file>` with Explorer in front: forwarded to the resident process, which shows the file and follows Explorer's selection (↓)");
        Guarded("context-menu-verb", () => ContextMenuVerb(explorer, window, ordered),
            "The registered context-menu command invoked by Explorer on 1 and on 3 selected files: one preview of the selection (Explorer starts one process per file; the first shows the whole selection, the others are recognized as already shown)");
    }

    private (bool, string) CliQuickView(nint explorer, dynamic window, string[] ordered)
    {
        Select(window, ordered[0], SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!PrepareExplorer(explorer, ordered[0], out string why))
        {
            return (false, why);
        }

        long t0 = Stopwatch.GetTimestamp();
        using Process client = Process.Start(new ProcessStartInfo(options.HostPath) { ArgumentList = { "--quickview", ordered[0] }, UseShellExecute = false })!;
        bool exited = client.WaitForExit(10000);
        double clientMs = Ms(t0, Stopwatch.GetTimestamp());
        (Mark? external, Mark? done) = WaitForExternal(t0);
        if (external is null || done?.Name != "full-visible")
        {
            return (false, $"not shown (client exited={exited}, external={external is not null}, done={done?.Name})");
        }

        Mark? selection = _log.Find(external.Request, "selection");
        Mark? shown = _log.Find(external.Request, "shown");
        bool shows = ShowsFile(external.Request, ordered[0]);
        string first = $"client process {clientMs:0} ms (exit {client.ExitCode}), full-visible {Ms(t0, done.Qpc):0} ms, source={selection?.Text("source")}, shown mode={shown?.Text("mode")} foreground={shown?.Bool("isForeground")} fellBack={shown?.Bool("fellBackToNoActivate")}";

        // Following: ↓ goes to whichever window has the keyboard (Quick View if it was activated, else Explorer).
        nint foreground = Native.GetForegroundWindow();
        SendKeyIfForeground(Native.VK_DOWN, foreground, out long tDown);
        (Mark? next, Mark? nextDone) = WaitForNavigation(tDown, "explorer-selection");
        bool followed = next is not null && nextDone?.Name == "full-visible" && ShowsFile(next.Request, ordered[1]);

        bool closed = CloseQuickViewFromForeground();
        bool ok = exited && client.ExitCode == 0 && shows && selection?.Text("source") == "explorer-view" && followed && closed;
        return (ok, $"{first}; ↓ followed to {Path.GetFileName(ordered[1])}={followed}; closed={closed}");
    }

    private (bool, string) ContextMenuVerb(nint explorer, dynamic window, string[] ordered)
    {
        if (RunHost("--register", "--no-startup", "--verb", TestVerb) != 0)
        {
            return (false, "--register failed");
        }

        try
        {
            // One file first (twice: Explorer's first use of a newly registered verb costs more), to separate
            // Explorer's launch cost from handling several clients.
            var singles = new List<string>();
            bool singleOk = true;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                Select(window, ordered[0], SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
                if (!PrepareExplorer(explorer, ordered[0], out string whySingle))
                {
                    return (false, whySingle);
                }

                long tSingle = Stopwatch.GetTimestamp();
                window.Document.SelectedItems().InvokeVerbEx(TestVerb, string.Empty);
                (_, Mark? singleDone) = WaitForExternal(tSingle);
                Mark? singleReceived = _log.Marks.FirstOrDefault(m => m.Name == "external-received" && m.Qpc >= tSingle);
                bool shownOk = singleDone?.Name == "full-visible" && singleReceived is not null;
                singles.Add(shownOk ? $"received {Ms(tSingle, singleReceived!.Qpc):0} ms / full-visible {Ms(tSingle, singleDone!.Qpc):0} ms" : "not shown");
                singleOk &= shownOk && CloseQuickViewFromForeground();
                Thread.Sleep(500);
            }

            string single = "1 file: " + string.Join(", then ", singles);

            Select(window, ordered[0], SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
            Select(window, ordered[2], SvsiSelect);
            Select(window, ordered[3], SvsiSelect);
            if (!PrepareExplorer(explorer, ordered[0], out string why))
            {
                return (false, why);
            }

            long t0 = Stopwatch.GetTimestamp();
            window.Document.SelectedItems().InvokeVerbEx(TestVerb, string.Empty); // runs in Explorer, like a menu click
            (Mark? external, Mark? done) = WaitForExternal(t0);
            if (external is null || done?.Name != "full-visible")
            {
                return (false, $"not shown (external={external is not null}, done={done?.Name})");
            }

            Thread.Sleep(1500); // late clients of the same selection must not change anything
            _log.Poll();
            int clients = _log.Marks.Count(m => m.Name == "external-received" && m.Qpc >= t0);
            Mark? firstReceived = _log.Marks.FirstOrDefault(m => m.Name == "external-received" && m.Qpc >= t0);
            Mark? lastReceived = _log.Marks.LastOrDefault(m => m.Name == "external-received" && m.Qpc >= t0);
            int batches = _log.Marks.Count(m => m.Name == "external" && m.Qpc >= t0);
            int duplicates = _log.Marks.Count(m => m.Name == "external-already-shown" && m.Qpc >= t0);
            Mark? nav = _log.Find(external.Request, "nav");
            Mark? shown = _log.Find(external.Request, "shown");
            bool multi = nav?.Text("mode") == "MultipleItems" && nav.Number("count") == 3;
            bool closed = CloseQuickViewFromForeground();
            return (multi && closed && singleOk && Native.IsWindow(explorer),
                $"{single}; 3 files: requests received {Ms(t0, firstReceived?.Qpc ?? t0):0}–{Ms(t0, lastReceived?.Qpc ?? t0):0} ms after invoke; " +
                $"{clients} client requests → {batches - duplicates} shown, {duplicates} recognized as already shown; " +
                $"mode={nav?.Text("mode")} count={nav?.Number("count")}, full-visible {Ms(t0, done.Qpc):0} ms, shown mode={shown?.Text("mode")} foreground={shown?.Bool("isForeground")}; closed={closed}");
        }
        finally
        {
            RunHost("--unregister", "--verb", TestVerb);
        }
    }

    /// <summary>First "external" request after <paramref name="qpc"/> and its completion.</summary>
    private (Mark? External, Mark? Done) WaitForExternal(long qpc)
    {
        Mark? external = null;
        Mark? done = null;
        WaitFor(
            () =>
            {
                _log.Poll();
                external ??= _log.Marks.FirstOrDefault(m => m.Name == "external" && m.Qpc >= qpc);
                done = external is null ? null : _log.ForRequest(external.Request).FirstOrDefault(m => m.Name is "full-visible" or "full-skipped" or "error");
                return done is not null;
            },
            10000,
            null);
        return (external, done);
    }

    /// <summary>Esc to whichever window has the keyboard (Quick View when activated, otherwise Explorer).</summary>
    private bool CloseQuickViewFromForeground()
    {
        long before = Stopwatch.GetTimestamp();
        nint foreground = Native.GetForegroundWindow();
        if (!SendKeyIfForeground(Native.VK_ESCAPE, foreground, out _))
        {
            return false;
        }

        return WaitFor(() => { _log.Poll(); return _log.HiddenSince(before) is not null; }, 3000, null);
    }

    private int RunHost(params string[] args)
    {
        var info = new ProcessStartInfo(options.HostPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        log($"host {string.Join(' ', args)} → exit {process.ExitCode}: {output.Trim()}");
        return process.ExitCode;
    }
}
