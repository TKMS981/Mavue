using System.Diagnostics;

namespace Mavue.QuickView.Harness;

/// <summary>Windows 11 Explorer tab-switch scenario: Quick View should follow the selection of the tab the user switches to.</summary>
internal sealed partial class Runner
{
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkT = 0x54;

    private void RunTabScenario(List<TestAsset> assets)
    {
        Guarded("tab-switch-follow", () => SwitchTabs(assets),
            "With Quick View open, Ctrl+Tab to another Explorer tab shows that tab's selected file; switching back shows the first tab's file again");
    }

    private (bool, string) SwitchTabs(List<TestAsset> assets)
    {
        string firstFolder = Path.GetDirectoryName(assets[0].Path)!;
        string firstFile = Directory.GetFiles(firstFolder).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).First();

        // Second folder with one image whose size differs from the first tab's file.
        string secondFolder = firstFolder + " タブ2";
        Directory.CreateDirectory(secondFolder);
        string secondFile = Path.Combine(secondFolder, "tab2 image.png");
        // The PNG may exist from an earlier run even when --cases filtered it out of this one.
        string? pngSource = assets.FirstOrDefault(a => a.Case == "png")?.Path
            ?? Directory.GetFiles(firstFolder, "*.png").FirstOrDefault();
        if (pngSource is null)
        {
            return (false, "png asset required");
        }

        if (!File.Exists(secondFile))
        {
            File.Copy(pngSource, secondFile);
        }

        nint explorer = OpenExplorer(firstFile);
        if (explorer == 0 || !ForceForeground(explorer))
        {
            return (false, "could not open/activate the first tab");
        }

        // New tab (Ctrl+T), then navigate it through automation (no typing needed).
        int tabsBefore = ShellWindowsFor(explorer).Count;
        if (!SendChord(explorer, VkControl, VkT))
        {
            return (false, "foreground changed before Ctrl+T");
        }

        dynamic? newTab = null;
        WaitFor(() => (newTab = ShellWindowsFor(explorer).Count > tabsBefore ? ShellWindowsFor(explorer).LastOrDefault() : null) is not null, 5000, null);
        if (newTab is null)
        {
            return (false, "Ctrl+T did not create a tab (tabs not supported?)");
        }

        newTab.Navigate2(secondFolder);
        dynamic? second = null;
        WaitFor(() => (second = ShellWindowsFor(explorer).FirstOrDefault(w => PathOf(w) == secondFolder)) is not null, 8000, null);
        if (second is null)
        {
            return (false, "second tab did not navigate");
        }

        Thread.Sleep(500);
        Select(second, secondFile, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);

        // Back to the first tab and open Quick View there.
        SendChord(explorer, VkControl, (ushort)Native.VK_TAB, VkShift);
        Thread.Sleep(600);
        dynamic? first = ShellWindowsFor(explorer).FirstOrDefault(w => PathOf(w) == firstFolder);
        if (first is null)
        {
            return (false, "first tab not found");
        }

        Select(first, firstFile, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        if (!OpenQuickView(explorer, firstFile, out string why))
        {
            return (false, "open on first tab: " + why);
        }

        var observed = new List<string>();
        (string Label, string Expected)[] switches = [("→ tab 2", secondFile), ("→ tab 1", firstFile)];
        foreach ((string label, string expected) in switches)
        {
            if (!SendChord(explorer, VkControl, (ushort)Native.VK_TAB))
            {
                return (false, $"{label}: foreground changed");
            }

            long t = Stopwatch.GetTimestamp();
            (Mark? nav, Mark? done) = WaitForNavigation(t - (long)(50 * TicksPerMs), "explorer-selection");
            bool switched = _log.Marks.Any(m => m.Name == "view-switched" && m.Qpc >= t - (long)(50 * TicksPerMs));
            if (nav is null || done?.Name != "full-visible" || !ShowsFile(nav.Request, expected))
            {
                return (false, $"{string.Join(", ", observed)}; {label}: view-switched={switched}, {Describe(nav, explorer)}, focus={Native.Focus(explorer).FocusClass}");
            }

            observed.Add($"{label} {Path.GetFileName(expected)} in {Ms(t, done?.Qpc ?? t):0.0} ms (view-switched={switched})");
        }

        bool closed = CloseFromExplorer(explorer);

        // Close the whole two-tab window so later scenarios start from a fresh single-tab window.
        Native.PostMessageW(explorer, Native.WM_CLOSE, 0, 0);
        WaitFor(() => !Native.IsWindow(explorer), 5000, null);
        _createdExplorers.Remove(explorer);
        return (closed, string.Join(", ", observed) + (closed ? "" : "; Esc did not close"));
    }

    /// <summary>All Shell automation entries (one per tab) of an Explorer frame.</summary>
    private static List<dynamic> ShellWindowsFor(nint hwnd)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        var result = new List<dynamic>();
        foreach (dynamic w in shell.Windows())
        {
            if ((nint)(long)w.HWND == hwnd)
            {
                result.Add(w);
            }
        }

        return result;
    }

    private static string? PathOf(dynamic window)
    {
        try
        {
            return window.Document?.Folder?.Self?.Path as string;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null; // tab still loading or showing Home
        }
    }

    /// <summary>Presses modifier(s)+key only while <paramref name="expectedForeground"/> is the foreground window.</summary>
    private static bool SendChord(nint expectedForeground, ushort modifier, ushort key, ushort secondModifier = 0)
    {
        if (Native.GetForegroundWindow() != expectedForeground)
        {
            return false;
        }

        var inputs = new List<Native.INPUT> { Key(modifier, false) };
        if (secondModifier != 0)
        {
            inputs.Add(Key(secondModifier, false));
        }

        inputs.Add(Key(key, false));
        inputs.Add(Key(key, true));
        if (secondModifier != 0)
        {
            inputs.Add(Key(secondModifier, true));
        }

        inputs.Add(Key(modifier, true));
        return Native.SendInput((uint)inputs.Count, [.. inputs], System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>()) == inputs.Count;
    }
}
