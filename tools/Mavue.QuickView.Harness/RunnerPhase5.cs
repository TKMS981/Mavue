using System.Diagnostics;
using System.Security.Cryptography;
using Mavue.Metadata;
using Windows.Graphics.Imaging;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Quick Preview Phase 5 (docs/ARCHITECTURE.md §4.7.13–14): the EXIF card, the check before sharing (clean copies:
/// original unchanged, nothing private left, Mark-of-the-Web kept, clipboard) and the similar-image search (results,
/// opening a result, the worker's limits and end, cancelling, Quick View staying responsive while it runs).
/// </summary>
internal sealed partial class Runner
{
    private const ushort VkE = 0x45;

    private void RunPhase5Scenarios(List<TestAsset> assets)
    {
        string root = Path.GetDirectoryName(assets[0].Path)! + " Phase5";
        string similar = root + " 類似";
        string many = root + " 大量";
        foreach (string folder in new[] { root, similar, many })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.CreateDirectory(folder);
        }

        string photo = Path.Combine(root, "01 photo.jpg");
        string target = Path.Combine(similar, "00 target.jpg");
        string bigTarget = Path.Combine(many, "0000 target.jpg");
        try
        {
            Task.Run(() => WritePhase5FilesAsync(photo, target, bigTarget)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Guarded("exif-card", () => (false, $"could not generate files: {ex.GetType().Name} {ex.Message}"), "generated files");
            return;
        }

        WriteHostSettings();
        Guarded("exif-card", () => ExifCardScenario(photo),
            "E on Quick View shows the EXIF card over the image's corner: camera, lens, settings, time, and the location/serial number hints; the choice is kept; E again hides it");
        Guarded("share-check", () => ShareCheckScenario(photo),
            "Check before sharing lists location, time, camera, serial numbers, owner, software, comments, thumbnail; \"Save a copy\" writes \"… (no metadata).jpg\" next to it: verified, nothing private left, orientation kept, the original byte-for-byte unchanged, Mark-of-the-Web copied; \"Copy to clipboard\" puts a clean copy on the clipboard");
        Guarded("similar-search", () => SimilarScenario(target),
            "Similar images (button): the worker finds the 3 near-copies among 24 images (resized, recompressed), not the others; the worker exits when done; clicking a result shows it pinned (Explorer's selection unchanged); closing the row releases it");
        Guarded("similar-resources", () => SimilarResourcesScenario(bigTarget),
            "A search over 600 images: Quick View still moves through Explorer's files while it runs (↓, normal latency), the worker stays within its job limits, Stop ends it at once, a second search reuses the cache; no worker process remains");
        WriteHostSettings();
    }

    private static async Task WritePhase5FilesAsync(string photo, string target, string bigTarget)
    {
        // A JPEG made by Windows, then given a full EXIF block (camera, GPS, serial numbers, owner, thumbnail size).
        await TestAssets.EncodeAsync(photo, BitmapEncoder.JpegEncoderId, 1200, 800, 0.9);
        byte[] jpeg = await File.ReadAllBytesAsync(photo);
        byte[] exif = Phase5Exif();
        byte[] withExif = [0xFF, 0xD8, 0xFF, 0xE1, (byte)((exif.Length + 8) >> 8), (byte)(exif.Length + 8), .. "Exif\0\0"u8, .. exif, .. jpeg[2..]];
        await File.WriteAllBytesAsync(photo, withExif);
        await File.WriteAllTextAsync(photo + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://photos.example.com/photo.jpg\r\n");

        // Similar: a target, three near-copies, twenty others.
        string folder = Path.GetDirectoryName(target)!;
        await TestAssets.EncodeAsync(target, BitmapEncoder.JpegEncoderId, 800, 600, 0.9);
        await TestAssets.EncodeAsync(Path.Combine(folder, "05 copy small.jpg"), BitmapEncoder.JpegEncoderId, 400, 300, 0.7);
        await TestAssets.EncodeAsync(Path.Combine(folder, "11 copy.png"), BitmapEncoder.PngEncoderId, 800, 600, null);
        await TestAssets.EncodeAsync(Path.Combine(folder, "17 copy low quality.jpg"), BitmapEncoder.JpegEncoderId, 800, 600, 0.3);
        for (int i = 0; i < 20; i++)
        {
            await EncodeSolidAsync(Path.Combine(folder, $"{i + 20} other.png"), BitmapEncoder.PngEncoderId, 300, 200, ((byte)(i * 12), (byte)(255 - (i * 11)), (byte)(i * 7 % 255)));
        }

        // Many: 600 images the decoder cannot shortcut (PNG has no reduced decoding), the target among them.
        string big = Path.GetDirectoryName(bigTarget)!;
        await TestAssets.EncodeAsync(bigTarget, BitmapEncoder.JpegEncoderId, 640, 480, 0.9);
        for (int i = 1; i < 600; i++)
        {
            await EncodeSolidAsync(Path.Combine(big, $"{i:D4} image.png"), BitmapEncoder.PngEncoderId, 1600, 1200, ((byte)(i * 37 % 256), (byte)(i * 91 % 256), (byte)(i * 53 % 256)));
        }
    }

    private static byte[] Phase5Exif()
    {
        // II*; IFD0: Make, Model, Orientation 1, Software, EXIF pointer, GPS pointer; EXIF: owner, serial, lens, ISO; GPS: lat.
        var w = new List<byte>();
        void U16(int v) => w.AddRange([(byte)v, (byte)(v >> 8)]);
        void U32(int v) => w.AddRange([(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]);
        string[] strings = ["Canon\0", "Canon EOS R5\0", "Lightroom\0", "Taro Yamada\0", "0123456789\0", "RF24-70mm F2.8\0", "N\0"];
        // Layout: header 8, IFD0 at 8 (6 entries) = 2+72+4 = 78 → 86; EXIF IFD at 86 (4 entries) = 54 → 140; GPS at 140 (2 entries) = 30 → 170; data at 170.
        int data = 170;
        var offsets = new List<int>();
        foreach (string s in strings)
        {
            offsets.Add(data);
            data += s.Length + (s.Length & 1);
        }

        int latitude = data;
        w.AddRange("II*\0"u8.ToArray());
        U32(8);
        U16(6);
        void Entry(int tag, int type, int count, int value) { U16(tag); U16(type); U32(count); U32(value); }
        Entry(0x010F, 2, strings[0].Length, offsets[0]);
        Entry(0x0110, 2, strings[1].Length, offsets[1]);
        Entry(0x0112, 3, 1, 1);
        Entry(0x0131, 2, strings[2].Length, offsets[2]);
        Entry(0x8769, 4, 1, 86);
        Entry(0x8825, 4, 1, 140);
        U32(0);
        U16(4);
        Entry(0x8827, 3, 1, 800);
        Entry(0xA430, 2, strings[3].Length, offsets[3]);
        Entry(0xA431, 2, strings[4].Length, offsets[4]);
        Entry(0xA434, 2, strings[5].Length, offsets[5]);
        U32(0);
        U16(2);
        Entry(0x0001, 2, 2, 'N');
        Entry(0x0002, 5, 3, latitude);
        U32(0);
        foreach (string s in strings)
        {
            w.AddRange(System.Text.Encoding.ASCII.GetBytes(s));
            if ((s.Length & 1) == 1)
            {
                w.Add(0);
            }
        }

        foreach (int v in new[] { 35, 1, 40, 1, 52, 1 })
        {
            U32(v);
        }

        return [.. w];
    }

    private (bool, string) ExifCardScenario(string photo)
    {
        if (!ShowInQuickView(photo, out long t0, out string why))
        {
            return (false, why);
        }

        nint host = HostWindow();
        ForceForeground(host);
        Thread.Sleep(200);
        long tE = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkE, host, out _);
        Mark? card = WaitForMark(tE, "exif-card", _ => true);
        Thread.Sleep(300);
        string all = string.Join(" | ", HostTexts());
        bool camera = all.Contains("Canon EOS R5", StringComparison.Ordinal);
        bool lens = all.Contains("RF24-70mm", StringComparison.Ordinal);
        bool location = all.Contains("位置情報", StringComparison.Ordinal) || all.Contains("Location", StringComparison.Ordinal);
        bool serial = all.Contains("シリアル番号", StringComparison.Ordinal) || all.Contains("Serial", StringComparison.Ordinal);
        bool saved = File.ReadAllText(HostSettingsPath).Contains("\"showExifPanel\": true", StringComparison.Ordinal);
        long tOff = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkE, host, out _);
        Thread.Sleep(400);
        bool hidden = UiaRectById(host, "QuickViewExifCard") == "missing";
        bool ok = card?.Bool("exif") == true && camera && lens && location && serial && saved && hidden;
        ok &= CloseHostOrExplorer();
        _ = tOff;
        return (ok, $"card: container={card?.Text("container")} exif={card?.Bool("exif")} in {card?.Number("ms")} ms; camera={camera}, lens={lens}, location hint={location}, serial hint={serial}; setting saved={saved}; E again hides={hidden}");
    }

    private (bool, string) ShareCheckScenario(string photo)
    {
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(photo)));
        if (!ShowInQuickView(photo, out long t0, out string why))
        {
            return (false, why);
        }

        nint host = HostWindow();
        var observed = new List<string>();
        RunUiaScript(host, "QuickViewShareCheckButton", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        Mark? check = WaitForMark(t0, "share-check", _ => true);
        string kinds = check?.Text("kinds") ?? string.Empty;
        bool found = new[] { "Location", "CameraDetails", "DeviceIdentity", "OwnerName", "EditingSoftware" }.All(k => kinds.Contains(k, StringComparison.Ordinal));
        observed.Add($"check: {check?.Number("findings")} findings ({kinds}), canStrip={check?.Bool("canStrip")}");
        bool ok = found && check?.Bool("canStrip") == true;

        string folder = Path.GetDirectoryName(photo)!;
        string[] existing = Directory.GetFiles(folder, "*.jpg");
        long tSave = Stopwatch.GetTimestamp();
        RunUiaScript(host, "QuickViewShareSave", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        Mark? saved = WaitForMark(tSave, "share-copy", m => m.Text("destination") == "SameFolder");
        string? copy = Directory.GetFiles(folder, "*.jpg").Except(existing).FirstOrDefault();
        bool clean = false, upright = false, motw = false;
        if (copy is not null)
        {
            ImageMetadata after = ImageMetadataReader.Read(copy, findTrailer: true);
            clean = PrivacyCheck.Review(after).All(f => f.Level == PrivacyLevel.Kept);
            upright = after.Exif?.Orientation is null or 1;
            motw = TryReadText(copy + ":Zone.Identifier")?.Contains("ZoneId=3", StringComparison.Ordinal) == true;
        }

        string after1 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(photo)));
        observed.Add($"saved \"{Path.GetFileName(copy)}\": verified={saved?.Bool("verified")}, removed {saved?.Number("removedBytes")} bytes in {saved?.Number("ms")} ms, clean={clean}, orientation ok={upright}, Mark-of-the-Web={motw}, original unchanged={before == after1}");
        ok &= saved?.Bool("verified") == true && copy is not null && clean && upright && motw && before == after1;

        ClearClipboard();
        long tClip = Stopwatch.GetTimestamp();
        RunUiaScript(host, "QuickViewShareClipboard", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        Mark? clip = WaitForMark(tClip, "share-copy", m => m.Text("destination") == "Clipboard");
        Thread.Sleep(300);
        bool onClipboard = ClipboardHasFiles();
        observed.Add($"clipboard: verified={clip?.Bool("verified")}, files on the clipboard={onClipboard}");
        ok &= clip?.Bool("verified") == true && onClipboard;
        ForceForeground(host);
        SendKeyIfForeground(Native.VK_ESCAPE, host, out _); // the first Esc closes the flyout
        Thread.Sleep(300);
        ok &= CloseHostWindow();
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) SimilarScenario(string target)
    {
        if (!ShowInQuickView(target, out long t0, out string why))
        {
            return (false, why);
        }

        nint explorer = _lastPhase4Explorer;
        nint host = HostWindow();
        var observed = new List<string>();
        long tStart = Stopwatch.GetTimestamp();
        RunUiaScript(host, "QuickViewSimilarButton", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        Mark? start = WaitForMark(tStart, "similar-start", _ => true);
        Mark? done = WaitForMark(tStart, "similar-done", _ => true, 60_000);
        Mark? ended = WaitForMark(tStart, "similar-worker-ended", _ => true, 10_000);
        Thread.Sleep(400);
        string names = RunUiaScript(host, "QuickViewSimilarResults", "if ($el) { foreach ($b in $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) { Write-Output $b.Current.Name } }");
        bool copies = names.Contains("05 copy small.jpg", StringComparison.Ordinal) && names.Contains("11 copy.png", StringComparison.Ordinal) && names.Contains("17 copy low quality.jpg", StringComparison.Ordinal);
        bool noOthers = !names.Contains("other.png", StringComparison.Ordinal);
        int workers = SimilarWorkerProcesses();
        observed.Add($"start: limit {start?.Number("memoryLimitMb")} MB, CPU {start?.Number("cpuPercent")} %, {start?.Number("parallel")} threads; done: {done?.Number("matches")} of {done?.Number("examined")} in {done?.Number("ms")} ms (worker {done?.Number("workerMs")} ms); copies found={copies}, others excluded={noOthers}; worker exit={ended?.Number("exitCode")}, workers left={workers}");
        bool ok = done is not null && done.Number("examined") == 23 && copies && noOthers && ended?.Number("exitCode") == 0 && workers == 0;

        // Open the first result: shown pinned, Explorer's selection stays on the target.
        long tOpen = Stopwatch.GetTimestamp();
        RunUiaScript(host, "QuickViewSimilarResults", "if ($el) { $b = $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))); if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } }");
        (Mark? nav, Mark? shown) = WaitForNavigation(tOpen, "similar");
        bool pinned = _log.Marks.Any(m => m.Name == "pin" && m.Qpc >= tOpen && m.Bool("pinned") == true);
        bool selectionKept = explorer != 0 && ExplorerSelectionIs(explorer, target);
        observed.Add($"result opened: {shown?.Name}, pinned={pinned}, Explorer selection kept={selectionKept}");
        ok &= shown?.Name == "full-visible" && pinned && selectionKept;

        RunUiaScript(host, "QuickViewSimilarClose", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        Thread.Sleep(400);
        bool closed = UiaRectById(host, "QuickViewSimilar") == "missing";
        observed.Add($"row closed={closed}");
        ok &= closed && CloseHostWindow();
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) SimilarResourcesScenario(string target)
    {
        if (!ShowInQuickView(target, out long t0, out string why))
        {
            return (false, why);
        }

        nint explorer = _lastPhase4Explorer;
        nint host = HostWindow();
        var observed = new List<string>();
        _host?.Refresh();
        long hostBefore = _host?.WorkingSet64 ?? 0;

        // 1. Stop at once: the worker ends.
        long tCancel = Stopwatch.GetTimestamp();
        // Start and Stop in one automation call (each call costs ~0.5 s: the search could finish in between).
        RunUiaScript(host, "QuickViewSimilarButton", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); " +
            "for ($i = 0; $i -lt 100; $i++) { $stop = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, 'QuickViewSimilarStop'))); " +
            "if ($stop -and -not $stop.Current.IsOffscreen) { $stop.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break }; Start-Sleep -Milliseconds 10 } }");
        Mark? cancelled = WaitForMark(tCancel, "similar-cancel", _ => true);
        Mark? stopped = WaitForMark(tCancel, "similar-worker-ended", _ => true);
        bool gone = WaitFor(() => SimilarWorkerProcesses() == 0, 4000, null);
        observed.Add($"stop: cancelled={cancelled is not null} (worker ended {(cancelled is null || stopped is null ? double.NaN : Ms(cancelled.Qpc, stopped.Qpc)):0} ms later, exit {stopped?.Number("exitCode")}), worker gone={gone}");
        bool ok = cancelled is not null && gone;

        // 2. A full search while Quick View moves through Explorer's files.
        long tSearch = Stopwatch.GetTimestamp();
        RunUiaScript(host, "QuickViewSimilarButton", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        WaitForMark(tSearch, "similar-start", _ => true);
        long peakWorker = 0;
        var latencies = new List<double>();
        ForceForeground(explorer);
        Thread.Sleep(200);
        for (int i = 0; i < 4; i++)
        {
            long tKey = Stopwatch.GetTimestamp();
            SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
            (Mark? nav, Mark? shown) = WaitForNavigation(tKey, "explorer-selection");
            if (nav is not null && shown is not null)
            {
                latencies.Add(Ms(tKey, shown.Qpc));
            }

            peakWorker = Math.Max(peakWorker, WorkerWorkingSet());
            Thread.Sleep(100);
        }

        Mark? done = WaitForMark(tSearch, "similar-done", _ => true, 120_000);
        Mark? ended = WaitForMark(tSearch, "similar-worker-ended", _ => true, 10_000);
        peakWorker = Math.Max(peakWorker, (long)((ended?.Number("peakMemoryMb") ?? 0) * 1024 * 1024));
        WaitFor(() => SimilarWorkerProcesses() == 0, 5000, null);
        _host?.Refresh();
        long hostAfter = _host?.WorkingSet64 ?? 0;
        observed.Add($"search of {done?.Number("examined")} images in {done?.Number("ms")} ms (cache hits {done?.Number("cacheHits")}); ↓ during it: {string.Join(", ", latencies.Select(l => l.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))} ms; worker peak {peakWorker / (1024 * 1024)} MB; host {hostBefore / (1024 * 1024)} → {hostAfter / (1024 * 1024)} MB");
        ok &= done is not null && latencies.Count == 4 && latencies.Max() < 1000 && peakWorker < 1024L * 1024 * 1024;

        // 3. Again on the same target (back to it first): the cache answers.
        ForceForeground(explorer);
        Thread.Sleep(200);
        for (int i = 0; i < 4; i++)
        {
            SendKeyIfForeground(VkUp, explorer, out _);
            Thread.Sleep(250);
        }

        long tAgain = Stopwatch.GetTimestamp();
        RunUiaScript(HostWindow(), "QuickViewSimilarButton", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        Mark? again = WaitForMark(tAgain, "similar-done", _ => true, 60_000);
        observed.Add($"again: {again?.Number("ms")} ms, cache hits {again?.Number("cacheHits")}");
        ok &= again?.Number("cacheHits") >= 500;
        ok &= WaitFor(() => SimilarWorkerProcesses() == 0, 5000, null);
        ok &= CloseHostOrExplorer() || CloseHostWindow();
        return (ok, string.Join("; ", observed));
    }

    /// <summary>Worker processes of the similar-image search (the host executable with --similar-worker).</summary>
    private static int SimilarWorkerProcesses() => WorkerQuery("@(Get-CimInstance Win32_Process -Filter \"Name='Mavue.QuickView.Host.exe'\" | Where-Object { $_.CommandLine -like '*--similar-worker*' }).Count");

    private static long WorkerWorkingSet() => LongQuery("(@(Get-CimInstance Win32_Process -Filter \"Name='Mavue.QuickView.Host.exe'\" | Where-Object { $_.CommandLine -like '*--similar-worker*' }) | Measure-Object -Property WorkingSetSize -Sum).Sum");

    private static int WorkerQuery(string script) => (int)Math.Min(int.MaxValue, LongQuery(script));

    private static long LongQuery(string script)
    {
        using Process ps = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"", StringComparison.Ordinal)}\"")
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        string output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(20000);
        return long.TryParse(output.Trim(), out long value) ? value : 0;
    }

    private static string? TryReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
