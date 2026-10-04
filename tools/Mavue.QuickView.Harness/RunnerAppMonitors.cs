using Windows.Graphics.Imaging;

namespace Mavue.QuickView.Harness;

/// <summary>
/// The main window on every monitor (different DPI) and its theme: the image is on screen after each move (the
/// window takes the monitor's DPI), and the app's background follows the theme setting.
/// </summary>
internal sealed partial class Runner
{
    private void RunAppMonitorScenarios(List<TestAsset> assets)
    {
        string folder = Path.GetDirectoryName(assets[0].Path)! + " App";
        Directory.CreateDirectory(folder);
        string red = Path.Combine(folder, "red 900x600.png");
        Task.Run(() => EncodeSolidAsync(red, BitmapEncoder.PngEncoderId, 900, 600, (220, 20, 20))).GetAwaiter().GetResult();
        Guarded("app-monitors", () => AppMonitors(red),
            "A 900x600 red PNG: moved to each monitor the window has that monitor's DPI and the image is on screen (center red), and Mavue keeps working");
    }

    private (bool, string) AppMonitors(string red)
    {
        using AppSession app = LaunchApp(red);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        app.WaitForState(s => s.Text("kind") == "Image", 15000);
        var observed = new List<string>();
        bool ok = true;
        foreach (Native.MonitorInfo m in Native.Monitors())
        {
            int w = (m.Work.Right - m.Work.Left) * 8 / 10;
            int h = (m.Work.Bottom - m.Work.Top) * 8 / 10;
            Native.ShowWindow(app.Window, Native.SW_RESTORE);
            Native.SetWindowPos(app.Window, 0, m.Work.Left + ((m.Work.Right - m.Work.Left - w) / 2), m.Work.Top + ((m.Work.Bottom - m.Work.Top - h) / 2), w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            ForceForeground(app.Window);
            uint dpi = 0;
            WaitFor(() => (dpi = Native.GetDpiForWindow(app.Window)) == m.Dpi, 3000, null);
            Native.GetWindowRect(app.Window, out Native.RECT r);
            (int R, int G, int B) center = (0, 0, 0);
            bool shown = WaitFor(() =>
            {
                center = ScreenRgb((r.Left + r.Right) / 2, r.Top + ((r.Bottom - r.Top) * 55 / 100));
                return center.R > 180 && center.G < 80 && center.B < 80;
            }, 4000, null);
            Screenshot(app.Window, Path.Combine(options.WorkDirectory, $"app-monitor-{m.Dpi}-{m.Bounds.Left}.png"));
            observed.Add($"{m.Width}x{m.Height}@{m.Dpi}: window dpi {dpi}, center {Rgb(center)}");
            ok &= dpi == m.Dpi && shown;
        }

        (bool closed, _) = app.Close(expectPlayerStop: false);
        return (ok && closed, string.Join("; ", observed));
    }
}
