using Microsoft.UI.Xaml;

namespace Mavue.App;

public partial class App : Application
{
    /// <summary>
    /// Command-line switch used by tools/smoke-test.ps1: start, render the first frame, verify
    /// localized resources resolve, then exit with code 0 (or 1 on failure).
    /// </summary>
    internal const string SmokeTestSwitch = "--smoke-test";

    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        bool smokeTest = Environment.GetCommandLineArgs().Contains(SmokeTestSwitch, StringComparer.OrdinalIgnoreCase);

        _window = new MainWindow();
        _window.Activate();

        if (smokeTest)
        {
            _window.RunSmokeTestAndExit();
        }
    }
}
