using System.Diagnostics;
using System.Runtime.InteropServices;
using Mavue.Core.Viewing;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Phase 1 checks that need more setup, run with <c>--phase1-real</c>: the pixel inspector with EXIF orientation,
/// view rotation and zoom; pin with mouse clicks in Explorer, Alt+Tab, other apps, Explorer closing, Space and P;
/// dragging the file into Word, Paint and Edge; the filmstrip in a folder of 30,000 files and among cloud-only
/// files (a test sync root that records every request for a placeholder's data).
/// </summary>
internal sealed partial class Runner
{
    private const ushort VkKeyP = 0x50;

    private void RunPhase1RealScenarios(List<TestAsset> assets)
    {
        string root = Path.GetDirectoryName(assets[0].Path)!;
        WritePhase1Settings(filmstrip: false, inspector: true);
        Guarded("inspector-transforms", () => InspectorTransforms(root),
            "Inspector: a TIFF stored turned (EXIF 6) whose colors encode each pixel's position, at 100 %, after Ctrl+R and zoomed in with Ctrl+wheel: the reported X/Y is the pixel whose color is reported, and that color is the one on screen; a JPEG with EXIF 8: the reported colors are those on screen");
        WritePhase1Settings(filmstrip: true, inspector: false);
        Guarded("pin-real", () => PinReal(root + " Phase1 フィルム"),
            "Pin by P and by the button: a mouse click on another file in Explorer, Alt+Tab, another application and closing the Explorer window leave the pinned preview; Esc on Quick View or Space in Explorer closes it; P again follows Explorer's selection");
        WritePhase1Settings(filmstrip: false, inspector: false);
        Guarded("drag-probe", () => DragProbe(Path.Combine(root + " Phase1", "01 colors.png")),
            "Diagnostics: a WinForms drop target in another process receives the dragged file (CF_HDROP) with Copy allowed, and logs the offered formats");
        Guarded("drag-paint-control", () => PaintControl(Path.Combine(root + " Phase1", "01 colors.png")),
            "Control experiment: the same file dragged from File Explorer itself onto Paint (does Paint open dropped files at all?)");
        Guarded("drag-apps", () => DragToApps(Path.Combine(root + " Phase1", "01 colors.png")),
            "The file name dragged to Edge (opened in a tab, separate profile), Paint (opened) and, with MAVUE_E2E_OFFICE=1, Word (inserted in a new document); the original is unchanged");
        WritePhase1Settings(filmstrip: true, inspector: false);
        Guarded("filmstrip-large", () => FilmstripLarge(root + " Phase1 大量"),
            "Filmstrip in a folder of 30,000 images: Space in the middle shows the preview as fast as usual, the strip reads only 121 items, ↓ ×50 reads the next range, End reads the last one; memory stays bounded");
        Guarded("filmstrip-cloud", () => FilmstripCloud(Path.Combine(Path.GetTempPath(), "Mavue.E2E.Cloud")),
            "Filmstrip among cloud-only files (test sync root): their items are shown, ↓ onto them shows the cloud notice or the card, and Quick View never requests their data (no download); they stay cloud-only");
        WriteHostSettings();
    }

    // ------------------------------------------------------------------ inspector: EXIF, rotation, zoom

    private (bool, string) InspectorTransforms(string root)
    {
        string folder = root + " Phase1 検査";
        Directory.CreateDirectory(folder);
        string tiff = Path.Combine(folder, "01 coords.tif");
        string jpeg = Path.Combine(folder, "02 quad8.jpg");
        Task.Run(async () =>
        {
            await WriteCoordinateTiffAsync(tiff, 1000, 700, orientation: 6);
            await WriteQuadrantJpegAsync(jpeg, 800, 600, orientation: 8);
        }).GetAwaiter().GetResult();

        var observed = new List<string>();
        bool ok = true;
        nint explorer = OpenExplorer(tiff);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Select(window, tiff, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, tiff, out string why))
        {
            return (false, why);
        }

        nint host = HostWindow();
        ok &= CheckCoordinates(host, "100 % (EXIF 6)", exact: true, observed);

        // Click into Quick View, then turn the view a quarter (Ctrl+R).
        if (ContentBox(host) is { } box)
        {
            Click(((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2));
        }

        long t = Stopwatch.GetTimestamp();
        bool turned = WaitFor(() => Native.GetForegroundWindow() == host, 2000, null) && SendChordIfForeground(host, VkControlKey, 0x52);
        (Mark? _, Mark? rotated) = WaitForNavigation(t, "rotate");
        turned &= rotated?.Name == "full-visible";
        Thread.Sleep(300);
        ok &= turned && CheckCoordinates(host, "turned by Ctrl+R", exact: true, observed);

        // Zoom in around the middle with Ctrl+wheel ×4 (re-decoded at the zoom).
        if (ContentBox(host) is { } zoomBox)
        {
            MoveMouse(((zoomBox.Left + zoomBox.Right) / 2, (zoomBox.Top + zoomBox.Bottom) / 2));
            Thread.Sleep(150);
            t = Stopwatch.GetTimestamp();
            for (int i = 0; i < 4; i++)
            {
                CtrlWheel(120);
                Thread.Sleep(80);
            }

            WaitForNavigation(t, "zoom-refine");
            Thread.Sleep(400);
            _log.Poll();
            double? factor = _log.Marks.LastOrDefault(m => m.Name == "zoom" && m.Qpc >= t)?.Number("factor");
            observed.Add($"zoom {factor:0.##}×");
            ok &= factor > 1.5 && CheckCoordinates(host, "zoomed in", exact: false, observed);
        }

        long tEsc = Stopwatch.GetTimestamp();
        ForceForeground(host);
        SendKeyIfForeground(Native.VK_ESCAPE, host, out _);
        WaitFor(() => { _log.Poll(); return _log.HiddenSince(tEsc) is not null; }, 3000, null);

        // JPEG with EXIF 8: the reported colors are those on screen in each quadrant.
        ForceForeground(explorer);
        Select(window, jpeg, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, jpeg, out why))
        {
            return (false, string.Join("; ", observed) + "; " + why);
        }

        if (ContentBox(host) is { } quad)
        {
            int matched = 0;
            foreach ((double fx, double fy) in new[] { (0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75) })
            {
                (int X, int Y) point = (quad.Left + (int)((quad.Right - quad.Left) * fx), quad.Top + (int)((quad.Bottom - quad.Top) * fy));
                MoveMouse(point);
                string panel = string.Empty;
                WaitFor(() => (panel = UiaName(host, "QuickViewPixelInspector") ?? string.Empty).Contains('#'), 3000, null);
                (byte r, byte g, byte b)? reported = ParseRgb(panel);
                (byte r, byte g, byte b) screen = ScreenColor(point);
                if (reported is { } c && Math.Abs(c.r - screen.r) <= 30 && Math.Abs(c.g - screen.g) <= 30 && Math.Abs(c.b - screen.b) <= 30)
                {
                    matched++;
                }
            }

            observed.Add($"JPEG EXIF 8: {matched}/4 quadrants match the screen");
            ok &= matched == 4;
        }

        ok &= CloseFromExplorer(explorer) || CloseHostWindow();
        return (ok, string.Join("; ", observed));
    }

    /// <summary>
    /// Points over the image: the reported color decodes to the stored pixel that the reported X/Y maps to through
    /// the EXIF orientation (exact), and it is the color on screen at the pointer.
    /// </summary>
    private bool CheckCoordinates(nint host, string label, bool exact, List<string> observed)
    {
        ParkPointer(host);
        if (ContentBox(host) is not { } box)
        {
            observed.Add($"{label}: image not found");
            return false;
        }

        int good = 0, total = 0;
        var notes = new List<string>();
        foreach ((double fx, double fy) in new[] { (0.21, 0.33), (0.5, 0.5), (0.77, 0.69) })
        {
            total++;
            (int X, int Y) point = (box.Left + (int)((box.Right - box.Left) * fx), box.Top + (int)((box.Bottom - box.Top) * fy));
            MoveMouse(point);
            string panel = string.Empty;
            WaitFor(() => (panel = UiaName(host, "QuickViewPixelInspector") ?? string.Empty).Contains('#'), 3000, null);
            int x = ParseAfter(panel, "X "), y = ParseAfter(panel, "Y ");
            (byte r, byte g, byte b)? rgb = ParseRgb(panel);
            if (x < 0 || rgb is not { } c)
            {
                notes.Add($"no reading \"{panel}\"");
                continue;
            }

            (int sx, int sy) = PixelInspection.StoredPixel(x, y, 1000, 700, 6);
            (int dx, int dy) = (c.r + ((c.b >> 4) * 256), c.g + ((c.b & 15) * 256));
            (byte r, byte g, byte b) screen = ScreenColor(point);
            bool position = (sx, sy) == (dx, dy);
            bool onScreen = exact
                ? Math.Abs(screen.r - c.r) <= 2 && Math.Abs(screen.g - c.g) <= 2 && Math.Abs(screen.b - c.b) <= 2
                : c.r is < 6 or > 249 || c.g is < 6 or > 249 || (Math.Abs(screen.r - c.r) <= 12 && Math.Abs(screen.g - c.g) <= 12 && Math.Abs(screen.b - c.b) <= 12);
            if (position && onScreen)
            {
                good++;
            }
            else
            {
                notes.Add($"({x},{y}) → stored ({sx},{sy}) but color says ({dx},{dy}); screen {screen} vs {c}");
            }
        }

        observed.Add($"{label}: {good}/{total}{(notes.Count > 0 ? " [" + string.Join("; ", notes) + "]" : string.Empty)}");
        return good == total;
    }

    private static (byte R, byte G, byte B)? ParseRgb(string panel)
    {
        int r = ParseAfter(panel, "R "), g = ParseAfter(panel, "G "), b = ParseAfter(panel, "B ");
        return r < 0 || g < 0 || b < 0 ? null : ((byte)r, (byte)g, (byte)b);
    }

    private static (byte R, byte G, byte B) ScreenColor((int X, int Y) point)
    {
        byte[] pixel = Native.CaptureScreen(point.X, point.Y, 1, 1);
        return (pixel[2], pixel[1], pixel[0]);
    }

    /// <summary>Screen box of what is not Quick View's #1E1E1E background, between the title bar and the info bar.</summary>
    private static Native.RECT? ContentBox(nint host)
    {
        if (!Native.GetWindowRect(host, out Native.RECT r))
        {
            return null;
        }

        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        byte[] pixels = Native.CaptureScreen(r.Left, r.Top, w, h);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 80; y < h - 200; y += 2)
        {
            for (int x = 20; x < w - 20; x += 2)
            {
                int i = ((y * w) + x) * 4;
                if (Math.Abs(pixels[i] - 30) + Math.Abs(pixels[i + 1] - 30) + Math.Abs(pixels[i + 2] - 30) > 40)
                {
                    (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                }
            }
        }

        return maxX < 0 ? null : new Native.RECT { Left = r.Left + minX + 4, Top = r.Top + minY + 4, Right = r.Left + maxX - 4, Bottom = r.Top + maxY - 4 };
    }

    /// <summary>A lossless TIFF whose colors encode each stored pixel's position: R = x mod 256, G = y mod 256, B = (x / 256) · 16 + y / 256.</summary>
    private static async Task WriteCoordinateTiffAsync(string path, uint width, uint height, int orientation)
    {
        byte[] pixels = new byte[width * height * 4];
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                long i = (((long)y * width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(((x >> 8) << 4) | (y >> 8)), (byte)(y & 255), (byte)(x & 255), 255);
            }
        }

        await EncodeAsync(path, BitmapEncoder.TiffEncoderId, width, height, pixels, orientation);
    }

    private static async Task WriteQuadrantJpegAsync(string path, uint width, uint height, int orientation)
    {
        (byte B, byte G, byte R)[] quadrants = [(0, 0, 230), (0, 200, 0), (220, 0, 0), (0, 220, 220)];
        byte[] pixels = new byte[width * height * 4];
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                (byte b, byte g, byte r) = quadrants[(y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1)];
                long i = (((long)y * width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
            }
        }

        await EncodeAsync(path, BitmapEncoder.JpegEncoderId, width, height, pixels, orientation);
    }

    private static async Task EncodeAsync(string path, Guid encoderId, uint width, uint height, byte[] pixels, int orientation)
    {
        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
        {
            ["System.Photo.Orientation"] = new BitmapTypedValue((ushort)orientation, Windows.Foundation.PropertyType.UInt16),
        });
        await encoder.FlushAsync();
    }

    // ------------------------------------------------------------------ pin with real-world actions

    private (bool, string) PinReal(string films)
    {
        string first = Path.Combine(films, "01 film.jpg");
        string second = Path.Combine(films, "02 film.jpg");
        string fourth = Path.Combine(films, "04 film.jpg");
        var observed = new List<string>();
        bool ok = true;

        // A: pinned with P; mouse click in Explorer, Alt+Tab, another app, Explorer closed; Esc on Quick View closes.
        nint explorer = OpenExplorer(first);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null || !OpenQuickView(explorer, first, out string why))
        {
            return (false, "A: Quick View not shown");
        }

        window.Document.CurrentViewMode = FvmDetails;
        nint host = HostWindow();
        long tPin = Stopwatch.GetTimestamp();
        bool pinnedByKey = ActivateHost(host) && SendKeyIfForeground(VkKeyP, host, out _) && WaitForPin(tPin, true);
        observed.Add($"A: P pins={pinnedByKey}");

        // Quick View covers the Explorer window it came from; as a user would, move Explorer where its files are visible.
        List<Native.MonitorInfo> screens = Native.Monitors();
        if (screens.Count > 1)
        {
            PutOn(explorer, screens[1], 0.6, 0.6);
            Thread.Sleep(800);
        }

        long tA = Stopwatch.GetTimestamp();
        string rect = UiaRect(explorer, "03 film");
        int[] r3 = rect.Split(',').Select(s => int.TryParse(s, out int v) ? v : -1).ToArray();
        bool clicked = false;
        if (r3.Length == 4 && r3[2] > 0)
        {
            Click((r3[0] + Math.Min(80, r3[2] / 2), r3[1] + (r3[3] / 2)));
            clicked = WaitFor(() => ExplorerSelectionIs(explorer, Path.Combine(films, "03 film.jpg")), 2000, null);
        }

        Thread.Sleep(600);
        _log.Poll();
        bool keptOnClick = clicked && !_log.Marks.Any(m => m.Name == "nav" && m.Qpc >= tA) && Native.IsWindowVisible(host);
        observed.Add($"mouse click on 03 in Explorer: selected={clicked}, preview kept 01={keptOnClick}");

        ForceForeground(explorer);
        SendAltTab(explorer);
        Thread.Sleep(1000);
        bool keptAltTab = Native.IsWindowVisible(host) && _log.HiddenSince(tA) is null;
        observed.Add($"Alt+Tab → {Native.Describe(Native.GetForegroundWindow())}: kept={keptAltTab}");

        bool keptOtherApp = true;
        if (options.AppPath is { } appPath && File.Exists(appPath))
        {
            using Process app = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false })!;
            nint appWindow = 0;
            WaitFor(() => (appWindow = Native.TopLevelWindows().FirstOrDefault(w => Native.IsWindowVisible(w) && Pid(w) == app.Id)) != 0, 10000, null);
            ForceForeground(appWindow);
            Thread.Sleep(1000);
            keptOtherApp = Native.IsWindowVisible(host) && _log.HiddenSince(tA) is null;
            app.Kill();
            observed.Add($"other app: kept={keptOtherApp}");
        }

        long tClosed = Stopwatch.GetTimestamp();
        window.Quit(); // the Explorer window Quick View came from closes
        Thread.Sleep(1500);
        _log.Poll();
        bool keptExplorerClosed = Native.IsWindowVisible(host) && _log.Marks.Any(m => m.Name == "explorer-closed-pinned" && m.Qpc >= tClosed);
        observed.Add($"Explorer window closed: kept={keptExplorerClosed}");
        bool escCloses = CloseHostWindow();
        observed.Add($"Esc on Quick View closes={escCloses}");
        _createdExplorers.Remove(explorer);
        ok &= pinnedByKey && keptOnClick && keptAltTab && keptOtherApp && keptExplorerClosed && escCloses;

        // B: pinned with the button; Space in Explorer closes it.
        explorer = OpenExplorer(second);
        window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null || !OpenQuickView(explorer, second, out why))
        {
            return (false, string.Join("; ", observed) + "; B: Quick View not shown");
        }

        host = HostWindow();
        tPin = Stopwatch.GetTimestamp();
        InvokeByUiAutomation(host, ["Pin this preview (P)|このプレビューを固定 (P)"]);
        bool pinnedByButton = WaitForPin(tPin, true);

        // The user clicks the file in Explorer (keyboard focus back in its file list), then presses Space.
        ExposeExplorer(explorer, host);

        int[] r2 = UiaRect(explorer, "02 film").Split(',').Select(s => int.TryParse(s, out int v) ? v : -1).ToArray();
        if (r2.Length == 4 && r2[2] > 0)
        {
            Click((r2[0] + Math.Min(80, r2[2] / 2), r2[1] + (r2[3] / 2)));
        }

        WaitFor(() => Native.GetForegroundWindow() == explorer && Native.Focus(explorer).FocusClass == ItemViewClass, 3000, null);
        long tSpace = Stopwatch.GetTimestamp();
        SendKeyIfForeground(Native.VK_SPACE, explorer, out _);
        bool spaceCloses = WaitFor(() => { _log.Poll(); return _log.HiddenSince(tSpace)?.Text("reason") == "space-toggle"; }, 3000, null);
        observed.Add($"B: button pins={pinnedByButton}, Space in Explorer closes={spaceCloses}");
        ok &= pinnedByButton && spaceCloses;

        // C: pinned, Explorer moves on twice, P again: follows Explorer's selection (the fourth file).
        Select(window, second, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, second, out why))
        {
            return (false, string.Join("; ", observed) + "; C: Quick View not shown");
        }

        tPin = Stopwatch.GetTimestamp();
        bool pinnedC = ActivateHost(host) && SendKeyIfForeground(VkKeyP, host, out _) && WaitForPin(tPin, true);
        ExposeExplorer(explorer, host);
        int[] r2c = UiaRect(explorer, "02 film").Split(',').Select(s => int.TryParse(s, out int v) ? v : -1).ToArray();
        if (r2c.Length == 4 && r2c[2] > 0)
        {
            Click((r2c[0] + Math.Min(80, r2c[2] / 2), r2c[1] + (r2c[3] / 2))); // back to Explorer's file list, as a user would
        }

        WaitFor(() => Native.GetForegroundWindow() == explorer && Native.Focus(explorer).FocusClass == ItemViewClass, 3000, null);
        SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
        Thread.Sleep(300);
        SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
        Thread.Sleep(500);
        long tUnpin = Stopwatch.GetTimestamp();
        bool unpinned = ActivateHost(host) && SendKeyIfForeground(VkKeyP, host, out _) && WaitForPin(tUnpin, false);
        (Mark? nav, Mark? done) = WaitForNavigation(tUnpin, "explorer-selection");
        bool follows = done?.Name == "full-visible" && nav is not null && ShowsFile(nav.Request, fourth);
        observed.Add($"C: P pins={pinnedC}, P unpins={unpinned} and follows Explorer to 04={follows}");
        ok &= pinnedC && unpinned && follows;
        ok &= CloseHostWindow();
        return (ok, string.Join("; ", observed));
    }

    private bool WaitForPin(long since, bool pinned) =>
        WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "pin" && m.Qpc >= since && m.Bool("pinned") == pinned); }, 3000, null);

    /// <summary>Clicks into Quick View's info bar (left of the buttons) so it becomes the active window.</summary>
    private static bool ActivateHost(nint host)
    {
        if (Native.GetForegroundWindow() == host)
        {
            return true;
        }

        Native.GetWindowRect(host, out Native.RECT r);
        Click((r.Left + ((r.Right - r.Left) / 3), r.Bottom - 120)); // empty space of the surface above the info bar
        return WaitFor(() => Native.GetForegroundWindow() == host, 2000, null);
    }

    // ------------------------------------------------------------------ drag into other applications

    private (bool, string) DragToApps(string file)
    {
        if (!File.Exists(file))
        {
            return (false, "run the Phase 1 scenarios first (01 colors.png)");
        }

        long size = new FileInfo(file).Length;
        DateTime written = File.GetLastWriteTimeUtc(file);
        List<Native.MonitorInfo> monitors = Native.Monitors();
        var observed = new List<string>();
        bool ok = true;

        // Word: a new document; the drop inserts the picture.
        // Office is opt-in (MAVUE_E2E_OFFICE=1): an unlicensed or signed-out Office shows dialogs that block automation.
        if (System.Environment.GetEnvironmentVariable("MAVUE_E2E_OFFICE") == "1" && Process.GetProcessesByName("WINWORD").Length == 0 && Type.GetTypeFromProgID("Word.Application") is { } wordType)
        {
            var wordsBefore = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
            dynamic word = Activator.CreateInstance(wordType)!;
            try
            {
                word.Visible = true;
                dynamic doc = word.Documents.Add();
                Thread.Sleep(4000); // Word takes the foreground while it starts; let it settle before Quick View opens
                nint wordWindow = (nint)(int)word.ActiveWindow.Hwnd;
                wordWindow = Native.GetAncestor(wordWindow, 2);
                (bool dropped, string note) = DropInto(file, wordWindow, monitors, () =>
                {
                    try
                    {
                        return (int)doc.InlineShapes.Count + (int)doc.Shapes.Count > 0;
                    }
                    catch (Exception ex) when (ex is COMException or MissingMemberException)
                    {
                        return false; // Word is busy with the drop; ask again
                    }
                });
                observed.Add($"Word: inserted={dropped} {note}");
                ok &= dropped;
                try
                {
                    doc.Close(0); // wdDoNotSaveChanges
                }
                catch (Exception ex) when (ex is COMException or MissingMemberException)
                {
                    // Busy (e.g. its licensing dialog): the instance is closed below.
                }
            }
            catch (Exception ex) when (ex is COMException or MissingMemberException)
            {
                observed.Add($"Word: automation refused ({ex.Message})");
                ok = false;
            }
            finally
            {
                try
                {
                    word.Quit(0);
                }
                catch (Exception ex) when (ex is COMException or MissingMemberException)
                {
                    // Closed below.
                }

                Marshal.FinalReleaseComObject(word);
                foreach (Process p in Process.GetProcessesByName("WINWORD").Where(p => !wordsBefore.Contains(p.Id)))
                {
                    p.Kill(); // only the instance this test started (its document was never saved)
                }
            }
        }
        else
        {
            observed.Add("Word: skipped (Office is opt-in: MAVUE_E2E_OFFICE=1)");
        }

        // Paint: the drop opens the picture.
        {
            var before = Process.GetProcessesByName("mspaint").Select(p => p.Id).ToHashSet();
            Process.Start(new ProcessStartInfo("mspaint.exe") { UseShellExecute = true });
            nint paint = 0;
            WaitFor(() => (paint = Native.TopLevelWindows().FirstOrDefault(w => Native.IsWindowVisible(w) && ProcessName(w) == "mspaint" && !before.Contains((int)Pid(w)))) != 0, 15000, null);
            if (paint == 0)
            {
                observed.Add("Paint: not started");
                ok = false;
            }
            else
            {
                Thread.Sleep(1500);
                (bool dropped, string note) = DropInto(file, paint, monitors, () => Native.WindowText(paint).Contains("01 colors", StringComparison.OrdinalIgnoreCase));
                observed.Add($"Paint: opened={dropped} (\"{Native.WindowText(paint)}\") {note}");
                ok &= dropped;
                foreach (Process p in Process.GetProcessesByName("mspaint").Where(p => !before.Contains(p.Id)))
                {
                    p.Kill();
                }
            }
        }

        // Edge, with a separate empty profile: the drop opens the file in the tab.
        {
            string profile = Path.Combine(Path.GetTempPath(), "Mavue.E2E.EdgeProfile");
            string edgeExe = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            var before = Native.TopLevelWindows().ToHashSet();
            using Process edge = Process.Start(new ProcessStartInfo(edgeExe, $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --new-window about:blank") { UseShellExecute = false })!;
            nint edgeWindow = 0;
            WaitFor(() => (edgeWindow = Native.TopLevelWindows().FirstOrDefault(w => !before.Contains(w) && Native.IsWindowVisible(w) && Native.ClassName(w) == "Chrome_WidgetWin_1" && ProcessName(w) == "msedge")) != 0, 15000, null);
            if (edgeWindow == 0)
            {
                observed.Add("Edge: not started");
                ok = false;
            }
            else
            {
                Thread.Sleep(2000);
                (bool dropped, string note) = DropInto(file, edgeWindow, monitors, () => Native.WindowText(edgeWindow).Contains("01 colors", StringComparison.OrdinalIgnoreCase));
                observed.Add($"Edge: opened={dropped} {note}");
                ok &= dropped;
            }

            try
            {
                edge.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        bool unchanged = File.Exists(file) && new FileInfo(file).Length == size && File.GetLastWriteTimeUtc(file) == written;
        observed.Add($"original unchanged={unchanged}");
        return (ok && unchanged, string.Join("; ", observed));
    }

    /// <summary>Shows <paramref name="file"/> in Quick View and drags its name onto <paramref name="target"/>.</summary>
    private (bool Dropped, string Note) DropInto(string file, nint target, List<Native.MonitorInfo> monitors, Func<bool> arrived)
    {
        if (monitors.Count > 1)
        {
            PutOn(target, monitors[1], 0.7, 0.7);
        }

        ForceForeground(target); // above other windows at that place (e.g. an Explorer window from an earlier drop)
        Thread.Sleep(300);
        nint explorer = OpenExplorer(file);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "(no Explorer)");
        }

        if (monitors.Count > 1)
        {
            PutOn(explorer, monitors[0], 0.6, 0.6);
        }

        Select(window, file, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, file, out string why))
        {
            return (false, $"({why})");
        }

        nint host = HostWindow();
        int[] chip = [];
        WaitFor(() => (chip = UiaRectById(host, "QuickViewFileChip").Split(',').Select(s => int.TryParse(s, out int v) ? v : -1).ToArray()) is [_, _, > 0, _], 4000, null);
        if (chip.Length != 4 || chip[2] <= 0)
        {
            return (false, "(file name area not found)");
        }

        Native.GetWindowRect(target, out Native.RECT t);
        long tDrag = Stopwatch.GetTimestamp();
        HoverDrag((chip[0] + Math.Min(60, chip[2] / 2), chip[1] + (chip[3] / 2)), ((t.Left + t.Right) / 2, (t.Top + t.Bottom) / 2));
        bool done = WaitFor(arrived, 8000, null);
        _log.Poll();
        WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "drag-out-end" && m.Qpc >= tDrag); }, 3000, null);
        string operations = _log.Marks.LastOrDefault(m => m.Name == "drag-out" && m.Qpc >= tDrag)?.Text("operations") ?? "no drag";
        string effect = _log.Marks.LastOrDefault(m => m.Name == "drag-out-end" && m.Qpc >= tDrag)?.Text("effect") ?? "-";
        CloseHostWindow();
        return (done && operations == "Copy", $"(offered={operations}, effect={effect})");
    }

    // ------------------------------------------------------------------ filmstrip: 30,000 files

    private (bool, string) FilmstripLarge(string folder)
    {
        const int Count = 30_000;
        Directory.CreateDirectory(folder);
        string source = Path.Combine(Path.GetDirectoryName(folder)!, "Mavue.E2E.LargeSource.jpg");
        if (!File.Exists(source))
        {
            Task.Run(() => TestAssets.EncodeAsync(source, BitmapEncoder.JpegEncoderId, 320, 240, 0.8)).GetAwaiter().GetResult();
        }

        long createStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < Count; i++)
        {
            string link = Path.Combine(folder, $"f{i:D5}.jpg");
            string linkSource = source + $".{i / 1000}";
            if (!File.Exists(linkSource))
            {
                File.Copy(source, linkSource);
            }

            if (!File.Exists(link) && !Native.CreateHardLink(link, linkSource, 0))
            {
                return (false, $"hard link {i} failed ({Marshal.GetLastPInvokeError()})");
            }
        }

        var observed = new List<string> { $"{Count} files ready in {Ms(createStart, Stopwatch.GetTimestamp()):0} ms" };
        string middle = Path.Combine(folder, "f15000.jpg");
        nint explorer = OpenExplorer(middle);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Thread.Sleep(2000); // Explorer enumerates the folder
        Select(window, middle, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        long t0 = Stopwatch.GetTimestamp();
        if (!OpenQuickView(explorer, middle, out string why))
        {
            return (false, why);
        }

        _log.Poll();
        Mark? hook = _log.Marks.FirstOrDefault(m => m.Name == "hook" && m.Qpc >= t0);
        Mark? visible = hook is null ? null : _log.Find(hook.Request, "full-visible");
        WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "filmstrip-range" && m.Qpc >= t0); }, 8000, null);
        Mark? range = _log.Marks.FirstOrDefault(m => m.Name == "filmstrip-range" && m.Qpc >= t0);
        double rangeMs = range is null ? -1 : (range.Qpc - (long)(range.Number("startQpc") ?? range.Qpc)) * 1000.0 / Stopwatch.Frequency;
        long privateBefore = HostPrivateBytes();
        bool firstOk = range?.Number("count") == 121 && range.Number("viewCount") == Count && visible is not null;
        observed.Add($"Space → full-visible {(hook is null || visible is null ? -1 : Ms(hook.Qpc, visible.Qpc)):0} ms; range {range?.Number("count")} of {range?.Number("viewCount")} read in {rangeMs:0} ms");

        // ↓ ×50 at a quick pace: the strip moves its highlight and reads the next range near its end.
        long tNav = Stopwatch.GetTimestamp();
        for (int i = 0; i < 50; i++)
        {
            SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
            Thread.Sleep(70);
        }

        bool reached = WaitFor(() => ExplorerSelectionIs(explorer, Path.Combine(folder, "f15050.jpg")), 3000, null);
        Thread.Sleep(1500);
        _log.Poll();
        List<Mark> reloads = _log.Marks.Where(m => m.Name == "filmstrip-range" && m.Qpc >= tNav).ToList();
        int errors = _log.Marks.Count(m => m.Name is "error" or "prefetch-error" && m.Qpc >= tNav);
        Mark? lastNav = _log.Marks.LastOrDefault(m => m.Name == "nav" && m.Qpc >= tNav);
        bool lastShown = lastNav is not null && _log.Find(lastNav.Request, "full-visible") is not null;
        bool navOk = reached && reloads.Count >= 1 && errors == 0 && lastShown;
        observed.Add($"↓ ×50: at f15050={reached}, ranges read again={reloads.Count} (max {reloads.Select(m => (m.Qpc - (long)(m.Number("startQpc") ?? m.Qpc)) * 1000.0 / Stopwatch.Frequency).DefaultIfEmpty(0).Max():0} ms), errors={errors}, last shown={lastShown}");

        // End: the last file; the range is the last 61 items.
        long tEnd = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkEnd, explorer, out _);
        WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "filmstrip-range" && m.Qpc >= tEnd); }, 6000, null);
        Mark? endRange = _log.Marks.LastOrDefault(m => m.Name == "filmstrip-range" && m.Qpc >= tEnd);
        bool endOk = endRange?.Number("count") == 61;
        long privateAfter = HostPrivateBytes();
        observed.Add($"End: range {endRange?.Number("count")}; host private memory {privateBefore / 1048576.0:0} → {privateAfter / 1048576.0:0} MB");
        bool memoryOk = privateAfter - privateBefore < 120L * 1024 * 1024;
        bool closed = CloseFromExplorer(explorer) || CloseHostWindow();
        return (firstOk && navOk && endOk && memoryOk && closed, string.Join("; ", observed));
    }

    private long HostPrivateBytes()
    {
        try
        {
            return _host is null ? 0 : Process.GetProcessById(_host.Id).PrivateMemorySize64;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------ filmstrip: cloud-only files

    private (bool, string) FilmstripCloud(string syncRoot)
    {
        if (Directory.Exists(syncRoot))
        {
            Directory.Delete(syncRoot, recursive: true);
        }

        var observed = new List<string>();
        using CloudFiles cloud = CloudFiles.Create(syncRoot);
        string local = Path.Combine(syncRoot, "00 local.jpg");
        Task.Run(() => TestAssets.EncodeAsync(local, BitmapEncoder.JpegEncoderId, 320, 240, 0.9)).GetAwaiter().GetResult();
        string[] names = ["01 cloud.jpg", "02 cloud.png", "03 cloud.mp4", "04 cloud.pdf", "05 cloud.heic"];
        cloud.CreatePlaceholders(names.Select(n => (n, n.EndsWith(".mp4", StringComparison.Ordinal) ? 50_000_000L : 2_000_000L)));
        string[] paths = [.. names.Select(n => Path.Combine(syncRoot, n))];
        bool dehydratedBefore = paths.All(CloudFiles.IsDehydrated);
        bool facts = paths.All(p => FileFacts.Read(p).Access == PreviewAccess.CloudPlaceholder);
        observed.Add($"placeholders cloud-only={dehydratedBefore}, seen as cloud by Quick View's file check={facts}");

        nint explorer = OpenExplorer(local);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Select(window, local, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        int fetchesBefore = CloudFiles.Fetches.Count;
        long t0 = Stopwatch.GetTimestamp();
        if (!OpenQuickView(explorer, local, out string why))
        {
            return (false, why);
        }

        WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Name == "filmstrip-range" && m.Qpc >= t0); }, 6000, null);
        Thread.Sleep(2500); // pictures of the strip
        string[] strip = UiaChildNames(HostWindow(), "QuickViewFilmstrip");
        bool listed = strip.Length == 6;
        observed.Add($"strip=[{string.Join(", ", strip)}]");

        // ↓ through the cloud-only files.
        var shown = new List<string>();
        foreach (string path in paths)
        {
            long t = Stopwatch.GetTimestamp();
            SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
            WaitFor(() => { _log.Poll(); return _log.Marks.Any(m => m.Qpc >= t && m.Name is "thumbnail" or "info-card" or "full-skipped"); }, 3000, null);
            Thread.Sleep(600);
            _log.Poll();
            Mark? nav = _log.Marks.LastOrDefault(m => m.Name == "nav" && m.Qpc >= t);
            Mark? info = nav is null ? null : _log.Find(nav.Request, "info");
            string kind = nav is not null && _log.Find(nav.Request, "info-card") is not null ? "card" : "notice";
            shown.Add($"{Path.GetFileName(path)}:{info?.Text("access")}/{kind}");
        }

        observed.Add("↓: " + string.Join(", ", shown));
        bool closed = CloseFromExplorer(explorer) || CloseHostWindow();
        Thread.Sleep(500);

        int hostId = _host?.Id ?? -1;
        var fetches = CloudFiles.Fetches.Skip(fetchesBefore).ToList();
        int byQuickView = fetches.Count(f => f.ProcessId == hostId);
        string others = string.Join(", ", fetches.Where(f => f.ProcessId != hostId).GroupBy(f => ProcessNameOf(f.ProcessId)).Select(g => $"{g.Key}×{g.Count()}"));
        bool dehydratedAfter = paths.All(CloudFiles.IsDehydrated);
        observed.Add($"data requests by Quick View={byQuickView}{(others.Length > 0 ? $" (others: {others})" : string.Empty)}; still cloud-only={dehydratedAfter}");
        return (dehydratedBefore && facts && listed && byQuickView == 0 && dehydratedAfter && shown.All(s => s.Contains("CloudPlaceholder", StringComparison.Ordinal)) && closed, string.Join("; ", observed));
    }

    private (bool, string) PaintControl(string file)
    {
        var before = Process.GetProcessesByName("mspaint").Select(p => p.Id).ToHashSet();
        Process.Start(new ProcessStartInfo("mspaint.exe") { UseShellExecute = true });
        nint paint = 0;
        WaitFor(() => (paint = Native.TopLevelWindows().FirstOrDefault(w => Native.IsWindowVisible(w) && ProcessName(w) == "mspaint" && !before.Contains((int)Pid(w)))) != 0, 15000, null);
        if (paint == 0)
        {
            return (false, "Paint not started");
        }

        List<Native.MonitorInfo> monitors = Native.Monitors();
        if (monitors.Count > 1)
        {
            PutOn(paint, monitors[1], 0.7, 0.7);
        }

        nint explorer = OpenExplorer(file);
        if (monitors.Count > 1)
        {
            PutOn(explorer, monitors[0], 0.6, 0.6);
        }

        ForceForeground(explorer);
        Thread.Sleep(1500);
        int[] item = UiaRect(explorer, "01 colors").Split(',').Select(s => int.TryParse(s, out int v) ? v : -1).ToArray();
        if (item.Length != 4 || item[2] <= 0)
        {
            return (false, "Explorer item not found");
        }

        Native.GetWindowRect(paint, out Native.RECT t);
        HoverDrag((item[0] + Math.Min(60, item[2] / 2), item[1] + (item[3] / 2)), ((t.Left + t.Right) / 2, (t.Top + t.Bottom) / 2));
        bool opened = WaitFor(() => Native.WindowText(paint).Contains("01 colors", StringComparison.OrdinalIgnoreCase), 8000, null);
        string title = Native.WindowText(paint);
        foreach (Process p in Process.GetProcessesByName("mspaint").Where(p => !before.Contains(p.Id)))
        {
            p.Kill();
        }

        // Passes either way: it tells whether Paint's refusal is Paint's own behavior.
        return (true, $"Paint opened a file dropped from Explorer={opened} (\"{title}\")");
    }

    /// <summary>Moves Explorer to a monitor Quick View is not on, so its files can be clicked (as a user would arrange them).</summary>
    private static void ExposeExplorer(nint explorer, nint host)
    {
        List<Native.MonitorInfo> monitors = Native.Monitors();
        Native.GetWindowRect(host, out Native.RECT q);
        Native.MonitorInfo? free = monitors.FirstOrDefault(m => !Inside(q, m));
        if (free is not null)
        {
            PutOn(explorer, free, 0.6, 0.6);
            Thread.Sleep(800);
        }
    }

    /// <summary>A drag that rests over the target for a moment before the drop (some targets need several drag-over events).</summary>
    private static void HoverDrag((int X, int Y) from, (int X, int Y) to)
    {
        MoveMouse(from);
        Thread.Sleep(150);
        Native.MouseEvent(0x0002, 0, 0, 0, 0);
        for (int i = 1; i <= 16; i++)
        {
            MoveMouse((from.X + ((to.X - from.X) * i / 16), from.Y + ((to.Y - from.Y) * i / 16)));
            Thread.Sleep(40);
        }

        for (int i = 0; i < 12; i++)
        {
            MoveMouse((to.X + (i % 2 == 0 ? 3 : -3), to.Y + (i % 3)));
            Thread.Sleep(60);
        }

        Native.MouseEvent(0x0004, 0, 0, 0, 0);
        Thread.Sleep(500);
    }

    /// <summary>Moves the pointer over the info bar so the inspector panel hides (it is not background for <see cref="ContentBox"/>).</summary>
    private void ParkPointer(nint host)
    {
        Native.GetWindowRect(host, out Native.RECT r);
        MoveMouse(((r.Left + r.Right) / 2, r.Bottom - 30));
        WaitFor(() => UiaName(host, "QuickViewPixelInspector") is null, 2000, null);
    }

    /// <summary>
    /// A WinForms drop target in another process that logs the formats and effects it is offered, for diagnosing
    /// drops that other applications refuse.
    /// </summary>
    private (bool, string) DragProbe(string file)
    {
        string log = Path.Combine(options.WorkDirectory, "drop-probe.log");
        File.Delete(log);
        string script = Path.Combine(AppContext.BaseDirectory, "scripts", "droptarget.ps1");
        using Process probe = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -STA -ExecutionPolicy Bypass -File \"{script}\" -Log \"{log}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        nint target = 0;
        WaitFor(() => (target = Native.TopLevelWindows().FirstOrDefault(w => Native.IsWindowVisible(w) && Native.WindowText(w) == "Mavue drop probe")) != 0, 15000, null);
        if (target == 0)
        {
            return (false, "probe window not found");
        }

        (bool dropped, string note) = DropInto(file, target, Native.Monitors(), () => File.Exists(log) && File.ReadAllText(log).Contains("drop ", StringComparison.Ordinal));
        string text = File.Exists(log) ? File.ReadAllText(log).Replace("\r\n", " / ", StringComparison.Ordinal) : "(no log)";
        try
        {
            probe.Kill();
        }
        catch (InvalidOperationException)
        {
            // Closed itself after the drop.
        }

        return (dropped, $"{note} {text}");
    }

    private static string ProcessNameOf(int id)
    {
        try
        {
            using Process p = Process.GetProcessById(id);
            return p.ProcessName;
        }
        catch (ArgumentException)
        {
            return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

internal static partial class Native
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(nint hwnd, [Out] char[] text, int max);

    public static string WindowText(nint hwnd)
    {
        char[] buffer = new char[512];
        int n = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, n));
    }
}
