using System.Diagnostics;
using System.Text;
using Windows.Graphics.Imaging;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Files Quick View cannot show (no decoder, damaged): moving to them with ↓ shows their message, and the message
/// stays — neither the stale-image guard nor the shell thumbnail lookup that ends later replaces it with "loading"
/// (docs/ARCHITECTURE.md §4.7.6 item 3).
/// </summary>
internal sealed partial class Runner
{
    private static readonly string[] LoadingTexts = ["Loading preview…", "プレビューを読み込んでいます…"];

    // QuickView_Error_Unsupported and QuickView_Error_Failed (en-US, ja-JP).
    private static readonly string[] MessageTexts =
    [
        "No decoder for this format is installed on this PC.",
        "This file could not be previewed. It may be damaged or in an unsupported format.",
        "この形式のデコーダーがこの PC にインストールされていません。",
        "このファイルをプレビューできませんでした。破損しているか、対応していない形式の可能性があります。",
    ];

    private void RunMessageScenario(List<TestAsset> assets)
    {
        string folder = Path.GetDirectoryName(assets[0].Path)! + " メッセージ";
        Directory.CreateDirectory(folder);
        string image = Path.Combine(folder, "01 image.jpg");
        string data = Path.Combine(folder, "02 data.bin");
        string text = Path.Combine(folder, "03 notes.docx");
        string broken = Path.Combine(folder, "04 broken.jpg");
        File.Delete(Path.Combine(folder, "03 notes.txt")); // left by runs before the text preview (it would sit between the files)
        if (!File.Exists(image))
        {
            Task.Run(() => TestAssets.EncodeAsync(image, BitmapEncoder.JpegEncoderId, 640, 480, 0.9)).GetAwaiter().GetResult();
        }

        var random = new Random(42);
        byte[] noise = new byte[200_000];
        random.NextBytes(noise);
        noise[0] = 0x17; // not a known signature
        File.WriteAllBytes(data, noise);
        File.WriteAllText(text, "Quick View test: a document type Quick View does not show (text inside).\r\n", Encoding.UTF8);
        byte[] damaged = new byte[50_000];
        random.NextBytes(damaged);
        damaged[0] = 0xFF;
        damaged[1] = 0xD8;
        damaged[2] = 0xFF; // JPEG signature, garbage after it
        File.WriteAllBytes(broken, damaged);

        Guarded("message-not-replaced", () => MessagesStay(image, [data, text, broken]),
            "↓ from an image to a file with no decoder (random bytes), a document type (.docx) and a damaged JPEG: each shows its message within the stale-image grace and keeps it (no \"loading\" afterwards, no stale image); ↑ back to the image shows it again");
    }

    private (bool, string) MessagesStay(string image, string[] unshowable)
    {
        nint explorer = OpenExplorer(image);
        dynamic? window = ShellWindowFor(explorer);
        if (explorer == 0 || window is null)
        {
            return (false, "Explorer window not found");
        }

        window.Document.CurrentViewMode = FvmDetails;
        window.Document.SortColumns = "prop:System.ItemNameDisplay;";
        Select(window, image, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, image, out string why))
        {
            return (false, why);
        }

        bool ok = true;
        var observed = new List<string>();
        foreach (string path in unshowable)
        {
            long t = Stopwatch.GetTimestamp();
            if (!SendKeyIfForeground(Native.VK_DOWN, explorer, out _))
            {
                return (false, string.Join(", ", observed) + "; foreground changed");
            }

            (Mark? nav, Mark? done) = WaitForNavigation(t, "explorer-selection");
            if (nav is null || done is null)
            {
                ok = false;
                observed.Add($"{Path.GetFileName(path)}: no result");
                continue;
            }

            // Past the stale-image grace (120 ms) and any thumbnail lookup.
            Thread.Sleep(900);
            _log.Poll();
            // Since Phase 1 a document type goes straight to the information card (no decoder, so no message). Text files
            // have been shown as text since the text preview (docs/ARCHITECTURE.md §4.7.12).
            bool expectsMessage = !path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
            Mark? skipped = _log.Find(nav.Request, "full-skipped") ?? _log.Find(nav.Request, "error") ?? _log.Find(nav.Request, "info-card");
            List<Mark> after = skipped is null ? [] : _log.ForRequest(nav.Request).Where(m => m.Qpc > skipped.Qpc).ToList();
            bool replacedLater = after.Any(m => m.Name is "stale-cleared" or "thumbnail" or "full-set");
            bool suppressed = after.Any(m => m.Name == "thumbnail-suppressed");
            bool card = _log.Find(nav.Request, "info-card") is not null;
            string[] texts = HostTexts();
            bool showsLoading = texts.Any(s => LoadingTexts.Contains(s));
            string? message = texts.FirstOrDefault(s => MessageTexts.Contains(s));
            bool itemOk = ShowsFile(nav.Request, path) && skipped is not null && !replacedLater && !showsLoading && card &&
                (expectsMessage ? message is not null : message is null);
            message ??= card ? "(information card)" : null;
            ok &= itemOk;
            observed.Add($"{Path.GetFileName(path)}: {skipped?.Text("reason") ?? skipped?.Name ?? "no message"} in {(skipped is null ? 0 : Ms(t, skipped.Qpc)):0} ms" +
                $"{(suppressed ? " (late thumbnail suppressed)" : string.Empty)}, on screen \"{Shorten(message)}\"{(showsLoading ? " + LOADING" : string.Empty)}{(replacedLater ? " REPLACED" : string.Empty)}");
        }

        // Back to the image: shown again.
        for (int i = 0; i < unshowable.Length; i++)
        {
            SendKeyIfForeground(VkUp, explorer, out _);
            Thread.Sleep(250);
        }

        long tBack = Stopwatch.GetTimestamp();
        bool back = WaitFor(
            () =>
            {
                _log.Poll();
                return _log.Marks.Any(m => m.Name == "full-visible" && m.Qpc >= tBack - Stopwatch.Frequency) && ExplorerSelectionIs(explorer, image);
            },
            5000,
            null);
        observed.Add(back ? "↑ back to the image: shown" : "↑ back: image not shown");
        bool closed = CloseFromExplorer(explorer);
        ok &= back && closed;
        return (ok, string.Join("; ", observed) + (closed ? string.Empty : "; Esc did not close"));
    }

    private static string Shorten(string? text) => text is null ? "(none)" : text.Length <= 24 ? text : text[..24] + "…";

    /// <summary>Names of the text elements of the Quick View window (UI Automation).</summary>
    private string[] HostTexts()
    {
        nint host = HostWindow();
        if (host == 0)
        {
            return [];
        }

        string script = $$"""
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
            $A = [System.Windows.Automation.AutomationElement]
            $win = $A::FromHandle([IntPtr]{{host}})
            $items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
            foreach ($item in $items) { if ($item.Current.Name) { Write-Output $item.Current.Name } }
            """;
        string file = Path.Combine(Path.GetTempPath(), $"mavue-uia-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            using Process ps = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8, CreateNoWindow = true })!;
            string output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(20000);
            return output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
