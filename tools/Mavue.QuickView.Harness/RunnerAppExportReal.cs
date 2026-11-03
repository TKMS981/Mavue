using Mavue.Metadata;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Opt-in: "Export without metadata…" on real camera files (iPhone HEIC, camera RAW) named in the environment variable
/// MAVUE_E2E_REAL_IMAGES (separated by ';'). Each file is copied to a temporary folder first; only the copies are
/// opened, and the report names the kinds of metadata found, never their values.
/// </summary>
internal sealed partial class Runner
{
    private void RunAppExportRealScenarios()
    {
        string? list = System.Environment.GetEnvironmentVariable("MAVUE_E2E_REAL_IMAGES");
        if (string.IsNullOrWhiteSpace(list))
        {
            return;
        }

        string folder = Path.Combine(options.WorkDirectory, "real-images");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
        int index = 0;
        foreach (string original in list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            index++;
            string copy = Path.Combine(folder, $"{index:00} real{Path.GetExtension(original).ToLowerInvariant()}");
            File.Copy(original, copy);
            Guarded($"app-export-real-{index:00}", () => AppExportReal(copy),
                "A real camera file (copy): the review lists what it carries; \"Save a copy\" converts it to JPEG in the worker, the copy is verified, nothing private is left, it is upright like the original, and the original is unchanged");
        }
    }

    private (bool, string) AppExportReal(string file)
    {
        string before = Hash(file);
        using AppSession app = LaunchApp(file);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        app.WaitForState(s => s.Text("kind") is "Image" or "Error" or "Unsupported", 20000);
        Thread.Sleep(500);
        int mark = app.Events.Count;
        RunUiaScript(app.Window, "MoreButton", OpenExportScript);
        AppEvent? check = app.WaitFor(e => e.Name == "export-clean-check" && e.Text("container") is not null, 15000, mark);
        Thread.Sleep(400);
        string folder = Path.GetDirectoryName(file)!;
        string[] existing = Directory.GetFiles(folder);
        mark = app.Events.Count;
        RunUiaScript(app.Window, "ExportSaveNext", InvokeScript);
        AppEvent? saved = app.WaitFor(e => e.Name == "export-clean" && e.Text("destination") == "SameFolder", 90000, mark);
        string? copy = Directory.GetFiles(folder).Except(existing).FirstOrDefault();
        string left = "?";
        bool clean = false, upright = false;
        string sizes = string.Empty;
        if (copy is not null)
        {
            IReadOnlyList<PrivacyFinding> after = PrivacyCheck.Review(ImageMetadataReader.Read(copy, findTrailer: true));
            clean = after.All(f => f.Level == PrivacyLevel.Kept);
            left = string.Join(",", after.Select(f => f.Kind));
            ((uint W, uint H) source, (uint W, uint H) result) = Task.Run(async () => (await OrientedSizeAsync(file), await OrientedSizeAsync(copy))).GetAwaiter().GetResult();
            upright = (source.W >= source.H) == (result.W >= result.H) && source.W > 0 && result.W > 0;
            sizes = $"{source.W}x{source.H} → {result.W}x{result.H}";
        }

        bool unchanged = Hash(file) == before;
        app.SendKey(Native.VK_ESCAPE);
        Thread.Sleep(400);
        (bool closed, _) = app.Close(expectPlayerStop: false);
        bool ok = saved?.Bool("verified") == true && copy is not null && clean && upright && unchanged;
        return (ok && closed, $"{Path.GetExtension(file)}: container={check?.Text("container")} found=[{check?.Text("kinds")}] canDecode={check?.Bool("canDecode")}; " +
            $"saved {Path.GetExtension(copy)} converted={saved?.Bool("converted")} verified={saved?.Bool("verified")} in {saved?.Number("ms")} ms{(saved?.Text("failure") is { } f ? " failure " + f : string.Empty)}; " +
            $"left=[{left}] clean={clean}; oriented {sizes} upright={upright}; original unchanged={unchanged}");
    }

    /// <summary>The size Windows shows the image at (orientation applied).</summary>
    private static async Task<(uint, uint)> OrientedSizeAsync(string path)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStream stream = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            return (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return (0, 0);
        }
    }
}
