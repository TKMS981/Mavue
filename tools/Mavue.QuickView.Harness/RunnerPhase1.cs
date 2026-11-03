using System.Diagnostics;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Quick Preview Phase 1 on the real desktop (docs/ARCHITECTURE.md §4.7.8): pixel inspector, filmstrip, pin, dragging
/// the file out, and Universal Preview's information card.
/// </summary>
internal sealed partial class Runner
{
    private void RunPhase1Scenarios(List<TestAsset> assets)
    {
        string root = Path.GetDirectoryName(assets[0].Path)!;
        string folder = root + " Phase1";
        string films = root + " Phase1 フィルム";
        string cards = root + " Phase1 カード";
        string drop = root + " Phase1 ドロップ先";
        foreach (string f in new[] { folder, films, cards })
        {
            Directory.CreateDirectory(f);
        }

        if (Directory.Exists(drop))
        {
            Directory.Delete(drop, recursive: true);
        }

        Directory.CreateDirectory(drop);

        // Quadrants: red, green, half-transparent blue, #123456.
        string colors = Path.Combine(folder, "01 colors.png");
        string dragImage = Path.Combine(folder, "02 drag.png");
        Task.Run(async () =>
        {
            await WriteQuadrantPngAsync(colors, 400, 300);
            await WriteQuadrantPngAsync(dragImage, 200, 150);
            for (int i = 1; i <= 6; i++)
            {
                string film = Path.Combine(films, $"0{i} film.jpg");
                if (!File.Exists(film))
                {
                    await TestAssets.EncodeAsync(film, BitmapEncoder.JpegEncoderId, 320, 240, 0.9);
                }
            }
        }).GetAwaiter().GetResult();

        string docx = Path.Combine(cards, "01 report.docx");
        string zip = Path.Combine(cards, "02 setup.7z"); // ZIP files have their own preview since Phase 3 (archive-* scenarios)
        string data = Path.Combine(cards, "03 data.bin");
        string subfolder = Path.Combine(cards, "04 folder");
        var random = new Random(7);
        byte[] bytes = new byte[40_000];
        random.NextBytes(bytes);
        bytes[0] = 0x11;
        File.WriteAllBytes(docx, bytes);
        File.WriteAllBytes(data, bytes);
        File.WriteAllBytes(zip, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04, .. new byte[24]]); // a 7z signature
        File.WriteAllText(zip + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://downloads.example.com/setup.7z\r\n");
        Directory.CreateDirectory(subfolder);
        for (int i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(subfolder, $"{i}.txt"), "x");
        }

        WritePhase1Settings(filmstrip: false, inspector: true);
        Guarded("pixel-inspector", () => PixelInspectorScenario(colors),
            "Inspector on: the pointer over a 400×300 PNG shows X/Y, RGB, HEX and alpha of the file's pixel (red, #123456, half-transparent blue), read from full-resolution tiles; it hides off the image");

        WritePhase1Settings(filmstrip: true, inspector: false);
        Guarded("filmstrip", () => FilmstripScenario(films),
            "Filmstrip on: Space on the 3rd of 6 images shows the 6 files in Explorer's order after the preview; clicking the 5th selects it in Explorer and Quick View follows; a multi-selection shows just the selected files");
        Guarded("pin", () => PinScenario(films),
            "Pin: the pinned preview ignores ↓ in Explorer, stays while another application is active and ignores Esc in Explorer; unpinned it follows Explorer's selection again");

        WritePhase1Settings(filmstrip: false, inspector: false);
        Guarded("drag-out", () => DragOutScenario(colors, dragImage, drop),
            "Dragging the file name, and the image while it fits, to an Explorer window of another folder on the same drive copies the file (Explorer would move it by default); the original stays");
        Guarded("info-card", () => InfoCardScenario(docx, zip, data, subfolder),
            "Universal Preview: a .docx and a .7z archive show the information card at once (no image decoder), the archive with its Mark-of-the-Web host; random data shows the card with the decoder's message; a folder shows its item count");
        WriteHostSettings();
    }

    private void WritePhase1Settings(bool filmstrip, bool inspector) => File.WriteAllText(
        HostSettingsPath,
        $"{{\"version\": 1, \"imageScale\": \"{(options.ScaleMode == "actual" ? "ActualSize" : "FitNoUpscale")}\", \"showFilmstrip\": {(filmstrip ? "true" : "false")}, \"showPixelInspector\": {(inspector ? "true" : "false")}{OpaqueBackground}}}");

    // ------------------------------------------------------------------ pixel inspector

    private (bool, string) PixelInspectorScenario(string colors)
    {
        nint explorer = OpenExplorer(colors);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        Select(window, colors, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, colors, out string why))
        {
            return (false, why);
        }

        nint host = HostWindow();
        if (!FindImageOnScreen(host, out Native.RECT image))
        {
            CloseFromExplorer(explorer);
            return (false, "image not found on screen");
        }

        var observed = new List<string> { $"image on screen {image.Right - image.Left}×{image.Bottom - image.Top}" };
        bool ok = true;
        int w = image.Right - image.Left, h = image.Bottom - image.Top;
        foreach ((double fx, double fy, string hex, string alpha, string label) in new[]
        {
            (0.25, 0.25, "#FF0000", "A 255 (100%)", "red"),
            (0.75, 0.75, "#123456", "A 255 (100%)", "#123456"),
            (0.25, 0.75, "#0000FF", "A 128 (50%)", "blue 50%"),
        })
        {
            MoveMouse((image.Left + (int)(w * fx), image.Top + (int)(h * fy)));
            string panel = string.Empty;
            WaitFor(() => (panel = UiaName(host, "QuickViewPixelInspector") ?? string.Empty).Contains(hex, StringComparison.Ordinal), 3000, null);
            bool itemOk = panel.Contains(hex, StringComparison.Ordinal) && panel.Contains(alpha, StringComparison.Ordinal);
            int x = ParseAfter(panel, "X "), y = ParseAfter(panel, "Y ");
            itemOk &= Math.Abs(x - (int)(400 * fx)) <= 4 && Math.Abs(y - (int)(300 * fy)) <= 4;
            ok &= itemOk;
            observed.Add(itemOk ? $"{label}: \"{panel}\"" : $"{label}: FAILED \"{panel}\"");
        }

        // Off the image (over the info bar): the panel goes away.
        Native.GetWindowRect(host, out Native.RECT hostRect);
        MoveMouse(((hostRect.Left + hostRect.Right) / 2, hostRect.Bottom - 20));
        bool hidden = WaitFor(() => UiaName(host, "QuickViewPixelInspector") is null, 2000, null);
        ok &= hidden;
        _log.Poll();
        int tiles = _log.Marks.Count(m => m.Name == "inspector-tile");
        observed.Add($"off the image: hidden={hidden}; tiles read={tiles}");
        ok &= tiles is >= 1 and <= 4; // a 400×300 image is at most four 256-pixel tiles
        bool closed = CloseFromExplorer(explorer);
        return (ok && closed, string.Join("; ", observed) + (closed ? string.Empty : "; Esc did not close"));
    }

    private static int ParseAfter(string text, string label)
    {
        int at = text.IndexOf(label, StringComparison.Ordinal);
        if (at < 0)
        {
            return -1;
        }

        int start = at + label.Length, end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return end > start ? int.Parse(text[start..end], System.Globalization.CultureInfo.InvariantCulture) : -1;
    }

    /// <summary>The bounding box of the quadrant PNG's pure red and #123456 pixels inside Quick View.</summary>
    private static bool FindImageOnScreen(nint host, out Native.RECT image)
    {
        image = default;
        if (!Native.GetWindowRect(host, out Native.RECT r))
        {
            return false;
        }

        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        byte[] pixels = Native.CaptureScreen(r.Left, r.Top, width, height);

        // The image's top-left quadrant is pure red and its bottom-right #123456. Each is found as the largest run of
        // its color. Since Phase 2 the bar is at the top and nothing with these colors is below the stage, so only the
        // window border is left out (the old 260 px margin cut the image off when the window was placed on a short screen).
        int searchHeight = Math.Max(1, height - 16);
        (int Left, int Top, int Right, int Bottom)? Box(Func<byte, byte, byte, bool> match)
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (int y = 0; y < searchHeight; y += 2)
            {
                int runStart = -1;
                for (int x = 0; x <= width; x++)
                {
                    int i = ((y * width) + Math.Min(x, width - 1)) * 4;
                    bool hit = x < width && match(pixels[i + 2], pixels[i + 1], pixels[i]);
                    if (hit && runStart < 0)
                    {
                        runStart = x;
                    }
                    else if (!hit && runStart >= 0)
                    {
                        if (x - runStart >= 40) // part of a quadrant, not an icon
                        {
                            (left, top, right, bottom) = (Math.Min(left, runStart), Math.Min(top, y), Math.Max(right, x - 1), Math.Max(bottom, y));
                        }

                        runStart = -1;
                    }
                }
            }

            return right < 0 ? null : (left, top, right, bottom);
        }

        var red = Box((rr, g, b) => rr > 240 && g < 15 && b < 15);
        var navy = Box((rr, g, b) => Math.Abs(rr - 0x12) < 6 && Math.Abs(g - 0x34) < 6 && Math.Abs(b - 0x56) < 6);
        if (red is not { } tl || navy is not { } br || br.Right <= tl.Left || br.Bottom <= tl.Top)
        {
            SaveCapture(Path.Combine(Path.GetTempPath(), "Mavue.QuickView.E2E", "phase1-capture.bmp"), pixels, width, height);
            return false;
        }

        image = new Native.RECT { Left = r.Left + tl.Left, Top = r.Top + tl.Top, Right = r.Left + br.Right + 1, Bottom = r.Top + br.Bottom + 2 };
        return true;
    }

    private static async Task WriteQuadrantPngAsync(string path, uint width, uint height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                (byte b, byte g, byte r, byte a) = (x < width / 2, y < height / 2) switch
                {
                    (true, true) => ((byte)0, (byte)0, (byte)255, (byte)255),
                    (false, true) => ((byte)0, (byte)255, (byte)0, (byte)255),
                    (true, false) => ((byte)255, (byte)0, (byte)0, (byte)128),
                    _ => ((byte)0x56, (byte)0x34, (byte)0x12, (byte)255),
                };
                long i = (((long)y * width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, a);
            }
        }

        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    // ------------------------------------------------------------------ filmstrip

    private (bool, string) FilmstripScenario(string films)
    {
        string third = Path.Combine(films, "03 film.jpg");
        string fifth = Path.Combine(films, "05 film.jpg");
        nint explorer = OpenExplorer(third);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Select(window, third, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        long t0 = Stopwatch.GetTimestamp();
        if (!OpenQuickView(explorer, third, out string why))
        {
            return (false, why);
        }

        var observed = new List<string>();
        WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "filmstrip-range" && m.Qpc >= t0); }, 5000, null);
        Mark? range = _log.Marks.LastOrDefault(m => m.Name == "filmstrip-range" && m.Qpc >= t0);
        Mark? shown = _log.Marks.LastOrDefault(m => m.Name == "full-visible" && m.Qpc >= t0);
        bool afterPreview = range is not null && shown is not null && range.Qpc > shown.Qpc;
        nint host = HostWindow();
        string[] names = UiaChildNames(host, "QuickViewFilmstrip");
        string[] expected = [.. Enumerable.Range(1, 6).Select(i => $"0{i} film.jpg")];
        bool listed = names.SequenceEqual(expected);
        observed.Add($"range read after the preview={afterPreview} ({range?.Number("count")} of {range?.Number("viewCount")}); strip=[{string.Join(", ", names)}]");

        long tClick = Stopwatch.GetTimestamp();
        string invoke = InvokeByUiAutomation(host, ["05 film.jpg"]);
        (Mark? nav, Mark? done) = WaitForNavigation(tClick, "explorer-selection");
        bool followed = done?.Name == "full-visible" && nav is not null && ShowsFile(nav.Request, fifth) && ExplorerSelectionIs(explorer, fifth);
        observed.Add($"click 05: {invoke}, Explorer selected it and Quick View followed={followed}");
        bool closed = CloseFromExplorer(explorer) || CloseHostWindow();

        // Multi-selection: the strip shows the selected files only.
        ForceForeground(explorer);
        Select(window, Path.Combine(films, "01 film.jpg"), SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        Select(window, Path.Combine(films, "02 film.jpg"), SvsiSelect);
        Select(window, Path.Combine(films, "04 film.jpg"), SvsiSelect);
        Thread.Sleep(400);
        bool multi = false;
        if (SendKeyIfForeground(Native.VK_SPACE, explorer, out long tMulti) && WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "full-visible" && m.Qpc >= tMulti); }, 8000, null))
        {
            Thread.Sleep(500);
            string[] multiNames = UiaChildNames(host, "QuickViewFilmstrip");
            multi = multiNames.SequenceEqual(["01 film.jpg", "02 film.jpg", "04 film.jpg"]);
            observed.Add($"multi-selection strip=[{string.Join(", ", multiNames)}]");
        }

        closed &= CloseFromExplorer(explorer) || CloseHostWindow();
        return (afterPreview && listed && followed && multi && closed, string.Join("; ", observed) + (closed ? string.Empty : "; not closed"));
    }

    // ------------------------------------------------------------------ pin

    private (bool, string) PinScenario(string films)
    {
        string first = Path.Combine(films, "01 film.jpg");
        string second = Path.Combine(films, "02 film.jpg");
        nint explorer = OpenExplorer(first);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Select(window, first, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, first, out string why))
        {
            return (false, why);
        }

        var observed = new List<string>();
        nint host = HostWindow();
        long tPin = Stopwatch.GetTimestamp();
        string pinned = InvokeByUiAutomation(host, ["Pin this preview (P)|このプレビューを固定 (P)"]);
        bool pinMarked = WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "pin" && m.Qpc >= tPin && m.Properties.TryGetValue("pinned", out var v) && v.GetBoolean()); }, 3000, null);
        observed.Add($"pin: {pinned}, logged={pinMarked}");

        // ↓ in Explorer: the selection moves, the pinned preview stays.
        ForceForeground(explorer);
        Thread.Sleep(300);
        long tDown = Stopwatch.GetTimestamp();
        SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
        Thread.Sleep(1200);
        _log.Poll();
        bool explorerMoved = ExplorerSelectionIs(explorer, second);
        bool stayed = !_log.Marks.Any(m => m.Name == "nav" && m.Qpc >= tDown) && Native.IsWindowVisible(host);
        observed.Add($"↓ in Explorer: Explorer moved={explorerMoved}, Quick View kept the pinned file={stayed}");

        // Another application: the pinned preview stays.
        bool keptWithOtherApp = true;
        if (options.AppPath is { } appPath && File.Exists(appPath))
        {
            using Process app = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false })!;
            nint appWindow = 0;
            WaitFor(() => (appWindow = Native.TopLevelWindows().FirstOrDefault(w => Native.IsWindowVisible(w) && Pid(w) == app.Id)) != 0, 10000, null);
            long tApp = Stopwatch.GetTimestamp();
            ForceForeground(appWindow);
            Thread.Sleep(1200);
            keptWithOtherApp = Native.IsWindowVisible(host) && _log.HiddenSince(tApp) is null;
            app.Kill();
            observed.Add($"other app active: Quick View stayed={keptWithOtherApp}");
        }

        // Esc in Explorer belongs to Explorer while pinned.
        ForceForeground(explorer);
        Thread.Sleep(300);
        long tEsc = Stopwatch.GetTimestamp();
        SendKeyIfForeground(Native.VK_ESCAPE, explorer, out _);
        Thread.Sleep(800);
        _log.Poll();
        bool escIgnored = Native.IsWindowVisible(host) && _log.HiddenSince(tEsc) is null;
        observed.Add($"Esc in Explorer ignored={escIgnored}");

        // Unpin: follows Explorer's current selection (the second file).
        long tUnpin = Stopwatch.GetTimestamp();
        string unpinned = InvokeByUiAutomation(host, ["Unpin (P)|固定を解除 (P)"]);
        (Mark? nav, Mark? done) = WaitForNavigation(tUnpin, "explorer-selection");
        bool follows = done?.Name == "full-visible" && nav is not null && ShowsFile(nav.Request, second);
        observed.Add($"unpin: {unpinned}, follows Explorer again (02)={follows}");

        bool closed = CloseFromExplorer(explorer) || CloseHostWindow();
        return (pinMarked && explorerMoved && stayed && keptWithOtherApp && escIgnored && follows && closed, string.Join("; ", observed) + (closed ? string.Empty : "; not closed"));
    }

    // ------------------------------------------------------------------ drag out

    private (bool, string) DragOutScenario(string colors, string dragImage, string drop)
    {
        List<Native.MonitorInfo> monitors = Native.Monitors();
        string keep = Path.Combine(drop, "00 keep.txt"); // something to select in the target folder
        File.WriteAllText(keep, "drop target");
        nint target = OpenExplorer(keep);
        if (target == 0)
        {
            return (false, "drop target window not found");
        }

        // Put the target on another monitor (or the right half) so Quick View does not cover it.
        if (monitors.Count > 1)
        {
            PutOn(target, monitors[1], 0.7, 0.7);
        }

        var observed = new List<string>();
        bool ok = true;
        foreach ((string file, bool fromImage) in new[] { (colors, false), (dragImage, true) })
        {
            nint explorer = OpenExplorer(file);
            dynamic? window = ShellWindowFor(explorer);
            if (explorer == 0 || window is null)
            {
                return (false, "Explorer window not found");
            }

            if (monitors.Count > 1)
            {
                PutOn(explorer, monitors[0], 0.6, 0.6);
            }

            Select(window, file, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
            if (!OpenQuickView(explorer, file, out string why))
            {
                return (false, why);
            }

            nint host = HostWindow();
            (int X, int Y) from;
            if (fromImage)
            {
                if (!FindImageOnScreen(host, out Native.RECT image))
                {
                    return (false, "image not found on screen");
                }

                from = ((image.Left + image.Right) / 2, (image.Top + image.Bottom) / 2);
            }
            else
            {
                string chip = UiaRectById(host, "QuickViewFileChip");
                int[] parts = chip.Split(',').Select(s => int.TryParse(s, out int v) ? v : -1).ToArray();
                if (parts.Length != 4 || parts.Any(v => v < -100000))
                {
                    return (false, $"file name area not found ({chip})");
                }

                from = (parts[0] + Math.Min(60, parts[2] / 2), parts[1] + (parts[3] / 2));
            }

            Native.GetWindowRect(target, out Native.RECT t);
            (int X, int Y) to = ((t.Left + t.Right) / 2, t.Top + ((t.Bottom - t.Top) * 2 / 3));
            long tDrag = Stopwatch.GetTimestamp();
            Drag(from, to);
            string copied = Path.Combine(drop, Path.GetFileName(file));
            bool arrived = WaitFor(() => File.Exists(copied), 6000, null);
            bool originalKept = File.Exists(file) && (!arrived || new FileInfo(copied).Length == new FileInfo(file).Length);
            _log.Poll();
            Mark? mark = _log.Marks.LastOrDefault(m => m.Name == "drag-out" && m.Qpc >= tDrag);
            bool itemOk = arrived && originalKept && mark?.Text("operations") == "Copy";
            ok &= itemOk;
            observed.Add($"{(fromImage ? "image" : "file name")}: copied={arrived}, original kept={originalKept}, operations={mark?.Text("operations") ?? "no drag"}");
            bool closed = CloseHostWindow() || CloseFromExplorer(explorer);
            ok &= closed;
        }

        return (ok, string.Join("; ", observed));
    }

    // ------------------------------------------------------------------ information card

    private (bool, string) InfoCardScenario(string docx, string zip, string data, string subfolder)
    {
        var observed = new List<string>();
        bool ok = true;
        foreach ((string path, string mustContain, bool decoderTried) in new[]
        {
            (docx, "01 report.docx", false),
            (zip, "example.com", false),
            (data, "この形式のデコーダー|No decoder", true),
            (subfolder, "3 個の項目|3 items", false),
        })
        {
            nint explorer = OpenExplorer(path);
            dynamic? window = ShellWindowFor(explorer);
            if (explorer == 0 || window is null)
            {
                return (false, $"Explorer window not found for {Path.GetFileName(path)}");
            }

            Select(window, path, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
            Thread.Sleep(options.SettleMilliseconds);
            if (!SendKeyIfForeground(Native.VK_SPACE, explorer, out long t0) && !(ForceForeground(explorer) && SendKeyIfForeground(Native.VK_SPACE, explorer, out t0)))
            {
                return (false, "foreground changed before Space");
            }

            bool carded = WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "info-card" && m.Qpc >= t0); }, 6000, null);
            Thread.Sleep(700); // the folder count and the cached thumbnail arrive afterwards
            _log.Poll();
            Mark? card = _log.Marks.LastOrDefault(m => m.Name == "info-card" && m.Qpc >= t0);
            bool triedDecoder = _log.Marks.Any(m => m.Name == "full-skipped" && m.Qpc >= t0);
            string[] texts = HostTexts();
            string all = string.Join(" | ", texts);
            bool contains = mustContain.Split('|').Any(s => all.Contains(s, StringComparison.Ordinal));
            bool loading = texts.Any(s => LoadingTexts.Contains(s));
            bool itemOk = carded && contains && !loading && triedDecoder == decoderTried;
            ok &= itemOk;
            observed.Add($"{Path.GetFileName(path)}: card={carded} ({card?.Text("category")}, motw={card?.Number("motw")?.ToString() ?? "-"}), decoder tried={triedDecoder}, shows \"{mustContain.Split('|')[0]}\"={contains}{(loading ? " LOADING" : string.Empty)}");
            ok &= CloseFromExplorer(explorer) || CloseHostWindow();
        }

        return (ok, string.Join("; ", observed));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Closes Quick View with Esc on its own window (when Explorer is not the foreground window).</summary>
    private bool CloseHostWindow()
    {
        nint host = HostWindow();
        if (host == 0 || !Native.IsWindowVisible(host))
        {
            return true;
        }

        long before = Stopwatch.GetTimestamp();
        ForceForeground(host);
        SendKeyIfForeground(Native.VK_ESCAPE, host, out _);
        return WaitFor(() => { _log.Poll(); return _log.HiddenSince(before) is not null; }, 3000, null);
    }

    /// <summary>The UI Automation name of the element with <paramref name="automationId"/> in the window, or null if it is not shown.</summary>
    private static string? UiaName(nint window, string automationId)
    {
        string output = RunUiaScript(window, automationId, "if ($el) { Write-Output $el.Current.Name } else { Write-Output '<none>' }");
        return output == "<none>" ? null : output;
    }

    /// <summary>"left,top,width,height" (physical pixels) of the element with <paramref name="automationId"/>.</summary>
    private static string UiaRectById(nint window, string automationId) =>
        RunUiaScript(window, automationId, "if ($el) { $r = $el.Current.BoundingRectangle; Write-Output ('{0},{1},{2},{3}' -f [int]$r.Left, [int]$r.Top, [int]$r.Width, [int]$r.Height) } else { Write-Output 'missing' }");

    /// <summary>Names of the buttons inside the element with <paramref name="automationId"/>, in order.</summary>
    private static string[] UiaChildNames(nint window, string automationId) =>
        RunUiaScript(window, automationId, "if ($el) { foreach ($b in $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) { Write-Output $b.Current.Name } }")
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string RunUiaScript(nint window, string automationId, string body)
    {
        string script = $$"""
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            Add-Type -Namespace Dpi -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr value);'
            [void][Dpi.Native]::SetProcessDpiAwarenessContext([IntPtr](-4))
            Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
            $A = [System.Windows.Automation.AutomationElement]
            $win = $A::FromHandle([IntPtr]{{window}})
            $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, '{{automationId}}')))
            {{body}}
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
}
