using System.Reflection;
using Mavue.Core.Settings;
using Mavue.Core.Viewing;
using Mavue.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;

namespace Mavue.App;

/// <summary>
/// Settings (shown in a dialog): appearance, how images open in Mavue and in Quick View, Windows integration
/// (Open with / Default apps / "Open in Mavue"), and the recent files. Every change is saved at once.
/// </summary>
internal sealed partial class SettingsPanel : UserControl
{
    private readonly string _settingsPath;
    private readonly TextBlock _integrationStatus = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly Button _register = new();
    private readonly Button _unregister = new();
    private readonly ToggleSwitch _pdfPreview = new();
    private bool _updating;

    public SettingsPanel(AppSettings settings, string settingsPath)
    {
        _settingsPath = settingsPath;
        var panel = new StackPanel { Spacing = 20, MinWidth = 420 };

        panel.Children.Add(Choice(
            "Settings_Theme",
            [("Settings_ThemeSystem", AppTheme.System), ("Settings_ThemeLight", AppTheme.Light), ("Settings_ThemeDark", AppTheme.Dark)],
            settings.Theme,
            theme =>
            {
                Save(s => s with { Theme = theme });
                ThemeChanged?.Invoke(theme);
            }));

        panel.Children.Add(Choice(
            "Settings_DefaultScale",
            [("Settings_ScaleFit", ImageScaleMode.FitNoUpscale), ("Settings_ScaleActual", ImageScaleMode.ActualSize)],
            settings.DefaultScale,
            scale =>
            {
                Save(s => s with { DefaultScale = scale });
                DefaultScaleChanged?.Invoke(scale);
            }));

        // Quick View reads its own settings file each time it opens (no restart needed).
        QuickViewSettings quickView = QuickViewSettings.Load(QuickViewSettings.DefaultPath);
        panel.Children.Add(Choice(
            "Settings_QuickViewScale",
            [("Settings_ScaleFit", ImageScaleMode.FitNoUpscale), ("Settings_ScaleActual", ImageScaleMode.ActualSize)],
            quickView.ImageScale,
            scale => _ = SaveQuickViewAsync(scale)));

        var integration = new StackPanel { Spacing = 8 };
        integration.Children.Add(Header("Settings_Integration"));
        integration.Children.Add(new TextBlock { Text = MainWindow.Text(PackageInfo.IsPackaged ? "Settings_IntegrationPackaged" : "Settings_IntegrationHint"), TextWrapping = TextWrapping.Wrap });
        integration.Children.Add(_integrationStatus);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _register.Content = MainWindow.Text("Settings_Register");
        _register.Click += (_, _) => Register();
        _unregister.Content = MainWindow.Text("Settings_Unregister");
        _unregister.Click += (_, _) => Unregister();
        var defaultApps = new Button { Content = MainWindow.Text("Settings_DefaultApps") };
        defaultApps.Click += (_, _) => ShellActions.OpenSettingsPage("ms-settings:defaultapps?registeredAppUser=" + AppRegistration.ApplicationName);
        if (!PackageInfo.IsPackaged)
        {
            // The package registers everything itself; HKCU registration would duplicate it.
            buttons.Children.Add(_register);
            buttons.Children.Add(_unregister);
        }

        buttons.Children.Add(defaultApps);
        integration.Children.Add(buttons);

        // PDF in Explorer's preview pane: by default the existing previewer (e.g. Edge's) is kept; this is the user's choice.
        _pdfPreview.Header = MainWindow.Text("Settings_PdfPreview");
        _pdfPreview.OnContent = MainWindow.Text("Settings_PdfPreviewMavue");
        _pdfPreview.OffContent = MainWindow.Text("Settings_PdfPreviewOther");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_pdfPreview, MainWindow.Text("Settings_PdfPreview"));
        _pdfPreview.Toggled += (_, _) =>
        {
            if (!_updating)
            {
                SetPdfPreview(_pdfPreview.IsOn);
            }
        };
        if (!PackageInfo.IsPackaged)
        {
            integration.Children.Add(_pdfPreview);
            integration.Children.Add(new TextBlock { Text = MainWindow.Text("Settings_PdfPreviewHint"), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        }
        panel.Children.Add(integration);

        var recent = new StackPanel { Spacing = 8 };
        recent.Children.Add(Header("Settings_RecentFiles"));
        var clear = new Button { Content = MainWindow.Text("MainWindow_ClearRecent") };
        clear.Click += (_, _) =>
        {
            Save(s => s with { RecentFiles = [] });
            RecentCleared?.Invoke();
            clear.IsEnabled = false;
        };
        clear.IsEnabled = settings.RecentFiles.Count > 0;
        recent.Children.Add(clear);
        panel.Children.Add(recent);

        // Version and the license texts shipped in the release folder (licenses\, tools/build-release.ps1).
        var about = new StackPanel { Spacing = 8 };
        about.Children.Add(Header("Settings_About"));
        about.Children.Add(new TextBlock { Text = MainWindow.Format("Settings_Version", ProductVersion()), IsTextSelectionEnabled = true });
        about.Children.Add(new TextBlock { Text = MainWindow.Text("Settings_LicenseHint"), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        string notices = Path.Combine(AppContext.BaseDirectory, "licenses", "THIRD-PARTY-NOTICES.txt");
        string licenses = File.Exists(notices) ? notices : Path.Combine(AppContext.BaseDirectory, "licenses");
        var showLicenses = new Button { Content = MainWindow.Text("Settings_Licenses"), IsEnabled = Path.Exists(licenses) };
        showLicenses.Click += (_, _) => ShellActions.ShowInFolder(licenses);
        about.Children.Add(showLicenses);
        panel.Children.Add(about);

        Content = panel;
        UpdateIntegrationStatus();
    }

    public event Action<AppTheme>? ThemeChanged;

    public event Action<ImageScaleMode>? DefaultScaleChanged;

    public event Action? RecentCleared;

    /// <summary>The product version (Directory.Build.props, or -p:Version from tools/build-release.ps1) without the commit suffix.</summary>
    private static string ProductVersion()
    {
        string? version = typeof(SettingsPanel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return version is null ? "?" : version.Split('+')[0];
    }

    private static TextBlock Header(string key) => new() { Text = MainWindow.Text(key), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };

    private static StackPanel Choice<T>(string header, (string Key, T Value)[] options, T current, Action<T> changed)
        where T : struct, Enum
    {
        var group = new RadioButtons { Header = Header(header) };
        foreach ((string key, T value) in options)
        {
            group.Items.Add(new RadioButton { Content = MainWindow.Text(key), Tag = value });
        }

        group.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => EqualityComparer<T>.Default.Equals(o.Value, current)));
        group.SelectionChanged += (_, _) =>
        {
            if (group.SelectedItem is RadioButton { Tag: T value })
            {
                changed(value);
            }
        };
        var panel = new StackPanel();
        panel.Children.Add(group);
        return panel;
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        try
        {
            AppSettings.Update(_settingsPath, change).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _integrationStatus.Text = ex.Message;
        }
    }

    private static async Task SaveQuickViewAsync(ImageScaleMode scale)
    {
        string path = QuickViewSettings.DefaultPath;
        try
        {
            await (QuickViewSettings.Load(path) with { ImageScale = scale }).SaveAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Quick View keeps its previous setting.
        }
    }

    private void Register()
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return;
        }

        try
        {
            var registration = new AppRegistration(Registry.CurrentUser);
            registration.Register(executable, App.RegistrationText(), registration.Handlers.TakenOverPreviews()); // keeps the PDF choice
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _integrationStatus.Text = ex.Message;
            return;
        }

        UpdateIntegrationStatus();
    }

    private void Unregister()
    {
        try
        {
            new AppRegistration(Registry.CurrentUser).Unregister();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _integrationStatus.Text = ex.Message;
            return;
        }

        UpdateIntegrationStatus();
    }

    /// <summary>Mavue's preview for PDF in Explorer (or the previous previewer back). Re-registers the rest unchanged.</summary>
    private void SetPdfPreview(bool mavue)
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return;
        }

        var registration = new AppRegistration(Registry.CurrentUser);
        var takeOver = registration.Handlers.TakenOverPreviews().Where(e => !e.Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (mavue)
        {
            takeOver.Add(".pdf");
        }

        try
        {
            ShellHandlerSummary? summary = registration.Register(executable, App.RegistrationText(), takeOver);
            UpdateIntegrationStatus();
            if (mavue && summary?.PreviewReplaced.Contains(".pdf", StringComparer.OrdinalIgnoreCase) != true &&
                summary?.PreviewAdded.Contains(".pdf", StringComparer.OrdinalIgnoreCase) != true)
            {
                // Not effective here (e.g. the default PDF app binds its own previewer above the per-user key).
                _integrationStatus.Text = MainWindow.Text("Settings_PdfPreviewNotPossible");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _integrationStatus.Text = ex.Message;
        }
    }

    private void UpdateIntegrationStatus()
    {
        if (PackageInfo.IsPackaged)
        {
            _integrationStatus.Text = string.Empty;
            return;
        }

        var registration = new AppRegistration(Registry.CurrentUser);
        string? registered = registration.RegisteredExecutable();
        bool current = registered is not null && string.Equals(registered, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        _integrationStatus.Text = registered is null
            ? MainWindow.Text("Settings_NotRegistered")
            : MainWindow.Format(current ? "Settings_Registered" : "Settings_RegisteredElsewhere", registration.RegisteredExtensions().Count, registered);
        _register.Content = MainWindow.Text(registered is null ? "Settings_Register" : "Settings_RegisterAgain");
        _unregister.IsEnabled = registered is not null;
        if (registered is not null && registration.Handlers.IsRegistered)
        {
            _integrationStatus.Text += " " + MainWindow.Format("Settings_ShellHandlers",
                registration.Handlers.RegisteredExtensions(ShellHandlerRegistration.PreviewHandlerIid).Count,
                string.Join(' ', registration.Handlers.RegisteredExtensions(ShellHandlerRegistration.ThumbnailProviderIid)));
        }

        _updating = true;
        _pdfPreview.IsEnabled = current && registration.Handlers.IsRegistered;
        _pdfPreview.IsOn = registration.Handlers.TakenOverPreviews().Contains(".pdf", StringComparer.OrdinalIgnoreCase) ||
            ShellHandlerRegistration.QueryShellExtension(".pdf", ShellHandlerRegistration.PreviewHandlerIid) == ShellHandlerRegistration.PreviewHandlerClsid;
        _updating = false;
    }
}
