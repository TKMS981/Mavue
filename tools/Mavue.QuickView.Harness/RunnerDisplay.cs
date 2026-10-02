using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;

namespace Mavue.QuickView.Harness;

/// <summary>Display-size scenario: small images must be shown at their own size, not enlarged.</summary>
internal sealed partial class Runner
{
    private const int SmallWidth = 240;
    private const int SmallHeight = 180;

    private void RunDisplayScenarios(List<TestAsset> assets)
    {
        RunMonitorScenarios(assets); // both modes: in actual mode the image must stay at its source size

        if (options.ScaleMode == "actual")
        {
            return; // the checks below are for the default "fit, never enlarge" mode
        }

        Guarded("small-image-not-enlarged", () => SmallImageNotEnlarged(assets),
            $"A {SmallWidth}x{SmallHeight} image is decoded and shown at {SmallWidth}x{SmallHeight} physical pixels (measured on screen), not stretched to the window");
        Guarded("settings-reload", () => SettingsReload(assets),
            "Changing imageScale in settings.json while Quick View is hidden applies from the next Space (actual size: decoded at source size), without restarting the host");
    }

    private string SettingsPath => Path.Combine(options.WorkDirectory, "settings-e2e.json");

    private static void WriteScale(string path, string mode) =>
        File.WriteAllText(path, $"{{\"version\": 1, \"imageScale\": \"{mode}\"}}");

    private (bool, string) SettingsReload(List<TestAsset> assets)
    {
        TestAsset? large = assets.FirstOrDefault(a => a.Case == "large-jpeg");
        if (large is null)
        {
            return (false, "large-jpeg asset required");
        }

        string Open()
        {
            nint explorer = OpenExplorer(large.Path);
            if (explorer == 0 || !OpenQuickView(explorer, large.Path, out _))
            {
                return "not shown";
            }

            Mark? full = _log.Marks.LastOrDefault(m => m.Name == "full-set");
            string decoded = $"{full?.Number("decodedWidth")}x{full?.Number("decodedHeight")}";
            CloseFromExplorer(explorer);
            Thread.Sleep(300);
            return decoded;
        }

        try
        {
            string fit = Open();
            WriteScale(SettingsPath, "ActualSize");
            string actual = Open();
            WriteScale(SettingsPath, "FitNoUpscale");
            string back = Open();
            bool ok = fit != "6000x4000" && fit != "not shown" && actual == "6000x4000" && back == fit;
            return (ok, $"fit {fit} → (settings: ActualSize) {actual} → (settings: FitNoUpscale) {back}");
        }
        finally
        {
            WriteScale(SettingsPath, "FitNoUpscale");
        }
    }

    private (bool, string) SmallImageNotEnlarged(List<TestAsset> assets)
    {
        string folder = Path.GetDirectoryName(assets[0].Path)! + " 小";
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"small {SmallWidth}x{SmallHeight}.jpg");
        if (!File.Exists(file))
        {
            Task.Run(() => TestAssets.EncodeAsync(file, BitmapEncoder.JpegEncoderId, SmallWidth, SmallHeight, 0.95)).GetAwaiter().GetResult();
        }

        nint explorer = OpenExplorer(file);
        if (explorer == 0)
        {
            return (false, "Explorer window not found");
        }

        if (!OpenQuickView(explorer, file, out string why))
        {
            return (false, why);
        }

        Mark? full = _log.Marks.LastOrDefault(m => m.Name == "full-set");
        string decoded = $"{full?.Number("decodedWidth")}x{full?.Number("decodedHeight")}";
        Thread.Sleep(300);
        (int width, int height)? onScreen = MeasureImageOnScreen();
        bool closed = CloseFromExplorer(explorer);
        Native.PostMessageW(explorer, Native.WM_CLOSE, 0, 0);
        _createdExplorers.Remove(explorer);

        bool decodedOk = decoded == $"{SmallWidth}x{SmallHeight}";
        bool screenOk = onScreen is { } s && Math.Abs(s.width - SmallWidth) <= 4 && Math.Abs(s.height - SmallHeight) <= 4;
        return (decodedOk && screenOk && closed, $"decoded {decoded}, on screen {onScreen?.width}x{onScreen?.height} px");
    }

    /// <summary>
    /// Captures the Quick View window and returns the bounding box of non-background pixels in its
    /// image area (title bar and info bar excluded), i.e. how large the image really is on screen.
    /// </summary>
    private (int Width, int Height)? MeasureImageOnScreen()
    {
        nint window = HostWindow();
        if (window == 0 || !Native.GetWindowRect(window, out Native.RECT r))
        {
            return null;
        }

        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        byte[] pixels = Native.CaptureScreen(r.Left, r.Top, w, h);
        SaveCapture(Path.Combine(options.WorkDirectory, "display-capture.bmp"), pixels, w, h);
        // Skip the title bar, the info bar and the invisible resize borders (GetWindowRect includes them,
        // so whatever is behind the window shows up at the edges of the capture).
        int top = h * 10 / 100, bottom = h * 80 / 100, left = w * 3 / 100, right = w * 97 / 100;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int i = ((y * w) + x) * 4;
                int distance = Math.Abs(pixels[i] - 30) + Math.Abs(pixels[i + 1] - 30) + Math.Abs(pixels[i + 2] - 30);
                if (distance > 40)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        return maxX < 0 ? null : (maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Keeps the capture next to the report so a failed size check can be inspected.</summary>
    private static void SaveCapture(string path, byte[] bgra, int width, int height)
    {
        using var file = new BinaryWriter(File.Create(path));
        file.Write((ushort)0x4D42);
        file.Write(54 + bgra.Length);
        file.Write(0);
        file.Write(54);
        file.Write(40);
        file.Write(width);
        file.Write(-height);
        file.Write((ushort)1);
        file.Write((ushort)32);
        file.Write(0);
        file.Write(bgra.Length);
        file.Write(0);
        file.Write(0);
        file.Write(0);
        file.Write(0);
        file.Write(bgra);
    }
}

internal static partial class Native
{
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, byte* bits, byte* info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    /// <summary>Copies a screen rectangle (physical pixels) as top-down BGRA.</summary>
    public static unsafe byte[] CaptureScreen(int x, int y, int width, int height)
    {
        nint screen = GetDC(0);
        nint memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height);
        nint old = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020); // SRCCOPY
            byte[] pixels = new byte[width * height * 4];
            byte* header = stackalloc byte[44];
            new Span<byte>(header, 44).Clear();
            *(uint*)header = 40;            // biSize
            *(int*)(header + 4) = width;    // biWidth
            *(int*)(header + 8) = -height;  // biHeight (top-down)
            *(ushort*)(header + 12) = 1;    // biPlanes
            *(ushort*)(header + 14) = 32;   // biBitCount
            fixed (byte* p = pixels)
            {
                GetDIBits(memory, bitmap, 0, (uint)height, p, header, 0);
            }

            return pixels;
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }
}
