using System.Diagnostics;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Quick Preview Phase 2 (window design, docs/ARCHITECTURE.md §4.7.9): the checkerboard behind transparent images
/// (and its switch in the "…" menu), and keyboard access to the top bar (F6 in, Esc back, Esc closes).
/// </summary>
internal sealed partial class Runner
{
    private const ushort VkF6 = 0x75;

    private void RunPhase2Scenarios(List<TestAsset> assets)
    {
        string colors = Path.Combine(Path.GetDirectoryName(assets[0].Path)! + " Phase1", "01 colors.png");
        WritePhase1Settings(filmstrip: false, inspector: false);
        Guarded("checkerboard", () => Checkerboard(colors),
            "A PNG with a half-transparent quadrant: a checkerboard shows through it (two alternating colors) while an opaque quadrant stays one color; turned off in the \"…\" menu, the half-transparent quadrant becomes one color");
        Guarded("toolbar-keyboard", () => ToolbarKeyboard(colors),
            "Quick View active: F6 moves the keyboard to a button of the top bar, Esc there returns to the content without closing, Esc again closes");
        WriteHostSettings();
    }

    private (bool, string) Checkerboard(string colors)
    {
        nint explorer = OpenExplorer(colors);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null || !OpenQuickView(explorer, colors, out string why))
        {
            return (false, "Quick View not shown");
        }

        nint host = HostWindow();
        if (!FindImageOnScreen(host, out Native.RECT image))
        {
            CloseFromExplorer(explorer);
            return (false, "image not found on screen");
        }

        (int distinctBlue, int distinctNavy) = QuadrantColors(image);
        bool on = distinctBlue >= 2 && distinctNavy == 1;

        // Off in the "…" menu: the half-transparent quadrant shows only the stage behind it.
        string menu = InvokeByUiAutomation(host, ["More options|その他の操作", "Checkerboard behind transparency|透明部分を市松模様で表示"]);
        Thread.Sleep(600);
        FindImageOnScreen(host, out image);
        (int offBlue, int offNavy) = QuadrantColors(image);
        bool off = offBlue == 1 && offNavy == 1;

        // On again (restores the setting).
        InvokeByUiAutomation(host, ["More options|その他の操作", "Checkerboard behind transparency|透明部分を市松模様で表示"]);
        bool closed = CloseFromExplorer(explorer) || CloseHostWindow();
        return (on && off && closed, $"on: half-transparent quadrant {distinctBlue} colors, opaque quadrant {distinctNavy}; menu {menu}; off: {offBlue} / {offNavy}");
    }

    /// <summary>Distinct colors (by more than 2 per channel) in the centers of the bottom-left (half-transparent) and bottom-right (opaque) quadrants.</summary>
    private static (int HalfTransparent, int Opaque) QuadrantColors(Native.RECT image)
    {
        int w = image.Right - image.Left, h = image.Bottom - image.Top;
        int Distinct(int left, int top)
        {
            byte[] pixels = Native.CaptureScreen(left, top, 48, 48);
            var colors = new List<(int, int, int)>();
            for (int i = 0; i < pixels.Length; i += 4)
            {
                (int b, int g, int r) = (pixels[i], pixels[i + 1], pixels[i + 2]);
                if (!colors.Any(c => Math.Abs(c.Item1 - r) <= 2 && Math.Abs(c.Item2 - g) <= 2 && Math.Abs(c.Item3 - b) <= 2))
                {
                    colors.Add((r, g, b));
                }
            }

            return colors.Count;
        }

        return (Distinct(image.Left + (w / 4) - 24, image.Top + (h * 3 / 4) - 24), Distinct(image.Left + (w * 3 / 4) - 24, image.Top + (h * 3 / 4) - 24));
    }

    private (bool, string) ToolbarKeyboard(string colors)
    {
        nint explorer = OpenExplorer(colors);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null || !OpenQuickView(explorer, colors, out string why))
        {
            return (false, "Quick View not shown");
        }

        nint host = HostWindow();
        if (!ActivateHost(host))
        {
            return (false, "Quick View could not be activated");
        }

        // F6 into the bar; the next key the window sees reports where the keyboard is.
        long t = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkF6, host, out _);
        Thread.Sleep(300);
        SendKeyIfForeground(0x10 /* Shift: harmless probe key */, host, out _);
        Thread.Sleep(300);
        _log.Poll();
        string focusInBar = _log.Marks.LastOrDefault(m => m.Name == "window-key" && m.Qpc >= t && m.Text("key") == "Shift")?.Text("focus") ?? "?";
        bool inBar = focusInBar.Contains("Button", StringComparison.Ordinal);

        long tEsc = Stopwatch.GetTimestamp();
        SendKeyIfForeground(Native.VK_ESCAPE, host, out _);
        Thread.Sleep(500);
        _log.Poll();
        bool stillOpen = Native.IsWindowVisible(host) && _log.HiddenSince(tEsc) is null;
        long tEsc2 = Stopwatch.GetTimestamp();
        SendKeyIfForeground(Native.VK_ESCAPE, host, out _);
        bool closed = WaitFor(() => { _log.Poll(); return _log.HiddenSince(tEsc2) is not null; }, 3000, null);
        return (inBar && stillOpen && closed, $"F6 → focus {focusInBar}; Esc in the bar kept Quick View open={stillOpen}; Esc again closed={closed}");
    }
}
