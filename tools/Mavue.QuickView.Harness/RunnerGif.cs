using System.Diagnostics;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Animated GIF in Quick View: frames advance on screen with the file's delays and loop; switching to another
/// file stops the player, and coming back starts exactly one new player (never two running at once).
/// </summary>
internal sealed partial class Runner
{
    private const int GifDelayMs = 100;

    private void RunGifScenario(List<TestAsset> assets)
    {
        string folder = Path.GetDirectoryName(assets[0].Path)! + " GIF";
        Directory.CreateDirectory(folder);
        string gif = Path.Combine(folder, "01 animation.gif");
        string still = Path.Combine(folder, "02 still.jpg");
        Task.Run(() => WriteAnimatedGifAsync(gif, 480, 360, [(0, 0, 220), (0, 200, 0), (220, 0, 0)], GifDelayMs / 10)).GetAwaiter().GetResult();
        if (!File.Exists(still))
        {
            Task.Run(() => TestAssets.EncodeAsync(still, BitmapEncoder.JpegEncoderId, 480, 360, 0.9)).GetAwaiter().GetResult();
        }

        Guarded("gif-animation", () => GifAnimation(gif, still),
            $"3-frame looping GIF ({GifDelayMs} ms): frames advance on screen with that delay and wrap around; ↓ to a still image stops the player; ↑ back starts one new player (no double playback)");
    }

    private (bool, string) GifAnimation(string gif, string still)
    {
        nint explorer = OpenExplorer(gif);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Select(window, gif, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        long tOpen = Stopwatch.GetTimestamp();
        if (!OpenQuickView(explorer, gif, out string why))
        {
            return (false, why);
        }

        // On screen: the center pixel must take more than one color while the animation runs.
        var colors = new HashSet<uint>();
        for (int i = 0; i < 8; i++)
        {
            if (ImageAreaCenterPixel() is { } color)
            {
                colors.Add(color & 0xF0F0F0); // ignore tiny dithering differences
            }

            Thread.Sleep(GifDelayMs * 4 / 3);
        }

        _log.Poll();
        List<Mark> frames = _log.Marks.Where(m => m.Name == "gif-frame" && m.Qpc >= tOpen).ToList();
        int firstPlayer = (int)(frames.FirstOrDefault()?.Number("player") ?? -1);
        bool singlePlayer = frames.All(m => (int)(m.Number("player") ?? -1) == firstPlayer);
        bool looped = frames.Any(m => m.Number("play") >= 1);
        double[] gaps = frames.Zip(frames.Skip(1), (a, b) => Ms(a.Qpc, b.Qpc)).Order().ToArray();
        double median = gaps.Length > 0 ? gaps[gaps.Length / 2] : 0;
        bool timing = median is >= GifDelayMs * 0.8 and <= GifDelayMs * 1.6;
        var observed = new List<string>
        {
            $"open: {frames.Count} frames shown by player {firstPlayer}, looped={looped}, median interval {median:0} ms, on-screen colors {colors.Count}",
        };
        bool ok = frames.Count >= 6 && singlePlayer && looped && timing && colors.Count >= 2;

        // ↓ to the still image: the player stops and nothing animates any more.
        long tDown = Stopwatch.GetTimestamp();
        SendKeyIfForeground(Native.VK_DOWN, explorer, out _);
        (Mark? toStill, Mark? stillDone) = WaitForNavigation(tDown, "explorer-selection");
        Thread.Sleep(GifDelayMs * 5);
        _log.Poll();
        bool stopped = _log.Marks.Any(m => m.Name == "gif-stop" && m.Qpc >= tDown && (int)(m.Number("player") ?? -2) == firstPlayer);
        Mark? stopMark = _log.Marks.LastOrDefault(m => m.Name == "gif-stop" && (int)(m.Number("player") ?? -2) == firstPlayer);
        int framesAfterStop = stopMark is null ? -1 : _log.Marks.Count(m => m.Name == "gif-frame" && m.Qpc > stopMark.Qpc);
        bool onStill = stillDone?.Name == "full-visible" && toStill is not null && ShowsFile(toStill.Request, still);
        observed.Add($"↓ still image: shown={onStill}, player {firstPlayer} stopped={stopped}, frames after stop={framesAfterStop}");
        ok &= onStill && stopped && framesAfterStop == 0;

        // ↑ back to the GIF: exactly one new player.
        long tUp = Stopwatch.GetTimestamp();
        SendKeyIfForeground(VkUp, explorer, out _);
        (_, Mark? backDone) = WaitForNavigation(tUp, "explorer-selection");
        Thread.Sleep(1200);
        _log.Poll();
        List<Mark> again = _log.Marks.Where(m => m.Name == "gif-frame" && m.Qpc >= tUp).ToList();
        var players = again.Select(m => (int)(m.Number("player") ?? -1)).Distinct().ToList();
        double seconds = again.Count > 1 ? Ms(again[0].Qpc, again[^1].Qpc) / 1000 : 0;
        double rate = seconds > 0 ? (again.Count - 1) / seconds : 0;
        bool oneNewPlayer = backDone?.Name == "full-visible" && players.Count == 1 && players[0] != firstPlayer;
        bool notDoubled = rate is > 0 and <= 1000.0 / GifDelayMs * 1.3;
        observed.Add($"↑ back: players {string.Join(',', players)}, {again.Count} frames, {rate:0.0} frames/s (expected about {1000.0 / GifDelayMs:0})");
        ok &= oneNewPlayer && notDoubled;

        long tClose = Stopwatch.GetTimestamp();
        bool closed = CloseFromExplorer(explorer);
        Thread.Sleep(300);
        _log.Poll();
        bool stoppedOnClose = players.Count == 1 && _log.Marks.Any(m => m.Name == "gif-stop" && m.Qpc >= tClose && (int)(m.Number("player") ?? -2) == players[0]);
        observed.Add($"Esc: closed={closed}, player stopped={stoppedOnClose}");
        ok &= closed && stoppedOnClose;
        return (ok, string.Join("; ", observed));
    }

    /// <summary>Screen color at the center of the image area (the window minus the info bar at the bottom).</summary>
    private uint? ImageAreaCenterPixel()
    {
        nint window = HostWindow();
        if (window == 0 || !Native.IsWindowVisible(window) || !Native.GetWindowRect(window, out Native.RECT r))
        {
            return null;
        }

        int x = (r.Left + r.Right) / 2;
        int y = r.Top + ((r.Bottom - r.Top) * 45 / 100);
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

    private static async Task WriteAnimatedGifAsync(string path, int width, int height, (byte B, byte G, byte R)[] frames, int delayCentiseconds)
    {
        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);
        await encoder.BitmapContainerProperties.SetPropertiesAsync(new BitmapPropertySet
        {
            ["/appext/Application"] = new BitmapTypedValue(Encoding.ASCII.GetBytes("NETSCAPE2.0"), PropertyType.UInt8Array),
            ["/appext/Data"] = new BitmapTypedValue(new byte[] { 3, 1, 0, 0, 0 }, PropertyType.UInt8Array), // loop forever
        });
        for (int i = 0; i < frames.Length; i++)
        {
            if (i > 0)
            {
                await encoder.GoToNextFrameAsync();
            }

            byte[] pixels = new byte[width * height * 4];
            for (int p = 0; p < pixels.Length; p += 4)
            {
                pixels[p] = frames[i].B;
                pixels[p + 1] = frames[i].G;
                pixels[p + 2] = frames[i].R;
                pixels[p + 3] = 255;
            }

            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                ["/grctlext/Delay"] = new BitmapTypedValue((ushort)delayCentiseconds, PropertyType.UInt16),
            });
        }

        await encoder.FlushAsync();
    }
}
