using System.Diagnostics;
using System.Security.Cryptography;
using Mavue.Metadata;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Phase 5 follow-up (docs/ARCHITECTURE.md §4.7.14–15): a converted clean copy of a HEIF photo made by the export
/// worker, turned and mirrored copies found by the similar search, and the search settings menu (sensitivity, cache
/// deletion). The main application's export uses Windows' save dialog and is checked by hand (TESTING.md).
/// </summary>
internal sealed partial class Runner
{
    private void RunPhase6Scenarios(List<TestAsset> assets)
    {
        string root = Path.GetDirectoryName(assets[0].Path)! + " Phase6";
        string rotations = root + " 回転";
        foreach (string folder in new[] { root, rotations })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.CreateDirectory(folder);
        }

        TestAsset? heif = assets.FirstOrDefault(a => a.Case == "heif");
        string heic = Path.Combine(root, "01 photo.heic");
        string target = Path.Combine(rotations, "01 target.jpg");
        try
        {
            if (heif is not null)
            {
                File.Copy(heif.Path, heic, overwrite: true);
                File.WriteAllText(heic + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            }

            Task.Run(() => WriteRotationFilesAsync(target)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Guarded("share-convert", () => (false, $"could not prepare files: {ex.GetType().Name} {ex.Message}"), "prepared files");
            return;
        }

        WriteHostSettings();
        Guarded("share-convert", () => heif is null ? (false, "no HEIF asset (HEIF extension missing?)") : ShareConvertScenario(heic),
            "Check before sharing on a HEIF photo: the copy is converted to JPEG by the export worker (memory-limited process): verified, nothing private left, decodes upright, the original unchanged, Mark-of-the-Web copied; no worker process remains");
        Guarded("similar-rotations", () => SimilarRotationsScenario(target),
            "The similar search (rotations on, the default) finds a copy turned by 90° and a mirrored copy, and says so in the result's tooltip");
        Guarded("similar-settings", () => SimilarSettingsScenario(target),
            "The search settings menu: \"nearly identical\" searches again with the 84 % threshold and is saved; deleting the cache removes the file and says how much was freed");
        WriteHostSettings();
    }

    private static async Task WriteRotationFilesAsync(string target)
    {
        string folder = Path.GetDirectoryName(target)!;
        await TestAssets.EncodeAsync(target, BitmapEncoder.JpegEncoderId, 800, 600, 0.9);
        await EncodeTurnedAsync(target, Path.Combine(folder, "02 turned.jpg"), BitmapRotation.Clockwise90Degrees, BitmapFlip.None);
        await EncodeTurnedAsync(target, Path.Combine(folder, "03 mirrored.jpg"), BitmapRotation.None, BitmapFlip.Horizontal);
        for (int i = 0; i < 8; i++)
        {
            await EncodeSolidAsync(Path.Combine(folder, $"{i + 10} other.png"), BitmapEncoder.PngEncoderId, 300, 200, ((byte)(i * 30), (byte)(200 - (i * 20)), 90));
        }
    }

    /// <summary>Re-encodes <paramref name="source"/> turned and/or flipped (the pixels, not an EXIF tag).</summary>
    private static async Task EncodeTurnedAsync(string source, string path, BitmapRotation rotation, BitmapFlip flip)
    {
        StorageFile input = await StorageFile.GetFileFromPathAsync(source);
        using IRandomAccessStream read = await input.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(read);
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform { Rotation = rotation, Flip = flip }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        bool swap = rotation is BitmapRotation.Clockwise90Degrees or BitmapRotation.Clockwise270Degrees;
        uint width = swap ? decoder.PixelHeight : decoder.PixelWidth;
        uint height = swap ? decoder.PixelWidth : decoder.PixelHeight;
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path));
        StorageFile file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using IRandomAccessStream write = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, write);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();
    }

    private (bool, string) ShareConvertScenario(string heic)
    {
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(heic)));
        if (!ShowInQuickView(heic, out long t0, out string why))
        {
            return (false, why);
        }

        nint host = HostWindow();
        var observed = new List<string>();
        RunUiaScript(host, "QuickViewShareCheckButton", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        Mark? check = WaitForMark(t0, "share-check", _ => true);
        observed.Add($"check: container={check?.Text("container")} canStrip={check?.Bool("canStrip")} findings={check?.Number("findings")}");
        bool ok = check?.Text("container") == "Heif" && check.Bool("canStrip") == false;

        string folder = Path.GetDirectoryName(heic)!;
        string[] existing = Directory.GetFiles(folder);
        _host?.Refresh();
        long hostBefore = _host?.WorkingSet64 ?? 0;
        long tSave = Stopwatch.GetTimestamp();
        RunUiaScript(host, "QuickViewShareSave", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        Mark? saved = WaitForMark(tSave, "share-copy", _ => true, 60_000);
        _host?.Refresh();
        long hostAfter = _host?.WorkingSet64 ?? 0;
        string? copy = Directory.GetFiles(folder).Except(existing).FirstOrDefault(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        bool clean = false, jpeg = false, motw = false;
        if (copy is not null)
        {
            ImageMetadata after = ImageMetadataReader.Read(copy, findTrailer: true);
            jpeg = after.Container == MetadataContainer.Jpeg;
            clean = PrivacyCheck.Review(after).All(f => f.Level == PrivacyLevel.Kept);
            motw = TryReadText(copy + ":Zone.Identifier")?.Contains("ZoneId=3", StringComparison.Ordinal) == true;
        }

        bool workersGone = WaitFor(() => LongQuery("@(Get-CimInstance Win32_Process -Filter \"Name='Mavue.QuickView.Host.exe'\" | Where-Object { $_.CommandLine -like '*--export-worker*' }).Count") == 0, 5000, null);
        string after1 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(heic)));
        observed.Add($"saved \"{Path.GetFileName(copy)}\": converted={saved?.Bool("converted")} verified={saved?.Bool("verified")} in {saved?.Number("ms")} ms, JPEG={jpeg}, clean={clean}, Mark-of-the-Web={motw}, original unchanged={before == after1}, workers left none={workersGone}; host {hostBefore / (1024 * 1024)} → {hostAfter / (1024 * 1024)} MB{(saved?.Text("error") is { } error ? " error " + error : string.Empty)}");
        ok &= saved?.Bool("converted") == true && saved.Bool("verified") == true && jpeg && clean && motw && before == after1 && workersGone;
        ForceForeground(host);
        SendKeyIfForeground(Native.VK_ESCAPE, host, out _);
        Thread.Sleep(300);
        ok &= CloseHostWindow();
        return (ok, string.Join("; ", observed));
    }

    private (bool, string) SimilarRotationsScenario(string target)
    {
        if (!ShowInQuickView(target, out _, out string why))
        {
            return (false, why);
        }

        nint host = HostWindow();
        long tStart = Stopwatch.GetTimestamp();
        RunUiaScript(host, "QuickViewSimilarButton", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        Mark? start = WaitForMark(tStart, "similar-start", _ => true);
        Mark? done = WaitForMark(tStart, "similar-done", _ => true, 60_000);
        Thread.Sleep(400);
        string names = RunUiaScript(host, "QuickViewSimilarResults", "if ($el) { foreach ($b in $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) { Write-Output $b.Current.Name } }");
        bool turned = names.Contains("02 turned.jpg", StringComparison.Ordinal);
        bool mirrored = names.Contains("03 mirrored.jpg", StringComparison.Ordinal);
        bool explained = names.Contains("回転", StringComparison.Ordinal) || names.Contains("turned", StringComparison.OrdinalIgnoreCase);
        bool ok = start?.Bool("rotations") == true && done is not null && turned && mirrored && explained && !names.Contains("other.png", StringComparison.Ordinal);
        RunUiaScript(host, "QuickViewSimilarClose", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        ok &= CloseHostWindow();
        return (ok, $"rotations={start?.Bool("rotations")}, threshold={start?.Number("threshold")}; {done?.Number("matches")} of {done?.Number("examined")}; turned found={turned}, mirrored found={mirrored}, tooltip explains={explained}");
    }

    private (bool, string) SimilarSettingsScenario(string target)
    {
        if (!ShowInQuickView(target, out _, out string why))
        {
            return (false, why);
        }

        nint host = HostWindow();
        var observed = new List<string>();
        long tStart = Stopwatch.GetTimestamp();
        RunUiaScript(host, "QuickViewSimilarButton", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        WaitForMark(tStart, "similar-done", _ => true, 60_000);

        // Open the menu and choose "nearly identical": a new search with the strict threshold, saved in the settings.
        long tStrict = Stopwatch.GetTimestamp();
        string menu = "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 400; " +
            "$root = [System.Windows.Automation.AutomationElement]::RootElement; " +
            "$item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, '{0}'))); " +
            "if ($item) { $p = $null; " +
            "if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); Write-Output 'select' } " +
            "elseif ($item.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); Write-Output 'toggle' } " +
            "elseif ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); Write-Output 'invoke' } " +
            "else { Write-Output 'no-pattern' } } else { Write-Output 'missing' } }";
        string strict = RunUiaScript(host, "QuickViewSimilarOptions", menu.Replace("{0}", "QuickViewSimilarStrict", StringComparison.Ordinal));
        Mark? restarted = WaitForMark(tStrict, "similar-start", _ => true);
        Thread.Sleep(500);
        bool saved = File.ReadAllText(HostSettingsPath).Contains("\"similarSensitivity\": \"Strict\"", StringComparison.Ordinal);
        observed.Add($"strict: menu={strict}, threshold={restarted?.Number("threshold")}, saved={saved}");
        bool ok = restarted?.Number("threshold") == 84 && saved;
        WaitForMark(tStrict, "similar-done", _ => true, 60_000);

        // Delete the cache from the same menu.
        string cache = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Mavue", "QuickView", "SimilarCache", "signatures.bin");
        bool existed = File.Exists(cache);
        long tClear = Stopwatch.GetTimestamp();
        string clear = RunUiaScript(HostWindow(), "QuickViewSimilarOptions", menu.Replace("{0}", "QuickViewSimilarClearCache", StringComparison.Ordinal));
        Mark? cleared = WaitForMark(tClear, "similar-cache-cleared", _ => true);
        observed.Add($"clear: menu={clear}, existed={existed}, deleted={cleared?.Bool("deleted")} ({cleared?.Number("bytes")} bytes), file gone={!File.Exists(cache)}");
        ok &= existed && cleared?.Bool("deleted") == true && !File.Exists(cache);
        RunUiaScript(HostWindow(), "QuickViewSimilarClose", "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }");
        ok &= CloseHostWindow();
        return (ok, string.Join("; ", observed));
    }
}
