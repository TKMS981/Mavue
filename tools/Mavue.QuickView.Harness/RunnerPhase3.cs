using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Windows.Graphics.Imaging;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Quick Preview Phase 3 (docs/ARCHITECTURE.md §4.7.10–11): ZIP archives without extraction (listing, images inside,
/// stepping, comic archives, hostile and damaged archives), and the light media controls (wheel seeking, waveform).
/// </summary>
internal sealed partial class Runner
{
    private const ushort VkBack = 0x08;

    private void RunPhase3Scenarios(List<TestAsset> assets)
    {
        string root = Path.GetDirectoryName(assets[0].Path)!;
        string folder = root + " Phase3";
        string comics = root + " Phase3 cbz";
        string hostile = root + " Phase3 安全";
        foreach (string f in new[] { folder, comics, hostile })
        {
            if (Directory.Exists(f))
            {
                Directory.Delete(f, recursive: true);
            }

            Directory.CreateDirectory(f);
        }

        string archive = Path.Combine(folder, "01 archive.zip");
        string comic = Path.Combine(comics, "01 comic.cbz");
        string bomb = Path.Combine(hostile, "01 bomb.zip");
        string damaged = Path.Combine(hostile, "02 damaged.zip");
        string fake = Path.Combine(hostile, "03 fake.zip");
        string split = Path.Combine(hostile, "04 split.zip");
        try
        {
            Task.Run(() => WritePhase3ArchivesAsync(archive, comic, bomb, damaged, fake, split)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Guarded("archive-list", () => (false, $"could not generate archives: {ex.GetType().Name} {ex.Message}"), "generated archives");
            return;
        }

        WritePhase1Settings(filmstrip: false, inspector: false);
        Guarded("archive-list", () => ArchiveListScenario(archive),
            "A ZIP (downloaded: Mark-of-the-Web) with folders, images, a Shift_JIS (CP932) name, an unsafe \"../\" name, an encrypted entry and a nested ZIP: the listing comes from the central directory at once, with counts, the internet origin and warnings; nothing is extracted");
        Guarded("archive-images", () => ArchiveImagesScenario(archive),
            "\"Show images\": the images inside the ZIP are decoded one at a time in natural order (IMG_2 before IMG_10); after that click Quick View has the keyboard: PageDown/PageUp step through them (stepping back is instant from the cache) without moving Explorer's selection, → steps too; Backspace returns to the list; Esc closes");
        Guarded("archive-cbz", () => ArchiveComicScenario(comic),
            "A comic book archive (.cbz) opens at its first page; the mouse wheel turns pages");
        Guarded("archive-safety", () => ArchiveSafetyScenario(bomb, damaged, fake),
            "Hostile and broken archives: a 60 MB decompression bomb inside a ZIP is listed with a warning and refused when shown (nothing decompressed), the next image still shows; a damaged ZIP, a non-ZIP named .zip and a part of a split ZIP each show their reason on the information card; the resident process keeps running");
        WriteHostSettings();
    }

    private static async Task WritePhase3ArchivesAsync(string archive, string comic, string bomb, string damaged, string fake, string split)
    {
        string temp = Path.Combine(Path.GetTempPath(), "Mavue.E2E.Phase3.images");
        Directory.CreateDirectory(temp);
        async Task<byte[]> Jpeg(string name, uint width, uint height)
        {
            string file = Path.Combine(temp, name);
            await TestAssets.EncodeAsync(file, BitmapEncoder.JpegEncoderId, width, height, 0.9);
            return await File.ReadAllBytesAsync(file);
        }

        byte[] small = await Jpeg("small.jpg", 640, 480);
        byte[] tall = await Jpeg("tall.jpg", 600, 900);

        // Legacy (no UTF-8 flag) names in Shift_JIS, as Japanese Windows tools write them.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] main = ZipBytes(
            z =>
            {
                AddEntry(z, "photos/IMG_10.jpg", small);
                AddEntry(z, "photos/IMG_2.jpg", small);
                AddEntry(z, "photos/IMG_1.jpg", small);
                AddEntry(z, "日本語/写真.jpg", small);
                AddEntry(z, "docs/readme.txt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("readme ", 400))));
                AddEntry(z, "../evil.txt", Encoding.UTF8.GetBytes("outside"));
                AddEntry(z, "secret.jpg", small);
                AddEntry(z, "nested.zip", [0x50, 0x4B, 0x05, 0x06, .. new byte[18]]);
            },
            Encoding.GetEncoding(932));
        List<int> headers = CentralHeaderOffsets(main);
        main[headers[6] + 8] |= 1; // secret.jpg: encrypted flag
        await File.WriteAllBytesAsync(archive, main);
        await File.WriteAllTextAsync(archive + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://downloads.example.com/archive.zip\r\n");

        await File.WriteAllBytesAsync(comic, ZipBytes(z =>
        {
            for (int i = 1; i <= 5; i++)
            {
                AddEntry(z, $"{i}.jpg", tall, CompressionLevel.NoCompression);
            }
        }));

        await File.WriteAllBytesAsync(bomb, ZipBytes(z =>
        {
            AddEntry(z, "a bomb.png", new byte[60 * 1024 * 1024]); // ~60 KB compressed: ratio ≈ 1,000
            AddEntry(z, "b ok.jpg", small);
        }));

        byte[] broken = ZipBytes(z => AddEntry(z, "a.jpg", small));
        BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(EndRecordOffset(broken) + 12), 5_000_000); // directory size beyond the file
        await File.WriteAllBytesAsync(damaged, broken);

        byte[] random = new byte[30_000];
        new Random(11).NextBytes(random);
        await File.WriteAllBytesAsync(fake, random);

        byte[] part = ZipBytes(z => AddEntry(z, "a.jpg", small));
        BinaryPrimitives.WriteUInt16LittleEndian(part.AsSpan(EndRecordOffset(part) + 4), 1); // disk 1 of a split archive
        await File.WriteAllBytesAsync(split, part);
    }

    private static byte[] ZipBytes(Action<ZipArchive> fill, Encoding? names = null)
    {
        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true, names))
        {
            fill(zip);
        }

        return memory.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] content, CompressionLevel level = CompressionLevel.Optimal)
    {
        using Stream stream = zip.CreateEntry(name, level).Open();
        stream.Write(content);
    }

    private static List<int> CentralHeaderOffsets(byte[] zip)
    {
        var offsets = new List<int>();
        for (int i = 0; i + 4 <= zip.Length; i++)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 1 && zip[i + 3] == 2)
            {
                offsets.Add(i);
            }
        }

        return offsets;
    }

    private static int EndRecordOffset(byte[] zip)
    {
        for (int i = zip.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == 0x06054b50)
            {
                return i;
            }
        }

        return -1;
    }

    private (bool, string) ArchiveListScenario(string archive)
    {
        nint explorer = OpenExplorer(archive);
        dynamic? window = ShellWindowFor(explorer);
        long t0 = Stopwatch.GetTimestamp();
        if (explorer == 0 || window is null || !OpenQuickView(explorer, archive, out string why))
        {
            return (false, "Quick View not shown");
        }

        _log.Poll();
        Mark? listed = _log.Marks.LastOrDefault(m => m.Name == "archive-listed" && m.Qpc >= t0);
        Mark? shown = listed is null ? null : _log.Find(listed.Request, "full-visible");
        Mark? info = listed is null ? null : _log.Find(listed.Request, "info");
        bool counts = listed?.Number("files") == 8 && listed.Number("folders") == 3 && listed.Number("images") == 4 &&
            listed.Number("unsafeNames") == 1 && listed.Number("encrypted") == 1 && listed.Number("nested") == 1 && listed.Number("motw") == 3;
        string[] texts = HostTexts();
        string all = string.Join(" | ", texts);
        bool origin = all.Contains("example.com", StringComparison.Ordinal);
        bool japanese = all.Contains("日本語", StringComparison.Ordinal);
        bool warning = all.Contains("安全でない名前", StringComparison.Ordinal) || all.Contains("unsafe names", StringComparison.Ordinal);
        bool loading = texts.Any(s => LoadingTexts.Contains(s));
        bool list = UiaRectById(HostWindow(), "QuickViewArchiveList") != "missing";
        bool closed = CloseFromExplorer(explorer) || CloseHostWindow();
        bool ok = counts && shown is not null && origin && japanese && warning && !loading && list && closed;
        return (ok, $"listed: files={listed?.Number("files")} folders={listed?.Number("folders")} images={listed?.Number("images")} unsafe={listed?.Number("unsafeNames")} encrypted={listed?.Number("encrypted")} nested={listed?.Number("nested")} motw={listed?.Number("motw")} names={listed?.Text("nameEncoding")} in {listed?.Number("listMs")} ms; shown {(info is null || shown is null ? double.NaN : Ms(info.Qpc, shown.Qpc)):0} ms after the file facts; origin={origin}, 日本語={japanese}, warning={warning}, list={list}{(loading ? " LOADING" : string.Empty)}");
    }

    private (bool, string) ArchiveImagesScenario(string archive)
    {
        nint explorer = OpenExplorer(archive);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null || !OpenQuickView(explorer, archive, out string why))
        {
            return (false, "Quick View not shown");
        }

        nint host = HostWindow();
        var observed = new List<string>();
        long t = Stopwatch.GetTimestamp();
        string invoked = RunUiaScript(host, "QuickViewArchiveShowImages", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output 'ok' } else { Write-Output 'missing' }");
        bool ok = ArchiveStep(t, "archive-show-images", 0, observed, "show images");

        // Clicking "Show images" makes Quick View a normal active window (as any click into it): PageDown/PageUp go to it.
        bool active = ForceForeground(host);
        Thread.Sleep(200);
        foreach ((ushort key, int expected, string name) in new[] { (VkPageDown, 1, "PageDown"), (VkPageDown, 2, "PageDown"), (VkPageUp, 1, "PageUp") })
        {
            long tKey = Stopwatch.GetTimestamp();
            SendKeyIfForeground(key, host, out _);
            ok &= ArchiveStep(tKey, "window-page", expected, observed, name);
        }

        Mark? back = _log.Marks.LastOrDefault(m => m.Name == "full-set" && m.Number("archiveIndex") == 1);
        bool cached = back?.Text("decoder") == "cache";
        bool selectionKept = ExplorerSelectionIs(explorer, archive);
        observed.Add($"back from the cache={cached}, Explorer selection kept={selectionKept}");
        ok &= cached && selectionKept;

        long tArrow = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkRight, host, out _);
        ok &= active && ArchiveStep(tArrow, "window-arrow", 2, observed, "→ on Quick View");

        long tBack = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkBack, host, out _);
        (Mark? nav, Mark? done) = WaitForNavigation(tBack, "archive-list");
        bool listed = done?.Name == "full-visible" && nav is not null && _log.Find(nav.Request, "archive-list") is not null;
        observed.Add($"Backspace: list={listed}");
        bool closed = CloseHostWindow();
        ok &= listed && closed && invoked == "ok";
        return (ok, string.Join("; ", observed));
    }

    /// <summary>Waits for an archive image step from <paramref name="source"/> and checks the image index.</summary>
    private bool ArchiveStep(long qpc, string source, int expectedIndex, List<string> observed, string label)
    {
        (Mark? nav, Mark? done) = WaitForNavigation(qpc, source);
        Mark? image = nav is null ? null : _log.Find(nav.Request, "archive-image");
        Mark? full = nav is null ? null : _log.Find(nav.Request, "full-set");
        bool ok = done?.Name == "full-visible" && image?.Number("index") == expectedIndex && full is not null;
        observed.Add(ok
            ? $"{label}: image {expectedIndex + 1}/{image!.Number("count")} {full!.Number("sourceWidth")}×{full.Number("sourceHeight")} in {Ms(qpc, done!.Qpc):0} ms ({full.Text("decoder")})"
            : $"{label}: FAIL (nav={nav is not null}, done={done?.Name} {done?.Text("reason")}, index={image?.Number("index")})");
        return ok;
    }

    private (bool, string) ArchiveComicScenario(string comic)
    {
        nint explorer = OpenExplorer(comic);
        dynamic? window = ShellWindowFor(explorer);
        long t0 = Stopwatch.GetTimestamp();
        if (explorer == 0 || window is null || !OpenQuickView(explorer, comic, out string why))
        {
            return (false, "Quick View not shown");
        }

        _log.Poll();
        Mark? first = _log.Marks.LastOrDefault(m => m.Name == "archive-image" && m.Qpc >= t0);
        bool noList = !_log.Marks.Any(m => m.Name == "archive-list" && m.Qpc >= t0);
        Mark? set = first is null ? null : _log.Find(first.Request, "full-set");
        var observed = new List<string> { $"opened at page {first?.Number("index") + 1}/{first?.Number("count")} ({set?.Number("sourceWidth")}×{set?.Number("sourceHeight")}), list skipped={noList}" };
        bool ok = first?.Number("index") == 0 && first.Number("count") == 5 && noList && set is not null;

        nint host = HostWindow();
        if (Native.GetWindowRect(host, out Native.RECT r))
        {
            long t = Stopwatch.GetTimestamp();
            Native.SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            Thread.Sleep(100);
            Native.MouseEvent(0x0800 /* MOUSEEVENTF_WHEEL */, 0, 0, unchecked((uint)-120), 0);
            ok &= ArchiveStep(t, "wheel", 1, observed, "wheel down");
        }

        ForceForeground(explorer);
        Thread.Sleep(200);
        long tPage = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkPageDown, explorer, out _);
        ok &= ArchiveStep(tPage, "hook-page", 2, observed, "PageDown in Explorer");
        bool kept = ExplorerSelectionIs(explorer, comic);
        observed.Add($"Explorer selection kept={kept}");
        ok &= kept;
        ok &= CloseFromExplorer(explorer) || CloseHostWindow();
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) ArchiveSafetyScenario(string bomb, string damaged, string fake)
    {
        int pid = _host?.Id ?? -1;
        nint explorer = OpenExplorer(bomb);
        dynamic? window = ShellWindowFor(explorer);
        long t0 = Stopwatch.GetTimestamp();
        if (explorer == 0 || window is null || !OpenQuickView(explorer, bomb, out string why))
        {
            return (false, "Quick View not shown");
        }

        var observed = new List<string>();
        _log.Poll();
        Mark? listed = _log.Marks.LastOrDefault(m => m.Name == "archive-listed" && m.Qpc >= t0);
        string listing = string.Join(" | ", HostTexts());
        bool warned = listed?.Number("suspiciousRatio") == 1 &&
            (listing.Contains("展開爆弾", StringComparison.Ordinal) || listing.Contains("decompression bomb", StringComparison.Ordinal));
        observed.Add($"bomb listed with warning={warned}");
        bool ok = warned;

        nint host = HostWindow();
        long tShow = Stopwatch.GetTimestamp();
        RunUiaScript(host, "QuickViewArchiveShowImages", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        (Mark? nav, Mark? done) = WaitForNavigation(tShow, "archive-show-images");
        bool refused = done?.Name == "full-skipped" && done.Text("reason") == "entry-limit-MaxCompressionRatio";
        _host?.Refresh();
        long workingSet = _host?.WorkingSet64 ?? 0;
        observed.Add($"bomb image: {done?.Name} {done?.Text("reason")} (host working set {workingSet / (1024 * 1024)} MB)");
        ok &= refused;

        ForceForeground(host);
        Thread.Sleep(200);
        long tNext = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkPageDown, host, out _);
        ok &= ArchiveStep(tNext, "window-page", 1, observed, "next image");
        ForceForeground(explorer);
        Thread.Sleep(200);

        foreach ((string path, string reason, string text) in new[]
        {
            (damaged, "archive-Damaged", "壊れています|damaged"),
            (fake, "archive-NotAnArchive", "有効な ZIP|not a valid ZIP"),
            (Path.Combine(Path.GetDirectoryName(fake)!, "04 split.zip"), "archive-MultiVolume", "分割|split"),
        })
        {
            long t = Stopwatch.GetTimestamp();
            SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
            (Mark? step, Mark? end) = WaitForNavigation(t, "explorer-selection");
            bool carded = WaitFor(() => { _log.Poll(); return step is not null && _log.Find(step.Request, "info-card") is not null; }, 4000, null);
            Mark? skipped = step is null ? null : _log.Find(step.Request, "full-skipped");
            string all = string.Join(" | ", HostTexts());
            bool says = text.Split('|').Any(s => all.Contains(s, StringComparison.Ordinal));
            bool itemOk = step is not null && ShowsFile(step.Request, path) && skipped?.Text("reason") == reason && carded && says;
            observed.Add($"{Path.GetFileName(path)}: {skipped?.Text("reason")}, card={carded}, message={says}");
            ok &= itemOk;
        }

        bool alive = _host is { HasExited: false } && _host.Id == pid;
        observed.Add($"resident process still running={alive}");
        ok &= alive && (CloseFromExplorer(explorer) || CloseHostWindow());
        return (ok, string.Join("; ", observed));
    }

    // ------------------------------------------------------------------ media (called from RunMediaScenario)

    private (bool, string) MediaWheelScenario(string clip)
    {
        nint explorer = OpenExplorer(clip);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        Select(window, clip, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        long tOpen = Stopwatch.GetTimestamp();
        if (!OpenQuickView(explorer, clip, out string why))
        {
            return (false, why);
        }

        WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "media-opened" && m.Qpc >= tOpen); }, 5000, null);
        nint host = HostWindow();
        var observed = new List<string>();
        bool bar = UiaRectById(host, "QuickViewMediaPlay") != "missing" && UiaRectById(host, "QuickViewMediaPosition") != "missing";
        observed.Add($"light controls shown={bar}");
        if (!Native.GetWindowRect(host, out Native.RECT r))
        {
            return (false, "Quick View window not found");
        }

        Native.SetCursorPos((r.Left + r.Right) / 2, r.Top + ((r.Bottom - r.Top) / 3));
        Thread.Sleep(150);
        long tWheel = Stopwatch.GetTimestamp();
        bool ok = bar;
        (int Delta, bool Shift, int Step)[] wheels = [(120, false, 5000), (120, true, 30000), (-240, false, -10000)];
        foreach ((int delta, bool shift, int step) in wheels)
        {
            long t = Stopwatch.GetTimestamp();
            if (shift)
            {
                Native.SendInput(1, [Key(VkShiftKey, false)], System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
                Thread.Sleep(30);
            }

            Native.MouseEvent(0x0800 /* MOUSEEVENTF_WHEEL */, 0, 0, unchecked((uint)delta), 0);
            if (shift)
            {
                Thread.Sleep(30);
                Native.SendInput(1, [Key(VkShiftKey, true)], System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
            }

            Mark? seek = null;
            WaitFor(() => { _log.Poll(); seek = _log.Marks.LastOrDefault(m => m.Name == "media-wheel-seek" && m.Qpc >= t); return seek is not null && (int)(seek.Number("stepMs") ?? 0) == step; }, 2000, null);
            bool stepOk = (int)(seek?.Number("stepMs") ?? 0) == step;
            observed.Add($"{(shift ? "Shift+" : string.Empty)}wheel {delta:+0;-0}: step {seek?.Number("stepMs"):0} ms → {seek?.Number("targetMs"):0} ms");
            ok &= stepOk;
            Thread.Sleep(400);
        }

        Mark? last = _log.Marks.LastOrDefault(m => m.Name == "media-wheel-seek");
        bool clamped = last?.Number("targetMs") is >= 18000 and <= 21000; // 30 s clip: +5, +30 (clamped to the end), −10
        bool navigated = _log.Marks.Any(m => m.Name == "nav" && m.Qpc >= tWheel);
        observed.Add($"clamped to the clip={clamped}, wheel moved files={navigated}");
        ok &= clamped && !navigated;
        ForceForeground(explorer);
        ok &= CloseFromExplorer(explorer) || CloseHostWindow();
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) AudioWaveformScenario(string audio)
    {
        nint explorer = OpenExplorer(audio);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        Select(window, audio, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        long tOpen = Stopwatch.GetTimestamp();
        if (!OpenQuickView(explorer, audio, out string why))
        {
            return (false, why);
        }

        Mark? waveform = null;
        WaitFor(() => { _log.Poll(); waveform = _log.Marks.LastOrDefault(m => m.Name == "audio-waveform" && m.Qpc >= tOpen); return waveform is not null; }, 8000, null);
        Mark? opened = _log.Marks.LastOrDefault(m => m.Name == "media-opened" && m.Qpc >= tOpen);
        nint host = HostWindow();
        string rect = UiaRectById(host, "QuickViewWaveform");
        var observed = new List<string> { $"waveform {waveform?.Text("outcome")} in {waveform?.Number("ms"):0} ms for {opened?.Number("durationMs"):0} ms of audio; band {rect}" };
        bool ok = waveform?.Text("outcome") == "exact" && rect != "missing";

        // Clicking the middle of the waveform moves to the middle of the file.
        string[] parts = rect.Split(',');
        if (parts.Length == 4 && int.TryParse(parts[0], out int left) && int.TryParse(parts[1], out int top) && int.TryParse(parts[2], out int width) && int.TryParse(parts[3], out int height))
        {
            long t = Stopwatch.GetTimestamp();
            Native.SetCursorPos(left + (width / 2), top + (height / 2));
            Thread.Sleep(100);
            Native.MouseEvent(0x0002 /* LEFTDOWN */, 0, 0, 0, 0);
            Native.MouseEvent(0x0004 /* LEFTUP */, 0, 0, 0, 0);
            Mark? seek = null;
            WaitFor(() => { _log.Poll(); seek = _log.Marks.FirstOrDefault(m => m.Name == "media-seek" && m.Qpc >= t); return seek is not null; }, 2000, null);
            double duration = opened?.Number("durationMs") ?? 0;
            bool middle = seek?.Number("targetMs") is { } target && duration > 0 && Math.Abs(target - (duration / 2)) < duration * 0.06;
            observed.Add($"click in the middle: {seek?.Text("source")} → {seek?.Number("targetMs"):0} ms (middle={middle})");
            ok &= middle;
        }
        else
        {
            ok = false;
        }

        ok &= CloseHostWindow();
        return (ok, string.Join("; ", observed));
    }
}
