using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Imaging;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Mavue.App (the main viewer) on the real desktop (<c>--app</c>): every main format opened from the command line,
/// ←/→ through a folder with images, PDF, video, audio and a damaged file, PDF page keys and wheel, media keys,
/// several files on the command line, the Open dialog, and release of the player and the file when the window closes.
/// Checks use the app's <c>--trace-file</c> log, screen pixels and the app's audio session.
/// </summary>
internal sealed partial class Runner
{
    private const string WinUiWindowClass = "WinUIDesktopWin32WindowClass";

    /// <summary>Runs only the app scenarios (Quick View is not started).</summary>
    public void RunApp(List<TestAsset> assets)
    {
        Native.SetProcessDpiAwarenessContext(-4);
        Native.PeekMessageW(out _, 0, 0, 0, 0);
        nint originalForeground = Native.GetForegroundWindow();
        Environment["os"] = System.Environment.OSVersion.VersionString;
        Environment["mode"] = "app";
        try
        {
            string root = Path.GetDirectoryName(assets[0].Path)!;
            string gifFolder = root + " GIF";
            string pdfFolder = root + " PDF";
            Directory.CreateDirectory(gifFolder);
            Directory.CreateDirectory(pdfFolder);
            string gif = Path.Combine(gifFolder, "01 animation.gif");
            Task.Run(() => WriteAnimatedGifAsync(gif, 480, 360, [(0, 0, 220), (0, 200, 0), (220, 0, 0)], GifDelayMs / 10)).GetAwaiter().GetResult();
            string pdf = Path.Combine(pdfFolder, "01 three pages.pdf");
            File.WriteAllBytes(pdf, TestAssets.MultiPagePdf(3));
            string pdfImage = Path.Combine(pdfFolder, "02 image.jpg");
            if (!File.Exists(pdfImage))
            {
                Task.Run(() => TestAssets.EncodeAsync(pdfImage, BitmapEncoder.JpegEncoderId, 640, 480, 0.9)).GetAwaiter().GetResult();
            }

            string mediaFolder = root + " Media";
            Directory.CreateDirectory(mediaFolder);
            var media = new MediaFiles(
                Path.Combine(mediaFolder, "01 clip.mp4"), Path.Combine(mediaFolder, "02 still.jpg"), Path.Combine(mediaFolder, "03 tone.mp3"),
                Path.Combine(mediaFolder, "04 document.pdf"), Path.Combine(mediaFolder, "05 broken.mp4"), Path.Combine(mediaFolder, "06 tone.wav"));
            Task.Run(() => PrepareMediaAsync(media, log)).GetAwaiter().GetResult();
            EnsureFormatFiles(media);
            string formats = Path.GetDirectoryName(media.Clip)! + " formats";
            string brokenImage = Path.Combine(root + " App", "broken.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(brokenImage)!);
            byte[] garbage = new byte[4096];
            new Random(3).NextBytes(garbage);
            garbage[0] = 0xFF; // JPEG start marker, then garbage
            garbage[1] = 0xD8;
            File.WriteAllBytes(brokenImage, garbage);

            Guarded("app-formats", () => AppFormats(assets, gif, pdf, media, formats, brokenImage),
                "Mavue.exe <file> for each main format: images (JPEG, PNG, WebP, AVIF, HEIF), animated GIF, PDF, MP4/WMV video, MP3/WAV/M4A/WMA/FLAC audio, a damaged MP4 and a damaged JPEG: the expected content is on screen (pixels / sound), damaged files show a message, and closing the window stops playback and ends the process");
            Guarded("app-folder-navigation", () => AppFolderNavigation(media),
                "One file opened: ←/→ step through the viewable files of its folder (video → image → MP3 → PDF → damaged MP4 → WAV and back); every switch stops the previous player, at most one player exists, sound only from the current file");
            Guarded("app-pdf-pages", () => AppPdfPages(pdf),
                "3-page PDF: PageDown ×3 (stops at the last page), PageUp, and the mouse wheel turn pages; ← to the image next to it and back opens the PDF at page 1");
            Guarded("app-media-keys", () => AppMediaKeys(media.Clip),
                "MP4: Space pauses (no sound), Ctrl+→ seeks +10 s (picture turns green), Ctrl+↓/↑ change the volume, Space resumes; closing the window while playing releases the player");
            Guarded("app-multiple-files", () => AppMultipleFiles(assets, pdf, media.Mp3),
                "Three files on the command line (JPEG, PDF, MP3): ←/→ step through exactly those three in that order");
            Guarded("app-open-dialog", AppOpenDialog,
                "Ctrl+O shows the Windows Open dialog owned by the app window; Esc cancels it and the app keeps working");
        }
        finally
        {
            if (originalForeground != 0 && Native.IsWindow(originalForeground))
            {
                ForceForeground(originalForeground);
            }

            _shell.Dispose();
        }
    }

    private (bool, string) AppFormats(List<TestAsset> assets, string gif, string pdf, MediaFiles media, string formats, string brokenImage)
    {
        var cases = new List<(string Path, string Kind)>();
        foreach (TestAsset asset in assets.Where(a => a.Case is "small-jpeg" or "png" or "webp" or "avif" or "heif"))
        {
            cases.Add((asset.Path, "Image"));
        }

        cases.Add((gif, "AnimatedImage"));
        cases.Add((pdf, "Pdf"));
        cases.Add((media.Clip, "Video"));
        cases.Add((Path.Combine(formats, "04 clip.wmv"), "Video"));
        cases.Add((media.Mp3, "Audio"));
        cases.Add((media.Wav, "Audio"));
        cases.Add((Path.Combine(formats, "01 tone.m4a"), "Audio"));
        cases.Add((Path.Combine(formats, "02 tone.wma"), "Audio"));
        cases.Add((Path.Combine(formats, "03 tone.flac"), "Audio"));
        cases.Add((media.Broken, "Message:MediaFailed"));
        cases.Add((brokenImage, "Message"));

        var observed = new List<string>();
        bool ok = true;
        foreach ((string path, string expected) in cases)
        {
            if (!File.Exists(path))
            {
                observed.Add($"{Path.GetFileName(path)}: missing (not generated)");
                ok = false;
                continue;
            }

            using AppSession app = LaunchApp(path);
            if (app.Window == 0)
            {
                observed.Add($"{Path.GetFileName(path)}: no window");
                ok = false;
                continue;
            }

            // A GIF is shown as a still first and becomes an animation right after.
            AppEvent? state = expected == "AnimatedImage"
                ? app.WaitForState(s => s.Text("kind") is "AnimatedImage" or "Message", 15000)
                : app.WaitForState(s => s.Text("kind") is not ("Loading" or "None"), 15000);
            string kind = state?.Text("kind") ?? "timeout";
            bool kindOk = expected.StartsWith("Message", StringComparison.Ordinal)
                ? kind == "Message" && (expected == "Message" || $"Message:{state?.Text("message")}" == expected)
                : kind == expected;
            string detail = $"{kind}{(state?.Text("message") is { } m ? $"({m})" : string.Empty)}";
            Thread.Sleep(900);
            switch (expected)
            {
                case "Image" or "Pdf" or "Video":
                    (bool visible, string pixel) = ContentVisible(app.Window);
                    detail += $" pixel {pixel}";
                    kindOk &= visible;
                    break;
                case "AnimatedImage":
                    var colors = new HashSet<uint>();
                    for (int i = 0; i < 8; i++)
                    {
                        if (WindowCenterPixel(app.Window) is { } c)
                        {
                            colors.Add(c & 0xF0F0F0);
                        }

                        Thread.Sleep(GifDelayMs * 4 / 3);
                    }

                    int frames = app.Events.Count(e => e.Name == "gif-frame");
                    detail += $" frames {frames}, colors {colors.Count}";
                    kindOk &= frames >= 5 && colors.Count >= 2;
                    break;
            }

            if (expected is "Video" or "Audio")
            {
                float? peak = PeakFor(app.Process, 1200);
                detail += $" peak {(peak is null ? "n/a" : $"{peak:0.000}")}";
                kindOk &= peak is > 0.01f;
            }

            (bool closed, bool released) = app.Close(expectPlayerStop: expected is "Video" or "Audio");
            detail += closed ? string.Empty : " (process did not exit)";
            detail += released ? string.Empty : " (no media-stop before exit)";
            kindOk &= closed && released;
            observed.Add($"{Path.GetExtension(path)} {(kindOk ? "OK" : "FAIL")} {detail} [{state?.Text("info")}]");
            ok &= kindOk;
        }

        return (ok, string.Join("; ", observed));
    }

    private (bool, string) AppFolderNavigation(MediaFiles f)
    {
        using AppSession app = LaunchApp(f.Clip);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        var observed = new List<string>();
        AppEvent? first = app.WaitForState(s => s.Text("kind") == "Video", 15000);
        AppEvent? list = app.WaitFor(e => e.Name == "file-list", 5000);
        bool ok = first is not null && list?.Number("count") == 6;
        observed.Add($"video, folder list {list?.Number("count")}");
        (string Path, string Kind, ushort Key)[] steps =
        [
            (f.Still, "Image", VkRight), (f.Mp3, "Audio", VkRight), (f.Pdf, "Pdf", VkRight), (f.Broken, "Message", VkRight), (f.Wav, "Audio", VkRight),
            (f.Broken, "Message", VkLeft), (f.Pdf, "Pdf", VkLeft), (f.Mp3, "Audio", VkLeft), (f.Still, "Image", VkLeft), (f.Clip, "Video", VkLeft),
        ];
        foreach ((string path, string kind, ushort key) in steps)
        {
            int before = app.Events.Count;
            int openPlayers = app.OpenPlayers();
            if (!app.SendKey(key))
            {
                observed.Add("foreground lost");
                ok = false;
                break;
            }

            AppEvent? open = app.WaitFor(e => e.Name == "open", 3000, before);
            AppEvent? state = app.WaitForState(s => s.Text("kind") is not ("Loading" or "None"), 15000, before);
            bool stepOk = open?.Text("path") == path && state?.Text("kind") == kind && openPlayers <= 1 && app.OpenPlayers() <= 1;
            string extra = string.Empty;
            if (kind == "Audio")
            {
                Thread.Sleep(600);
                float? peak = PeakFor(app.Process, 1000);
                extra = $" peak {(peak is null ? "n/a" : $"{peak:0.000}")}";
                stepOk &= peak is > 0.01f;
            }
            else if (kind is "Image" or "Pdf" or "Message")
            {
                Thread.Sleep(600);
                float? peak = PeakFor(app.Process, 600);
                extra = peak is > 0.01f ? $" (sound still playing: {peak:0.000})" : string.Empty;
                stepOk &= peak is null or < 0.01f;
            }

            observed.Add($"{(key == VkRight ? "→" : "←")}{kind}{(stepOk ? string.Empty : $"(FAIL got {state?.Text("kind")} {Path.GetFileName(open?.Text("path"))})")}{extra}");
            ok &= stepOk;
        }

        int players = app.Events.Count(e => e.Name == "media-open");
        int maxOpen = app.MaxOpenPlayers();
        (bool closed, bool released) = app.Close(expectPlayerStop: true);
        observed.Add($"{players} players, at once ≤ {maxOpen}; closed={closed}, released={released}");
        ok &= maxOpen <= 1 && closed && released;
        return (ok, string.Join(" → ", observed));
    }

    private (bool, string) AppPdfPages(string pdf)
    {
        using AppSession app = LaunchApp(pdf);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        AppEvent? first = app.WaitForState(s => s.Text("kind") == "Pdf", 15000);
        var observed = new List<string> { $"page {first?.Number("pageIndex") + 1}/{first?.Number("pageCount")}" };
        bool ok = first?.Number("pageIndex") == 0 && first.Number("pageCount") == 3;
        foreach ((ushort key, int expected) in new[] { (VkPageDown, 1), (VkPageDown, 2), (VkPageDown, 2), (VkPageUp, 1) })
        {
            int before = app.Events.Count;
            long t = Stopwatch.GetTimestamp();
            app.SendKey(key);
            AppEvent? state = app.WaitForState(s => s.Text("kind") == "Pdf", 1500, before);
            int page = (int)(state?.Number("pageIndex") ?? app.LastState()?.Number("pageIndex") ?? -1);
            bool stepOk = page == expected;
            observed.Add($"{(key == VkPageDown ? "PageDown" : "PageUp")} → {page + 1}{(state is null ? " (no change)" : $" in {Ms(t, state.Qpc):0} ms")}");
            ok &= stepOk;
        }

        // Wheel down over the page: 2 → 3.
        if (Native.GetWindowRect(app.Window, out Native.RECT r))
        {
            int before = app.Events.Count;
            Native.SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            Thread.Sleep(150);
            Native.MouseEvent(0x0800 /* MOUSEEVENTF_WHEEL */, 0, 0, unchecked((uint)-120), 0);
            AppEvent? state = app.WaitForState(s => s.Text("kind") == "Pdf", 2000, before);
            bool wheelOk = state?.Number("pageIndex") == 2;
            observed.Add($"wheel → {state?.Number("pageIndex") + 1}");
            ok &= wheelOk;
        }

        // → to the image next to the PDF, ← back: page 1 again.
        int mark = app.Events.Count;
        app.SendKey(VkRight);
        AppEvent? image = app.WaitForState(s => s.Text("kind") == "Image", 5000, mark);
        mark = app.Events.Count;
        app.SendKey(VkLeft);
        AppEvent? back = app.WaitForState(s => s.Text("kind") == "Pdf", 5000, mark);
        bool backOk = image is not null && back?.Number("pageIndex") == 0;
        observed.Add($"→ image, ← PDF at page {back?.Number("pageIndex") + 1}");
        ok &= backOk;
        (bool closed, _) = app.Close(expectPlayerStop: false);
        ok &= closed;
        return (ok, string.Join(", ", observed));
    }

    private (bool, string) AppMediaKeys(string clip)
    {
        using AppSession app = LaunchApp(clip);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        AppEvent? opened = app.WaitForState(s => s.Text("kind") == "Video", 15000);
        Thread.Sleep(1500);
        var observed = new List<string> { $"video, screen {ScreenColorAt(app.Window)}" };
        bool ok = opened is not null && ScreenColorAt(app.Window) == "red";

        int before = app.Events.Count;
        app.SendKey(Native.VK_SPACE);
        AppEvent? pause = app.WaitFor(e => e.Name == "media-command" && e.Text("command") == "TogglePlay", 2000, before);
        AppEvent? paused = app.WaitFor(e => e.Name == "media-state" && e.Text("state") == "Paused", 2000, before);
        Thread.Sleep(800);
        float? silentPeak = PeakFor(app.Process, 800);
        before = app.Events.Count;
        app.SendKey(VkRight, control: true);
        AppEvent? seek = app.WaitFor(e => e.Name == "media-command" && e.Text("command") == "SeekForward", 2000, before);
        Thread.Sleep(800);
        string seekColor = ScreenColorAt(app.Window);
        bool seekOk = pause?.Number("positionMs") is { } p1 && seek?.Number("positionMs") is { } p2 && Math.Abs(p2 - p1 - 10000) < 400;
        observed.Add($"Space: paused={paused is not null} at {pause?.Number("positionMs"):0} ms, peak {silentPeak:0.000}; Ctrl+→ {seek?.Number("positionMs"):0} ms, screen {seekColor}");
        ok &= paused is not null && silentPeak is null or < 0.01f && seekOk && seekColor == "green";

        before = app.Events.Count;
        app.SendKey(Native.VK_DOWN, control: true);
        AppEvent? down = app.WaitFor(e => e.Name == "media-command" && e.Text("command") == "VolumeDown", 2000, before);
        before = app.Events.Count;
        app.SendKey(VkUp, control: true);
        AppEvent? up = app.WaitFor(e => e.Name == "media-command" && e.Text("command") == "VolumeUp", 2000, before);
        before = app.Events.Count;
        app.SendKey(Native.VK_SPACE);
        AppEvent? playing = app.WaitFor(e => e.Name == "media-state" && e.Text("state") == "Playing", 2000, before);
        Thread.Sleep(600);
        float? peak = PeakFor(app.Process, 800);
        bool moved = app.Events.Any(e => e.Name == "open" && e.Number("index") != 0);
        observed.Add($"Ctrl+↓/↑ volume {down?.Number("volume")} → {up?.Number("volume")}; Space: playing={playing is not null}, peak {peak:0.000}; files moved by Ctrl+arrows={moved}");
        ok &= down?.Number("volume") is { } v1 && up?.Number("volume") is { } v2 && Math.Abs(v2 - v1 - 0.1) < 0.001 && playing is not null && peak is > 0.01f && !moved;

        (bool closed, bool released) = app.Close(expectPlayerStop: true);
        observed.Add($"close while playing: exited={closed}, player released={released}");
        ok &= closed && released;
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) AppMultipleFiles(List<TestAsset> assets, string pdf, string mp3)
    {
        string jpeg = assets.First(a => a.Case == "small-jpeg").Path;
        using AppSession app = LaunchApp(jpeg, pdf, mp3);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        AppEvent? first = app.WaitForState(s => s.Text("kind") == "Image", 10000);
        var kinds = new List<string> { first?.Text("kind") ?? "?" };
        foreach (ushort key in new[] { VkRight, VkRight, VkRight, VkLeft })
        {
            int before = app.Events.Count;
            app.SendKey(key);
            AppEvent? state = app.WaitForState(s => s.Text("kind") is not ("Loading" or "None"), 4000, before);
            kinds.Add(state?.Text("kind") ?? "no change");
        }

        bool listOk = !app.Events.Any(e => e.Name == "file-list");
        (bool closed, bool released) = app.Close(expectPlayerStop: true);
        bool ok = string.Join(',', kinds) == "Image,Pdf,Audio,no change,Pdf" && listOk && closed && released;
        return (ok, $"{string.Join(" → ", kinds)}; folder list used={!listOk}; closed={closed}, released={released}");
    }

    private (bool, string) AppOpenDialog()
    {
        using AppSession app = LaunchApp();
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        Thread.Sleep(800);
        ForceForeground(app.Window);
        var before = Native.TopLevelWindows().ToHashSet();
        Native.INPUT[] inputs = [Key(VkControlKey, false), Key(0x4F /* O */, false), Key(0x4F, true), Key(VkControlKey, true)];
        Native.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
        nint dialog = 0;
        WaitFor(
            () =>
            {
                // The Windows file picker of an unpackaged app is hosted out of process (PickerHost.exe).
                dialog = Native.TopLevelWindows().FirstOrDefault(w => !before.Contains(w) && Native.ClassName(w) == "#32770" && Native.IsWindowVisible(w));
                return dialog != 0;
            },
            8000,
            null);
        bool shown = dialog != 0;
        string host = shown ? ProcessName(dialog) : "-";
        bool cancelled = false;
        if (shown)
        {
            ForceForeground(dialog);
            Thread.Sleep(300);
            SendKeyIfForeground(Native.VK_ESCAPE, dialog, out _);
            cancelled = WaitFor(() => !Native.IsWindow(dialog) || !Native.IsWindowVisible(dialog), 5000, null);
        }

        bool responsive = app.Process.Responding;
        (bool closed, _) = app.Close(expectPlayerStop: false);
        return (shown && cancelled && responsive && closed, $"dialog shown={shown} (process {host}), cancelled with Esc={cancelled}, app responding={responsive}, closed={closed}");
    }

    private AppSession LaunchApp(params string[] files)
    {
        string trace = Path.Combine(options.WorkDirectory, $"app-trace-{DateTime.Now:HHmmss-fff}.jsonl");
        var start = new ProcessStartInfo(options.AppPath ?? throw new InvalidOperationException("Mavue.exe path not set")) { UseShellExecute = false };
        start.ArgumentList.Add("--trace-file");
        start.ArgumentList.Add(trace);
        foreach (string file in files)
        {
            start.ArgumentList.Add(file);
        }

        Process process = Process.Start(start) ?? throw new InvalidOperationException("Mavue.exe did not start");
        nint window = 0;
        WaitFor(
            () =>
            {
                window = Native.TopLevelWindows().FirstOrDefault(w => Pid(w) == process.Id && Native.ClassName(w) == WinUiWindowClass && Native.IsWindowVisible(w));
                return window != 0;
            },
            15000,
            null);
        if (window != 0)
        {
            ForceForeground(window);
        }

        return new AppSession(process, window, new AppTrace(trace));
    }

    private (bool Visible, string Pixel) ContentVisible(nint window)
    {
        // The content area is centered; the left edge of the area is background (images are fitted with margins
        // in a wide window). Content is visible when the center differs from the background.
        if (!Native.GetWindowRect(window, out Native.RECT r))
        {
            return (false, "no rect");
        }

        int y = (r.Top + r.Bottom) / 2;
        uint? center = PixelAt((r.Left + r.Right) / 2, y);
        uint? edge = PixelAt(r.Left + 24, y);
        return (center is not null && edge is not null && Distance(center.Value, edge.Value) > 24, $"{center:X6}/{edge:X6}");
    }

    private static int Distance(uint a, uint b) =>
        Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) + Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) + Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));

    private static uint? WindowCenterPixel(nint window) =>
        Native.GetWindowRect(window, out Native.RECT r) ? PixelAt((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2) : null;

    private static string ScreenColorAt(nint window)
    {
        if (WindowCenterPixel(window) is not { } c)
        {
            return "none";
        }

        int red = (int)(c & 0xFF);
        int green = (int)((c >> 8) & 0xFF);
        int blue = (int)((c >> 16) & 0xFF);
        return (red, green, blue) switch
        {
            _ when red > 150 && green < 90 && blue < 90 => "red",
            _ when green > 150 && red < 90 && blue < 90 => "green",
            _ when blue > 150 && red < 90 && green < 90 => "blue",
            _ => $"other({red},{green},{blue})",
        };
    }

    private static uint? PixelAt(int x, int y)
    {
        nint dc = Native.GetDC(0);
        try
        {
            return Native.GetPixel(dc, x, y);
        }
        finally
        {
            Native.ReleaseDC(0, dc);
        }
    }

    private static float? PeakFor(Process process, int ms)
    {
        float? best = null;
        long end = Stopwatch.GetTimestamp() + (ms * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < end)
        {
            if (AudioMeter.PeakForProcess((uint)process.Id) is { } now)
            {
                best = Math.Max(best ?? 0, now);
            }

            Thread.Sleep(50);
        }

        return best;
    }

    /// <summary>One running Mavue.exe with its trace.</summary>
    private sealed class AppSession(Process process, nint window, AppTrace trace) : IDisposable
    {
        public Process Process { get; } = process;

        public nint Window { get; } = window;

        public IReadOnlyList<AppEvent> Events
        {
            get
            {
                trace.Poll();
                return trace.Events;
            }
        }

        public AppEvent? WaitFor(Func<AppEvent, bool> match, int timeoutMs, int skip = 0)
        {
            AppEvent? found = null;
            Runner.WaitFor(() => { trace.Poll(); found = trace.Events.Skip(skip).FirstOrDefault(match); return found is not null; }, timeoutMs, null);
            return found;
        }

        public AppEvent? WaitForState(Func<AppEvent, bool> match, int timeoutMs, int skip = 0) =>
            WaitFor(e => e.Name == "state" && match(e), timeoutMs, skip);

        public AppEvent? LastState()
        {
            trace.Poll();
            return trace.Events.LastOrDefault(e => e.Name == "state" && e.Text("kind") == "Pdf");
        }

        /// <summary>Players opened and not yet stopped.</summary>
        public int OpenPlayers()
        {
            trace.Poll();
            var open = new HashSet<int>();
            foreach (AppEvent e in trace.Events)
            {
                int id = (int)(e.Number("player") ?? -1);
                if (e.Name == "media-open")
                {
                    open.Add(id);
                }
                else if (e.Name == "media-stop")
                {
                    open.Remove(id);
                }
            }

            return open.Count;
        }

        public int MaxOpenPlayers()
        {
            trace.Poll();
            var open = new HashSet<int>();
            int max = 0;
            foreach (AppEvent e in trace.Events)
            {
                int id = (int)(e.Number("player") ?? -1);
                if (e.Name == "media-open")
                {
                    open.Add(id);
                }
                else if (e.Name == "media-stop")
                {
                    open.Remove(id);
                }

                max = Math.Max(max, open.Count);
            }

            return max;
        }

        public bool SendKey(ushort vk, bool control = false)
        {
            if (Native.GetForegroundWindow() != Window && !ForceForeground(Window))
            {
                return false;
            }

            Native.INPUT[] inputs = control
                ? [Key(VkControlKey, false), Key(vk, false), Key(vk, true), Key(VkControlKey, true)]
                : [Key(vk, false), Key(vk, true)];
            return Native.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>()) == inputs.Length;
        }

        /// <summary>Closes the window like the title-bar button; true when the process exits (and, for media, the player was stopped first).</summary>
        public (bool Exited, bool Released) Close(bool expectPlayerStop)
        {
            bool hadPlayer = OpenPlayers() > 0;
            Native.PostMessageW(Window, Native.WM_CLOSE, 0, 0);
            bool exited = Process.WaitForExit(8000);
            trace.Poll();
            bool released = !expectPlayerStop || !hadPlayer || OpenPlayers() == 0;
            return (exited, released && (!exited || trace.Events.Any(e => e.Name == "closed")));
        }

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit(3000);
            }

            Process.Dispose();
        }
    }

    private sealed record AppEvent(string Name, long Qpc, Dictionary<string, JsonElement> Properties)
    {
        public string? Text(string key) => Properties.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public double? Number(string key) => Properties.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }

    /// <summary>Tails the app's trace file (same incremental reading as <see cref="TimingLog"/>).</summary>
    private sealed class AppTrace(string path)
    {
        private readonly List<AppEvent> _events = [];
        private readonly StringBuilder _partial = new();
        private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
        private long _position;

        public IReadOnlyList<AppEvent> Events => _events;

        public void Poll()
        {
            if (!File.Exists(path))
            {
                return;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = _position;
            byte[] buffer = new byte[64 * 1024];
            char[] chars = new char[buffer.Length + 4];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                _position += read;
                _partial.Append(chars, 0, _decoder.GetChars(buffer, 0, read, chars, 0, flush: false));
            }

            string text = _partial.ToString();
            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0)
            {
                return;
            }

            foreach (string line in text[..lastNewline].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                var props = new Dictionary<string, JsonElement>();
                foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                {
                    props[p.Name] = p.Value.Clone();
                }

                _events.Add(new AppEvent(props["event"].GetString()!, props["qpc"].GetInt64(), props));
            }

            _partial.Clear().Append(text[(lastNewline + 1)..]);
        }
    }
}
