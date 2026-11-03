using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Mavue.QuickView.Harness;

/// <summary>
/// The see-through background as it looks (docs/ARCHITECTURE.md §4.7.9): a bright striped window behind Quick View,
/// then 0 / 15 / 30 % in the dark theme, 15 % in the light theme, in high contrast and with Windows' transparency effects
/// off (each system setting is changed only for its case and restored). Screenshots go to the work directory for review.
/// Also checks that image, PDF page and text pixels are the same at 0 % and 30 %.
/// </summary>
internal sealed partial class Runner
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private void RunAppearanceLookScenarios(string solid)
    {
        string folder = Path.GetDirectoryName(solid)!;
        string text = Path.Combine(folder, "02 text.txt");
        string pdf = Path.Combine(folder, "03 document.pdf");
        File.WriteAllText(text, "Quick View text preview\nThe background of text stays opaque.\n");
        File.WriteAllBytes(pdf, Mavue.Pdf.Tests.TestPdf.Create(1));

        Guarded("window-look", () => WindowLookScenario(solid),
            "Over a bright window: 0 % is the opaque stage (#1E1E1E); 15 % and 30 % let it show through, 30 % more than 15 %; light theme 15 % is a light material; high contrast is solid; with transparency effects off the stage is the solid stage color; the image's pixels never change");
        Guarded("window-monitors", () => WindowMonitorsScenario(solid),
            "At 15 % on each monitor: Quick View opens on the Explorer window's monitor at the size QuickViewWindowSize gives for that work area and DPI, with the see-through backdrop, the image planned at that DPI and its pixels unchanged (screenshots for review)");
        Guarded("window-content-colors", () => WindowContentColorsScenario(solid, pdf, text),
            "At 30 % (bright window behind) the image, a PDF page and the text view show exactly the same pixels as at 0 %");
    }

    private (bool, string) WindowLookScenario(string image)
    {
        var observed = new List<string>();
        bool ok = true;
        using Process? stripes = ShowStripes();
        try
        {
            var cases = new (string Label, int Transparency, bool Light, bool HighContrast, bool EffectsOff)[]
            {
                ("dark-0", 0, false, false, false),
                ("dark-15", 15, false, false, false),
                ("dark-30", 30, false, false, false),
                ("light-15", 15, true, false, false),
                ("contrast-15", 15, false, true, false),
                ("effects-off-30", 30, false, false, true),
            };
            var luminance = new Dictionary<string, double>();
            foreach ((string label, int transparency, bool light, bool contrast, bool effectsOff) in cases)
            {
                object? lightBefore = Registry.CurrentUser.OpenSubKey(PersonalizeKey)?.GetValue("AppsUseLightTheme");
                object? effectsBefore = Registry.CurrentUser.OpenSubKey(PersonalizeKey)?.GetValue("EnableTransparency");
                bool contrastBefore = HighContrastOn();
                try
                {
                    if (light)
                    {
                        SetPersonalize("AppsUseLightTheme", 1);
                    }

                    if (effectsOff)
                    {
                        SetPersonalize("EnableTransparency", 0);
                    }

                    if (contrast && !contrastBefore)
                    {
                        SetHighContrast(true);
                    }

                    File.WriteAllText(HostSettingsPath, $"{{\"version\": 1, \"imageScale\": \"FitNoUpscale\", \"backgroundTransparency\": {transparency}}}");
                    (string kind, (byte R, byte G, byte B) pixel, (byte R, byte G, byte B) stage, string why) = LookAt(image, label);
                    double lum = (0.299 * stage.R) + (0.587 * stage.G) + (0.114 * stage.B);
                    luminance[label] = lum;
                    bool imageSame = Math.Abs(pixel.R - AppearanceColor.R) + Math.Abs(pixel.G - AppearanceColor.G) + Math.Abs(pixel.B - AppearanceColor.B) <= 9;
                    bool good = why.Length == 0 && imageSame && label switch
                    {
                        "dark-0" => kind == "Mica" && Near(stage, (0x1E, 0x1E, 0x1E), 4),
                        "dark-15" or "dark-30" => kind == "Acrylic",
                        "light-15" => kind == "Acrylic" && lum > 170,
                        "contrast-15" => kind == "Solid",
                        "effects-off-30" => kind == "Acrylic" && Near(stage, (0x1E, 0x1E, 0x1E), 4),
                        _ => false,
                    };
                    ok &= good;
                    observed.Add($"{label}: {(why.Length > 0 ? why : $"backdrop {kind}, stage #{stage.R:X2}{stage.G:X2}{stage.B:X2} (lum {lum:0}), image #{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}")}{(good ? string.Empty : " ✗")}");
                }
                finally
                {
                    if (light)
                    {
                        SetPersonalize("AppsUseLightTheme", lightBefore is int l ? l : 0);
                    }

                    if (effectsOff)
                    {
                        SetPersonalize("EnableTransparency", effectsBefore is int e ? e : 1);
                    }

                    if (contrast && !contrastBefore)
                    {
                        SetHighContrast(false);
                    }
                }
            }

            // More transparency shows more of the bright window behind.
            bool rising = luminance.GetValueOrDefault("dark-0") < luminance.GetValueOrDefault("dark-15") && luminance.GetValueOrDefault("dark-15") < luminance.GetValueOrDefault("dark-30");
            ok &= rising;
            observed.Add($"0 < 15 < 30 % brightness over the bright window: {rising}; restored: high contrast {HighContrastOn()}, light apps {Registry.CurrentUser.OpenSubKey(PersonalizeKey)?.GetValue("AppsUseLightTheme")}, transparency effects {Registry.CurrentUser.OpenSubKey(PersonalizeKey)?.GetValue("EnableTransparency")}");
        }
        finally
        {
            StopStripes(stripes);
            WriteHostSettings();
        }

        observed.Add("screenshots: " + Path.Combine(options.WorkDirectory, "look-*.png"));
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) WindowContentColorsScenario(string image, string pdf, string text)
    {
        var observed = new List<string>();
        bool ok = true;
        using Process? stripes = ShowStripes();
        try
        {
            foreach ((string file, string label) in new[] { (image, "image"), (pdf, "pdf"), (text, "text") })
            {
                var samples = new List<string>();
                foreach (int transparency in new[] { 0, 30 })
                {
                    File.WriteAllText(HostSettingsPath, $"{{\"version\": 1, \"imageScale\": \"FitNoUpscale\", \"backgroundTransparency\": {transparency}}}");
                    samples.Add(ContentSample(file, $"{label}-{transparency}"));
                }

                bool same = samples[0] == samples[1] && !samples[0].StartsWith('!');
                ok &= same;
                observed.Add($"{label}: 0 % {samples[0]} | 30 % {samples[1]} same={same}");
            }
        }
        finally
        {
            StopStripes(stripes);
            WriteHostSettings();
        }

        return (ok, string.Join("; ", observed));
    }

    private (bool, string) WindowMonitorsScenario(string image)
    {
        List<Native.MonitorInfo> monitors = Native.Monitors();
        var observed = new List<string>();
        bool ok = monitors.Count > 1;
        try
        {
            File.WriteAllText(HostSettingsPath, "{\"version\": 1, \"imageScale\": \"FitNoUpscale\", \"backgroundTransparency\": 15}");
            foreach (Native.MonitorInfo monitor in monitors)
            {
                nint explorer = OpenExplorer(image);
                if (explorer == 0)
                {
                    observed.Add($"{monitor.Width}x{monitor.Height}: Explorer window not found");
                    ok = false;
                    continue;
                }

                Native.ShowWindow(explorer, Native.SW_RESTORE);
                PutOn(explorer, monitor, 0.4, 0.4);
                Thread.Sleep(500);
                if (!OpenQuickView(explorer, image, out string why))
                {
                    observed.Add($"{monitor.Width}x{monitor.Height}: {why}");
                    ok = false;
                    continue;
                }

                Thread.Sleep(600);
                Mark? placed = _log.Marks.LastOrDefault(m => m.Name == "placed");
                Mark? full = _log.Marks.LastOrDefault(m => m.Name == "full-set");
                Mark? backdrop = _log.Marks.LastOrDefault(m => m.Name == "backdrop");
                nint host = HostWindow();
                bool onMonitor = Native.GetWindowRect(host, out Native.RECT r) && Inside(r, monitor);
                (byte R, byte G, byte B) pixel = ScreenColor(((r.Left + r.Right) / 2, r.Top + ((r.Bottom - r.Top) * 55 / 100)));
                Native.RECT work = monitor.Work;
                (int width, int height) = Mavue.Core.Viewing.QuickViewWindowSize.For(work.Right - work.Left, work.Bottom - work.Top, monitor.Dpi / 96.0);
                bool sized = Math.Abs((placed?.Number("width") ?? 0) - width) <= 2 && Math.Abs((placed?.Number("height") ?? 0) - height) <= 2;
                bool dpi = placed?.Number("dpi") == monitor.Dpi && full?.Number("dpi") == monitor.Dpi;
                bool oneToOne = full?.Number("decodedWidth") == 640 && full.Number("decodedHeight") == 420; // a small image is never enlarged
                bool imageSame = Near(pixel, AppearanceColor, 3);
                string kind = backdrop?.Text("kind") ?? "?";
                Screenshot(host, Path.Combine(options.WorkDirectory, $"look-monitor-{monitor.Width}x{monitor.Height}-{monitor.Dpi}.png"));
                bool good = onMonitor && sized && dpi && oneToOne && imageSame && kind == "Acrylic";
                ok &= good;
                observed.Add($"{monitor.Width}x{monitor.Height}@{monitor.Dpi}: window {placed?.Number("width")}x{placed?.Number("height")} (expected {width}x{height}) on monitor={onMonitor}, dpi {placed?.Number("dpi")}/{full?.Number("dpi")}, decoded {full?.Number("decodedWidth")}x{full?.Number("decodedHeight")}, backdrop {kind}, image #{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}{(good ? string.Empty : " ✗")}");
                CloseFromExplorer(explorer);

                // A PDF has the most tools in the bar: on the smaller window they must all stay inside it (review screenshot).
                string pdf = Path.Combine(Path.GetDirectoryName(image)!, "03 document.pdf");
                string pdfWhy = File.Exists(pdf) && OpenExplorer(pdf) == explorer ? string.Empty : "PDF not selected"; // same folder: selects it in this window
                if (pdfWhy.Length == 0 && OpenQuickView(explorer, pdf, out pdfWhy))
                {
                    Thread.Sleep(800);
                    nint pdfHost = HostWindow();
                    string more = RunUiaScript(pdfHost, "QuickViewMoreButton", "if ($el) { $b = $el.Current.BoundingRectangle; $w = $win.Current.BoundingRectangle; Write-Output ([string]($b.Right -le $w.Right -and $b.Width -gt 0)) } else { Write-Output 'missing' }");
                    Screenshot(pdfHost, Path.Combine(options.WorkDirectory, $"look-monitor-pdf-{monitor.Width}x{monitor.Height}-{monitor.Dpi}.png"));
                    ok &= more == "True";
                    observed.Add($"  pdf tool bar inside the window={more}");
                    CloseFromExplorer(explorer);
                }
                else
                {
                    observed.Add($"  pdf: {pdfWhy}");
                }

                Native.PostMessageW(explorer, Native.WM_CLOSE, 0, 0);
                _createdExplorers.Remove(explorer);
                Thread.Sleep(400);
            }
        }
        finally
        {
            WriteHostSettings();
        }

        return (ok, string.Join("; ", observed) + (monitors.Count > 1 ? string.Empty : " (only one monitor)"));
    }

    /// <summary>Opens Quick View on <paramref name="file"/> over the stripes and returns the backdrop, the image center and the stage color.</summary>
    private (string Kind, (byte R, byte G, byte B) Pixel, (byte R, byte G, byte B) Stage, string Why) LookAt(string file, string label)
    {
        nint explorer = OpenSmallExplorer(file);
        if (explorer == 0)
        {
            return ("?", default, default, "Explorer window not found");
        }

        if (_stripesWindow != 0)
        {
            Screenshot(_stripesWindow, Path.Combine(options.WorkDirectory, $"look-before-{label}.png"));
        }

        if (!OpenQuickView(explorer, file, out string why))
        {
            return ("?", default, default, why);
        }

        Thread.Sleep(900); // theme changes and the backdrop settle
        Mark? backdrop = _log.Marks.LastOrDefault(m => m.Name == "backdrop");
        (byte R, byte G, byte B) pixel = default, stage = default;
        nint host = HostWindow();
        if (Native.GetWindowRect(host, out Native.RECT r))
        {
            pixel = ScreenColor(((r.Left + r.Right) / 2, r.Top + ((r.Bottom - r.Top) * 55 / 100)));
            stage = new[] { ScreenColor((r.Left + 40, r.Bottom - 40)), ScreenColor((r.Right - 40, r.Bottom - 40)) }
                .OrderByDescending(c => c.R + c.G + c.B).First(); // the corner over the stripes, not over Explorer
            Screenshot(host, Path.Combine(options.WorkDirectory, $"look-{label}.png"));
        }

        CloseFromExplorer(explorer);
        Thread.Sleep(300);
        return (backdrop?.Text("kind") ?? "?", pixel, stage, string.Empty);
    }

    /// <summary>Pixels of the content (not the stage around it) at fixed points, as text, for comparing two settings.</summary>
    private string ContentSample(string file, string label)
    {
        nint explorer = OpenSmallExplorer(file);
        if (explorer == 0 || !OpenQuickView(explorer, file, out _))
        {
            return "!not shown";
        }

        Thread.Sleep(1200);
        var colors = new List<string>();
        nint host = HostWindow();
        if (Native.GetWindowRect(host, out Native.RECT r))
        {
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            // Image and PDF page: around the center. Text: its view covers the stage, so also near the bottom corners.
            foreach ((int x, int y) in new[] { (w / 2, h * 55 / 100), ((w / 2) + 30, h * 45 / 100), (60, h - 60), (w - 60, h - 60) })
            {
                (byte cr, byte cg, byte cb) = ScreenColor((r.Left + x, r.Top + y));
                colors.Add($"#{cr:X2}{cg:X2}{cb:X2}");
            }

            Screenshot(host, Path.Combine(options.WorkDirectory, $"look-content-{label}.png"));
        }

        CloseFromExplorer(explorer);
        Thread.Sleep(300);

        // The corners are the stage for the image and the PDF (see-through by design); only the text view covers them.
        return Path.GetExtension(file) == ".txt" ? string.Join(",", colors) : string.Join(",", colors.Take(2));
    }

    /// <summary>Explorer in the middle 40 % of its monitor, so the stripes show around Quick View's corners.</summary>
    private nint OpenSmallExplorer(string file)
    {
        nint explorer = OpenExplorer(file);
        if (explorer != 0)
        {
            Native.MonitorInfo? monitor = Native.Monitors().FirstOrDefault(m => Native.GetWindowRect(explorer, out Native.RECT e) && e.Left + 50 >= m.Bounds.Left && e.Left + 50 < m.Bounds.Right && e.Top + 50 >= m.Bounds.Top && e.Top + 50 < m.Bounds.Bottom);
            if (monitor is not null)
            {
                PutOn(explorer, monitor, 0.4, 0.4);
                Thread.Sleep(300);
            }
        }

        return explorer;
    }

    private static bool Near((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    /// <summary>A borderless window of bright vertical stripes over the primary monitor (not topmost; Explorer and Quick View go above it).</summary>
    private nint _stripesWindow;

    private Process? ShowStripes()
    {
        string script = Path.Combine(options.WorkDirectory, "stripes.ps1");
        File.WriteAllText(script, """
            Add-Type -AssemblyName System.Windows.Forms, System.Drawing
            $f = New-Object System.Windows.Forms.Form
            $f.FormBorderStyle = 'None'; $f.ShowInTaskbar = $false; $f.StartPosition = 'Manual'
            $f.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            $f.Add_Paint({ param($s, $e)
              $colors = @([System.Drawing.Color]::White, [System.Drawing.Color]::Gold, [System.Drawing.Color]::DeepSkyBlue, [System.Drawing.Color]::Tomato)
              $w = 120; for ($x = 0; $x -lt $s.Width; $x += $w) { $b = New-Object System.Drawing.SolidBrush ($colors[($x / $w) % 4]); $e.Graphics.FillRectangle($b, $x, 0, $w, $s.Height); $b.Dispose() } })
            [System.Windows.Forms.Application]::Run($f)
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        // The neutral window (cold runs) would otherwise be raised behind Explorer before every Space and hide the stripes.
        _neutralPaused = true;
        if (_neutralWindow != 0 && Native.IsWindow(_neutralWindow))
        {
            Native.ShowWindow(_neutralWindow, 6 /* SW_MINIMIZE */);
        }

        Process? process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"") { UseShellExecute = false, CreateNoWindow = true });
        nint window = 0;
        WaitFor(() => process is not null && (window = Native.TopLevelWindows().FirstOrDefault(w => Pid(w) == process.Id && Native.IsWindowVisible(w))) != 0, 10000, null);
        if (window != 0)
        {
            ForceForeground(window); // above the other apps; Explorer and Quick View then open above it
        }

        _stripesWindow = window;

        Thread.Sleep(500);
        return process;
    }

    private void StopStripes(Process? process)
    {
        _neutralPaused = false;
        if (_neutralWindow != 0 && Native.IsWindow(_neutralWindow))
        {
            Native.ShowWindow(_neutralWindow, Native.SW_RESTORE);
        }

        try
        {
            if (process is { HasExited: false })
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private static void SetPersonalize(string name, int value)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true))
        {
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        nint text = Marshal.StringToHGlobalUni("ImmersiveColorSet");
        try
        {
            Native.SendMessageTimeoutW(0xFFFF /* HWND_BROADCAST */, 0x001A /* WM_SETTINGCHANGE */, 0, text, 0x0002, 3000, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }

        Thread.Sleep(2500);
    }

    private static bool HighContrastOn()
    {
        var hc = new HighContrast { Size = Marshal.SizeOf<HighContrast>() };
        return Appearance.SystemParametersInfoW(0x0042 /* SPI_GETHIGHCONTRAST */, (uint)hc.Size, ref hc, 0) && (hc.Flags & 1) != 0;
    }

    private static void SetHighContrast(bool on)
    {
        var hc = new HighContrast { Size = Marshal.SizeOf<HighContrast>() };
        Appearance.SystemParametersInfoW(0x0042, (uint)hc.Size, ref hc, 0);
        hc.Flags = on ? hc.Flags | 1 : hc.Flags & ~1;
        hc.DefaultScheme = 0;
        Appearance.SystemParametersInfoW(0x0043 /* SPI_SETHIGHCONTRAST */, (uint)hc.Size, ref hc, 3 /* update and broadcast */);
        Thread.Sleep(4000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public int Size;
        public int Flags;
        public nint DefaultScheme;
    }

    private static partial class Appearance
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SystemParametersInfoW(uint action, uint param, ref HighContrast value, uint winIni);
    }
}
