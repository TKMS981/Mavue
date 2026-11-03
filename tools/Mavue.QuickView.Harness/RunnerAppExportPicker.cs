using System.Security.Cryptography;
using Mavue.Metadata;

namespace Mavue.QuickView.Harness;

/// <summary>
/// "Export without metadata…" › "Choose where…": the Windows save dialog. It lives in the picker's host process, where
/// UI Automation sees its controls only as panes, so it is driven with window messages: the file name box (an Edit with
/// control id 1001) gets WM_SETTEXT, Save (IDOK 1), Cancel (IDCANCEL 2) and the overwrite question's Yes (IDYES 6) are
/// WM_COMMAND. Checks a copy in another folder, a converted copy (HEIC → PNG), choosing the original (refused,
/// unchanged) and cancelling.
/// </summary>
internal sealed partial class Runner
{
    private const string FileDialogClass = "#32770";

    private void RunAppExportPickerScenarios(string photo, string? heic)
    {
        string chosen = Path.GetDirectoryName(photo)! + " Chosen";
        if (Directory.Exists(chosen))
        {
            Directory.Delete(chosen, recursive: true);
        }

        Directory.CreateDirectory(chosen);
        Guarded("app-export-picker", () => AppExportPicker(photo, chosen),
            "\"Choose where…\" on a JPEG: the save dialog writes a verified, clean copy in another folder (Mark-of-the-Web kept); choosing the original itself is refused and the original stays byte-for-byte the same; cancelling the dialog writes nothing and the dialog keeps working");
        Guarded("app-export-picker-convert", () => heic is null ? (false, "no HEIF asset (HEIF extension missing?)") : AppExportPickerConvert(heic, chosen),
            "\"Choose where…\" on a HEIC photo with PNG chosen: a converted, verified PNG in the chosen folder with nothing private left; the original unchanged");
    }

    private (bool, string) AppExportPicker(string photo, string folder)
    {
        string before = Hash(photo);
        using AppSession app = LaunchApp(photo);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        var observed = new List<string>();
        app.WaitForState(s => s.Text("kind") == "Image", 15000);
        Thread.Sleep(500);
        int mark = app.Events.Count;
        RunUiaScript(app.Window, "MoreButton", OpenExportScript);
        bool ok = app.WaitFor(e => e.Name == "export-clean-check" && e.Text("kinds") is not null, 10000, mark) is not null;
        Thread.Sleep(400);

        // 1. Another folder.
        string target = Path.Combine(folder, "picked copy.jpg");
        mark = app.Events.Count;
        string typed = SaveThroughDialog(app, target, overwrite: false);
        AppEvent? saved = app.WaitFor(e => e.Name == "export-clean" && e.Text("destination") == "Chosen", 15000, mark);
        bool clean = File.Exists(target) && PrivacyCheck.Review(ImageMetadataReader.Read(target, findTrailer: true)).All(f => f.Level == PrivacyLevel.Kept);
        bool motw = TryReadText(target + ":Zone.Identifier")?.Contains("ZoneId=3", StringComparison.Ordinal) == true;
        observed.Add($"other folder ({typed}): exists={File.Exists(target)} verified={saved?.Bool("verified")} converted={saved?.Bool("converted")} clean={clean} Mark-of-the-Web={motw}");
        ok &= saved?.Bool("verified") == true && saved.Bool("converted") == false && clean && motw;

        // 2. The original itself (the dialog asks to replace it; yes): refused, nothing written.
        mark = app.Events.Count;
        typed = SaveThroughDialog(app, photo, overwrite: true);
        AppEvent? refused = app.WaitFor(e => e.Name == "export-clean" && e.Text("destination") == "Chosen", 10000, mark);
        bool unchanged = Hash(photo) == before;
        observed.Add($"original ({typed}): refused={refused?.Text("refused")} unchanged={unchanged}");
        ok &= refused?.Text("refused") == "original" && unchanged;

        // 3. Cancel.
        int files = Directory.GetFiles(folder).Length;
        mark = app.Events.Count;
        string cancelled = CancelDialog(app);
        AppEvent? cancel = app.WaitFor(e => e.Name == "export-clean" && e.Text("destination") == "Chosen", 10000, mark);
        observed.Add($"cancel ({cancelled}): cancelled={cancel?.Bool("cancelled")} nothing written={Directory.GetFiles(folder).Length == files}");
        ok &= cancel?.Bool("cancelled") == true && Directory.GetFiles(folder).Length == files;

        // The dialog still works after a cancel: save again.
        string again = Path.Combine(folder, "picked again.jpg");
        mark = app.Events.Count;
        SaveThroughDialog(app, again, overwrite: false);
        AppEvent? second = app.WaitFor(e => e.Name == "export-clean" && e.Text("destination") == "Chosen", 15000, mark);
        observed.Add($"after cancel: verified={second?.Bool("verified")} exists={File.Exists(again)}");
        ok &= second?.Bool("verified") == true && File.Exists(again) && Hash(photo) == before;

        app.SendKey(Native.VK_ESCAPE);
        Thread.Sleep(400);
        (bool closed, _) = app.Close(expectPlayerStop: false);
        return (ok && closed, string.Join("; ", observed));
    }

    private (bool, string) AppExportPickerConvert(string heic, string folder)
    {
        string before = Hash(heic);
        using AppSession app = LaunchApp(heic);
        if (app.Window == 0)
        {
            return (false, "no window");
        }

        app.WaitForState(s => s.Text("kind") == "Image", 15000);
        Thread.Sleep(500);
        int mark = app.Events.Count;
        RunUiaScript(app.Window, "MoreButton", OpenExportScript);
        AppEvent? check = app.WaitFor(e => e.Name == "export-clean-check" && e.Text("container") is not null, 10000, mark);
        Thread.Sleep(400);
        string picked = RunUiaScript(app.Window, "ExportFormat",
            "if ($el) { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 500; " +
            "$items = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))); " +
            "$png = $items | Where-Object { $_.Current.Name -like '*PNG*' } | Select-Object -First 1; " +
            "if ($png) { $png.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300; " +
            "try { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}; Write-Output ('selected ' + $png.Current.Name) } else { Write-Output 'no-png' } } else { Write-Output 'no-combo' }");
        Thread.Sleep(400);

        string target = Path.Combine(folder, "picked photo.png");
        mark = app.Events.Count;
        string typed = SaveThroughDialog(app, target, overwrite: false);
        AppEvent? saved = app.WaitFor(e => e.Name == "export-clean" && e.Text("destination") == "Chosen" && e.Bool("verified") is not null, 60000, mark);
        bool png = File.Exists(target) && ImageMetadataReader.Read(target, findTrailer: true).Container == MetadataContainer.Png;
        bool clean = png && PrivacyCheck.Review(ImageMetadataReader.Read(target, findTrailer: true)).All(f => f.Level == PrivacyLevel.Kept);
        bool unchanged = Hash(heic) == before;
        app.SendKey(Native.VK_ESCAPE);
        Thread.Sleep(400);
        (bool closed, _) = app.Close(expectPlayerStop: false);
        bool ok = check?.Text("container") == "Heif" && saved?.Bool("verified") == true && saved.Bool("converted") == true && saved.Text("format") == "Png" && png && clean && unchanged;
        return (ok && closed, $"format: {picked}; save ({typed}): verified={saved?.Bool("verified")} converted={saved?.Bool("converted")} format={saved?.Text("format")} in {saved?.Number("ms")} ms, PNG={png}, clean={clean}, original unchanged={unchanged}{(saved?.Text("error") is { } error ? " error " + error : string.Empty)}");
    }

    /// <summary>Clicks "Choose where…", types <paramref name="path"/> in the save dialog and saves (answering Yes to replacing).</summary>
    private string SaveThroughDialog(AppSession app, string path, bool overwrite)
    {
        nint dialog = OpenFileDialog(app);
        if (dialog == 0)
        {
            return "no save dialog";
        }

        nint nameBox = Native.ChildWindows(dialog).FirstOrDefault(w => Native.GetDlgCtrlID(w) == 1001 && Native.ClassName(w) == "Edit");
        if (nameBox == 0)
        {
            return "no file name box";
        }

        Native.SendMessageText(nameBox, Native.WM_SETTEXT, 0, path);
        Thread.Sleep(300);
        Native.PostMessageW(dialog, Native.WM_COMMAND, 1, 0); // IDOK: Save
        if (overwrite)
        {
            nint question = 0;
            WaitFor(() => (question = OwnedDialog(dialog)) != 0, 5000, null);
            if (question == 0)
            {
                return "typed, saved, no replace question";
            }

            Thread.Sleep(300);
            Native.SendMessage(question, 0x0466, 6, 0); // TDM_CLICK_BUTTON IDYES (task dialog)
            Thread.Sleep(300);
            if (Native.IsWindow(question) && Native.IsWindowVisible(question))
            {
                Native.PostMessageW(question, Native.WM_COMMAND, 6, 0); // IDYES (message box)
            }

            return "typed, saved, replace? yes";
        }

        return "typed, saved";
    }

    private string CancelDialog(AppSession app)
    {
        nint dialog = OpenFileDialog(app);
        if (dialog == 0)
        {
            return "no save dialog";
        }

        Native.PostMessageW(dialog, Native.WM_COMMAND, 2, 0); // IDCANCEL
        return "cancelled";
    }

    /// <summary>Invokes "Choose where…" without waiting (the click handler awaits the dialog) and returns the dialog's window.</summary>
    private nint OpenFileDialog(AppSession app)
    {
        _ = Task.Run(() => RunUiaScript(app.Window, "ExportChoose", InvokeScript));
        nint dialog = 0;
        WaitFor(() => (dialog = OwnedDialog(app.Window)) != 0, 10000, null);
        Thread.Sleep(dialog == 0 ? 0 : 800); // the dialog fills its folder view
        return dialog;
    }

    /// <summary>
    /// A visible dialog owned by <paramref name="owner"/>. The save dialog belongs to the picker's host process, not to
    /// Mavue.exe, so it is found by its owner window rather than by process.
    /// </summary>
    private static nint OwnedDialog(nint owner) =>
        Native.TopLevelWindows().FirstOrDefault(w => Native.GetWindow(w, Native.GW_OWNER) == owner && Native.ClassName(w) == FileDialogClass && Native.IsWindowVisible(w));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
