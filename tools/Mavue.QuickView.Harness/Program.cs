using System.Globalization;
using System.Text;
using Mavue.QuickView.Harness;

// Mavue Quick View end-to-end harness (real Windows desktop).
//
//   Mavue.QuickView.Harness [--activation panel|noactivate|auto|hookgrant|setforeground|appwindow|attach|noactivate-topmost]
//                           [--attach-host <timing log of an independently started host>] [--child-host]
//                           [--no-navigation] [--quicklook-after-host <seconds>] [--host <path to Mavue.QuickView.Host.exe>]
//                           [--decoder winrt|xaml] [--warm] [--list-explorer] [--thumb <file>]
//                           [--iterations N] [--cases small-jpeg,pdf,...] [--huge] [--no-prewarm]
//                           [--no-scenarios] [--configuration Debug|Release] [--out report.json]
//
// It opens File Explorer windows, injects Space/Esc/Alt+Tab with SendInput and inspects the foreground
// window and screen pixels. Run it only on an unlocked, interactive desktop and do not type meanwhile.
// Every injected key is guarded: it is sent only if the expected window is still in the foreground.

string Arg(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

string repo = FindRepoRoot(AppContext.BaseDirectory);
string configuration = Arg("--configuration", "Debug");
string binTail = Path.Combine("bin", configuration, "net10.0-windows10.0.26100.0", "win-x64");
var options = new HarnessOptions
{
    Activation = Arg("--activation", "panel"),
    Decoder = Arg("--decoder", "winrt"),
    Interpolation = Arg("--interpolation", "fant"),
    Iterations = int.Parse(Arg("--iterations", "3"), CultureInfo.InvariantCulture),
    Cases = args.Contains("--cases") ? Arg("--cases", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet() : null,
    IncludeHuge = args.Contains("--huge"),
    Prewarm = !args.Contains("--no-prewarm"),
    Scenarios = !args.Contains("--no-scenarios"),
    Cold = !args.Contains("--warm"),
    Navigation = !args.Contains("--no-navigation"),
    QuickLookAfterHostSeconds = args.Contains("--quicklook-after-host") ? int.Parse(Arg("--quicklook-after-host", "12"), CultureInfo.InvariantCulture) : null,
    HostPath = args.Contains("--host") ? Path.GetFullPath(Arg("--host", "")) : Path.Combine(repo, "src", "Mavue.QuickView.Host", binTail, "Mavue.QuickView.Host.exe"),
    AppPath = args.Contains("--app-path") ? Path.GetFullPath(Arg("--app-path", "")) : Path.Combine(repo, "src", "Mavue.App", binTail, "Mavue.exe"),
    WorkDirectory = Path.Combine(Path.GetTempPath(), "Mavue.QuickView.E2E"),
    AttachHostLog = args.Contains("--attach-host") ? Arg("--attach-host", "") : null,
    ChildHost = args.Contains("--child-host"),
    HostExtraArgs = Arg("--host-args", ""),
    ScaleMode = Arg("--scale", "fit"),
    SettleMilliseconds = int.Parse(Arg("--settle", "500"), CultureInfo.InvariantCulture),
    PauseAfterSampleMilliseconds = int.Parse(Arg("--pause", "0"), CultureInfo.InvariantCulture),
    OnlyScenarios = args.Contains("--only") ? Arg("--only", "").Split(',', StringSplitOptions.RemoveEmptyEntries) : null,
};

if (args.Contains("--thumb"))
{
    // Diagnostics: cached shell thumbnail from MTA vs STA threads.
    string file = Arg("--thumb", "");
    foreach (ApartmentState apartment in new[] { ApartmentState.STA, ApartmentState.MTA, ApartmentState.STA, ApartmentState.MTA })
    {
        var t = new Thread(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var img = Mavue.QuickView.Shell.ShellThumbnail.TryGetCached(file, 1024);
                Console.WriteLine($"{apartment}: {(img is null ? "not cached" : $"{img.Width}x{img.Height}")} in {sw.Elapsed.TotalMilliseconds:0.00} ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{apartment}: {ex.GetType().Name} 0x{ex.HResult:X8}");
            }
        });
        t.SetApartmentState(apartment);
        t.Start();
        t.Join();
    }

    return 0;
}

if (args.Contains("--list-explorer"))
{
    var sta = new Thread(() =>
    {
        Console.WriteLine($"apartment: {Thread.CurrentThread.GetApartmentState()}");
        using var shell = new Mavue.QuickView.Shell.ExplorerSelectionProvider { Trace = Console.WriteLine };
        foreach (var s in shell.EnumerateWindows())
        {
            Console.WriteLine($"frame=0x{s.TopLevelWindow:X} view=0x{s.ShellViewWindow:X} selected={s.Paths.Count}");
        }
    });
    sta.SetApartmentState(ApartmentState.STA);
    sta.Start();
    sta.Join();
    return 0;
}

if (!File.Exists(options.HostPath))
{
    Console.Error.WriteLine($"Host not built: {options.HostPath}");
    return 2;
}

// The host is framework-dependent; a per-user .NET install needs DOTNET_ROOT (docs/BUILD.md §2.1).
string userDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet");
if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is null && Directory.Exists(Path.Combine(userDotnet, "shared", "Microsoft.NETCore.App")))
{
    Environment.SetEnvironmentVariable("DOTNET_ROOT", userDotnet);
}

Console.OutputEncoding = Encoding.UTF8;
Action<string> log = line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
List<TestAsset> assets = await TestAssets.PrepareAsync(options.WorkDirectory, options.IncludeHuge, log);
using var runner = new Runner(options, log);
Exception? failure = null;
var runThread = new Thread(() =>
{
    try
    {
        if (args.Contains("--app"))
        {
            runner.RunApp(assets); // Mavue.App scenarios only (no Quick View host)
        }
        else
        {
            runner.Run(assets);
        }
    }
    catch (Exception ex)
    {
        failure = ex;
    }
});
runThread.SetApartmentState(ApartmentState.STA);
runThread.Start();
runThread.Join();
if (failure is not null)
{
    Console.Error.WriteLine(failure);
}

string report = Arg("--out", Path.Combine(options.WorkDirectory, $"report-{options.Activation}-{options.Decoder}-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
File.WriteAllText(report, runner.ToJson());
Console.WriteLine();
Console.WriteLine(Summary(runner));
Console.WriteLine($"report: {report}");
return runner.Samples.All(s => s.Ok) && runner.Scenarios.All(s => s.Passed) ? 0 : 1;

static string Summary(Runner runner)
{
    var sb = new StringBuilder();
    sb.AppendLine("environment: " + string.Join(", ", runner.Environment.Select(kv => $"{kv.Key}={kv.Value}")));
    sb.AppendLine();
    sb.AppendLine("| case | n ok | hook | selection | shown | first frame | thumb visible (cached) | full visible | screen pixel | QV fg at show / +150ms | Explorer kept focus | decode ms | decoded | peak WS MB | Esc → Explorer |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
    foreach (IGrouping<string, Sample> g in runner.Samples.GroupBy(s => s.Case))
    {
        static string M(IEnumerable<double?> values)
        {
            var v = values.Where(x => x is not null).Select(x => x!.Value).OrderBy(x => x).ToList();
            return v.Count == 0 ? "–" : v.Count == 1 ? $"{v[0]:0.0}" : $"{v[v.Count / 2]:0.0} ({v[0]:0.0}–{v[^1]:0.0})";
        }

        var s = g.ToList();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| {g.Key} | {s.Count(x => x.Ok)}/{s.Count} | {M(s.Select(x => x.HookMs))} | {M(s.Select(x => x.SelectionMs))} | {M(s.Select(x => x.ShownMs))} | {M(s.Select(x => x.FirstFrameMs))} | {M(s.Select(x => x.ThumbnailVisibleMs))} ({s.Count(x => x.ThumbnailCached == true)}/{s.Count}) | {M(s.Select(x => x.FullVisibleMs))} | {M(s.Select(x => x.ScreenPixelMs))} | {s.Count(x => x.ForegroundAtShown == true)}/{s.Count(x => x.ForegroundAfter150Ms == true)} of {s.Count} | {s.Count(x => x.ExplorerKeptFocusWhileShown == true)}/{s.Count} (above owner {s.Count(x => x.AboveOwnerAtShow == true)}) | {M(s.Select(x => x.DecodeMs))} | {s.LastOrDefault()?.Decoded} | {s.Max(x => x.HostPeakWorkingSetMb):0} | {s.Count(x => x.EscapeClosedAndReturnedToExplorer == true)}/{s.Count} |"));
        foreach (Sample failed in s.Where(x => !x.Ok || x.QuickLookWindowAppeared))
        {
            sb.AppendLine($"|  ↳ #{failed.Iteration} | {failed.FullOutcome} | {failed.Note} {(failed.QuickLookWindowAppeared ? "(QuickLook window appeared)" : "")} setForeground={failed.SetForegroundResult} hookProbe={failed.HookProbe} fallback={failed.FellBackToNoActivate} realUserInput={failed.RealUserInputDuringSample} |");
        }
    }

    var all = runner.Samples.Where(x => x.HookProbe is not null).ToList();
    if (all.Count > 0)
    {
        sb.AppendLine();
        sb.AppendLine($"hook probe (AllowSetForegroundWindow inside hook) TRUE: {all.Count(x => x.HookProbe == true)}/{all.Count}; " +
            $"with real user input during sample: TRUE {all.Count(x => x.RealUserInputDuringSample && x.HookProbe == true)}/{all.Count(x => x.RealUserInputDuringSample)}, " +
            $"without: TRUE {all.Count(x => !x.RealUserInputDuringSample && x.HookProbe == true)}/{all.Count(x => !x.RealUserInputDuringSample)}; " +
            $"fallback to no-activate: {all.Count(x => x.FellBackToNoActivate == true)}");
    }

    sb.AppendLine($"real (non-injected) user input events during run: {runner.RealUserInputEvents}");
    sb.AppendLine();
    foreach (ScenarioResult r in runner.Scenarios)
    {
        sb.AppendLine($"scenario {r.Name}: {(r.Passed ? "PASS" : "FAIL")} — expected: {r.Expected} — observed: {r.Observed}");
    }

    return sb.ToString();
}

static string FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Mavue.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new InvalidOperationException("Mavue.slnx not found above " + start);
}
