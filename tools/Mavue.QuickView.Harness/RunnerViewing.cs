using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Graphics.Imaging;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Viewing features of the main window and Quick View: zoom (keys, Ctrl+wheel, sharp re-decoding), rotation,
/// PDF page box, Home/End and thumbnails, GIF pause and frame steps, SVG, the information pane, copying the file,
/// and the saved settings (recent files, window position) — all checked through the trace / timing log.
/// </summary>
internal sealed partial class Runner
{
    private const ushort VkHome = 0x24;
    private const ushort VkEnd = 0x23;
    private const ushort VkOemPlus = 0xBB;
    private const ushort VkOemMinus = 0xBD;
    private const ushort VkOemComma = 0xBC;
    private const ushort VkOemPeriod = 0xBE;

    private string AppSettingsPath => Path.Combine(options.WorkDirectory, "app-settings-e2e.json");

    internal static void WriteTestSvg(string path) => File.WriteAllText(
        path,
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"300\" viewBox=\"0 0 400 300\">" +
        "<rect width=\"400\" height=\"300\" fill=\"#2266cc\"/><circle cx=\"200\" cy=\"150\" r=\"100\" fill=\"#ffcc00\"/></svg>\n");

    private void RunAppViewingScenarios(List<TestAsset> assets, string gif, string pdf)
    {
        string root = Path.GetDirectoryName(assets[0].Path)!;
        string large = assets.FirstOrDefault(a => a.Case == "large-jpeg")?.Path ?? assets[0].Path;
        string svg = Path.Combine(root + " App", "drawing.svg");
        WriteTestSvg(svg);

        Guarded("app-zoom-rotate", () => AppZoomRotate(large),
            "24 MP JPEG: Ctrl+1 shows 100 % and decodes the full 6000 px again (sharp), Ctrl+- / Ctrl++ step the zoom, Ctrl+wheel zooms, Ctrl+R turns the image (width and height swap), Ctrl+0 fits again; the next file starts unturned and fitted");
        File.Delete(AppSettingsPath); // the PDF layout is remembered; start from the default (continuous)
        Guarded("app-pdf-navigation", () => AppPdfNavigation(pdf),
            "3-page PDF: the page thumbnails list the pages, Ctrl+G + \"3\" + Enter goes to page 3, Home to page 1, End to page 3, Ctrl+R turns the page");
        Guarded("app-gif-controls", () => AppGifControls(gif),
            "Animated GIF: Space pauses (no frames for a second), '.' and ',' step one frame forward and back, Space plays again");
        Guarded("app-svg", () => AppSvg(svg),
            "SVG: shown (drawing pixels on screen, 400 × 300), Ctrl++ zooms it");
        RunPdfViewerScenarios();
        Guarded("app-info-copy-settings", () => AppInfoCopySettings(large),
            "Ctrl+I shows the file information (name, folder, size, dates, dimensions…), Ctrl+C puts the file on the clipboard, and after closing the settings file has the recent file and the window position");
    }

    private (bool, string) AppZoomRotate(string large)
    {
        using AppSession app = LaunchApp(large);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        var observed = new List<string>();
        AppEvent? fitted = app.WaitForState(s => s.Text("kind") == "Image", 15000);
        double fitZoom = fitted?.Number("zoom") ?? 0;
        bool ok = fitZoom is > 0 and < 1;
        observed.Add($"fit {fitZoom:0.###}");

        // Ctrl+1: 100 %, then (after the zoom pauses) a full-resolution decode.
        int mark = app.Events.Count;
        app.SendChord(VkControlKey, 0x31);
        AppEvent? actual = app.WaitForState(s => s.Number("zoom") == 1, 3000, mark);
        AppEvent? sharp = app.WaitFor(e => e.Name == "shown" && e.Number("bitmapWidth") == 6000, 8000, mark);
        observed.Add($"Ctrl+1 → {actual?.Number("zoom")}, re-decoded {sharp?.Number("bitmapWidth")} px");
        ok &= actual is not null && sharp is not null;

        // Ctrl+- twice: 75 %, 66.7 %; Ctrl++ : 75 %.
        foreach ((ushort key, double expected) in new[] { (VkOemMinus, 0.75), (VkOemMinus, 2.0 / 3), (VkOemPlus, 0.75) })
        {
            mark = app.Events.Count;
            app.SendChord(VkControlKey, key);
            AppEvent? step = app.WaitFor(e => e.Name == "zoom", 2000, mark);
            bool stepOk = step?.Number("factor") is { } f && Math.Abs(f - expected) < 0.001;
            observed.Add($"{(key == VkOemPlus ? "Ctrl++" : "Ctrl+-")} → {step?.Number("factor"):0.###}");
            ok &= stepOk;
        }

        // Ctrl + wheel up over the image: × 1.2.
        if (Native.GetWindowRect(app.Window, out Native.RECT r))
        {
            mark = app.Events.Count;
            Native.SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            Thread.Sleep(150);
            CtrlWheel(120);
            AppEvent? wheel = app.WaitFor(e => e.Name == "zoom", 2000, mark);
            bool wheelOk = wheel?.Number("factor") is { } w && Math.Abs(w - 0.9) < 0.001;
            observed.Add($"Ctrl+wheel → {wheel?.Number("factor"):0.###}");
            ok &= wheelOk;
        }

        // Ctrl+R: a quarter turn; the element swaps width and height.
        mark = app.Events.Count;
        app.SendChord(VkControlKey, 0x52);
        AppEvent? turned = app.WaitFor(e => e.Name == "shown" && e.Number("bitmapHeight") > e.Number("bitmapWidth"), 8000, mark);
        AppEvent? turnedState = app.WaitForState(s => s.Number("quarterTurns") == 1, 3000, mark);
        observed.Add($"Ctrl+R → turns {turnedState?.Number("quarterTurns")}, bitmap {turned?.Number("bitmapWidth")}×{turned?.Number("bitmapHeight")}");
        ok &= turned is not null && turnedState is not null;

        // Ctrl+0: fit again (turned: portrait).
        mark = app.Events.Count;
        app.SendChord(VkControlKey, 0x30);
        AppEvent? refit = app.WaitForState(s => s.Text("zoomMode") == "Fit", 3000, mark);
        observed.Add($"Ctrl+0 → {refit?.Text("zoomMode")} {refit?.Number("zoom"):0.###}");
        ok &= refit is not null && refit.Number("zoom") < fitZoom; // portrait fits smaller in a landscape window

        // → next file: unturned, fitted.
        mark = app.Events.Count;
        app.SendKey(VkRight);
        AppEvent? next = app.WaitForState(s => s.Text("kind") is "Image" or "Pdf" or "Message", 10000, mark);
        bool nextOk = next?.Number("quarterTurns") == 0 && next.Text("zoomMode") == "Fit";
        observed.Add($"→ next file: turns {next?.Number("quarterTurns")}, {next?.Text("zoomMode")}");
        ok &= nextOk;

        (bool closed, _) = app.Close(expectPlayerStop: false);
        ok &= closed;
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) AppPdfNavigation(string pdf)
    {
        using AppSession app = LaunchApp(pdf);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        var observed = new List<string>();
        AppEvent? first = app.WaitForState(s => s.Text("kind") == "Pdf", 15000);
        AppEvent? thumbnails = app.WaitFor(e => e.Name == "thumbnails", 5000);
        bool ok = first is not null && thumbnails?.Number("count") == 3;
        observed.Add($"thumbnails {thumbnails?.Number("count")}");

        int mark = app.Events.Count;
        app.SendChord(VkControlKey, 0x47); // Ctrl+G
        Thread.Sleep(300);
        app.SendKey(0x33); // "3"
        app.SendKey(0x0D); // Enter
        AppEvent? third = app.WaitForState(s => s.Text("kind") == "Pdf" && s.Number("pageIndex") == 2, 4000, mark);
        observed.Add($"Ctrl+G 3 Enter → page {(third is null ? "?" : "3")}");
        ok &= third is not null;

        foreach ((ushort key, int page, string name) in new[] { (VkHome, 0, "Home"), (VkEnd, 2, "End") })
        {
            mark = app.Events.Count;
            app.SendKey(key);
            AppEvent? state = app.WaitForState(s => s.Text("kind") == "Pdf" && s.Number("pageIndex") == page, 4000, mark);
            observed.Add($"{name} → page {(state is null ? "?" : (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))}");
            ok &= state is not null;
        }

        mark = app.Events.Count;
        app.SendChord(VkControlKey, 0x52);
        AppEvent? turned = app.WaitFor(e => e.Name == "pdf-rendered" && e.Number("width") > e.Number("height"), 6000, mark);
        observed.Add($"Ctrl+R → landscape page {turned is not null}");
        ok &= turned is not null;

        (bool closed, _) = app.Close(expectPlayerStop: false);
        ok &= closed;
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) AppGifControls(string gif)
    {
        using AppSession app = LaunchApp(gif);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        AppEvent? playing = app.WaitForState(s => s.Text("kind") == "AnimatedImage", 15000);
        Thread.Sleep(600);
        int mark = app.Events.Count;
        app.SendKey(Native.VK_SPACE);
        AppEvent? paused = app.WaitFor(e => e.Name == "gif-pause", 2000, mark);
        Thread.Sleep(300);
        int afterPause = app.Events.Count;
        Thread.Sleep(1000);
        int framesWhilePaused = app.Events.Skip(afterPause).Count(e => e.Name == "gif-frame");

        mark = app.Events.Count;
        app.SendKey(VkOemPeriod);
        AppEvent? forward = app.WaitFor(e => e.Name == "gif-step", 2000, mark);
        mark = app.Events.Count;
        app.SendKey(VkOemComma);
        AppEvent? back = app.WaitFor(e => e.Name == "gif-step", 2000, mark);
        bool stepsOk = forward?.Number("frame") is { } f1 && back?.Number("frame") is { } f2 && (int)f2 == ((int)f1 + 2) % 3;

        mark = app.Events.Count;
        app.SendKey(Native.VK_SPACE);
        AppEvent? resumed = app.WaitFor(e => e.Name == "gif-resume", 2000, mark);
        Thread.Sleep(800);
        int framesAfter = app.Events.Skip(mark).Count(e => e.Name == "gif-frame");
        (bool closed, _) = app.Close(expectPlayerStop: false);
        bool ok = playing is not null && paused is not null && framesWhilePaused == 0 && stepsOk && resumed is not null && framesAfter >= 3 && closed;
        return (ok, $"pause={paused is not null}, frames while paused {framesWhilePaused}; '.' → frame {forward?.Number("frame")}, ',' → frame {back?.Number("frame")}; resume={resumed is not null}, frames after {framesAfter}");
    }

    private (bool, string) AppSvg(string svg)
    {
        using AppSession app = LaunchApp(svg);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        AppEvent? shown = app.WaitForState(s => s.Text("kind") is "Image" or "Message", 10000);
        Thread.Sleep(800);
        (bool visible, string pixel) = ContentVisible(app.Window);
        int mark = app.Events.Count;
        app.SendChord(VkControlKey, VkOemPlus);
        AppEvent? zoom = app.WaitFor(e => e.Name == "zoom", 2000, mark);
        (bool closed, _) = app.Close(expectPlayerStop: false);
        bool ok = shown?.Text("format") == "Svg" && shown.Text("kind") == "Image" && shown.Number("width") == 400 && visible && zoom?.Number("factor") == 1.25 && closed;
        return (ok, $"{shown?.Text("kind")} {shown?.Number("width")}×{shown?.Number("height")}, pixel {pixel}, Ctrl++ → {zoom?.Number("factor")}");
    }

    private (bool, string) AppInfoCopySettings(string file)
    {
        using AppSession app = LaunchApp(file);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        app.WaitForState(s => s.Text("kind") == "Image", 15000);
        int mark = app.Events.Count;
        app.SendChord(VkControlKey, 0x49); // Ctrl+I
        AppEvent? info = app.WaitFor(e => e.Name == "info-pane", 5000, mark);
        string fields = info?.Text("fields") ?? string.Empty;
        bool infoOk = info?.Number("rows") >= 6 && fields.Contains("Size", StringComparison.Ordinal) && fields.Contains("Dimensions", StringComparison.Ordinal);

        mark = app.Events.Count;
        ClearClipboard();
        app.SendChord(VkControlKey, 0x43); // Ctrl+C
        AppEvent? copied = app.WaitFor(e => e.Name is "copied" or "copy-error", 5000, mark);
        bool clipboardHasFile = WaitFor(ClipboardHasFiles, 3000, null);

        (bool closed, _) = app.Close(expectPlayerStop: false);
        Thread.Sleep(300);
        string settings = File.Exists(AppSettingsPath) ? File.ReadAllText(AppSettingsPath) : string.Empty;
        bool recent = false, window = false, infoPane = false;
        if (settings.Length > 0)
        {
            using JsonDocument doc = JsonDocument.Parse(settings);
            recent = doc.RootElement.TryGetProperty("recentFiles", out JsonElement list) && list.EnumerateArray().Any(e => string.Equals(e.GetString(), file, StringComparison.OrdinalIgnoreCase));
            window = doc.RootElement.TryGetProperty("window", out JsonElement w) && w.ValueKind == JsonValueKind.Object;
            infoPane = doc.RootElement.TryGetProperty("showInfoPane", out JsonElement pane) && pane.GetBoolean();
        }

        // Leave the default settings for the scenarios that follow.
        File.Delete(AppSettingsPath);
        bool ok = infoOk && copied?.Name == "copied" && clipboardHasFile && closed && recent && window && infoPane;
        return (ok, $"info rows {info?.Number("rows")} ({fields}); Ctrl+C → {copied?.Name}, clipboard has the file={clipboardHasFile}; settings: recent={recent}, window={window}, info pane={infoPane}");
    }

    /// <summary>Quick View: Ctrl+wheel zoom while Explorer keeps the keyboard, Ctrl+R / Ctrl+- after clicking into Quick View, SVG, and the next file reset.</summary>
    private void RunViewScenario(List<TestAsset> assets)
    {
        string folder = Path.GetDirectoryName(assets[0].Path)! + " View";
        Directory.CreateDirectory(folder);
        string photo = Path.Combine(folder, "01 photo.jpg");
        string drawing = Path.Combine(folder, "02 drawing.svg");
        if (!File.Exists(photo))
        {
            Task.Run(() => TestAssets.EncodeAsync(photo, BitmapEncoder.JpegEncoderId, 3200, 2400, 0.9)).GetAwaiter().GetResult();
        }

        WriteTestSvg(drawing);
        Guarded("quickview-zoom-rotate", () => QuickViewZoomRotate(photo, drawing),
            "Quick View on a 3200 × 2400 JPEG: Ctrl+wheel over the window zooms (Explorer keeps the keyboard) and the image is decoded again for the zoom; after a click into Quick View, Ctrl+R turns it and Ctrl+- zooms out; ↓ shows the SVG drawing fitted and unturned; Esc closes");
    }

    private (bool, string) QuickViewZoomRotate(string photo, string drawing)
    {
        nint explorer = OpenExplorer(photo);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Select(window, photo, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, photo, out string why))
        {
            return (false, why);
        }

        var observed = new List<string>();
        nint host = HostWindow();
        if (!Native.GetWindowRect(host, out Native.RECT r))
        {
            return (false, "no Quick View window");
        }

        // Ctrl + wheel up twice over Quick View, Explorer in front.
        long t = Stopwatch.GetTimestamp();
        Native.SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        Thread.Sleep(150);
        CtrlWheel(120);
        Thread.Sleep(80);
        CtrlWheel(120);
        (Mark? refine, Mark? refined) = WaitForNavigation(t, "zoom-refine");
        _log.Poll();
        List<Mark> zooms = _log.Marks.Where(m => m.Name == "zoom" && m.Qpc >= t).ToList();
        Mark? refineFull = refine is null ? null : _log.Find(refine.Request, "full-set");
        bool explorerKept = Native.GetForegroundWindow() == explorer;
        bool wheelOk = zooms.Count == 2 && zooms[1].Number("factor") > zooms[0].Number("factor") && refined?.Name == "full-visible" &&
            refineFull?.Number("decodedWidth") is { } decoded && Math.Abs(decoded - (zooms[1].Number("width") ?? 0)) <= 2 && explorerKept;
        observed.Add($"Ctrl+wheel ×2 → {zooms.LastOrDefault()?.Number("factor"):0.###}, re-decoded {refineFull?.Number("decodedWidth")} px (shown {zooms.LastOrDefault()?.Number("width")}), Explorer kept the keyboard={explorerKept}");

        // Click into Quick View (it becomes the active window), then Ctrl+R and Ctrl+-.
        Native.MouseEvent(0x0002 /* LEFTDOWN */, 0, 0, 0, 0);
        Native.MouseEvent(0x0004 /* LEFTUP */, 0, 0, 0, 0);
        bool active = WaitFor(() => Native.GetForegroundWindow() == host, 2000, null);
        t = Stopwatch.GetTimestamp();
        SendChordIfForeground(host, VkControlKey, 0x52);
        (Mark? rotate, Mark? rotated) = WaitForNavigation(t, "rotate");
        bool rotateOk = rotated?.Name == "full-visible" && _log.Marks.Any(m => m.Name == "rotate" && m.Qpc >= t && m.Number("quarterTurns") == 1);
        observed.Add($"click → active={active}; Ctrl+R → turned={rotateOk}");

        t = Stopwatch.GetTimestamp();
        SendChordIfForeground(host, VkControlKey, VkOemMinus);
        bool zoomOut = WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "zoom" && m.Qpc >= t && m.Text("command") == "Out"); }, 2000, null);
        observed.Add($"Ctrl+- → {zoomOut}");

        // ↓ in Quick View: the SVG, fitted and unturned (no zoom carried over).
        t = Stopwatch.GetTimestamp();
        SendKeyIfForeground(Native.VK_DOWN, host, out _);
        (Mark? toSvg, Mark? svgDone) = WaitForNavigation(t, "explorer-selection"); // a single item: Explorer's selection moves
        Mark? svgFull = toSvg is null ? null : _log.Find(toSvg.Request, "full-set");
        bool svgOk = svgDone?.Name == "full-visible" && svgFull?.Text("decoder") == "svg" && ShowsFile(toSvg!.Request, drawing);
        bool svgPixels = WaitFor(() => TryImagePixel(out _), 2000, null);
        observed.Add($"↓ SVG shown={svgOk}, pixels={svgPixels}");

        long tClose = Stopwatch.GetTimestamp();
        SendKeyIfForeground(Native.VK_ESCAPE, host, out _);
        bool closed = WaitFor(() => { _log.Poll(); return _log.HiddenSince(tClose) is not null; }, 3000, null);
        observed.Add($"Esc closed={closed}");
        return (wheelOk && active && rotateOk && zoomOut && svgOk && svgPixels && closed, string.Join("; ", observed));
    }

    private static void CtrlWheel(int delta)
    {
        Native.INPUT[] down = [Key(VkControlKey, false)];
        Native.SendInput(1, down, Marshal.SizeOf<Native.INPUT>());
        Thread.Sleep(30);
        Native.MouseEvent(0x0800 /* MOUSEEVENTF_WHEEL */, 0, 0, unchecked((uint)delta), 0);
        Thread.Sleep(30);
        Native.INPUT[] up = [Key(VkControlKey, true)];
        Native.SendInput(1, up, Marshal.SizeOf<Native.INPUT>());
    }

    private static bool SendChordIfForeground(nint expected, ushort modifier, ushort vk)
    {
        if (Native.GetForegroundWindow() != expected)
        {
            return false;
        }

        Native.INPUT[] inputs = [Key(modifier, false), Key(vk, false), Key(vk, true), Key(modifier, true)];
        return Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>()) == inputs.Length;
    }

    private static void ClearClipboard()
    {
        if (Native.OpenClipboard(0))
        {
            Native.EmptyClipboard();
            Native.CloseClipboard();
        }
    }

    private static bool ClipboardHasFiles() => Native.IsClipboardFormatAvailable(15 /* CF_HDROP */);
}

internal static partial class Native
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);
}
