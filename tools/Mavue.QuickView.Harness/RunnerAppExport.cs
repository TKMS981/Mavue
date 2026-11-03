using System.Security.Cryptography;
using Mavue.Metadata;
using Windows.Graphics.Imaging;

namespace Mavue.QuickView.Harness;

/// <summary>
/// The main app's "Export without metadata…" (ARCHITECTURE §4.7.15): the same review and copy as Quick View's check
/// before sharing. "Save a copy" and "Copy to clipboard" need no save dialog, so they are driven here; the save dialog
/// ("Choose where…") is checked by hand (TESTING.md).
/// </summary>
internal sealed partial class Runner
{
    private const string InvokeScript = "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output 'ok' } else { Write-Output 'missing' }";

    /// <summary>Opens the command bar's overflow and invokes "Export without metadata…".</summary>
    private const string OpenExportScript =
        "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 600; " +
        "$item = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, 'ExportCleanButton'))); " +
        "if (-not $item) { $item = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, 'ExportCleanButton'))) }; " +
        "if ($item) { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output 'ok' } else { Write-Output 'no-command' } } else { Write-Output 'no-more' }";

    private void RunAppExportScenarios(List<TestAsset> assets)
    {
        string root = Path.GetDirectoryName(assets[0].Path)! + " App Export";
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        string photo = Path.Combine(root, "01 photo.jpg");
        string heic = Path.Combine(root, "02 photo.heic");
        string svg = Path.Combine(root, "03 drawing.svg");
        TestAsset? heif = assets.FirstOrDefault(a => a.Case == "heif");
        try
        {
            Task.Run(async () =>
            {
                await TestAssets.EncodeAsync(photo, BitmapEncoder.JpegEncoderId, 1200, 800, 0.9);
                byte[] jpeg = await File.ReadAllBytesAsync(photo);
                byte[] exif = Phase5Exif();
                await File.WriteAllBytesAsync(photo, [0xFF, 0xD8, 0xFF, 0xE1, (byte)((exif.Length + 8) >> 8), (byte)(exif.Length + 8), .. "Exif\0\0"u8, .. exif, .. jpeg[2..]]);
            }).GetAwaiter().GetResult();
            File.WriteAllText(photo + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            if (heif is not null)
            {
                File.Copy(heif.Path, heic, overwrite: true);
            }

            File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\"><title>Secret plan</title><rect width=\"200\" height=\"100\" fill=\"#3a7\"/></svg>");
        }
        catch (Exception ex)
        {
            Guarded("app-export-clean", () => (false, $"could not prepare files: {ex.GetType().Name} {ex.Message}"), "prepared files");
            return;
        }

        Guarded("app-export-clean", () => AppExportClean(photo),
            "… › Export without metadata on a JPEG with location, camera, serial number and owner: the dialog lists them; \"Save a copy\" writes \"… (no metadata).jpg\" next to it (verified, nothing private left, Mark-of-the-Web copied, the original byte-for-byte unchanged); \"Copy to clipboard\" puts a clean copy on the clipboard");
        Guarded("app-export-convert", () => heif is null ? (false, "no HEIF asset (HEIF extension missing?)") : AppExportConvert(heic),
            "The same on a HEIC photo: converted to JPEG by Mavue.exe's export worker (memory-limited job), verified, the original unchanged; no worker process remains");
        Guarded("app-export-unsupported", () => AppExportUnsupported(svg),
            "On an SVG drawing the command explains that the type is not handled (nothing is read or written) and the app keeps working");
        RunAppExportPickerScenarios(photo, heif is null ? null : heic);
        RunAppExportRealScenarios();
    }

    private (bool, string) AppExportClean(string photo)
    {
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(photo)));
        using AppSession app = LaunchApp(photo);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        var observed = new List<string>();
        app.WaitForState(s => s.Text("kind") == "Image", 15000);
        Thread.Sleep(500);
        int mark = app.Events.Count;
        string open = RunUiaScript(app.Window, "MoreButton", OpenExportScript);
        AppEvent? check = app.WaitFor(e => e.Name == "export-clean-check", 10000, mark);
        string kinds = check?.Text("kinds") ?? string.Empty;
        bool found = new[] { "Location", "CameraDetails", "DeviceIdentity", "OwnerName" }.All(k => kinds.Contains(k, StringComparison.Ordinal));
        observed.Add($"open={open}; check: {check?.Number("findings")} findings ({kinds}), canStrip={check?.Bool("canStrip")}");
        bool ok = found && check?.Bool("canStrip") == true;
        Thread.Sleep(400);

        string folder = Path.GetDirectoryName(photo)!;
        string[] existing = Directory.GetFiles(folder, "*.jpg");
        mark = app.Events.Count;
        string clicked = RunUiaScript(app.Window, "ExportSaveNext", InvokeScript);
        AppEvent? saved = app.WaitFor(e => e.Name == "export-clean" && e.Text("destination") == "SameFolder", 15000, mark);
        string? copy = Directory.GetFiles(folder, "*.jpg").Except(existing).FirstOrDefault();
        bool clean = false, motw = false;
        if (copy is not null)
        {
            ImageMetadata after = ImageMetadataReader.Read(copy, findTrailer: true);
            clean = PrivacyCheck.Review(after).All(f => f.Level == PrivacyLevel.Kept);
            motw = TryReadText(copy + ":Zone.Identifier")?.Contains("ZoneId=3", StringComparison.Ordinal) == true;
        }

        observed.Add($"save ({clicked}): \"{Path.GetFileName(copy)}\" verified={saved?.Bool("verified")} converted={saved?.Bool("converted")} in {saved?.Number("ms")} ms, clean={clean}, Mark-of-the-Web={motw}{(saved?.Text("failure") is { } f ? " failure " + f : string.Empty)}");
        ok &= saved?.Bool("verified") == true && saved.Bool("converted") == false && copy is not null && clean && motw;

        ClearClipboard();
        mark = app.Events.Count;
        RunUiaScript(app.Window, "ExportClipboard", InvokeScript);
        AppEvent? clip = app.WaitFor(e => e.Name == "export-clean" && e.Text("destination") == "Clipboard", 15000, mark);
        bool onClipboard = ClipboardHasFiles();
        observed.Add($"clipboard: verified={clip?.Bool("verified")}, files on the clipboard={onClipboard}");
        ok &= clip?.Bool("verified") == true && onClipboard;

        string after1 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(photo)));
        observed.Add($"original unchanged={before == after1}");
        ok &= before == after1;
        app.SendKey(Native.VK_ESCAPE); // close the dialog
        Thread.Sleep(400);
        (bool closed, _) = app.Close(expectPlayerStop: false);
        return (ok && closed, string.Join("; ", observed));
    }

    private (bool, string) AppExportConvert(string heic)
    {
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(heic)));
        using AppSession app = LaunchApp(heic);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        var observed = new List<string>();
        app.WaitForState(s => s.Text("kind") == "Image", 15000);
        Thread.Sleep(500);
        int mark = app.Events.Count;
        RunUiaScript(app.Window, "MoreButton", OpenExportScript);
        AppEvent? check = app.WaitFor(e => e.Name == "export-clean-check", 10000, mark);
        observed.Add($"check: container={check?.Text("container")} canStrip={check?.Bool("canStrip")} canDecode={check?.Bool("canDecode")}");
        bool ok = check?.Text("container") == "Heif" && check.Bool("canStrip") == false && check.Bool("canDecode") == true;
        Thread.Sleep(400);

        string folder = Path.GetDirectoryName(heic)!;
        string[] existing = Directory.GetFiles(folder);
        mark = app.Events.Count;
        RunUiaScript(app.Window, "ExportSaveNext", InvokeScript);
        AppEvent? saved = app.WaitFor(e => e.Name == "export-clean" && e.Text("destination") == "SameFolder", 60000, mark);
        string? copy = Directory.GetFiles(folder).Except(existing).FirstOrDefault(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
        bool jpeg = copy is not null && ImageMetadataReader.Read(copy, findTrailer: true).Container == MetadataContainer.Jpeg;
        bool workersGone = WaitFor(() => LongQuery("@(Get-CimInstance Win32_Process -Filter \"Name='Mavue.exe'\" | Where-Object { $_.CommandLine -like '*--export-worker*' }).Count") == 0, 5000, null);
        string after1 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(heic)));
        observed.Add($"saved \"{Path.GetFileName(copy)}\": converted={saved?.Bool("converted")} verified={saved?.Bool("verified")} in {saved?.Number("ms")} ms, JPEG={jpeg}, original unchanged={before == after1}, workers left none={workersGone}{(saved?.Text("error") is { } error ? " error " + error : string.Empty)}");
        ok &= saved?.Bool("converted") == true && saved.Bool("verified") == true && jpeg && before == after1 && workersGone;
        app.SendKey(Native.VK_ESCAPE);
        Thread.Sleep(400);
        (bool closed, _) = app.Close(expectPlayerStop: false);
        return (ok && closed, string.Join("; ", observed));
    }

    private (bool, string) AppExportUnsupported(string svg)
    {
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(svg)));
        string folder = Path.GetDirectoryName(svg)!;
        int filesBefore = Directory.GetFiles(folder).Length;
        using AppSession app = LaunchApp(svg);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        app.WaitForState(s => s.Text("kind") is "Svg" or "Image", 15000);
        Thread.Sleep(500);
        int mark = app.Events.Count;
        string open = RunUiaScript(app.Window, "MoreButton", OpenExportScript);
        AppEvent? check = app.WaitFor(e => e.Name == "export-clean-check", 10000, mark);
        Thread.Sleep(400);
        app.SendKey(Native.VK_ESCAPE); // close the explanation
        Thread.Sleep(400);
        AppEvent? state = app.Events.LastOrDefault(e => e.Name == "state");
        bool unchanged = before == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(svg))) && Directory.GetFiles(folder).Length == filesBefore;
        (bool closed, _) = app.Close(expectPlayerStop: false);
        bool ok = check?.Text("refused") == "svg" && unchanged && state?.Text("kind") == "Image" && closed;
        return (ok, $"open={open}, refused={check?.Text("refused")}, nothing written={unchanged}, still showing={state?.Text("kind")}");
    }
}
