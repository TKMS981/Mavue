using Microsoft.UI.Xaml;

namespace Mavue.App;

public partial class App : Application
{
    /// <summary>
    /// Command-line switch used by tools/smoke-test.ps1: start, render the first frame, verify
    /// localized resources resolve, then exit with code 0 (or 1 on failure).
    /// </summary>
    internal const string SmokeTestSwitch = "--smoke-test";

    /// <summary>Diagnostics for automated tests: <c>--trace-file &lt;path&gt;</c> writes viewer events as JSON Lines.</summary>
    internal const string TraceFileSwitch = "--trace-file";

    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLine = Environment.GetCommandLineArgs();
        bool smokeTest = commandLine.Contains(SmokeTestSwitch, StringComparer.OrdinalIgnoreCase);
        (IReadOnlyList<string> files, string? traceFile) = ParseArguments(commandLine.Skip(1).ToList());

        _window = new MainWindow(traceFile);
        _window.Activate();

        if (smokeTest)
        {
            _window.RunSmokeTestAndExit();
            return;
        }

        // "Mavue.exe <file> [<file> ...]" — from Explorer's "Open with", a file association, or a shortcut.
        _window.OpenFiles(files);
    }

    /// <summary>Files to open (full paths) and the optional trace file; unknown switches are ignored.</summary>
    internal static (IReadOnlyList<string> Files, string? TraceFile) ParseArguments(IReadOnlyList<string> arguments)
    {
        var files = new List<string>();
        string? traceFile = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            if (string.Equals(argument, TraceFileSwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Count)
            {
                traceFile = arguments[++i];
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            try
            {
                files.Add(Path.GetFullPath(argument));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a usable path; skip it.
            }
        }

        return (files, traceFile);
    }
}
