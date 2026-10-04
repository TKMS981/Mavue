using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Mavue.Pdf.Tests;
using Microsoft.Win32;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Harness;

/// <summary>
/// File Explorer integration of Mavue.exe (--explorer): the preview handler in Explorer's real preview pane and the
/// thumbnail provider through Windows' thumbnail machinery, registered with Mavue.exe --register. Progress comes from
/// the DLL's diagnostics trace (HKCU\Software\Mavue\Shell "TraceFile", set only while this runs); what is on screen
/// from screen pixels; sound from the preview host's audio session; Explorer's responsiveness from WM_NULL round
/// trips while previews load. --explorer-restart adds register → restart File Explorer → unregister → register.
/// The registration is left as Mavue.exe --register leaves it (the default, without taking over other previewers).
/// </summary>
internal sealed partial class Runner
{
    private const string PreviewWindowClass = "Mavue.Shell.Preview";
    private const string PreviewIid = "{8895B1C6-B41F-4C1C-A562-0D564250836F}";
    private const string ThumbnailIid = "{E357FCCD-A995-4576-B01F-234630154E96}";
    private const string MavuePreviewClsid = "{AB883DEA-90EE-4AF4-944A-45CEDD231E53}";
    private const string MavueThumbnailClsid = "{B4E9FA4B-4DA4-4A1A-9DC7-DE422F056135}";
    private const ushort VkMenu = 0x12;

    private string _shellTrace = string.Empty;

    private sealed record ExplorerFiles(
        string Folder, string Red, string Large, string Gif, string Svg, string Video, string Audio, string BrokenJpeg,
        string EmptyPng, string Text, string Pdf, string LongPdf, string BrokenPdf, string? Japanese, IReadOnlyList<string> Codecs,
        IReadOnlyList<string> MediaFormats);

    public void RunExplorer(List<TestAsset> assets, bool restart, bool lightTheme = false)
    {
        Native.SetProcessDpiAwarenessContext(-4);
        Native.PeekMessageW(out _, 0, 0, 0, 0);
        nint originalForeground = Native.GetForegroundWindow();
        Environment["os"] = System.Environment.OSVersion.VersionString;
        Environment["mode"] = "explorer";
        string app = options.AppPath ?? string.Empty;
        string dll = Path.Combine(Path.GetDirectoryName(app) ?? string.Empty, "Mavue.Shell.Preview.dll");
        if (!File.Exists(app) || !File.Exists(dll))
        {
            Guarded("explorer-register", () => (false, $"Mavue.exe or Mavue.Shell.Preview.dll missing next to {app}"), "the app and the preview DLL are built");
            return;
        }

        Environment["app"] = app;
        ExplorerFiles files = Task.Run(() => PrepareExplorerFilesAsync(assets)).GetAwaiter().GetResult();
        // LocalLow: the preview host (prevhost.exe) runs at low integrity and can write only there.
        string traceFolder = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Mavue");
        Directory.CreateDirectory(traceFolder);
        _shellTrace = Path.Combine(traceFolder, $"shell-trace-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Mavue\Shell", writable: true))
        {
            key.SetValue("TraceFile", _shellTrace, RegistryValueKind.String);
        }

        Environment["shellTrace"] = _shellTrace;
        nint explorer = 0;
        bool paneWasOpen = true;
        try
        {
            Guarded("explorer-register", () => ExplorerRegister(app),
                "Mavue.exe --register: Mavue's preview handler for images, SVG, video and audio (no handler before); PDF keeps the existing previewer (Edge); thumbnails only for PDF and SVG (Windows keeps JPEG/PNG/video/audio)");
            dynamic? window = null;
            Guarded("explorer-preview-pane", () =>
            {
                (bool ok, string observed, nint hwnd, dynamic? w, bool wasOpen) = ExplorerPreviewPane(files);
                explorer = hwnd;
                window = w;
                paneWasOpen = wasOpen;
                return (ok, observed);
            }, "Explorer's preview pane, one file after the other: PNG (red on screen), 24 MP JPEG, animated GIF (colors change), SVG (drawn), WebP/AVIF/HEIC, MP4 (video plays with sound after a click), WAV (sound), a damaged JPEG and an empty PNG (a message), a text file and a PDF (other previewers, Mavue not called); one preview host process throughout, Explorer answers within 1 s all the time");
            if (explorer != 0 && window is not null)
            {
                Guarded("explorer-preview-pdf", () => ExplorerPreviewPdf(app, files, explorer, window),
                    "Mavue.exe --register --prefer-mavue-preview takes over the PDF preview: 8-page and Japanese PDF shown (pages drawn, wheel scrolls), 600-page PDF draws only the first pages, a damaged PDF shows a message; Mavue.exe --register gives the PDF preview back to Edge");
                Guarded("explorer-thumbnails", () => ExplorerThumbnails(files, explorer, window),
                    "Thumbnails through Windows (IShellItemImageFactory, Mavue's provider in the isolated thumbnail process): PDF first page, SVG; damaged PDF and non-SVG text fail quickly; JPEG stays Windows' own; Explorer's large icons show the SVG thumbnail on screen");
            }

            if (explorer != 0 && window is not null)
            {
                Guarded("explorer-preview-keyboard", () => ExplorerPreviewKeyboard(files, explorer, window),
                    "With the preview pane on media files, ↓ in Explorer moves the selection every time (the preview never takes the keyboard or delays Explorer's input)");
            }

            if (explorer != 0 && window is not null)
            {
                Guarded("explorer-monitors", () => ExplorerMonitors(files, explorer, window),
                    "On every monitor (different DPI) the preview has that monitor's DPI, fills the pane and shows the image; moving Explorer to a monitor with another DPI while a file is previewed re-lays it out");
            }

            if (explorer != 0 && window is not null)
            {
                Guarded("explorer-theme", () => ExplorerTheme(files, explorer, window),
                    "The preview follows Windows' app theme (dark or light): background, text and controls readable; screenshots saved for review");
            }

            if (lightTheme && explorer != 0 && window is not null)
            {
                Guarded("theme-light", () => LightTheme(files, explorer, window),
                    "Windows switched to light mode (restored afterwards): the Explorer preview, Quick View and Mavue follow it (light backgrounds, readable text); screenshots saved");
            }

            if (explorer != 0 && window is not null)
            {
                Guarded("explorer-shortcuts", () => ExplorerShortcuts(files, explorer, window),
                    "Explorer's shortcuts with a file previewed: an injected Ctrl+T behaves with Mavue's PNG preview as with Windows' text previewer (and Edge's PDF previewer). Physical Ctrl+T opens a tab in all cases (checked by hand)");
            }

            Guarded("explorer-32bit", () => Explorer32Bit(files),
                "32-bit applications (their file dialogs): thumbnails of PDF/SVG come from Mavue's x86 build in Windows' 32-bit isolated process, a preview handler created out of process runs in Windows' 32-bit prevhost and shows the image");

            if (restart)
            {
                Guarded("explorer-lifecycle", () => ExplorerLifecycle(app, files),
                    "Registered → File Explorer restarted → previews and thumbnails still Mavue's → Mavue.exe --unregister: no Mavue class or binding left, Explorer no longer calls Mavue → Mavue.exe --register again");
            }
        }
        finally
        {
            if (explorer != 0 && Native.IsWindow(explorer) && !paneWasOpen)
            {
                ForceForeground(explorer);
                SendChordIfForeground(explorer, VkMenu, 0x50); // close the preview pane again (Alt+P)
            }

            RunMavue(app, "--register"); // leave the default registration
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Mavue\Shell", writable: true))
            {
                key?.DeleteValue("TraceFile", throwOnMissingValue: false);
            }

            foreach (nint w in _createdExplorers.Where(Native.IsWindow))
            {
                Native.PostMessageW(w, Native.WM_CLOSE, 0, 0);
            }

            if (originalForeground != 0 && Native.IsWindow(originalForeground))
            {
                ForceForeground(originalForeground);
            }

            _shell.Dispose();
        }
    }

    private async Task<ExplorerFiles> PrepareExplorerFilesAsync(List<TestAsset> assets)
    {
        string root = Path.GetDirectoryName(assets[0].Path)!;
        string folder = Path.Combine(options.WorkDirectory, $"Mavue Explorer プレビュー {DateTime.Now:MMdd-HHmmss}"); // new files: nothing cached
        Directory.CreateDirectory(folder);
        string P(string name) => Path.Combine(folder, name);

        await EncodeSolidAsync(P("01 赤.png"), BitmapEncoder.PngEncoderId, 900, 600, (220, 20, 20));
        string large = assets.FirstOrDefault(a => a.Case == "large-jpeg")?.Path ?? assets[0].Path;
        File.Copy(large, P("02 large 24MP.jpg"));
        await WriteAnimatedGifAsync(P("03 anim.gif"), 400, 300, [(220, 0, 0), (0, 200, 0), (0, 0, 220)], 40);
        WriteTestSvg(P("04 drawing.svg"));
        var codecs = new List<string>();
        foreach (string kind in new[] { "webp", "avif", "heif" })
        {
            if (assets.FirstOrDefault(a => a.Case == kind) is { } asset)
            {
                string target = P($"05 sample{Path.GetExtension(asset.Path)}");
                File.Copy(asset.Path, target, overwrite: true);
                codecs.Add(target);
            }
        }

        string mediaFolder = root + " Media";
        Directory.CreateDirectory(mediaFolder);
        var media = new MediaFiles(Path.Combine(mediaFolder, "01 clip.mp4"), Path.Combine(mediaFolder, "02 still.jpg"), Path.Combine(mediaFolder, "03 tone.mp3"),
            Path.Combine(mediaFolder, "04 document.pdf"), Path.Combine(mediaFolder, "05 broken.mp4"), Path.Combine(mediaFolder, "06 tone.wav"));
        await PrepareMediaAsync(media, log);
        File.Copy(media.Clip, P("06 clip.mp4"));
        var mediaFormats = new List<string>();
        foreach (string source in new[] { media.Mp3 }.Concat(Directory.Exists(mediaFolder + " formats") ? Directory.GetFiles(mediaFolder + " formats") : []))
        {
            string target = P("06 " + Path.GetFileName(source));
            File.Copy(source, target, overwrite: true);
            mediaFormats.Add(target);
        }

        File.Copy(media.Wav, P("07 tone.wav"));
        byte[] noise = new byte[50_000];
        new Random(3).NextBytes(noise);
        File.WriteAllBytes(P("08 broken.jpg"), noise);
        File.WriteAllBytes(P("09 empty.png"), []);
        File.WriteAllText(P("10 note.txt"), "plain text: previewed by Windows, not Mavue\r\n");
        File.WriteAllBytes(P("11 文書.pdf"), TestPdf.Create(8));
        File.WriteAllBytes(P("12 long.pdf"), TestPdf.Create(600));
        File.WriteAllBytes(P("13 broken.pdf"), Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\n%%EOF"));
        string japaneseSource = Path.Combine(root + " QV PDF", "02 日本語.pdf");
        string? japanese = null;
        if (File.Exists(japaneseSource))
        {
            japanese = P("14 日本語.pdf");
            File.Copy(japaneseSource, japanese);
        }

        return new ExplorerFiles(folder, P("01 赤.png"), P("02 large 24MP.jpg"), P("03 anim.gif"), P("04 drawing.svg"), P("06 clip.mp4"), P("07 tone.wav"),
            P("08 broken.jpg"), P("09 empty.png"), P("10 note.txt"), P("11 文書.pdf"), P("12 long.pdf"), P("13 broken.pdf"), japanese, codecs, mediaFormats);
    }

    private static async Task EncodeSolidAsync(string path, Guid encoder, uint width, uint height, (byte R, byte G, byte B) color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (color.B, color.G, color.R, 255);
        }

        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        BitmapEncoder bitmapEncoder = await BitmapEncoder.CreateAsync(encoder, stream);
        bitmapEncoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await bitmapEncoder.FlushAsync();
    }

    // ------------------------------------------------------------------ registration

    /// <summary>Runs Mavue.exe with registration switches; returns its console output.</summary>
    private static string RunMavue(string app, params string[] arguments)
    {
        var start = new ProcessStartInfo(app) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(30000);
        Thread.Sleep(500); // SHChangeNotify(ASSOCCHANGED) reaches Explorer
        return output.Trim();
    }

    /// <summary>The handler Explorer resolves for the extension (AssocQueryString), upper-case braced, or "-".</summary>
    private static string Handler(string extension, string iid)
    {
        char[] buffer = new char[64];
        uint length = (uint)buffer.Length;
        int hr = AssocQueryStringW(0, 16 /* ASSOCSTR_SHELLEXTENSION */, extension, iid, buffer, ref length);
        return hr == 0 ? new string(buffer, 0, (int)Math.Max(0, length - 1)).ToUpperInvariant() : "-";
    }

    private (bool, string) ExplorerRegister(string app)
    {
        string output = RunMavue(app, "--register");
        var wrong = new List<string>();
        foreach (string e in new[] { ".png", ".jpg", ".gif", ".svg", ".heic", ".webp", ".mp4", ".mkv", ".wav", ".mp3" })
        {
            if (Handler(e, PreviewIid) != MavuePreviewClsid)
            {
                wrong.Add($"preview {e}={Handler(e, PreviewIid)}");
            }
        }

        foreach (string e in new[] { ".pdf", ".svg" })
        {
            if (Handler(e, ThumbnailIid) != MavueThumbnailClsid)
            {
                wrong.Add($"thumbnail {e}={Handler(e, ThumbnailIid)}");
            }
        }

        foreach (string e in new[] { ".jpg", ".png", ".mp4", ".mp3" })
        {
            if (Handler(e, ThumbnailIid) == MavueThumbnailClsid)
            {
                wrong.Add($"thumbnail {e} taken over");
            }
        }

        string pdfPreview = Handler(".pdf", PreviewIid);
        if (pdfPreview == MavuePreviewClsid)
        {
            wrong.Add("PDF preview taken over without --prefer-mavue-preview");
        }

        string surrogate = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppID\{E2B69F32-83E0-4DF1-BC8A-0D556307129F}")?.GetValue("DllSurrogate", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "-";
        string firstLines = string.Join(" | ", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Skip(1).Take(2));
        return (wrong.Count == 0, $"{firstLines}; .pdf preview stays {pdfPreview}; preview surrogate {surrogate}" + (wrong.Count > 0 ? "; WRONG: " + string.Join(", ", wrong) : string.Empty));
    }

    // ------------------------------------------------------------------ trace

    private List<JsonElement> ShellEvents()
    {
        if (!File.Exists(_shellTrace))
        {
            return [];
        }

        using var stream = new FileStream(_shellTrace, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var events = new List<JsonElement>();
        foreach (string line in reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                events.Add(JsonDocument.Parse(line).RootElement.Clone());
            }
            catch (JsonException)
            {
                // a line being written
            }
        }

        return events;
    }

    private static string EventName(JsonElement e) => e.GetProperty("event").GetString() ?? string.Empty;

    private static long Qpc(JsonElement e) => e.GetProperty("qpc").GetInt64();

    private JsonElement? WaitShell(long since, int timeoutMs, Func<JsonElement, bool> match)
    {
        JsonElement? found = null;
        WaitFor(() => (found = ShellEvents().Where(e => Qpc(e) >= since).Cast<JsonElement?>().FirstOrDefault(e => match(e!.Value))) is not null, timeoutMs, null);
        return found;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) ? v.ToString() : "?";

    // ------------------------------------------------------------------ preview pane

    private nint PreviewWindowIn(nint explorer) =>
        Native.ChildWindows(explorer).FirstOrDefault(w => Native.ClassName(w) == PreviewWindowClass && Native.IsWindowVisible(w));

    private static (int R, int G, int B) ScreenRgb(int x, int y)
    {
        nint dc = Native.GetDC(0);
        try
        {
            uint c = Native.GetPixel(dc, x, y);
            return ((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));
        }
        finally
        {
            Native.ReleaseDC(0, dc);
        }
    }

    private static string Rgb((int R, int G, int B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool Near((int R, int G, int B) c, (int R, int G, int B) expected) =>
        Math.Abs(c.R - expected.R) <= 10 && Math.Abs(c.G - expected.G) <= 10 && Math.Abs(c.B - expected.B) <= 10;

    /// <summary>Explorer's UI thread answers WM_NULL; the longest round trip while <paramref name="body"/> runs.</summary>
    private static (T Result, double MaxMs) WhileTimingExplorer<T>(nint explorer, Func<T> body)
    {
        double max = 0;
        bool stop = false;
        var pinger = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                long t = Stopwatch.GetTimestamp();
                Native.SendMessageTimeoutW(explorer, 0 /* WM_NULL */, 0, 0, 0x0002 /* SMTO_ABORTIFHUNG */, 5000, out _);
                max = Math.Max(max, Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                Thread.Sleep(40);
            }
        }) { IsBackground = true };
        pinger.Start();
        try
        {
            return (body(), max);
        }
        finally
        {
            Volatile.Write(ref stop, true);
            pinger.Join();
        }
    }

    private sealed record PreviewOutcome(string Kind, JsonElement? Start, JsonElement? Result, nint Window, double Ms);

    /// <summary>Selects <paramref name="file"/> and waits for Mavue's preview of it (shown, loaded media, or a message).</summary>
    private PreviewOutcome PreviewOf(dynamic window, nint explorer, string file, int timeoutMs = 8000)
    {
        long t = Stopwatch.GetTimestamp();
        Select(window, file, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        JsonElement? start = WaitShell(t, 3000, e => EventName(e) == "preview-start");
        if (start is null)
        {
            return new PreviewOutcome("none", null, null, 0, Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }

        long startQpc = Qpc(start.Value);
        JsonElement? result = WaitShell(startQpc, timeoutMs, e => EventName(e) is "preview-shown" or "preview-failed" ||
            (EventName(e) == "preview-loaded" && Str(e, "kind") == "media"));
        double ms = result is { } r ? (Qpc(r) - t) * 1000.0 / Stopwatch.Frequency : Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        Thread.Sleep(250); // the frame reaches the screen
        return new PreviewOutcome(result is { } done ? EventName(done) : "timeout", start, result, PreviewWindowIn(explorer), ms);
    }

    private static (int X, int Y) Center(nint hwnd)
    {
        Native.GetWindowRect(hwnd, out Native.RECT r);
        return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }

    private (bool Ok, string Observed, nint Explorer, dynamic? Window, bool PaneWasOpen) ExplorerPreviewPane(ExplorerFiles f)
    {
        nint explorer = OpenExplorer(f.Red);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found", 0, null, true);
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        ForceForeground(explorer);
        var observed = new List<string>();
        bool ok = true;

        // The preview pane: open it with Alt+P when the first preview does not come (and close it again at the end).
        Select(window, f.Text, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        Thread.Sleep(300);
        PreviewOutcome red = PreviewOf(window, explorer, f.Red);
        bool paneWasOpen = red.Kind != "none";
        if (!paneWasOpen)
        {
            ForceForeground(explorer);
            SendChordIfForeground(explorer, VkMenu, 0x50);
            Thread.Sleep(1500);
            Select(window, f.Text, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
            Thread.Sleep(300);
            red = PreviewOf(window, explorer, f.Red);
        }

        var hosts = new HashSet<int>();
        void Host(PreviewOutcome o)
        {
            if (o.Start is { } s)
            {
                hosts.Add(s.GetProperty("pid").GetInt32());
            }
        }

        Host(red);
        (int R, int G, int B) redPixel = red.Window != 0 ? ScreenRgb(Center(red.Window).X, Center(red.Window).Y) : (0, 0, 0);
        bool redOk = red.Kind == "preview-shown" && redPixel.R > 180 && redPixel.G < 80 && redPixel.B < 80;
        observed.Add($"pane {(paneWasOpen ? "already open" : "opened with Alt+P")}; PNG: {red.Kind} {red.Ms:0} ms, center {Rgb(redPixel)}");
        ok &= redOk;

        (PreviewOutcome large, double largePing) = WhileTimingExplorer<PreviewOutcome>(explorer, () => (PreviewOutcome)PreviewOf(window, explorer, f.Large));
        Host(large);
        JsonElement? largeLoaded = large.Start is { } ls ? WaitShell(Qpc(ls), 100, e => EventName(e) == "preview-loaded") : null;
        observed.Add($"24 MP JPEG: {large.Kind} {large.Ms:0} ms (decoded {Str(largeLoaded ?? default, "decodedWidth")}x{Str(largeLoaded ?? default, "decodedHeight")} in {Str(largeLoaded ?? default, "ms")} ms), Explorer max {largePing:0} ms");
        ok &= large.Kind == "preview-shown" && large.Ms < 3000 && largePing < 1000;

        PreviewOutcome gif = PreviewOf(window, explorer, f.Gif);
        Host(gif);
        var colors = new HashSet<string>();
        if (gif.Window != 0)
        {
            (int x, int y) = Center(gif.Window);
            for (int i = 0; i < 16; i++)
            {
                (int R, int G, int B) c = ScreenRgb(x, y);
                colors.Add(c.R > 150 ? "red" : c.G > 150 ? "green" : c.B > 150 ? "blue" : "other");
                Thread.Sleep(100);
            }
        }

        observed.Add($"GIF: {gif.Kind}, colors {string.Join('/', colors)}");
        ok &= gif.Kind == "preview-shown" && colors.Count(c => c != "other") >= 2;

        PreviewOutcome svg = PreviewOf(window, explorer, f.Svg);
        Host(svg);
        (int R, int G, int B) svgPixel = svg.Window != 0 ? ScreenRgb(Center(svg.Window).X, Center(svg.Window).Y) : (0, 0, 0);
        observed.Add($"SVG: {svg.Kind}, center {Rgb(svgPixel)} (circle #FFCC00)");
        ok &= svg.Kind == "preview-shown" && svgPixel.R > 200 && svgPixel.G > 160 && svgPixel.B < 80;

        foreach (string codec in f.Codecs)
        {
            PreviewOutcome o = PreviewOf(window, explorer, codec);
            Host(o);
            observed.Add($"{Path.GetExtension(codec)}: {o.Kind} {o.Ms:0} ms");
            ok &= o.Kind == "preview-shown";
        }

        // Video: loads (first frame), plays with sound after a click on the play button.
        PreviewOutcome video = PreviewOf(window, explorer, f.Video, 10000);
        Host(video);
        string videoState = video.Result is { } vr ? $"video={Str(vr, "video")} audio={Str(vr, "audio")} {Str(vr, "width")}x{Str(vr, "height")} {Str(vr, "duration")} s" : "-";
        float? videoPeak = null;
        (int R, int G, int B) videoPixel = (0, 0, 0);
        if (video.Kind == "preview-loaded" && video.Window != 0 && video.Start is { } vs)
        {
            ClickPlay(video.Window);
            videoPeak = PreviewHostPeak(vs.GetProperty("pid").GetInt32(), 2500);
            nint videoChild = Native.ChildWindows(video.Window).FirstOrDefault(w => Native.ClassName(w) == "Mavue.Shell.Preview.Video");
            if (videoChild != 0)
            {
                videoPixel = ScreenRgb(Center(videoChild).X, Center(videoChild).Y);
            }
        }

        observed.Add($"MP4: {video.Kind} {video.Ms:0} ms ({videoState}), play → peak {videoPeak:0.000}, picture {Rgb(videoPixel)}");
        ok &= video.Kind == "preview-loaded" && videoPeak > 0.02 && videoPixel.R > 150 && videoPixel.G < 100;

        // Other containers and codecs (MP3, M4A, WMA, FLAC, WMV): loaded, with Explorer responsive.
        foreach (string file in f.MediaFormats)
        {
            ForceForeground(explorer);
            (PreviewOutcome o, double ping) = WhileTimingExplorer<PreviewOutcome>(explorer, () => (PreviewOutcome)PreviewOf(window, explorer, file, 10000));
            Host(o);
            Thread.Sleep(1500);
            nint foreground = Native.GetForegroundWindow();
            bool kept = foreground == explorer; // a preview never takes the foreground (Explorer keeps the keyboard)
            observed.Add($"{Path.GetExtension(file)}: {o.Kind} {o.Ms:0} ms (video={Str(o.Result ?? default, "video")}), Explorer max {ping:0} ms, foreground {(kept ? "Explorer" : Native.Describe(foreground))}");
            ok &= o.Kind == "preview-loaded" && ping < 1000 && kept;
        }

        PreviewOutcome audio = PreviewOf(window, explorer, f.Audio);
        Host(audio);
        float? stoppedPeak = video.Start is { } stopped ? PreviewHostPeak(stopped.GetProperty("pid").GetInt32(), 600, quiet: true) : null;
        float? audioPeak = null;
        if (audio.Kind == "preview-loaded" && audio.Window != 0 && audio.Start is { } au)
        {
            ClickPlay(audio.Window);
            audioPeak = PreviewHostPeak(au.GetProperty("pid").GetInt32(), 2500);
        }

        observed.Add($"previous video stopped (peak {stoppedPeak:0.000}); WAV: {audio.Kind} audio={Str(audio.Result ?? default, "audio")}, play → peak {audioPeak:0.000}");
        ok &= audio.Kind == "preview-loaded" && audioPeak > 0.02 && stoppedPeak < 0.01;

        // Damaged and empty files: a message, quickly, with Explorer responsive.
        (PreviewOutcome broken, double brokenPing) = WhileTimingExplorer<PreviewOutcome>(explorer, () => (PreviewOutcome)PreviewOf(window, explorer, f.BrokenJpeg));
        PreviewOutcome empty = PreviewOf(window, explorer, f.EmptyPng);
        Host(broken);
        Host(empty);
        observed.Add($"damaged JPEG: {broken.Kind} (message {Str(broken.Result ?? default, "message")}) {broken.Ms:0} ms, Explorer max {brokenPing:0} ms; empty PNG: {empty.Kind} (message {Str(empty.Result ?? default, "message")})");
        ok &= broken.Kind == "preview-failed" && Str(broken.Result ?? default, "message") == "101" && broken.Ms < 3000 && brokenPing < 1000 &&
            empty.Kind == "preview-failed" && Str(empty.Result ?? default, "message") == "107";

        // Other types keep their previewers: Mavue is not called.
        PreviewOutcome text = PreviewOf(window, explorer, f.Text);
        PreviewOutcome pdf = PreviewOf(window, explorer, f.Pdf);
        observed.Add($"text file: {(text.Kind == "none" ? "not Mavue" : text.Kind)}, PDF: {(pdf.Kind == "none" ? "not Mavue (" + Handler(".pdf", PreviewIid) + ")" : pdf.Kind)}");
        ok &= text.Kind == "none" && pdf.Kind == "none";

        string hostInfo = string.Join(", ", hosts.Select(pid =>
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                return $"{p.ProcessName} {pid} alive";
            }
            catch (ArgumentException)
            {
                return $"{pid} gone";
            }
        }));
        observed.Add($"preview host: {hostInfo}");
        ok &= hosts.Count == 1 && !hostInfo.Contains("gone", StringComparison.Ordinal) && hostInfo.Contains("prevhost", StringComparison.OrdinalIgnoreCase);
        return (ok, string.Join("; ", observed), explorer, window, paneWasOpen);
    }

    private (bool, string) ExplorerPreviewKeyboard(ExplorerFiles f, nint explorer, dynamic window)
    {
        var observed = new List<string>();
        bool ok = true;
        var order = f.MediaFormats.Order(StringComparer.Ordinal).ToList();
        if (order.Count == 0)
        {
            return (false, "no media files");
        }

        Select(window, order[0], SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        ForceForeground(explorer);
        Thread.Sleep(1500);
        // The files sorted by name: 06 clip.mp4, 06 tone.mp3 / 01 tone.m4a …, 07 tone.wav — ↓ walks the folder order.
        string[] folderOrder = Directory.GetFiles(f.Folder).Order(StringComparer.Ordinal).ToArray();
        int index = Array.IndexOf(folderOrder, order[0]);
        for (int step = 1; step <= 7 && index + step < folderOrder.Length; step++)
        {
            string expected = folderOrder[index + step];
            long t = Stopwatch.GetTimestamp();
            bool sent = SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
            string selected = "?";
            bool moved = WaitFor(() =>
            {
                try
                {
                    foreach (dynamic item in window.Document.SelectedItems())
                    {
                        selected = Path.GetFileName((string)item.Path);
                    }
                }
                catch (COMException)
                {
                }

                return selected == Path.GetFileName(expected);
            }, 3000, null);
            JsonElement? preview = WaitShell(t, 3000, e => EventName(e) is "preview-shown" or "preview-failed" || (EventName(e) == "preview-loaded" && Str(e, "kind") == "media"));
            (nint focus, string focusClass) = Native.Focus(explorer);
            observed.Add($"↓ {Path.GetFileName(expected)}: sent={sent} selected={moved} ({Stopwatch.GetElapsedTime(t).TotalMilliseconds:0} ms) preview={(preview is { } p ? EventName(p) : "-")} focus={focusClass}");
            ok &= sent && moved;
            Thread.Sleep(1200);
        }

        // Fast: the next ↓ as soon as the previous file's preview has started (as when Quick View is followed by key).
        Select(window, order[0], SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        ForceForeground(explorer);
        Thread.Sleep(1500);
        var fast = new List<string>();
        for (int step = 1; step <= 7 && index + step < folderOrder.Length; step++)
        {
            string expected = folderOrder[index + step];
            long t = Stopwatch.GetTimestamp();
            bool sent = SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
            string selected = "?";
            bool moved = WaitFor(() =>
            {
                try
                {
                    foreach (dynamic item in window.Document.SelectedItems())
                    {
                        selected = Path.GetFileName((string)item.Path);
                    }
                }
                catch (COMException)
                {
                }

                return selected == Path.GetFileName(expected);
            }, 3000, null);
            JsonElement? started = WaitShell(t, 1500, e => EventName(e) == "preview-start");
            Thread.Sleep(started is { } st ? 0 : 200);
            fast.Add($"{Path.GetFileName(expected)} {(moved ? "ok" : $"LOST (selected {selected})")}");
            ok &= sent && moved;
            if (!moved)
            {
                Select(window, expected, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
                ForceForeground(explorer);
                Thread.Sleep(500);
            }
        }

        observed.Add("fast ↓: " + string.Join(", ", fast));
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) ExplorerShortcuts(ExplorerFiles f, nint explorer, dynamic window)
    {
        var observed = new List<string>();
        var opens = new Dictionary<string, bool>();
        foreach ((string file, string who) in new[] { (f.Text, "Windows TXT"), (f.Pdf, "Edge PDF"), (f.Red, "Mavue PNG") })
        {
            Select(window, file, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
            Thread.Sleep(2000);
            bool forced = false;
            if (Native.GetForegroundWindow() != explorer)
            {
                forced = ForceForeground(explorer); // harness crutch (AttachThreadInput); avoided when Explorer is already in front
            }

            int before = ShellWindowsFor(explorer).Count;
            bool sent = SendChord(explorer, VkControl, VkT);
            bool opened = WaitFor(() => ShellWindowsFor(explorer).Count > before, 4000, null);
            (nint focus, string focusClass) = Native.Focus(explorer);
            observed.Add($"{who}: Ctrl+T sent={sent} tab={opened} (focus {focusClass}, forced foreground={forced})");
            opens[who] = opened;

            if (opened)
            {
                Thread.Sleep(500);
                SendChord(explorer, VkControl, 0x57); // Ctrl+W closes the new tab
                Thread.Sleep(800);
                window = ShellWindowFor(explorer) ?? window;
            }
        }

        // Injected (SendInput) chords do nothing while any out-of-process previewer is attached to Explorer's window;
        // physical keys work (checked by hand). Mavue's previewer must behave like Windows' own.
        bool ok = opens["Mavue PNG"] == opens["Windows TXT"];
        return (ok, string.Join("; ", observed) + "; Mavue like Windows' previewer=" + ok);
    }

    /// <summary>Runs a helper script in 32-bit PowerShell (SysWOW64); returns its output.</summary>
    private static string Run32(string script, string arguments)
    {
        string powershell = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe");
        using Process ps = Process.Start(new ProcessStartInfo(powershell, $"-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File \"{Path.Combine(AppContext.BaseDirectory, "scripts", script)}\" {arguments}")
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        string output = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit(60000);
        return output;
    }

    private static string ImagePath(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.MainModule?.FileName ?? "?";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "?";
        }
    }

    private (bool, string) Explorer32Bit(ExplorerFiles f)
    {
        // ASCII names in a fresh folder (PowerShell 5 arguments; nothing in the thumbnail cache).
        string folder = Path.Combine(options.WorkDirectory, $"mavue32-{DateTime.Now:HHmmss}");
        Directory.CreateDirectory(folder);
        File.Copy(f.Pdf, Path.Combine(folder, "doc.pdf"));
        File.Copy(f.Svg, Path.Combine(folder, "drawing.svg"));
        string photo = Path.Combine(folder, "photo.jpg");
        File.Copy(f.Large, photo);

        long t = Stopwatch.GetTimestamp();
        string thumbs = Run32("thumbnails.ps1", $"-Folder \"{folder}\"");
        List<JsonElement> thumbEvents = ShellEvents().Where(e => Qpc(e) >= t && EventName(e) == "thumbnail").ToList();
        bool thumbsIn32 = thumbEvents.Count >= 2 && thumbEvents.All(e => ImagePath(e.GetProperty("pid").GetInt32()) is var path && (path == "?" || path.Contains("SysWOW64", StringComparison.OrdinalIgnoreCase)));
        bool thumbsOk = thumbs.Contains("doc.pdf        181x256", StringComparison.Ordinal) && thumbs.Contains("drawing.svg", StringComparison.Ordinal) && !thumbs.Contains("drawing.svg    hr=", StringComparison.Ordinal);

        t = Stopwatch.GetTimestamp();
        string preview = Run32("preview-out-of-process.ps1", $"-File \"{photo}\"");
        JsonElement? loaded = WaitShell(t, 3000, e => EventName(e) == "preview-loaded");
        JsonElement? start = WaitShell(t, 100, e => EventName(e) == "preview-start");
        string thumbLines = string.Join(" | ", thumbs.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("32-bit", StringComparison.Ordinal)));
        string observed = $"32-bit thumbnails: {thumbLines} (Mavue events {thumbEvents.Count}, 32-bit process={thumbsIn32}); 32-bit preview: {preview.Split('\n').LastOrDefault()?.Trim()}, loaded {Str(loaded ?? default, "kind")} {Str(loaded ?? default, "decodedWidth")} px in another prevhost={start is { } s && s.GetProperty("pid").GetInt32() != 0}";
        return (thumbsOk && thumbsIn32 && loaded is not null && preview.Contains("DoPreview 0x00000000", StringComparison.Ordinal), observed);
    }

    private (bool, string) ExplorerMonitors(ExplorerFiles f, nint explorer, dynamic window)
    {
        List<Native.MonitorInfo> monitors = Native.Monitors();
        var observed = new List<string>();
        bool ok = monitors.Count > 0;
        void Place(Native.MonitorInfo m)
        {
            int w = (m.Work.Right - m.Work.Left) * 8 / 10;
            int h = (m.Work.Bottom - m.Work.Top) * 8 / 10;
            Native.SetWindowPos(explorer, 0, m.Work.Left + ((m.Work.Right - m.Work.Left - w) / 2), m.Work.Top + ((m.Work.Bottom - m.Work.Top - h) / 2), w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Thread.Sleep(1200);
            ForceForeground(explorer);
        }

        bool Fits(nint preview)
        {
            Native.GetWindowRect(preview, out Native.RECT p);
            Native.GetWindowRect(explorer, out Native.RECT e);
            return p.Right - p.Left > 100 && p.Bottom - p.Top > 100 && p.Left >= e.Left && p.Right <= e.Right && p.Top >= e.Top && p.Bottom <= e.Bottom;
        }

        foreach (Native.MonitorInfo m in monitors)
        {
            Place(m);
            Select(window, f.Text, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
            Thread.Sleep(500);
            PreviewOutcome o = PreviewOf(window, explorer, f.Red);
            WaitFor(() => o.Window != 0 && Fits(o.Window), 3000, null); // Explorer may still be laying out the pane after the move
            int dpi = o.Start is { } st && st.TryGetProperty("dpi", out JsonElement d) ? d.GetInt32() : 0;
            (int R, int G, int B) center = o.Window != 0 ? ScreenRgb(Center(o.Window).X, Center(o.Window).Y) : (0, 0, 0);
            bool fits = o.Window != 0 && Fits(o.Window);
            string rects = "-";
            if (o.Window != 0)
            {
                Native.GetWindowRect(o.Window, out Native.RECT pr);
                Native.GetWindowRect(explorer, out Native.RECT er);
                rects = $"preview [{pr.Left},{pr.Top},{pr.Right},{pr.Bottom}] in explorer [{er.Left},{er.Top},{er.Right},{er.Bottom}], client {Str(o.Result ?? default, "clientWidth")}x{Str(o.Result ?? default, "clientHeight")}";
                Screenshot(explorer, Path.Combine(options.WorkDirectory, $"explorer-monitor-{m.Dpi}-{m.Bounds.Left}.png"));
            }

            observed.Add($"{m.Bounds.Right - m.Bounds.Left}x{m.Bounds.Bottom - m.Bounds.Top}@{m.Dpi}: preview dpi {dpi}, fits pane={fits}, center {Rgb(center)} ({rects})");
            ok &= o.Kind == "preview-shown" && dpi == m.Dpi && fits && center.R > 180 && center.G < 80;
        }

        // Moving the window to another DPI while the PNG is previewed.
        Native.MonitorInfo? other = monitors.Count > 1 ? monitors.FirstOrDefault(m => m.Dpi != monitors[^1].Dpi) : null;
        if (other is not null)
        {
            long t = Stopwatch.GetTimestamp();
            Place(other);
            JsonElement? changed = WaitShell(t, 3000, e => EventName(e) == "preview-dpi");
            nint preview = PreviewWindowIn(explorer);
            WaitFor(() => preview != 0 && Fits(preview), 3000, null);
            (int R, int G, int B) center = preview != 0 ? ScreenRgb(Center(preview).X, Center(preview).Y) : (0, 0, 0);
            bool fits = preview != 0 && Fits(preview);
            observed.Add($"moved to @{other.Dpi} while shown: dpi change seen={(changed is { } c ? Str(c, "dpi") : "no")}, fits pane={fits}, center {Rgb(center)}");
            ok &= changed is not null && Str(changed.Value, "dpi") == other.Dpi.ToString(System.Globalization.CultureInfo.InvariantCulture) && fits && center.R > 180 && center.G < 80;
        }

        // Back to the first monitor, and settled (the preview has taken that DPI) before the next scenario starts.
        long back = Stopwatch.GetTimestamp();
        Place(monitors[0]);
        if (other is not null && other.Dpi != monitors[0].Dpi)
        {
            WaitShell(back, 3000, e => EventName(e) == "preview-dpi");
        }

        Thread.Sleep(500);
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) ExplorerTheme(ExplorerFiles f, nint explorer, dynamic window)
    {
        bool dark = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") is 0;
        var observed = new List<string> { $"Windows apps theme: {(dark ? "dark" : "light")}" };
        bool ok = true;
        int index = 0;
        foreach ((string file, string label) in new[] { (f.Red, "PNG"), (f.Svg, "SVG"), (f.Audio, "WAV"), (f.BrokenJpeg, "damaged") })
        {
            ForceForeground(explorer);
            Select(window, f.Text, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused); // a new preview even if the file was selected
            Thread.Sleep(400);
            long t = Stopwatch.GetTimestamp();
            PreviewOutcome o = PreviewOf(window, explorer, file);
            Thread.Sleep(400);
            List<JsonElement> visuals = ShellEvents().Where(e => Qpc(e) >= t && EventName(e) == "preview-visuals").ToList();
            string given = string.Join(",", visuals.Select(v => v.TryGetProperty("background", out JsonElement b) ? $"bg #{Bgr(b.GetUInt32())}" : v.TryGetProperty("text", out JsonElement x) ? $"text #{Bgr(x.GetUInt32())}" : "?"));
            (int R, int G, int B) background = (0, 0, 0);
            if (o.Window != 0)
            {
                Native.GetWindowRect(o.Window, out Native.RECT r);
                background = ScreenRgb(r.Left + 3, r.Top + 3);
                string shot = Path.Combine(options.WorkDirectory, $"explorer-theme-{(dark ? "dark" : "light")}-{index++}-{label}.png");
                Screenshot(explorer, shot);
            }

            double luminance = (0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B);
            bool matches = dark ? luminance < 90 : luminance > 170;
            observed.Add($"{label}: {o.Kind}, background {Rgb(background)} (given {given})");
            ok &= o.Window != 0 && matches;
        }

        observed.Add("screenshots in " + options.WorkDirectory);
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) LightTheme(ExplorerFiles f, nint explorer, dynamic window)
    {
        const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        object? before = Registry.CurrentUser.OpenSubKey(Personalize)?.GetValue("AppsUseLightTheme");
        void SetAppsTheme(int light)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Personalize, writable: true))
            {
                key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
            }

            Native.SendMessageTimeoutW(0xFFFF /* HWND_BROADCAST */, 0x001A /* WM_SETTINGCHANGE */, 0, Marshal.StringToHGlobalUni("ImmersiveColorSet"), 0x0002, 3000, out _);
            Thread.Sleep(2500);
        }

        var observed = new List<string>();
        bool ok;
        try
        {
            SetAppsTheme(1);
            (bool previewOk, string preview) = ((bool, string))ExplorerTheme(f, explorer, window);
            observed.Add(preview);
            ok = previewOk;

            // Quick View (the resident host if one runs, else this build's) on the red PNG.
            string host = File.Exists(options.HostPath) ? options.HostPath : string.Empty;
            bool residentBefore = Process.GetProcessesByName("Mavue.QuickView.Host").Length > 0;
            if (host.Length > 0)
            {
                using Process? client = Process.Start(new ProcessStartInfo(host) { UseShellExecute = false, ArgumentList = { "--quickview", f.Red } });
                client?.WaitForExit(10000);
                nint qv = 0;
                WaitFor(() => (qv = Native.TopLevelWindows().FirstOrDefault(w => Native.IsWindowVisible(w) && Native.ClassName(w) == "WinUIDesktopWin32WindowClass" &&
                    Process.GetProcessById((int)Pid(w)).ProcessName == "Mavue.QuickView.Host")) != 0, 8000, null);
                Thread.Sleep(1500);
                (int R, int G, int B) corner = (0, 0, 0);
                if (qv != 0)
                {
                    Native.GetWindowRect(qv, out Native.RECT r);
                    corner = ScreenRgb(r.Left + 40, r.Top + ((r.Bottom - r.Top) / 2));
                    Screenshot(qv, Path.Combine(options.WorkDirectory, "theme-light-quickview.png"));
                    ForceForeground(qv);
                    SendKeyIfForeground(Native.VK_ESCAPE, qv, out _);
                }

                if (!residentBefore)
                {
                    // Without a resident host the --quickview client became one: end it (it would block later runs).
                    using Process? stop = Process.Start(new ProcessStartInfo(host) { UseShellExecute = false, ArgumentList = { "--shutdown" } });
                    stop?.WaitForExit(10000);
                }

                bool light = corner.R > 200 && corner.G > 200 && corner.B > 200;
                observed.Add($"Quick View: background {Rgb(corner)} light={light}");
                ok &= qv != 0 && light;
            }

            using (AppSession app = LaunchApp(f.Red))
            {
                app.WaitForState(st => st.Text("kind") == "Image", 15000);
                Thread.Sleep(1500);
                Native.GetWindowRect(app.Window, out Native.RECT r);
                (int R, int G, int B) corner = ScreenRgb(r.Left + 30, r.Top + ((r.Bottom - r.Top) / 2));
                Screenshot(app.Window, Path.Combine(options.WorkDirectory, "theme-light-app.png"));
                bool light = corner.R > 200 && corner.G > 200 && corner.B > 200;
                observed.Add($"Mavue: background {Rgb(corner)} light={light}");
                ok &= light;
                app.Close(expectPlayerStop: false);
            }
        }
        finally
        {
            SetAppsTheme(before is int value ? value : 0);
        }

        observed.Add($"theme restored to AppsUseLightTheme={before}");
        return (ok, string.Join("; ", observed));
    }

    private static string Bgr(uint colorref) => $"{colorref & 0xFF:X2}{(colorref >> 8) & 0xFF:X2}{(colorref >> 16) & 0xFF:X2}";

    /// <summary>A PNG of a window as on screen (review aid; physical pixels, scaled down to at most 1600 px wide).</summary>
    private static void Screenshot(nint hwnd, string path)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "scripts", "screenshot.ps1");
        if (!File.Exists(script))
        {
            return;
        }

        using Process? ps = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Out \"{path}\" -Hwnd {hwnd}") { UseShellExecute = false, CreateNoWindow = true });
        ps?.WaitForExit(20000);
    }

    /// <summary>The play button of Mavue's media preview (bottom left, as laid out by PreviewWindow::LayoutMedia).</summary>
    private static void ClickPlay(nint preview)
    {
        Native.GetWindowRect(preview, out Native.RECT r);
        double scale = Native.GetDpiForWindow(preview) / 96.0;
        int x = r.Left + (int)((8 + 18) * scale);
        int y = r.Bottom - (int)(22 * scale);
        Click((x, y));
    }

    private static float? PreviewHostPeak(int pid, int ms, bool quiet = false)
    {
        float? best = null;
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ms)
        {
            if (AudioMeter.PeakForProcess((uint)pid) is { } now)
            {
                best = Math.Max(best ?? 0, now);
                if (!quiet && best > 0.05)
                {
                    break;
                }
            }

            Thread.Sleep(40);
        }

        return best ?? 0;
    }

    // ------------------------------------------------------------------ PDF (taken over on request)

    private (bool, string) ExplorerPreviewPdf(string app, ExplorerFiles f, nint explorer, dynamic window)
    {
        var observed = new List<string>();
        Select(window, f.Text, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        string output = RunMavue(app, "--register", "--prefer-mavue-preview");
        string taken = Handler(".pdf", PreviewIid);
        observed.Add($"--prefer-mavue-preview: .pdf preview {(taken == MavuePreviewClsid ? "Mavue" : taken)} ({output.Split('\n').Skip(1).FirstOrDefault()?.Trim()})");
        bool ok = taken == MavuePreviewClsid;

        PreviewOutcome doc = PreviewOf(window, explorer, f.Pdf, 10000);
        JsonElement? loaded = doc.Start is { } ds ? WaitShell(Qpc(ds), 2000, e => EventName(e) == "preview-loaded") : null;
        JsonElement? firstPage = doc.Start is { } ds2 ? WaitShell(Qpc(ds2), 4000, e => EventName(e) == "preview-page" && Str(e, "drawn") == "True") : null;
        Thread.Sleep(300);
        (int R, int G, int B) paper = (0, 0, 0);
        bool scrolled = false;
        if (doc.Window != 0)
        {
            Native.GetWindowRect(doc.Window, out Native.RECT r);
            // Inside page 1's tinted area (the page is fitted to the pane width; its tint starts 40 pt from the edges).
            int pageWidth = int.Parse(Str(firstPage ?? default, "width") is var w && w != "?" ? w : "0", System.Globalization.CultureInfo.InvariantCulture);
            int pageTop = r.Top + (int)(12 * Native.GetDpiForWindow(doc.Window) / 96.0);
            paper = ScreenRgb((r.Left + r.Right) / 2, pageTop + Math.Max(4, pageWidth * 80 / 595));
            long t = Stopwatch.GetTimestamp();
            Native.SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            Thread.Sleep(150);
            for (int i = 0; i < 40; i++)
            {
                Native.MouseEvent(0x0800, 0, 0, unchecked((uint)-120), 0);
                Thread.Sleep(30);
            }

            scrolled = WaitShell(t, 3000, e => EventName(e) == "preview-page" && int.Parse(Str(e, "page"), System.Globalization.CultureInfo.InvariantCulture) >= 2) is not null;
        }

        observed.Add($"8-page PDF: {doc.Kind} {doc.Ms:0} ms, pages {Str(loaded ?? default, "pages")}, first page drawn {Str(firstPage ?? default, "width")}x{Str(firstPage ?? default, "height")} in {Str(firstPage ?? default, "ms")} ms, paper {Rgb(paper)}, wheel → later pages drawn={scrolled}");
        ok &= doc.Kind == "preview-shown" && Str(loaded ?? default, "pages") == "8" && firstPage is not null && Near(paper, (0xD9, 0xEB, 0xFA)) && scrolled; // TestPdf fills page 1 with #D9EBFA

        if (f.Japanese is { } japanese)
        {
            PreviewOutcome jp = PreviewOf(window, explorer, japanese, 10000);
            JsonElement? jpPage = jp.Start is { } js ? WaitShell(Qpc(js), 4000, e => EventName(e) == "preview-page" && Str(e, "drawn") == "True") : null;
            observed.Add($"Japanese PDF: {jp.Kind}, page drawn={jpPage is not null}");
            ok &= jp.Kind == "preview-shown" && jpPage is not null;
        }

        (PreviewOutcome longDoc, double ping) = WhileTimingExplorer<PreviewOutcome>(explorer, () => (PreviewOutcome)PreviewOf(window, explorer, f.LongPdf, 10000));
        Thread.Sleep(1500);
        List<JsonElement> pages = longDoc.Start is { } lsd ? ShellEvents().Where(e => Qpc(e) >= Qpc(lsd) && EventName(e) == "preview-page").ToList() : [];
        JsonElement? longLoaded = longDoc.Start is { } lsd2 ? WaitShell(Qpc(lsd2), 100, e => EventName(e) == "preview-loaded") : null;
        long privateMb = 0;
        if (longDoc.Start is { } host)
        {
            try
            {
                using Process p = Process.GetProcessById(host.GetProperty("pid").GetInt32());
                privateMb = p.PrivateMemorySize64 / (1024 * 1024);
            }
            catch (ArgumentException)
            {
                privateMb = -1;
            }
        }

        int maxPage = pages.Count == 0 ? -1 : pages.Max(e => int.Parse(Str(e, "page"), System.Globalization.CultureInfo.InvariantCulture));
        observed.Add($"600-page PDF: {longDoc.Kind} {longDoc.Ms:0} ms (opened in {Str(longLoaded ?? default, "ms")} ms), {pages.Count} pages drawn (up to page {maxPage + 1}), preview host private {privateMb} MB, Explorer max {ping:0} ms");
        ok &= longDoc.Kind == "preview-shown" && Str(longLoaded ?? default, "pages") == "600" && pages.Count is > 0 and <= 6 && maxPage <= 4 && privateMb is > 0 and < 400 && ping < 1000;

        PreviewOutcome broken = PreviewOf(window, explorer, f.BrokenPdf);
        observed.Add($"damaged PDF: {broken.Kind} (message {Str(broken.Result ?? default, "message")})");
        ok &= broken.Kind == "preview-failed";

        Select(window, f.Text, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        RunMavue(app, "--register");
        string back = Handler(".pdf", PreviewIid);
        PreviewOutcome after = PreviewOf(window, explorer, f.Pdf);
        observed.Add($"--register again: .pdf preview {back}, Mavue called={after.Kind != "none"}");
        ok &= back != MavuePreviewClsid && back != "-" && after.Kind == "none";
        return (ok, string.Join("; ", observed));
    }

    // ------------------------------------------------------------------ thumbnails

    private (bool, string) ExplorerThumbnails(ExplorerFiles f, nint explorer, dynamic window)
    {
        var observed = new List<string>();
        bool ok = true;

        // Fresh copies in a folder no Explorer window shows: Windows' thumbnail cache has nothing for them yet.
        string fresh = Path.Combine(options.WorkDirectory, $"Mavue Explorer サムネイル {DateTime.Now:MMdd-HHmmss}");
        Directory.CreateDirectory(fresh);
        string Copy(string file)
        {
            string target = Path.Combine(fresh, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
            return target;
        }

        foreach ((string file, bool mavue, bool succeeds) in new[] { (Copy(f.Pdf), true, true), (Copy(f.Svg), true, true), (Copy(f.LongPdf), true, true), (Copy(f.BrokenPdf), true, false), (Copy(f.Red), false, true) })
        {
            long t = Stopwatch.GetTimestamp();
            (int hr, int width, int height) = ShellThumbnail(file, 256);
            double ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            JsonElement? e = WaitShell(t, 1500, x => EventName(x) == "thumbnail");
            string process = "-";
            if (e is { } ev)
            {
                try
                {
                    using Process p = Process.GetProcessById(ev.GetProperty("pid").GetInt32());
                    process = p.ProcessName;
                }
                catch (ArgumentException)
                {
                    process = "gone";
                }
            }

            observed.Add($"{Path.GetFileName(file)}: {(hr == 0 ? $"{width}x{height}" : $"0x{hr:X8}")} {ms:0} ms ({(e is null ? "not Mavue" : $"Mavue {Str(e.Value, "kind")} in {process}")})");
            ok &= (hr == 0) == succeeds && (e is not null) == mavue && (!mavue || process.Equals("dllhost", StringComparison.OrdinalIgnoreCase)) && ms < 3000;
        }

        // On screen: Explorer's large icons show the SVG drawn by Mavue (blue background, yellow circle).
        window.Document.CurrentViewMode = 1; // FVM_ICON
        window.Document.IconSize = 256;
        Select(window, f.Svg, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        ForceForeground(explorer);
        Thread.Sleep(2500);
        string rect = UiaRect(explorer, "04 drawing");
        string seen = "-";
        bool onScreen = false;
        if (rect.Split(',') is [string l, string tp, string w, string h] && int.TryParse(l, out int left) && int.TryParse(tp, out int top) && int.TryParse(w, out int width2) && int.TryParse(h, out int height2))
        {
            var found = new HashSet<string>();
            for (int dy = 15; dy <= 70; dy += 5)
            {
                (int R, int G, int B) c = ScreenRgb(left + (width2 / 2), top + (height2 * dy / 100));
                if (c.B > 150 && c.R < 80)
                {
                    found.Add("blue");
                }
                else if (c.R > 200 && c.G > 160 && c.B < 80)
                {
                    found.Add("yellow");
                }
            }

            seen = string.Join('/', found);
            onScreen = found.Count == 2;
        }

        observed.Add($"large icons: SVG thumbnail on screen {seen} (item {rect})");
        ok &= onScreen;
        window.Document.CurrentViewMode = FvmDetails;
        return (ok, string.Join("; ", observed));
    }

    /// <summary>A thumbnail only (no icon) through the shell, as Explorer gets it (thumbnail cache, isolated provider).</summary>
    private static (int Hr, int Width, int Height) ShellThumbnail(string path, int size)
    {
        Guid iid = typeof(IShellItemImageFactory).GUID;
        int hr = SHCreateItemFromParsingName(path, 0, ref iid, out IShellItemImageFactory factory);
        if (hr != 0)
        {
            return (hr, 0, 0);
        }

        hr = factory.GetImage(new NativeSize { Cx = size, Cy = size }, 0x8 /* SIIGBF_THUMBNAILONLY */, out nint bitmap);
        Marshal.ReleaseComObject(factory);
        if (hr != 0)
        {
            return (hr, 0, 0);
        }

        Native.GetObjectW(bitmap, Marshal.SizeOf<Native.BITMAP>(), out Native.BITMAP info);
        Native.DeleteGdiObject(bitmap);
        return (0, info.bmWidth, info.bmHeight);
    }

    /// <summary>Screen rectangle "left,top,width,height" (physical pixels) of the Explorer item whose name starts with the prefix.</summary>
    private static string UiaRect(nint explorer, string namePrefix)
    {
        string prefix = namePrefix.Replace("'", "''", StringComparison.Ordinal);
        string script = $$"""
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            Add-Type -Namespace Dpi -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr value);'
            [void][Dpi.Native]::SetProcessDpiAwarenessContext([IntPtr](-4))
            Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
            $A = [System.Windows.Automation.AutomationElement]
            $win = $A::FromHandle([IntPtr]{{explorer}})
            $items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
            foreach ($item in $items) { if ($item.Current.Name.StartsWith('{{prefix}}')) { $r = $item.Current.BoundingRectangle; Write-Output ("{0},{1},{2},{3}" -f [int]$r.Left, [int]$r.Top, [int]$r.Width, [int]$r.Height); exit 0 } }
            Write-Output "missing"
            """;
        string file = Path.Combine(Path.GetTempPath(), $"mavue-uia-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            using Process ps = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8, CreateNoWindow = true })!;
            string output = ps.StandardOutput.ReadToEnd().Trim();
            ps.WaitForExit(20000);
            return output;
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ------------------------------------------------------------------ register → restart → unregister

    private (bool, string) ExplorerLifecycle(string app, ExplorerFiles f)
    {
        var observed = new List<string>();
        RunMavue(app, "--register");

        // Restart File Explorer (all its processes), as after signing in again.
        Process[] explorers = Process.GetProcessesByName("explorer");
        foreach (Process p in explorers)
        {
            try
            {
                p.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // already gone
            }
        }

        _createdExplorers.Clear();
        Thread.Sleep(1500);
        if (Process.GetProcessesByName("explorer").Length == 0)
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }

        bool shellBack = WaitFor(() => Native.TopLevelWindows().Any(w => Native.ClassName(w) == "Shell_TrayWnd"), 30000, null);
        Thread.Sleep(4000);
        bool quickViewAlive = Process.GetProcessesByName("Mavue.QuickView.Host").Length > 0;
        observed.Add($"File Explorer restarted ({explorers.Length} processes), taskbar back={shellBack}, resident Quick View still running={quickViewAlive}");
        bool ok = shellBack;

        // After the restart: still Mavue's previews and thumbnails.
        (nint explorer, dynamic? window) = OpenFolderWindow(f.Folder);
        if (explorer == 0 || window is null)
        {
            return (false, string.Join("; ", observed) + "; Explorer window not found after the restart");
        }

        window.Document.CurrentViewMode = FvmDetails;
        ForceForeground(explorer);
        Select(window, f.Text, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        Thread.Sleep(500);
        PreviewOutcome red = PreviewOf(window, explorer, f.Red);
        if (red.Kind == "none")
        {
            ForceForeground(explorer);
            SendChordIfForeground(explorer, VkMenu, 0x50); // the pane was closed in the new Explorer
            Thread.Sleep(1500);
            Select(window, f.Text, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
            Thread.Sleep(300);
            red = PreviewOf(window, explorer, f.Red);
        }

        string svgAfterRestart = Path.Combine(f.Folder, "15 after restart.svg");
        File.Copy(f.Svg, svgAfterRestart, overwrite: true);
        long t = Stopwatch.GetTimestamp();
        (int hr, int w, int h) = ShellThumbnail(svgAfterRestart, 256);
        bool mavueThumb = WaitShell(t, 1500, e => EventName(e) == "thumbnail") is not null;
        observed.Add($"after restart: PNG preview {red.Kind}, new SVG thumbnail {(hr == 0 ? $"{w}x{h}" : $"0x{hr:X8}")} by Mavue={mavueThumb}");
        ok &= red.Kind == "preview-shown" && hr == 0 && mavueThumb;

        // Unregister: nothing of Mavue's left, Explorer stops calling it.
        Select(window, f.Text, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        string unregister = RunMavue(app, "--unregister");
        bool classes = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{MavuePreviewClsid}") is not null ||
            Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{MavueThumbnailClsid}") is not null;
        string pngPreview = Handler(".png", PreviewIid);
        string svgThumb = Handler(".svg", ThumbnailIid);
        string pdfPreview = Handler(".pdf", PreviewIid);
        PreviewOutcome gone = PreviewOf(window, explorer, f.Red);
        string svgAfterUnregister = Path.Combine(f.Folder, "16 after unregister.svg");
        File.Copy(f.Svg, svgAfterUnregister, overwrite: true);
        t = Stopwatch.GetTimestamp();
        (int hr2, _, _) = ShellThumbnail(svgAfterUnregister, 256);
        bool thumbCalled = WaitShell(t, 1500, e => EventName(e) == "thumbnail") is not null;
        observed.Add($"--unregister: \"{unregister.Split('\n').FirstOrDefault()?.Trim()}\", classes left={classes}, .png preview {pngPreview}, .svg thumbnail {svgThumb}, .pdf preview {pdfPreview} (unchanged), PNG preview by Mavue={gone.Kind != "none"}, SVG thumbnail 0x{hr2:X8} by Mavue={thumbCalled}");
        ok &= !classes && pngPreview == "-" && svgThumb == "-" && pdfPreview != "-" && gone.Kind == "none" && !thumbCalled;

        RunMavue(app, "--register");
        string again = Handler(".png", PreviewIid);
        observed.Add($"--register again: .png preview {(again == MavuePreviewClsid ? "Mavue" : again)}");
        ok &= again == MavuePreviewClsid;
        return (ok, string.Join("; ", observed));
    }

    private (nint Hwnd, dynamic? Window) OpenFolderWindow(string folder)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        shell.Open(folder);
        nint hwnd = 0;
        dynamic? found = null;
        WaitFor(() =>
        {
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if (string.Equals(w.Document?.Folder?.Self?.Path as string, folder, StringComparison.OrdinalIgnoreCase))
                    {
                        hwnd = (nint)(long)w.HWND;
                        found = w;
                        return true;
                    }
                }
                catch (COMException)
                {
                    // a window that is closing
                }
            }

            return false;
        }, 20000, null);
        if (hwnd != 0)
        {
            _createdExplorers.Add(hwnd);
        }

        return (hwnd, found);
    }

    // ------------------------------------------------------------------ interop

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out nint bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Cx;
        public int Cy;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid riid, out IShellItemImageFactory item);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(uint flags, int str, string association, string extra, [Out] char[] output, ref uint length);
}

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static partial int GetObjectW(nint handle, int size, out BITMAP bitmap);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteGdiObject(nint handle);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint SendMessageTimeoutW(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);

    [LibraryImport("user32.dll")]
    private static unsafe partial int EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [ThreadStatic]
    private static List<nint>? s_children;

    [UnmanagedCallersOnly]
    private static int ChildCallback(nint hwnd, nint lParam)
    {
        s_children!.Add(hwnd);
        return 1;
    }

    /// <summary>All descendant windows (including other processes' windows parented into this one).</summary>
    public static unsafe List<nint> ChildWindows(nint parent)
    {
        s_children = [];
        EnumChildWindows(parent, &ChildCallback, 0);
        List<nint> result = s_children;
        s_children = null;
        return result;
    }
}

/// <summary>Diagnostics: every keyboard focus change (EVENT_OBJECT_FOCUS / EVENT_SYSTEM_FOREGROUND) while it runs.</summary>
internal sealed class FocusRecorder : IDisposable
{
    private static readonly List<string> s_events = [];
    private readonly Thread _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _started = new();

    public FocusRecorder()
    {
        lock (s_events)
        {
            s_events.Clear();
        }

        _thread = new Thread(Run) { IsBackground = true };
        _thread.Start();
        _started.Wait(2000);
    }

    public static IReadOnlyList<string> Events
    {
        get
        {
            lock (s_events)
            {
                return s_events.ToArray();
            }
        }
    }

    private unsafe void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        nint focusHook = Native.SetWinEventHook(0x8005 /* EVENT_OBJECT_FOCUS */, 0x8005, 0, &Callback, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
        nint foregroundHook = Native.SetWinEventHook(0x0003 /* EVENT_SYSTEM_FOREGROUND */, 0x0003, 0, &Callback, 0, 0, 0);
        _started.Set();
        while (Native.GetMessageW(out Native.MSG message, 0, 0, 0) > 0)
        {
            _ = message;
        }

        Native.UnhookWinEvent(focusHook);
        Native.UnhookWinEvent(foregroundHook);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void Callback(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        string text = $"{Stopwatch.GetTimestamp()} {(eventType == 3 ? "foreground" : "focus")} {Native.ClassName(hwnd)} {Native.Describe(hwnd)}";
        lock (s_events)
        {
            s_events.Add(text);
        }
    }

    public void Dispose()
    {
        Native.PostThreadMessageW(_threadId, 0x0012 /* WM_QUIT */, 0, 0);
        _thread.Join(2000);
        _started.Dispose();
    }
}

internal static partial class Native
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    public static unsafe partial nint SetWinEventHook(uint min, uint max, nint module, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint process, uint thread, uint flags);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    public static partial int GetMessageW(out MSG message, nint hwnd, uint min, uint max);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
}
