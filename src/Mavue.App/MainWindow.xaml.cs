using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Mavue.App;

public sealed partial class MainWindow : Window
{
    private static readonly ResourceLoader Resources = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = Resources.GetString("AppDisplayName");
        ExtendsContentIntoTitleBar = false;
    }

    /// <summary>Waits for the first rendered frame, checks resources, then exits (see App.SmokeTestSwitch).</summary>
    internal void RunSmokeTestAndExit()
    {
        void OnRendering(object? sender, object e)
        {
            CompositionTarget.Rendering -= OnRendering;
            bool ok = !string.IsNullOrEmpty(Title) && !string.IsNullOrEmpty(WelcomeText.Text);
            Environment.ExitCode = ok ? 0 : 1;
            Close();
            Application.Current.Exit();
        }

        CompositionTarget.Rendering += OnRendering;
    }
}
