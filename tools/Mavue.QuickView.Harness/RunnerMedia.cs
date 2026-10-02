using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Video and audio in Quick View (MediaPlayerElement): a generated 30 s video (red → green → blue, 10 s each, with a
/// tone) plays with picture and sound; Enter pauses, Ctrl+→ seeks 10 s, Ctrl+↑/↓ change the volume; switching to an
/// image, audio, a PDF or a damaged file stops and releases the player, and only one player ever runs.
/// </summary>
internal sealed partial class Runner
{
    private const ushort VkReturn = 0x0D;
    private const ushort VkControlKey = 0x11;
    private const ushort VkRight = 0x27;
    private const ushort VkLeft = 0x25;

    private void RunMediaScenario(List<TestAsset> assets)
    {
        string folder = Path.GetDirectoryName(assets[0].Path)! + " Media";
        Directory.CreateDirectory(folder);
        var files = new MediaFiles(
            Path.Combine(folder, "01 clip.mp4"),
            Path.Combine(folder, "02 still.jpg"),
            Path.Combine(folder, "03 tone.mp3"),
            Path.Combine(folder, "04 document.pdf"),
            Path.Combine(folder, "05 broken.mp4"),
            Path.Combine(folder, "06 tone.wav"));
        string? assetError = null;
        try
        {
            Task.Run(() => PrepareMediaAsync(files, log)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            assetError = $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
        }

        if (assetError is not null)
        {
            Guarded("media-video", () => (false, "could not generate media files: " + assetError), "generated media files");
            return;
        }

        Guarded("media-video", () => MediaVideo(files),
            "MP4 (H.264/AAC, 30 s: red/green/blue): picture on screen and sound from the host's audio session; Enter (Quick View focused) pauses and resumes, Ctrl+→ seeks +10 s (picture turns green), Ctrl+↓/↑ change the volume; plain ↓ still moves to the next file and stops the player; Esc closes and the player is released");
        Guarded("media-esc-repeat", () => MediaEscRepeat(files, 20),
            "Diagnostics for the media-video flake, 20 times: Quick View activated (as after a click), Explorer ↓ image / ↑ video, Quick View activated again, Esc on Quick View closes it");
        Guarded("media-formats", () => MediaFormats(files),
            "Other containers/codecs Windows can encode (M4A/AAC, WMA, FLAC, WMV) open and play in Quick View with the right kind (audio/video) and duration; formats that need Store extensions (MKV/WebM/HEVC…) are not generated here");
        Guarded("media-switch", () => MediaSwitch(files),
            "↓ through video → image → MP3 → PDF → damaged MP4 → WAV and ↑ back to the video: every switch stops the previous player, audio plays with sound, the damaged file shows a message and Quick View keeps working, PDF/images still render, one player at a time; Space closes and releases the player");
    }

    private sealed record MediaFiles(string Clip, string Still, string Mp3, string Pdf, string Broken, string Wav);

    private (bool, string) MediaVideo(MediaFiles f)
    {
        nint explorer = OpenExplorer(f.Clip);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Select(window, f.Clip, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        long tOpen = Stopwatch.GetTimestamp();
        if (!OpenQuickView(explorer, f.Clip, out string why))
        {
            return (false, why);
        }

        var observed = new List<string>();
        Mark? opened = _log.Marks.LastOrDefault(m => m.Name == "media-opened" && m.Qpc >= tOpen);
        int player = (int)(opened?.Number("player") ?? -1);
        Mark? openStart = _log.Marks.FirstOrDefault(m => m.Name == "media-open" && m.Qpc >= tOpen);
        bool video = opened?.Text("kind") == "video" && opened.Number("width") > 0 && opened.Number("durationMs") is > 29000 and < 31000;
        Thread.Sleep(1500);
        string color = ScreenColor();
        float? peak = HostAudioPeak(1500);
        bool playing = _log.Marks.Any(m => m.Name == "media-state" && m.Text("state") == "Playing" && (int)(m.Number("player") ?? -2) == player);
        observed.Add($"open: video={video} {opened?.Number("width")}x{opened?.Number("height")} {opened?.Number("durationMs"):0} ms, shown {(openStart is null || opened is null ? double.NaN : Ms(openStart.Qpc, opened.Qpc)):0} ms after open, playing={playing}, screen={color}, audio peak={Peak(peak)}");
        bool ok = video && playing && color == "red" && peak is > 0.01f;

        // Keyboard on the Quick View window (as after clicking it): Enter, Ctrl+→, Ctrl+↓, Ctrl+↑, Enter.
        nint host = HostWindow();
        if (!ForceForeground(host))
        {
            return (false, string.Join("; ", observed) + "; could not focus Quick View");
        }

        Thread.Sleep(300);
        long tKeys = Stopwatch.GetTimestamp();
        Mark? pause = MediaKey(host, VkReturn, false, "TogglePlay");
        bool paused = WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "media-state" && m.Text("state") == "Paused" && m.Qpc >= (pause?.Qpc ?? long.MaxValue)); }, 2000, null);
        Thread.Sleep(1000);
        float? pausedPeak = HostAudioPeak(800);
        Mark? seek = MediaKey(host, VkRight, true, "SeekForward");
        double? before = pause?.Number("positionMs");
        double? after = seek?.Number("positionMs");
        bool seekOk = before is not null && after is not null && Math.Abs(after.Value - before.Value - 10000) < 400; // no movement while paused
        Thread.Sleep(800);
        string seekColor = ScreenColor();
        observed.Add($"Enter: paused={paused} at {before:0} ms, audio peak while paused={Peak(pausedPeak)}; Ctrl+→: {after:0} ms, screen={seekColor}");
        ok &= paused && seekOk && seekColor == "green" && pausedPeak is null or < 0.01f;

        Mark? down = MediaKey(host, Native.VK_DOWN, true, "VolumeDown");
        Mark? up = MediaKey(host, VkUp, true, "VolumeUp");
        bool volumeOk = down?.Number("volume") is { } v1 && up?.Number("volume") is { } v2 && Math.Abs(v1 - (v2 - 0.1)) < 0.001;
        Mark? resume = MediaKey(host, VkReturn, false, "TogglePlay");
        bool resumed = WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "media-state" && m.Text("state") == "Playing" && m.Qpc >= (resume?.Qpc ?? long.MaxValue)); }, 2000, null);
        bool navigated = _log.Marks.Any(m => m.Name == "nav" && m.Qpc >= tKeys);
        observed.Add($"Ctrl+↓/↑ volume {down?.Number("volume")} → {up?.Number("volume")}; Enter: resumed={resumed}; Ctrl+arrows moved files={navigated}");
        ok &= volumeOk && resumed && !navigated;

        // Plain → / ← on the focused Quick View window still move between files (→ stops this player, ← starts a new one);
        // with one selected item they move Explorer's selection, so the navigation is reported as explorer-selection.
        long tRight = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkRight, host, out _);
        (Mark? right, Mark? rightDone) = WaitForNavigation(tRight, "explorer-selection");
        bool rightOk = rightDone?.Name == "full-visible" && right is not null && ShowsFile(right.Request, f.Still) && StoppedSince(player, tRight);
        long tLeft = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkLeft, host, out _);
        (Mark? left, Mark? leftDone) = WaitForNavigation(tLeft, "explorer-selection");
        Mark? leftOpened = _log.Marks.LastOrDefault(m => m.Name == "media-opened" && m.Qpc >= tLeft);
        bool leftOk = leftDone?.Name == "full-visible" && left is not null && ShowsFile(left.Request, f.Clip) && leftOpened?.Text("kind") == "video";
        observed.Add($"→ on Quick View: image, player stopped={rightOk}; ← back: video again={leftOk}");
        ok &= rightOk && leftOk;
        player = (int)(leftOpened?.Number("player") ?? -1);

        // Plain ↓ (from Explorer, through the hook) still moves to the next file and stops this player.
        ForceForeground(explorer);
        Thread.Sleep(200);
        long tDown = Stopwatch.GetTimestamp();
        SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
        (Mark? toStill, Mark? stillDone) = WaitForNavigation(tDown, "explorer-selection");
        bool onStill = stillDone?.Name == "full-visible" && toStill is not null && ShowsFile(toStill.Request, f.Still);
        bool stopped = StoppedSince(player, tDown);
        Thread.Sleep(600);
        float? afterPeak = HostAudioPeak(800);
        observed.Add($"↓ image: shown={onStill}, player {player} stopped={stopped}, audio peak after={Peak(afterPeak)}");
        ok &= onStill && stopped && afterPeak is null or < 0.01f;

        // ↑ back to the video: a new player. Quick View was clicked into earlier, so it now behaves as a normal window:
        // Esc is pressed on Quick View itself (the transport controls must not take it) and releases the player.
        long tUp = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkUp, explorer, out _);
        (_, Mark? backDone) = WaitForNavigation(tUp, "explorer-selection");
        Mark? again = _log.Marks.LastOrDefault(m => m.Name == "media-opened" && m.Qpc >= tUp);
        int second = (int)(again?.Number("player") ?? -1);
        Thread.Sleep(800);
        long tClose = Stopwatch.GetTimestamp();
        bool focusedHost = ForceForeground(host);
        bool sent = focusedHost && SendKeyIfForeground(Native.VK_ESCAPE, host, out _);
        bool closed = sent && WaitFor(() => { _log.Poll(); return _log.HiddenSince(tClose) is not null; }, 3000, null);
        Thread.Sleep(300);
        _log.Poll();
        bool released = StoppedSince(second, tClose);
        observed.Add($"↑ video again: player {second} (new={second > player}), Esc on Quick View: closed={closed}{(closed ? string.Empty : $" (foreground={focusedHost}, sent={sent})")}, released={released}");
        ok &= backDone?.Name == "full-visible" && second > player && closed && released;
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) MediaSwitch(MediaFiles f)
    {
        nint explorer = OpenExplorer(f.Clip);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Select(window, f.Clip, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        long tStart = Stopwatch.GetTimestamp();
        if (!OpenQuickView(explorer, f.Clip, out string why))
        {
            return (false, why);
        }

        var observed = new List<string> { "video" };
        bool ok = true;
        (string Path, string Expect)[] steps =
        [
            (f.Still, "image"), (f.Mp3, "audio"), (f.Pdf, "image"), (f.Broken, "failed"), (f.Wav, "audio"),
        ];
        foreach ((string path, string expect) in steps)
        {
            ok &= SwitchStep(explorer, Native.VK_DOWN, path, expect, observed);
        }

        foreach ((string path, string expect) in new[] { (f.Broken, "failed"), (f.Pdf, "image"), (f.Mp3, "audio"), (f.Still, "image"), (f.Clip, "video") })
        {
            ok &= SwitchStep(explorer, VkUp, path, expect, observed);
        }

        // One player at a time: every player that started was stopped before the next one opened.
        _log.Poll();
        List<Mark> media = _log.Marks.Where(m => m.Qpc >= tStart && (m.Name == "media-open" || m.Name == "media-stop")).ToList();
        var running = new HashSet<int>();
        int maxRunning = 0;
        foreach (Mark m in media)
        {
            int id = (int)(m.Number("player") ?? -1);
            if (m.Name == "media-open")
            {
                running.Add(id);
            }
            else
            {
                running.Remove(id); // a stop of an earlier scenario's player is ignored
            }

            maxRunning = Math.Max(maxRunning, running.Count);
        }

        // Space (from Explorer) closes Quick View and releases the last player.
        int last = (int)(_log.Marks.LastOrDefault(m => m.Name == "media-open")?.Number("player") ?? -1);
        long tClose = Stopwatch.GetTimestamp();
        bool closed = SendKeyIfForeground(Native.VK_SPACE, explorer, out _) &&
            WaitFor(() => { _log.Poll(); return _log.HiddenSince(tClose) is not null; }, 3000, null);
        Thread.Sleep(300);
        _log.Poll();
        bool released = StoppedSince(last, tClose);
        observed.Add($"{media.Count(m => m.Name == "media-open")} players, at once ≤ {maxRunning}; Space: closed={closed}, released={released}");
        ok &= maxRunning == 1 && closed && released;
        return (ok, string.Join(" → ", observed));
    }

    private (bool, string) MediaEscRepeat(MediaFiles f, int cycles)
    {
        nint explorer = OpenExplorer(f.Clip);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        int passed = 0;
        var failures = new List<string>();
        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            Select(window, f.Clip, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
            if (!OpenQuickView(explorer, f.Clip, out string why))
            {
                failures.Add($"#{cycle} open: {why}");
                continue;
            }

            nint host = HostWindow();
            bool first = ForceForeground(host);
            Thread.Sleep(300);
            ForceForeground(explorer);
            Thread.Sleep(200);
            long tDown = Stopwatch.GetTimestamp();
            SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
            WaitForNavigation(tDown, "explorer-selection");
            long tUp = Stopwatch.GetTimestamp();
            SendKeyIfForeground(VkUp, explorer, out _);
            (_, Mark? back) = WaitForNavigation(tUp, "explorer-selection");
            Thread.Sleep(800);
            long tClose = Stopwatch.GetTimestamp();
            bool focusedHost = ForceForeground(host);
            bool sent = focusedHost && SendKeyIfForeground(Native.VK_ESCAPE, host, out _);
            bool closed = sent && WaitFor(() => { _log.Poll(); return _log.HiddenSince(tClose) is not null; }, 3000, null);
            if (closed)
            {
                passed++;
                continue;
            }

            _log.Poll();
            Mark? activated = _log.Marks.LastOrDefault(m => m.Name == "window-activated" && m.Qpc >= tClose);
            List<string> keys = _log.Marks.Where(m => m.Name == "window-key" && m.Qpc >= tClose).Select(m => $"{m.Text("key")}@{m.Text("focus")}").ToList();
            nint fg = Native.GetForegroundWindow();
            failures.Add($"#{cycle}: first activation={first}, back={back?.Name}, foreground ok={focusedHost} (now {(fg == host ? "Quick View" : fg == explorer ? "Explorer" : Native.ClassName(fg))}), Esc sent={sent}, activated focus={activated?.Text("focus") ?? "no mark"}, keys seen=[{string.Join(' ', keys)}]");
            ForceForeground(explorer);
            CloseFromExplorer(explorer);
            EnsureHiddenNow();
        }

        return (passed == cycles, $"{passed}/{cycles} closed" + (failures.Count > 0 ? "; " + string.Join("; ", failures) : string.Empty));
    }

    /// <summary>M4A, WMA, FLAC and WMV made with Windows' encoders from the WAV and MP4 (kept between runs).</summary>
    private static (List<(string File, string Kind)> Ready, List<string> Errors) EnsureFormatFiles(MediaFiles f)
    {
        string folder = Path.GetDirectoryName(f.Clip)! + " formats";
        Directory.CreateDirectory(folder);
        (string File, string Kind, Func<string, Task> Make)[] formats =
        [
            (Path.Combine(folder, "01 tone.m4a"), "audio", path => TranscodeAsync(f.Wav, path, MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium))),
            (Path.Combine(folder, "02 tone.wma"), "audio", path => TranscodeAsync(f.Wav, path, MediaEncodingProfile.CreateWma(AudioEncodingQuality.Medium))),
            (Path.Combine(folder, "03 tone.flac"), "audio", path => TranscodeAsync(f.Wav, path, MediaEncodingProfile.CreateFlac(AudioEncodingQuality.Medium))),
            (Path.Combine(folder, "04 clip.wmv"), "video", path => TranscodeAsync(f.Clip, path, MediaEncodingProfile.CreateWmv(VideoEncodingQuality.Vga))),
        ];
        var errors = new List<string>();
        var ready = new List<(string File, string Kind)>();
        foreach ((string file, string kind, Func<string, Task> make) in formats)
        {
            try
            {
                if (!File.Exists(file))
                {
                    Task.Run(() => make(file)).GetAwaiter().GetResult();
                }

                ready.Add((file, kind));
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetExtension(file)}: not generated ({ex.GetType().Name} 0x{ex.HResult:X8})");
            }
        }

        return (ready, errors);
    }

    private (bool, string) MediaFormats(MediaFiles f)
    {
        (List<(string File, string Kind)> ready, List<string> observed) = EnsureFormatFiles(f);
        int expectedCount = 4;
        if (ready.Count == 0)
        {
            return (false, string.Join("; ", observed));
        }

        nint explorer = OpenExplorer(ready[0].File);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Select(window, ready[0].File, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        long t = Stopwatch.GetTimestamp();
        if (!OpenQuickView(explorer, ready[0].File, out string why))
        {
            return (false, why);
        }

        bool ok = true;
        for (int i = 0; i < ready.Count; i++)
        {
            if (i > 0)
            {
                t = Stopwatch.GetTimestamp();
                SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
                WaitForNavigation(t, "explorer-selection");
            }

            _log.Poll();
            Mark? opened = _log.Marks.LastOrDefault(m => m.Name == "media-opened" && m.Qpc >= t);
            Mark? info = _log.Marks.LastOrDefault(m => m.Name == "info" && m.Qpc >= t);
            bool fileOk = opened?.Text("kind") == ready[i].Kind && opened.Number("durationMs") > 0;
            observed.Add($"{Path.GetExtension(ready[i].File)} ({info?.Text("format")}): {(fileOk ? $"{opened!.Text("kind")} {opened.Number("durationMs"):0} ms" : $"FAIL kind={opened?.Text("kind")} failed={_log.Marks.Any(m => m.Name == "media-failed" && m.Qpc >= t)}")}");
            ok &= fileOk;
        }

        ok &= CloseFromExplorer(explorer) && ready.Count == expectedCount;
        return (ok, string.Join("; ", observed));
    }

    /// <summary>One arrow key from Explorer; <paramref name="expect"/> is image, video, audio or failed.</summary>
    private bool SwitchStep(nint explorer, ushort key, string path, string expect, List<string> observed)
    {
        int previous = (int)(_log.Marks.LastOrDefault(m => m.Name == "media-open")?.Number("player") ?? -1);
        bool previousRunning = previous > 0 && !_log.Marks.Any(m => m.Name == "media-stop" && (int)(m.Number("player") ?? -2) == previous);
        long t = Stopwatch.GetTimestamp();
        SendKeyIfForeground(key, explorer, out _);
        (Mark? nav, Mark? done) = WaitForNavigation(t, "explorer-selection");
        bool shown = nav is not null && ShowsFile(nav.Request, path);
        bool stopped = !previousRunning || StoppedSince(previous, t);
        Mark? opened = nav is null ? null : _log.Marks.LastOrDefault(m => m.Name == "media-opened" && m.Qpc >= t);
        Mark? failed = nav is null ? null : _log.Marks.LastOrDefault(m => m.Name == "media-failed" && m.Qpc >= t);
        bool ok = expect switch
        {
            "image" => done?.Name == "full-visible" && opened is null,
            "video" => done?.Name == "full-visible" && opened?.Text("kind") == "video",
            "audio" => done?.Name == "full-visible" && opened?.Text("kind") == "audio",
            _ => done?.Name == "full-skipped" && failed is not null && Native.IsWindowVisible(HostWindow()),
        };
        string extra = string.Empty;
        if (expect == "audio")
        {
            Thread.Sleep(700);
            float? peak = HostAudioPeak(1200);
            extra = $" peak {Peak(peak)}";
            ok &= peak is > 0.01f;
        }

        ok &= shown && stopped;
        observed.Add($"{(key == VkUp ? "↑" : "↓")}{expect}{(ok ? string.Empty : $"(FAIL shown={shown} done={done?.Name} stopped={stopped})")}{extra}");
        return ok;
    }

    /// <summary>Presses a key (optionally with Ctrl) on the focused Quick View window and returns its media-command mark.</summary>
    private Mark? MediaKey(nint host, ushort vk, bool control, string command)
    {
        if (Native.GetForegroundWindow() != host)
        {
            return null;
        }

        long t = Stopwatch.GetTimestamp();
        Native.INPUT[] inputs = control
            ? [Key(VkControlKey, false), Key(vk, false), Key(vk, true), Key(VkControlKey, true)]
            : [Key(vk, false), Key(vk, true)];
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        Mark? mark = null;
        WaitFor(() => { _log.Poll(); mark = _log.Marks.FirstOrDefault(m => m.Name == "media-command" && m.Qpc >= t && m.Text("command") == command); return mark is not null; }, 2000, null);
        Thread.Sleep(150);
        return mark;
    }

    private bool StoppedSince(int player, long qpc)
    {
        _log.Poll();
        return WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "media-stop" && m.Qpc >= qpc && (int)(m.Number("player") ?? -2) == player); }, 2000, null);
    }

    private string ScreenColor()
    {
        if (ImageAreaCenterPixel() is not { } c)
        {
            return "none";
        }

        int r = (int)(c & 0xFF);
        int g = (int)((c >> 8) & 0xFF);
        int b = (int)((c >> 16) & 0xFF);
        return (r, g, b) switch
        {
            _ when r > 150 && g < 90 && b < 90 => "red",
            _ when g > 150 && r < 90 && b < 90 => "green",
            _ when b > 150 && r < 90 && g < 90 => "blue",
            _ => $"other({r},{g},{b})",
        };
    }

    private string Peak(float? peak) => peak is null ? $"n/a [{AudioMeter.Describe()}; host {_host?.Id}]" : $"{peak:0.000}";

    /// <summary>Highest peak level of the host's audio sessions on the default output device during <paramref name="ms"/>.</summary>
    private float? HostAudioPeak(int ms)
    {
        if (_host is null)
        {
            return null;
        }

        float? best = null;
        long end = Stopwatch.GetTimestamp() + (ms * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < end)
        {
            float? now = AudioMeter.PeakForProcess((uint)_host.Id);
            if (now is not null)
            {
                best = Math.Max(best ?? 0, now.Value);
            }

            Thread.Sleep(50);
        }

        return best;
    }

    private static async Task PrepareMediaAsync(MediaFiles f, Action<string> log)
    {
        if (!File.Exists(f.Still))
        {
            await TestAssets.EncodeAsync(f.Still, BitmapEncoder.JpegEncoderId, 640, 480, 0.9);
        }

        File.WriteAllBytes(f.Pdf, TestAssets.MultiPagePdf(2));

        // Damaged: a valid MP4 signature followed by garbage.
        byte[] broken = new byte[64 * 1024];
        new Random(7).NextBytes(broken);
        "\0\0\0\u0018ftypisom\0\0\u0002\0isomavc1"u8.ToArray().CopyTo(broken, 0);
        File.WriteAllBytes(f.Broken, broken);

        if (!File.Exists(f.Wav))
        {
            WriteToneWav(f.Wav, 20, 440);
        }

        if (!File.Exists(f.Mp3))
        {
            log("generating 03 tone.mp3 (Media Foundation MP3 encoder) ...");
            await TranscodeAsync(f.Wav, f.Mp3, MediaEncodingProfile.CreateMp3(AudioEncodingQuality.Medium));
        }

        if (!File.Exists(f.Clip))
        {
            log("generating 01 clip.mp4 (MediaComposition, H.264/AAC) ...");
            string tone = Path.Combine(Path.GetTempPath(), "mavue-harness-tone-30s.wav");
            WriteToneWav(tone, 30, 330);
            try
            {
                await WriteColorVideoAsync(f.Clip, tone);
            }
            finally
            {
                File.Delete(tone);
            }
        }
    }

    /// <summary>16-bit stereo 44.1 kHz sine tone.</summary>
    private static void WriteToneWav(string path, int seconds, double frequency)
    {
        const int Rate = 44100;
        int samples = Rate * seconds;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples * 4));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(Rate);
        writer.Write(Rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 4);
        for (int i = 0; i < samples; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * frequency * i / Rate) * 8000);
            writer.Write(v);
            writer.Write(v);
        }
    }

    private static async Task TranscodeAsync(string source, string target, MediaEncodingProfile profile)
    {
        StorageFile input = await StorageFile.GetFileFromPathAsync(source);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(target)!);
        StorageFile output = await folder.CreateFileAsync(Path.GetFileName(target), CreationCollisionOption.ReplaceExisting);
        PrepareTranscodeResult prepared = await new MediaTranscoder().PrepareFileTranscodeAsync(input, output, profile);
        if (!prepared.CanTranscode)
        {
            await output.DeleteAsync();
            throw new InvalidOperationException($"cannot transcode to {Path.GetExtension(target)}: {prepared.FailureReason}");
        }

        await prepared.TranscodeAsync();
    }

    private static async Task WriteColorVideoAsync(string target, string tone)
    {
        var composition = new MediaComposition();
        foreach (Windows.UI.Color color in new[] { Windows.UI.Color.FromArgb(255, 220, 0, 0), Windows.UI.Color.FromArgb(255, 0, 200, 0), Windows.UI.Color.FromArgb(255, 0, 0, 220) })
        {
            composition.Clips.Add(MediaClip.CreateFromColor(color, TimeSpan.FromSeconds(10)));
        }

        composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(tone)));
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(target)!);
        StorageFile output = await folder.CreateFileAsync(Path.GetFileName(target), CreationCollisionOption.ReplaceExisting);
        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
        Windows.Media.Transcoding.TranscodeFailureReason result = await composition.RenderToFileAsync(output, MediaTrimmingPreference.Fast, profile);
        if (result != Windows.Media.Transcoding.TranscodeFailureReason.None)
        {
            await output.DeleteAsync();
            throw new InvalidOperationException($"video render failed: {result}");
        }
    }
}

/// <summary>Core Audio session meter (harness-only): is the host actually producing sound?</summary>
internal static class AudioMeter
{
    /// <summary>Diagnostics: "pid:peak" of every session on the default output device.</summary>
    public static string Describe()
    {
        var parts = new List<string>();
        try
        {
            ForEachSession((pid, peak) => parts.Add($"{pid}:{peak:0.000}"));
        }
        catch (Exception ex)
        {
            parts.Add($"{ex.GetType().Name} 0x{ex.HResult:X8}");
        }

        return string.Join(' ', parts);
    }

    public static float? PeakForProcess(uint pid)
    {
        float? peak = null;
        try
        {
            ForEachSession((sessionPid, value) =>
            {
                if (sessionPid == pid)
                {
                    peak = Math.Max(peak ?? 0, value);
                }
            });
        }
        catch (COMException)
        {
            return null;
        }

        return peak;
    }

    private static void ForEachSession(Action<uint, float> visit)
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out IMMDevice device));
        Guid iid = typeof(IAudioSessionManager2).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, 0, out object managerObject));
        var manager = (IAudioSessionManager2)managerObject;
        Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions));
        Marshal.ThrowExceptionForHR(sessions.GetCount(out int count));
        for (int i = 0; i < count; i++)
        {
            Marshal.ThrowExceptionForHR(sessions.GetSession(i, out object session));
            if (session is IAudioSessionControl2 control && control.GetProcessId(out uint sessionPid) >= 0 && session is IAudioMeterInformation meter && meter.GetPeakValue(out float value) == 0)
            {
                visit(sessionPid, value);
            }
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(nint sessionGuid, int flags, out nint control);

        [PreserveSig]
        int GetSimpleAudioVolume(nint sessionGuid, int flags, out nint volume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetDisplayName(out nint name);

        [PreserveSig]
        int SetDisplayName(nint name, nint context);

        [PreserveSig]
        int GetIconPath(out nint path);

        [PreserveSig]
        int SetIconPath(nint path, nint context);

        [PreserveSig]
        int GetGroupingParam(out Guid group);

        [PreserveSig]
        int SetGroupingParam(nint group, nint context);

        [PreserveSig]
        int RegisterAudioSessionNotification(nint client);

        [PreserveSig]
        int UnregisterAudioSessionNotification(nint client);

        // IAudioSessionControl2
        [PreserveSig]
        int GetSessionIdentifier(out nint id);

        [PreserveSig]
        int GetSessionInstanceIdentifier(out nint id);

        [PreserveSig]
        int GetProcessId(out uint pid);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);
    }
}
