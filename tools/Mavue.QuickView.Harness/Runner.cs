using System.Diagnostics;
using System.Text.Json;
using Mavue.QuickView.Shell;

namespace Mavue.QuickView.Harness;

internal sealed record HarnessOptions
{
    public string Activation { get; init; } = "panel";
    public string Decoder { get; init; } = "winrt";
    public string Interpolation { get; init; } = "fant";
    public int Iterations { get; init; } = 3;
    public HashSet<string>? Cases { get; init; }
    public bool IncludeHuge { get; init; }
    public bool Prewarm { get; init; } = true;
    public bool Scenarios { get; init; } = true;

    /// <summary>Run the Explorer-selection navigation scenarios (single and multi-selection).</summary>
    public bool Navigation { get; init; } = true;

    /// <summary>If set, (re)start QuickLook after the host and wait this many seconds before measuring.</summary>
    public int? QuickLookAfterHostSeconds { get; init; }
    public int SettleMilliseconds { get; init; } = 500;

    /// <summary>Pause after each open/close sample, e.g. to let the host record its idle memory (1 s after hiding).</summary>
    public int PauseAfterSampleMilliseconds { get; init; }

    /// <summary>Run only the navigation scenarios whose names start with one of these (null = all).</summary>
    public IReadOnlyList<string>? OnlyScenarios { get; init; }

    /// <summary>
    /// Before every Space, activate an unrelated window and then Explorer, so Quick View never benefits
    /// from having been the foreground process just before (the realistic "first Space" condition).
    /// </summary>
    public bool Cold { get; init; } = true;
    public required string HostPath { get; init; }
    public string? AppPath { get; init; }
    public required string WorkDirectory { get; init; }

    /// <summary>
    /// Timing log of a host started independently (e.g. via WMI) instead of by the harness. Removes any
    /// foreground-rights effect of the host being the harness's child process.
    /// </summary>
    public string? AttachHostLog { get; init; }

    /// <summary>
    /// Start the host as a child of the harness (legacy). Measured to be unrepresentative: a host started by
    /// the harness could raise/activate its window where an independently started host could not.
    /// Default is a detached start through WMI (Win32_Process.Create), so the host's parent is unrelated.
    /// </summary>
    public bool ChildHost { get; init; }

    /// <summary>Extra command-line arguments passed to the host (e.g. "--no-idle-trim").</summary>
    public string HostExtraArgs { get; init; } = string.Empty;

    /// <summary>Image scale mode written to the host's private settings file: "fit" (default) or "actual".</summary>
    public string ScaleMode { get; init; } = "fit";
}

/// <summary>One Space → preview → Esc measurement. Times are milliseconds after the injected Space.</summary>
internal sealed record Sample
{
    public required string Case { get; init; }
    public int Iteration { get; init; }
    public bool Ok { get; set; }
    public string Note { get; set; } = string.Empty;
    public double? HookMs { get; set; }
    public double? SelectionMs { get; set; }
    public double? ShownMs { get; set; }
    public double? FirstFrameMs { get; set; }
    public double? ThumbnailVisibleMs { get; set; }
    public bool? ThumbnailCached { get; set; }
    public double? FullVisibleMs { get; set; }
    public double? ScreenPixelMs { get; set; }
    public string? FullOutcome { get; set; }
    public bool? ForegroundAtShown { get; set; }
    public bool? ForegroundAfter150Ms { get; set; }
    public bool? SetForegroundResult { get; set; }
    public double? DecodeMs { get; set; }
    public string? Decoded { get; set; }
    public string? Source { get; set; }
    public double? HostPeakWorkingSetMb { get; set; }
    public double? HostWorkingSetMb { get; set; }
    public bool? EscapeClosedAndReturnedToExplorer { get; set; }
    public bool? ExplorerKeptFocusWhileShown { get; set; }
    public bool? HookProbe { get; set; }
    public bool? FellBackToNoActivate { get; set; }
    public bool? AboveOwnerAtShow { get; set; }
    public bool RealUserInputDuringSample { get; set; }
    public string? HiddenReason { get; set; }
    public bool QuickLookWindowAppeared { get; set; }
}

internal sealed record ScenarioResult(string Name, bool Passed, string Expected, string Observed);

/// <summary>Drives Explorer + Mavue.QuickView.Host on the real desktop and records what actually happened.</summary>
internal sealed partial class Runner(HarnessOptions options, Action<string> log) : IDisposable
{
    private const string ExplorerClass = "CabinetWClass";
    private const string ItemViewClass = "DirectUIHWND";
    internal static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    private readonly ExplorerSelectionProvider _shell = new();
    private readonly List<nint> _createdExplorers = [];
    private readonly List<Sample> _samples = [];
    private readonly List<ScenarioResult> _scenarios = [];
    private TimingLog _log = null!;
    private Process? _host;
    private nint _hostWindow;
    private Process? _neutralApp;
    private nint _neutralWindow;
    private readonly UserInputMonitor _userInput = new();

    /// <summary>True when the current request's window was shown without activation (mode or auto fallback).</summary>
    private bool ShownWithoutActivation(long request) =>
        NoActivate || _log.Find(request, "shown")?.Bool("fellBackToNoActivate") == true;

    private bool NoActivate => options.Activation.StartsWith("noactivate", StringComparison.Ordinal) || options.Activation == "panel"
        || (Environment.TryGetValue("activation", out object? a) && a is string s && (s.StartsWith("NoActivate", StringComparison.Ordinal) || s == "Panel"));

    public IReadOnlyList<Sample> Samples => _samples;

    public IReadOnlyList<ScenarioResult> Scenarios => _scenarios;

    public Dictionary<string, object?> Environment { get; } = [];

    /// <summary>Runs all cases. Must be called on an STA thread (Shell COM requirement).</summary>
    public void Run(List<TestAsset> assets)
    {
        Native.SetProcessDpiAwarenessContext(-4); // per-monitor v2: window rects and screen pixels in physical pixels
        Native.PeekMessageW(out _, 0, 0, 0, 0);
        nint originalForeground = Native.GetForegroundWindow();
        Environment["os"] = System.Environment.OSVersion.VersionString;
        Environment["quickLookRunning"] = Process.GetProcessesByName("QuickLook").Length > 0;
        Environment["activation"] = options.Activation;
        Environment["decoder"] = options.Decoder;
        Environment["interpolation"] = options.Interpolation;
        Environment["prewarm"] = options.Prewarm;

        if (options.Cases is { } cases)
        {
            assets = assets.Where(a => cases.Contains(a.Case)).ToList();
        }

        try
        {
            // Host first, then other applications: like a resident process started at logon followed by
            // ordinary use of other apps. The reverse order measurably flatters foreground behavior.
            StartHost();
            StartNeutralApp();
            if (options.QuickLookAfterHostSeconds is { } wait)
            {
                StartQuickLookAfterHost(wait);
            }

            nint firstExplorer = 0;
            foreach (TestAsset asset in assets)
            {
                nint explorer = OpenExplorer(asset.Path);
                if (explorer == 0)
                {
                    _samples.Add(new Sample { Case = asset.Case, Note = "Explorer window with the file selected not found" });
                    continue;
                }

                firstExplorer = firstExplorer == 0 ? explorer : firstExplorer;
                for (int i = 1; i <= options.Iterations; i++)
                {
                    Sample sample = MeasureOpenAndEscape(asset, explorer, i);
                    _samples.Add(sample);
                    log($"{asset.Case} #{i}: {(sample.Ok ? "OK" : "FAIL")} full={sample.FullVisibleMs:0.0}ms pixel={sample.ScreenPixelMs:0.0}ms fg={sample.ForegroundAtShown} {sample.Note}");
                    Thread.Sleep(options.PauseAfterSampleMilliseconds);
                }
            }

            if (options.Navigation && assets.Count > 0)
            {
                RunNavigationScenarios(assets);
                RunTabScenario(assets);
                RunDisplayScenarios(assets);
                RunContextMenuScenarios(assets);
                RunPdfPageScenario(assets);
                RunGifScenario(assets);
                RunMediaScenario(assets);
            }

            TestAsset? small = assets.FirstOrDefault(a => a.Case == "small-jpeg");
            if (options.Scenarios && small is not null)
            {
                nint explorer = OpenExplorer(small.Path);
                if (explorer != 0)
                {
                    RunScenarios(small, explorer);
                }
            }
        }
        finally
        {
            if (_host is { HasExited: false })
            {
                // Resident cost while Quick View is hidden (after a short settle, caches cleared on hide).
                Thread.Sleep(1500);
                _host.Refresh();
                Environment["hostIdleWorkingSetMb"] = Math.Round(_host.WorkingSet64 / (1024.0 * 1024), 1);
                Environment["hostIdlePrivateMb"] = Math.Round(_host.PrivateMemorySize64 / (1024.0 * 1024), 1);
            }

            ShutdownHost();
            if (_neutralApp is { HasExited: false })
            {
                _neutralApp.Kill();
            }

            foreach (nint window in _createdExplorers.Where(Native.IsWindow))
            {
                Native.PostMessageW(window, Native.WM_CLOSE, 0, 0);
            }

            if (originalForeground != 0 && Native.IsWindow(originalForeground))
            {
                ForceForeground(originalForeground);
            }

            _shell.Dispose(); // release Shell COM proxies on the STA thread that created them
        }
    }

    public void Dispose() => _userInput.Dispose();

    public int RealUserInputEvents => _userInput.RealInputCount;

    private void StartHost()
    {
        if (options.AttachHostLog is { } attachLog)
        {
            _log = new TimingLog(attachLog);
            _host = Process.GetProcessesByName("Mavue.QuickView.Host").FirstOrDefault()
                ?? throw new InvalidOperationException("No running Mavue.QuickView.Host to attach to");
            _log.Poll();
            Mark? launchedMark = _log.FindGlobal("launched");
            Environment["attachedHost"] = true;
            Environment["activation"] = launchedMark?.Text("activation");
            Environment["timingLog"] = attachLog;
            log($"attached to host pid {_host.Id} ({launchedMark?.Text("activation")})");
            return;
        }

        string logPath = Path.Combine(options.WorkDirectory, $"timing-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        _log = new TimingLog(logPath);
        // The host gets its own settings file so the user's Quick View preferences are never read or changed.
        string settingsPath = Path.Combine(options.WorkDirectory, "settings-e2e.json");
        File.WriteAllText(settingsPath, $"{{\"version\": 1, \"imageScale\": \"{(options.ScaleMode == "actual" ? "ActualSize" : "FitNoUpscale")}\"}}");
        Environment["scaleMode"] = options.ScaleMode;
        string args = $"--activation {options.Activation} --decoder {options.Decoder} --interpolation {options.Interpolation} --timing-log \"{logPath}\" --settings \"{settingsPath}\"" + (options.Prewarm ? string.Empty : " --no-prewarm") + " --trace-shell " + options.HostExtraArgs;
        long start = Stopwatch.GetTimestamp();
        if (options.ChildHost)
        {
            _host = Process.Start(new ProcessStartInfo(options.HostPath, args) { UseShellExecute = false })
                ?? throw new InvalidOperationException("Failed to start Mavue.QuickView.Host");
        }
        else
        {
            _host = StartDetached(args);
        }

        Environment["hostStart"] = options.ChildHost ? "child-of-harness" : "detached-wmi";
        WaitFor(() => { _log.Poll(); return _log.FindGlobal("ready") is not null; }, 15000, "host ready");
        Mark ready = _log.FindGlobal("ready")!;
        Mark launched = _log.FindGlobal("launched")!;
        Environment["hostProcessStartToReadyMs"] = Ms(start, ready.Qpc);
        Environment["hostLaunchedToReadyMs"] = Ms(launched.Qpc, ready.Qpc);
        Environment["hookInstalled"] = ready.Bool("hookInstalled");
        Environment["timingLog"] = logPath;
        log($"host ready: process start → ready {Environment["hostProcessStartToReadyMs"]:0.0} ms");
    }

    /// <summary>An unrelated application window used to reset the foreground history between samples.</summary>
    private void StartNeutralApp()
    {
        Environment["cold"] = options.Cold;
        if (!options.Cold || options.AppPath is null || !File.Exists(options.AppPath))
        {
            return;
        }

        _neutralApp = Process.Start(new ProcessStartInfo(options.AppPath) { UseShellExecute = false });
        WaitFor(() => (_neutralWindow = Native.TopLevelWindows().FirstOrDefault(w => Native.IsWindowVisible(w) && Pid(w) == _neutralApp!.Id)) != 0, 15000, null);
        Environment["neutralWindow"] = _neutralWindow != 0;
    }

    /// <summary>Starts the host through WMI so it is not a descendant of the harness.</summary>
    private Process StartDetached(string args)
    {
        string dotnetRoot = System.Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
        string script = Path.Combine(options.WorkDirectory, "start-host.cmd");
        File.WriteAllText(script, $"@set DOTNET_ROOT={dotnetRoot}\r\n@start \"\" \"{options.HostPath}\" {args}\r\n", System.Text.Encoding.ASCII);
        var before = Process.GetProcessesByName("Mavue.QuickView.Host").Select(p => p.Id).ToHashSet();
        string command = $"Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{{ CommandLine = 'cmd.exe /c \"{script}\"' }} | Out-Null";
        using (Process ps = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            ps.WaitForExit(30000);
        }

        Process? host = null;
        WaitFor(() => (host = Process.GetProcessesByName("Mavue.QuickView.Host").FirstOrDefault(p => !before.Contains(p.Id))) is not null, 15000, "detached host process");
        return host!;
    }

    private void ShutdownHost()
    {
        if (options.AttachHostLog is not null || _host is null || _host.HasExited)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(options.HostPath, "--shutdown") { UseShellExecute = false })?.WaitForExit(5000);
        if (!_host.WaitForExit(5000))
        {
            _host.Kill();
        }
    }

    private Sample MeasureOpenAndEscape(TestAsset asset, nint explorer, int iteration)
    {
        var sample = new Sample { Case = asset.Case, Iteration = iteration };
        if (!PrepareExplorer(explorer, asset.Path, out string why))
        {
            sample.Note = why;
            return sample;
        }

        Thread.Sleep(options.SettleMilliseconds);
        bool quickLookBefore = QuickLookWindowVisible();
        long sampleStart = Stopwatch.GetTimestamp() - (long)(1000 * TicksPerMs); // include the second before Space

        if (!SendKeyIfForeground(Native.VK_SPACE, explorer, out long t0))
        {
            sample.Note = $"foreground changed before Space; not sent (now {Native.Describe(Native.GetForegroundWindow())})";
            return sample;
        }

        long? request = WaitForRequest(t0);
        if (request is null)
        {
            sample.Note = "hook did not report the Space press";
            return sample;
        }

        string[] terminal = ["full-visible", "full-skipped", "error", "no-selection"];
        long deadline = Stopwatch.GetTimestamp() + (long)(30000 * TicksPerMs);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            _log.Poll();
            if (sample.ScreenPixelMs is null && TryImagePixel(out _))
            {
                sample.ScreenPixelMs = Ms(t0, Stopwatch.GetTimestamp());
            }

            bool done = terminal.Any(t => _log.Find(request.Value, t) is not null);
            Mark? fullVisible = _log.Find(request.Value, "full-visible");
            bool pixelGaveUp = fullVisible is not null && Ms(fullVisible.Qpc, Stopwatch.GetTimestamp()) > 2000;
            bool pixelDone = sample.ScreenPixelMs is not null || fullVisible is null || pixelGaveUp;
            if (sample.ExplorerKeptFocusWhileShown is null && _log.Find(request.Value, "shown") is not null)
            {
                nint fgNow = Native.GetForegroundWindow();
                sample.ExplorerKeptFocusWhileShown = fgNow == explorer && Native.Focus(explorer).FocusClass == ItemViewClass;
            }
            if (_log.Find(request.Value, "no-selection") is not null || (done && pixelDone && _log.Find(request.Value, "foreground-check") is not null))
            {
                break;
            }

            Thread.Sleep(1);
        }

        sample.QuickLookWindowAppeared = !quickLookBefore && QuickLookWindowVisible();
        FillFromMarks(sample, request.Value, t0);
        if (_host is not null)
        {
            _host.Refresh();
            sample.HostPeakWorkingSetMb = _host.PeakWorkingSet64 / (1024.0 * 1024);
            sample.HostWorkingSetMb = _host.WorkingSet64 / (1024.0 * 1024);
        }

        sample.EscapeClosedAndReturnedToExplorer = CloseWithEscape(request.Value, explorer, out string closeNote);
        sample.HiddenReason = _log.Find(request.Value, "hidden")?.Text("reason");
        sample.HookProbe = _log.Find(request.Value, "hook-probe")?.Bool("allowSetForegroundInHook");
        sample.FellBackToNoActivate = _log.Find(request.Value, "shown")?.Bool("fellBackToNoActivate");
        sample.AboveOwnerAtShow = _log.Find(request.Value, "shown")?.Bool("aboveOwner");
        sample.RealUserInputDuringSample = _userInput.RealInputSince(sampleStart);
        if (sample.ScreenPixelMs is null && sample.FullOutcome == "full-visible")
        {
            sample.Note = "image decoded but not visible on screen (window behind other windows?)";
        }

        sample.Ok = sample.FullOutcome == "full-visible" && sample.ScreenPixelMs is not null && sample.EscapeClosedAndReturnedToExplorer == true;
        sample.Note = string.Join("; ", new[] { sample.Note, closeNote }.Where(s => !string.IsNullOrEmpty(s)));
        return sample;
    }

    private void FillFromMarks(Sample sample, long request, long t0)
    {
        double? At(string name) => _log.Find(request, name) is { } m ? Ms(t0, m.Qpc) : null;
        sample.HookMs = At("hook");
        sample.SelectionMs = At("selection");
        sample.ShownMs = At("shown");
        sample.FirstFrameMs = At("first-frame");
        sample.ThumbnailVisibleMs = At("thumbnail-visible");
        sample.ThumbnailCached = _log.Find(request, "thumbnail")?.Bool("cached");
        sample.FullVisibleMs = At("full-visible");
        sample.FullOutcome = new[] { "full-visible", "full-skipped", "error", "no-selection" }.FirstOrDefault(t => _log.Find(request, t) is not null) ?? "timeout";
        Mark? shown = _log.Find(request, "shown");
        sample.ForegroundAtShown = shown?.Bool("isForeground");
        sample.SetForegroundResult = shown?.Bool("setForegroundResult");
        sample.ForegroundAfter150Ms = _log.Find(request, "foreground-check")?.Bool("isForeground");
        if (_log.Find(request, "full-set") is { } full)
        {
            sample.DecodeMs = full.Number("decodeMs");
            sample.Decoded = $"{full.Number("decodedWidth")}x{full.Number("decodedHeight")}";
            sample.Source = $"{full.Number("sourceWidth")}x{full.Number("sourceHeight")}";
        }

        if (_log.Find(request, "error") is { } error)
        {
            sample.Note = $"error {error.Text("type")} 0x{(int)(error.Number("hresult") ?? 0):X8}";
        }
        else if (_log.Find(request, "full-skipped") is { } skipped)
        {
            sample.Note = $"full skipped: {skipped.Text("reason")}";
        }
    }

    private bool CloseWithEscape(long request, nint explorer, out string note)
    {
        note = string.Empty;
        nint foreground = Native.GetForegroundWindow();
        bool sent = foreground == HostWindow()
            ? SendKeyIfForeground(Native.VK_ESCAPE, foreground, out _)
            : ShownWithoutActivation(request) && foreground == explorer && SendKeyIfForeground(Native.VK_ESCAPE, explorer, out _);
        if (!sent)
        {
            note = $"Esc not sent (foreground = {Native.Describe(foreground)})";
            EnsureHidden(request, explorer);
            return false;
        }

        WaitFor(() => { _log.Poll(); return _log.Find(request, "hidden") is not null; }, 3000, null);
        Thread.Sleep(250);
        nint after = Native.GetForegroundWindow();
        bool ok = _log.Find(request, "hidden") is not null && after == explorer && Native.Focus(explorer).FocusClass == ItemViewClass && !Native.IsWindowVisible(HostWindow());
        if (!ok)
        {
            note = $"after Esc foreground = {Native.Describe(after)}, focus = {Native.Focus(after).FocusClass}";
        }

        return ok;
    }

    private void EnsureHidden(long request, nint explorer)
    {
        if (!Native.IsWindowVisible(HostWindow()))
        {
            return;
        }

        // Recovery path for the harness itself: Space in the owning Explorer toggles Quick View off.
        if (ForceForeground(explorer))
        {
            SendKeyIfForeground(Native.VK_SPACE, explorer, out _);
            WaitFor(() => { _log.Poll(); return _log.Find(request, "hidden") is not null; }, 3000, null);
        }
    }

    private void RunScenarios(TestAsset asset, nint explorer)
    {
        // 1. Space opens, Space (in Quick View) closes, focus returns to Explorer.
        Scenario("space-then-space", asset, explorer, (request, t0) =>
        {
            nint fg = Native.GetForegroundWindow();
            nint expected = ShownWithoutActivation(request) ? explorer : HostWindow();
            if (fg != expected)
            {
                return (false, $"unexpected foreground after Space ({Native.Describe(fg)})");
            }

            SendKeyIfForeground(Native.VK_SPACE, fg, out long spaceAt);

            // Passive display: the closing Space goes to Explorer and is logged as a new request.
            WaitFor(() => { _log.Poll(); return _log.HiddenSince(spaceAt) is not null; }, 3000, null);
            Thread.Sleep(250);
            nint after = Native.GetForegroundWindow();
            Mark? hidden = _log.HiddenSince(spaceAt);
            bool ok = hidden is not null && after == explorer && Native.Focus(after).FocusClass == ItemViewClass;
            return (ok, $"hidden={hidden?.Text("reason")}, foreground={Native.Describe(after)}, focus={Native.Focus(after).FocusClass}");
        }, "Space toggles Quick View off; Explorer item view regains focus");

        // 2. User clicks back into Explorer (simulated activation) → Quick View stays; Space there closes it.
        Scenario("activate-explorer-then-space", asset, explorer, (request, t0) =>
        {
            ForceForeground(explorer);
            Thread.Sleep(400);
            bool stillVisible = Native.IsWindowVisible(HostWindow());
            SendKeyIfForeground(Native.VK_SPACE, explorer, out long spaceAt);

            // The Space in Explorer is a new request; look for the hide that follows it.
            WaitFor(() => { _log.Poll(); return _log.HiddenSince(spaceAt) is not null; }, 3000, null);
            string? reason = _log.HiddenSince(spaceAt)?.Text("reason");
            return (stillVisible && reason == "space-toggle", $"visibleAfterExplorerActivated={stillVisible}, hiddenReason={reason}");
        }, "Quick View stays open while its Explorer window is active; Space in Explorer closes it");

        // 3. Switching to an unrelated application hides Quick View.
        if (options.AppPath is { } appPath && File.Exists(appPath))
        {
            Scenario("switch-to-other-app", asset, explorer, (request, t0) =>
            {
                using Process app = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false })!;
                nint appWindow = 0;
                WaitFor(() => (appWindow = Native.TopLevelWindows().FirstOrDefault(w => Native.IsWindowVisible(w) && Pid(w) == app.Id)) != 0, 10000, null);
                ForceForeground(appWindow);
                WaitFor(() => { _log.Poll(); return _log.Find(request, "hidden") is not null; }, 3000, null);
                string? reason = _log.Find(request, "hidden")?.Text("reason");
                app.Kill();
                return (reason == "foreground-other-app" && !Native.IsWindowVisible(HostWindow()), $"hiddenReason={reason}");
            }, "Activating another application hides Quick View without stealing focus back");
        }

        // 4. Alt+Tab from Quick View.
        Scenario("alt-tab", asset, explorer, (request, t0) =>
        {
            nint fg = Native.GetForegroundWindow();
            bool passive = ShownWithoutActivation(request);
            nint expected = passive ? explorer : HostWindow();
            if (fg != expected)
            {
                return (false, $"unexpected foreground before Alt+Tab ({Native.Describe(fg)})");
            }

            SendAltTab(fg);
            Thread.Sleep(1200);
            _log.Poll();
            nint after = Native.GetForegroundWindow();
            bool visible = Native.IsWindowVisible(HostWindow());
            string transient = string.Join(",", _log.ForRequest(request).Where(m => m.Name == "foreground-transient").Select(m => m.Text("class")).Distinct());
            // Activated Quick View: Alt+Tab should land on the Explorer window it came from (Quick View may stay).
            // Non-activated Quick View: Alt+Tab leaves Explorer for another app, so Quick View should hide.
            bool ok = passive ? after != explorer && !visible : after == explorer;
            return (ok, $"foreground={Native.Describe(after)}, quickViewVisible={visible}, hiddenReason={_log.Find(request, "hidden")?.Text("reason")}, transientClasses=[{transient}]");
        }, NoActivate
            ? "Alt+Tab from Explorer (Quick View shown passively) switches to another app and Quick View hides"
            : "Alt+Tab from an activated Quick View switches to the originating Explorer window");
    }

    private void Scenario(string name, TestAsset asset, nint explorer, Func<long, long, (bool Ok, string Observed)> body, string expected)
    {
        try
        {
            if (!PrepareExplorer(explorer, asset.Path, out string why))
            {
                _scenarios.Add(new ScenarioResult(name, false, expected, why));
                return;
            }

            Thread.Sleep(options.SettleMilliseconds);
            if (!SendKeyIfForeground(Native.VK_SPACE, explorer, out long t0))
            {
                _scenarios.Add(new ScenarioResult(name, false, expected, $"foreground changed before Space (now {Native.Describe(Native.GetForegroundWindow())})"));
                return;
            }

            long? request = WaitForRequest(t0);
            if (request is null || !WaitFor(() => { _log.Poll(); return _log.Find(request.Value, "full-visible") is not null; }, 10000, null))
            {
                _scenarios.Add(new ScenarioResult(name, false, expected, "Quick View did not show the image"));
                return;
            }

            Thread.Sleep(200);
            (bool ok, string observed) = body(request.Value, t0);
            _scenarios.Add(new ScenarioResult(name, ok, expected, observed));
            log($"scenario {name}: {(ok ? "PASS" : "FAIL")} {observed}");
            EnsureHidden(request.Value, explorer);
        }
        catch (Exception ex)
        {
            _scenarios.Add(new ScenarioResult(name, false, expected, $"harness error {ex.GetType().Name}: {ex.Message}"));
        }
    }

    private long? WaitForRequest(long t0)
    {
        long? request = null;
        WaitFor(() => { _log.Poll(); return (request = _log.RequestAfter(t0)) is not null; }, 3000, null);
        return request;
    }

    private bool PrepareExplorer(nint explorer, string path, out string why)
    {
        why = string.Empty;
        if (!Native.IsWindow(explorer))
        {
            why = "Explorer window closed";
            return false;
        }

        if (options.Cold && _neutralWindow != 0 && Native.IsWindow(_neutralWindow))
        {
            ForceForeground(_neutralWindow);
            Thread.Sleep(100);
        }

        if (!ForceForeground(explorer))
        {
            why = $"could not activate Explorer (foreground = {Native.Describe(Native.GetForegroundWindow())})";
            return false;
        }

        if (!WaitFor(() => Native.Focus(explorer).FocusClass == ItemViewClass, 3000, null))
        {
            why = $"Explorer focus is {Native.Focus(explorer).FocusClass}, not the item view";
            return false;
        }

        ExplorerSelection? selection = _shell.EnumerateWindows().FirstOrDefault(s => s.TopLevelWindow == explorer);
        if (selection is null || !selection.Paths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            why = $"Explorer selection is not the test file ({selection?.Paths.Count ?? 0} items selected)";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Opens the folder through the running shell (Shell.Application.Open) so the window lives in the
    /// user's main explorer.exe like windows opened by the user. Launching "explorer.exe /select" from
    /// this process was observed to create a separate explorer.exe per window, which is not representative.
    /// The file is then selected through Shell automation (FolderItem + SelectItem).
    /// </summary>
    private nint OpenExplorer(string path)
    {
        var before = Native.TopLevelWindows().Where(w => Native.ClassName(w) == ExplorerClass).ToHashSet();
        string folder = Path.GetDirectoryName(path)!;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        shell.Open(folder);

        nint window = 0;
        WaitFor(
            () =>
            {
                foreach (dynamic w in shell.Windows())
                {
                    nint hwnd = (nint)(long)w.HWND;
                    string? location = w.Document?.Folder?.Self?.Path as string;
                    // Shell.Open re-activates an already open window for the same folder; reuse it.
                    if (string.Equals(location, folder, StringComparison.OrdinalIgnoreCase))
                    {
                        // SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED
                        w.Document.SelectItem(w.Document.Folder.ParseName(Path.GetFileName(path)), 1 | 4 | 8 | 16);
                        window = hwnd;
                        return true;
                    }
                }

                return false;
            },
            15000,
            null);
        if (window != 0)
        {
            if (!before.Contains(window) && !_createdExplorers.Contains(window))
            {
                _createdExplorers.Add(window);
            }

            Native.GetWindowThreadProcessId(window, out uint pid);
            Environment[$"explorerProcess:{Path.GetFileName(path)}"] = pid;
        }

        nint found = 0;
        WaitFor(
            () =>
            {
                foreach (ExplorerSelection s in _shell.EnumerateWindows())
                {
                    if ((window == 0 || s.TopLevelWindow == window) && s.Paths.Contains(path, StringComparer.OrdinalIgnoreCase) && Native.IsWindowVisible(s.TopLevelWindow))
                    {
                        found = s.TopLevelWindow;
                        return true;
                    }
                }

                return false;
            },
            15000,
            null);
        if (found != 0 && !before.Contains(found) && !_createdExplorers.Contains(found))
        {
            _createdExplorers.Add(found);
        }

        return found;
    }

    /// <summary>
    /// Harness-only: activates a window from this background console process using AttachThreadInput.
    /// This is a test-setup crutch to put Explorer in the state a user would (by clicking it); Mavue's
    /// product code does not use it (except in the explicitly labelled comparison mode).
    /// </summary>
    private static bool ForceForeground(nint window)
    {
        if (Native.GetForegroundWindow() == window)
        {
            return true;
        }

        nint current = Native.GetForegroundWindow();
        uint foregroundThread = current != 0 ? Native.GetWindowThreadProcessId(current, out _) : 0;
        uint thisThread = Native.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != thisThread && Native.AttachThreadInput(thisThread, foregroundThread, true);
        Native.BringWindowToTop(window);
        Native.SetForegroundWindow(window);
        if (attached)
        {
            Native.AttachThreadInput(thisThread, foregroundThread, false);
        }

        return WaitFor(() => Native.GetForegroundWindow() == window, 2000, null);
    }

    /// <summary>Injects a key press only if <paramref name="expectedForeground"/> is still the foreground window.</summary>
    private static bool SendKeyIfForeground(ushort vk, nint expectedForeground, out long timestamp)
    {
        timestamp = 0;
        if (Native.GetForegroundWindow() != expectedForeground)
        {
            return false;
        }

        Native.INPUT[] inputs = [Key(vk, false), Key(vk, true)];
        timestamp = Stopwatch.GetTimestamp();
        return Native.SendInput(2, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>()) == 2;
    }

    private static void SendAltTab(nint expectedForeground)
    {
        if (Native.GetForegroundWindow() != expectedForeground)
        {
            return;
        }

        Native.INPUT[] inputs = [Key(Native.VK_MENU, false), Key(Native.VK_TAB, false), Key(Native.VK_TAB, true), Key(Native.VK_MENU, true)];
        Native.SendInput(4, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    private static Native.INPUT Key(ushort vk, bool up) => new()
    {
        type = 1, // INPUT_KEYBOARD
        ki = new Native.KEYBDINPUT
        {
            wVk = vk,
            wScan = (ushort)Native.MapVirtualKeyW(vk, 0),
            dwFlags = up ? Native.KEYEVENTF_KEYUP : 0,
        },
    };

    private nint HostWindow()
    {
        if (_hostWindow != 0 && Native.IsWindow(_hostWindow))
        {
            return _hostWindow;
        }

        if (_host is null)
        {
            return 0;
        }

        // The preview window is the host's only WinUI top-level window.
        _hostWindow = Native.TopLevelWindows().FirstOrDefault(w => Pid(w) == _host.Id && Native.ClassName(w) == "WinUIDesktopWin32WindowClass");
        return _hostWindow;
    }

    /// <summary>True when the screen shows image pixels (not the window's #1E1E1E background) inside Quick View.</summary>
    private bool TryImagePixel(out uint color)
    {
        color = 0;
        nint window = HostWindow();
        if (window == 0 || !Native.IsWindowVisible(window) || !Native.GetWindowRect(window, out Native.RECT r))
        {
            return false;
        }

        // Upper-middle point: inside the image area but away from the centered status text.
        int x = (r.Left + r.Right) / 2;
        int y = r.Top + ((r.Bottom - r.Top) * 35 / 100);
        nint dc = Native.GetDC(0);
        try
        {
            color = Native.GetPixel(dc, x, y);
        }
        finally
        {
            Native.ReleaseDC(0, dc);
        }

        int red = (int)(color & 0xFF), green = (int)((color >> 8) & 0xFF), blue = (int)((color >> 16) & 0xFF);
        return Math.Abs(red - 30) + Math.Abs(green - 30) + Math.Abs(blue - 30) > 60 && color != 0xFFFFFFFF;
    }

    private static bool QuickLookWindowVisible()
    {
        var ids = Process.GetProcessesByName("QuickLook").Select(p => (uint)p.Id).ToHashSet();
        return ids.Count > 0 && Native.TopLevelWindows().Any(w => Native.IsWindowVisible(w) && ids.Contains(Pid(w)) && Native.GetWindowRect(w, out Native.RECT r) && r.Right - r.Left > 200);
    }

    private static uint Pid(nint window)
    {
        Native.GetWindowThreadProcessId(window, out uint pid);
        return pid;
    }

    private static double Ms(long from, long to) => (to - from) / TicksPerMs;

    private static bool WaitFor(Func<bool> condition, int timeoutMs, string? what)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeoutMs * TicksPerMs);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        if (what is not null)
        {
            throw new TimeoutException($"Timed out waiting for {what}");
        }

        return condition();
    }

    public string ToJson() => JsonSerializer.Serialize(
        new { environment = Environment, samples = _samples, scenarios = _scenarios },
        new JsonSerializerOptions { WriteIndented = true });
}
