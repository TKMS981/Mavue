using Mavue.Core.Viewing;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Window design (docs/ARCHITECTURE.md §4.7.9, 2026-11-03): the initial size follows QuickViewWindowSize for the
/// monitor's work area and scale, and the background transparency from settings switches the backdrop (Mica at 0,
/// tinted acrylic above 0) without changing the image's own pixels. The other scenarios run with transparency 0
/// (their "image on screen" check compares against the opaque stage color).
/// </summary>
internal sealed partial class Runner
{
    /// <summary>Added to every settings file the harness writes: an opaque background, as the pixel checks expect.</summary>
    private string OpaqueBackground => $", \"backgroundTransparency\": {options.BackgroundTransparency}";

    private static readonly (byte R, byte G, byte B) AppearanceColor = (0x33, 0x66, 0xCC);

    private void RunAppearanceScenarios(List<TestAsset> assets)
    {
        string folder = Path.GetDirectoryName(assets[0].Path)! + " Appearance";
        Directory.CreateDirectory(folder);
        string solid = Path.Combine(folder, "01 solid.png");
        Task.Run(() => WriteSolidPngAsync(solid, 640, 420, AppearanceColor)).GetAwaiter().GetResult();
        try
        {
            Guarded("window-size", () => WindowSizeScenario(solid),
                "Quick View opens at the size QuickViewWindowSize gives for the monitor's work area and scale (60 % × 64 %, at most 1600 × 1040 DIP, at most 16:10), smaller than the earlier 70 % × 75 %");
            Guarded("window-transparency", () => WindowTransparencyScenario(solid),
                "backgroundTransparency 20 in settings: the next Space shows the tinted acrylic (backdrop mark) and the image's pixels on screen are the same as with 0 (Mica, opaque stage); 0 again: Mica");
            RunAppearanceLookScenarios(solid);
        }
        finally
        {
            WriteHostSettings();
        }
    }

    private (bool, string) WindowSizeScenario(string image)
    {
        WriteHostSettings();
        nint explorer = OpenExplorer(image);
        if (explorer == 0)
        {
            return (false, "Explorer window not found");
        }

        if (!OpenQuickView(explorer, image, out string why))
        {
            return (false, why);
        }

        Mark? placed = _log.Marks.LastOrDefault(m => m.Name == "placed");
        CloseFromExplorer(explorer);
        if (placed?.Number("workWidth") is not { } workWidth || placed.Number("workHeight") is not { } workHeight || placed.Number("dpi") is not { } dpi)
        {
            return (false, "no placed mark with the work area");
        }

        (int width, int height) = QuickViewWindowSize.For((int)workWidth, (int)workHeight, dpi / 96.0);
        double shownWidth = placed.Number("width") ?? 0, shownHeight = placed.Number("height") ?? 0;
        bool expected = Math.Abs(shownWidth - width) <= 2 && Math.Abs(shownHeight - height) <= 2;
        bool smaller = shownWidth < workWidth * 0.7 && shownHeight < workHeight * 0.75;
        return (expected && smaller, $"work area {workWidth}x{workHeight} @{dpi} dpi: window {shownWidth}x{shownHeight} (expected {width}x{height}; earlier {(int)(workWidth * 0.7)}x{(int)(workHeight * 0.75)})");
    }

    private (bool, string) WindowTransparencyScenario(string image)
    {
        var observed = new List<string>();
        bool ok = true;
        (byte R, byte G, byte B)? opaquePixel = null;
        foreach (int transparency in new[] { 0, 20, 0 })
        {
            File.WriteAllText(HostSettingsPath, $"{{\"version\": 1, \"imageScale\": \"{(options.ScaleMode == "actual" ? "ActualSize" : "FitNoUpscale")}\", \"backgroundTransparency\": {transparency}}}");
            nint explorer = OpenExplorer(image);
            string why = "Explorer window not found";
            if (explorer == 0 || !OpenQuickView(explorer, image, out why))
            {
                observed.Add($"{transparency}%: {why}");
                ok = false;
                continue;
            }

            Thread.Sleep(400); // the backdrop settles
            Mark? backdrop = _log.Marks.LastOrDefault(m => m.Name == "backdrop");
            (byte R, byte G, byte B) pixel = (0, 0, 0);
            (byte R, byte G, byte B) stage = (0, 0, 0);
            if (Native.GetWindowRect(HostWindow(), out Native.RECT r))
            {
                // Window center is inside the image (centered on the stage under the bars); the stage is sampled near the bottom-left corner.
                pixel = ScreenColor(((r.Left + r.Right) / 2, r.Top + ((r.Bottom - r.Top) * 55 / 100)));
                stage = ScreenColor((r.Left + 24, r.Bottom - 24));
            }

            CloseFromExplorer(explorer);
            Thread.Sleep(300);
            string kind = backdrop?.Text("kind") ?? "?";
            bool kindOk = transparency == 0 ? kind is "Mica" or "Solid" : kind is "Acrylic" or "Solid"; // Solid: Low tier, or transparency effects unsupported
            bool near = Math.Abs(pixel.R - AppearanceColor.R) + Math.Abs(pixel.G - AppearanceColor.G) + Math.Abs(pixel.B - AppearanceColor.B) <= 9;
            bool same = opaquePixel is not { } o || Math.Abs(pixel.R - o.R) + Math.Abs(pixel.G - o.G) + Math.Abs(pixel.B - o.B) <= 3;
            opaquePixel ??= pixel;
            ok &= kindOk && near && same;
            observed.Add($"{transparency}%: backdrop {kind} (transparency {backdrop?.Number("transparency")}), image pixel #{pixel.R:X2}{pixel.G:X2}{pixel.B:X2} same={same}, stage #{stage.R:X2}{stage.G:X2}{stage.B:X2}");
        }

        return (ok, string.Join("; ", observed));
    }

    private static async Task WriteSolidPngAsync(string path, uint width, uint height, (byte R, byte G, byte B) color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (color.B, color.G, color.R, 255);
        }

        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
    }
}
