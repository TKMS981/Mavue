namespace Mavue.QuickView.Host;

/// <summary>How the preview window is brought to the user when Space is pressed (compared in docs/QUICKVIEW-POC.md).</summary>
internal enum ActivationMode
{
    /// <summary>
    /// Activate only when the hook-time probe (see <see cref="HookGrant"/>) reports foreground rights;
    /// otherwise show passively like <see cref="NoActivate"/> without attempting activation (no taskbar flash).
    /// Comparison mode: with a physical keyboard the probe failed in 9 of 11 presses (docs/QUICKVIEW-POC.md).
    /// </summary>
    Auto,

    /// <summary>AppWindow.Show(activateWindow: true) only.</summary>
    AppWindowShow,

    /// <summary>AppWindow.Show(true), then SetForegroundWindow if the window did not become foreground.</summary>
    SetForeground,

    /// <summary>Like SetForeground, but wrapped in AttachThreadInput with the foreground thread (comparison only).</summary>
    AttachThreadInput,

    /// <summary>
    /// Inside the hook callback (while the Space event is being processed) call
    /// AllowSetForegroundWindow(own process), then SetForegroundWindow as in <see cref="SetForeground"/>.
    /// </summary>
    HookGrant,

    /// <summary>
    /// Show without activation, placed directly above the Explorer window (no topmost). Explorer
    /// keeps keyboard focus; Space/Esc reach Quick View through the hook. Clicking the window activates it
    /// normally. Does not depend on foreground rights, so it behaves the same for every key press.
    /// </summary>
    NoActivate,

    /// <summary>Like <see cref="NoActivate"/> but raised with a momentary HWND_TOPMOST (comparison only).</summary>
    NoActivateTopmost,

    /// <summary>
    /// Default. Floating panel: shown without activation in the topmost band while visible. Topmost is the only
    /// z-order placement that does not depend on foreground rights. Scope limits: the window never takes
    /// focus by itself, hides as soon as another application is activated, and drops topmost when hidden.
    /// </summary>
    Panel,
}

/// <summary>Full-quality image decode path.</summary>
internal enum DecoderMode
{
    /// <summary>WinRT BitmapDecoder with a scaling BitmapTransform → SoftwareBitmapSource.</summary>
    WinRt,

    /// <summary>XAML BitmapImage with DecodePixelWidth/Height (decoded by the XAML image pipeline).</summary>
    Xaml,
}

/// <summary>What to do when another Space-key preview tool (QuickLook, Seer) is running.</summary>
internal enum OtherQuickLookPolicy
{
    /// <summary>
    /// Default. Keep Mavue's hook first in the low-level hook chain so Mavue handles Space and the other
    /// tool never sees it (measured: QuickLook passes Space on, so whichever hook is first decides).
    /// </summary>
    Prefer,

    /// <summary>Leave Space to the other tool while it runs (Mavue stays reachable from the context menu).</summary>
    Yield,
}

internal enum RegistrationCommand
{
    None,
    Register,
    Unregister,
    Status,
}

/// <summary>Command-line options. Unknown arguments are ignored.</summary>
internal sealed record HostOptions
{
    public ActivationMode Activation { get; init; } = ActivationMode.Panel;

    public DecoderMode Decoder { get; init; } = DecoderMode.WinRt;

    /// <summary>Decode JPEG through WIC directly (faster; falls back to WinRT for color-managed images). Off for comparison.</summary>
    public bool UseWicForJpeg { get; init; } = true;

    /// <summary>Settings file (default: %LOCALAPPDATA%\Mavue\QuickView\settings.json). Tests use their own.</summary>
    public string? SettingsPath { get; init; }

    /// <summary>After Quick View hides, compact the managed heap (decode buffers are large and short-lived).</summary>
    public bool IdleTrim { get; init; } = true;

    /// <summary>Scaling interpolation for the WinRT decode path (fant|linear|cubic|nearest), for measurement.</summary>
    public string Interpolation { get; init; } = "fant";

    /// <summary>JSON Lines latency log for the E2E harness; null disables it.</summary>
    public string? TimingLog { get; init; }

    /// <summary>Render the hidden window once at startup so the first Space does not pay XAML/DWM setup.</summary>
    public bool Prewarm { get; init; } = true;

    public OtherQuickLookPolicy OtherQuickLook { get; init; } = OtherQuickLookPolicy.Prefer;

    /// <summary>Also react to Space in Open/Save dialogs.</summary>
    public bool IncludeFileDialogs { get; init; }

    /// <summary>Write Shell COM HRESULT traces to the timing log (diagnostics).</summary>
    public bool TraceShell { get; init; }

    /// <summary>Ask a running instance to exit, then exit.</summary>
    public bool Shutdown { get; init; }

    /// <summary>
    /// <c>--quickview path...</c>: preview these files (Explorer's "Mavue Quick View" command). All arguments
    /// after the switch are paths. Forwarded to the resident process if one runs; otherwise this process
    /// becomes the resident process and shows them.
    /// </summary>
    public IReadOnlyList<string> QuickViewPaths { get; init; } = [];

    /// <summary>Per-user registration command (<c>--register</c>, <c>--unregister</c>, <c>--registration-status</c>).</summary>
    public RegistrationCommand Registration { get; init; }

    /// <summary>With <c>--register</c>: do not start the resident process at sign-in.</summary>
    public bool NoStartAtSignIn { get; init; }

    /// <summary>Context-menu key name (tests use their own so the user's registration is untouched).</summary>
    public string? VerbName { get; init; }

    public static HostOptions Parse(IReadOnlyList<string> args)
    {
        var options = new HostOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string? next = i + 1 < args.Count ? args[i + 1] : null;
            switch (arg.ToLowerInvariant())
            {
                case "--activation" when next is not null:
                    options = options with { Activation = next.ToLowerInvariant() switch
                    {
                        "appwindow" => ActivationMode.AppWindowShow,
                        "setforeground" => ActivationMode.SetForeground,
                        "attach" => ActivationMode.AttachThreadInput,
                        "hookgrant" => ActivationMode.HookGrant,
                        "auto" => ActivationMode.Auto,
                        "noactivate" => ActivationMode.NoActivate,
                        "noactivate-topmost" => ActivationMode.NoActivateTopmost,
                        "panel" => ActivationMode.Panel,
                        _ => ActivationMode.Panel,
                    } };
                    i++;
                    break;
                case "--decoder" when next is not null:
                    options = options with { Decoder = next.Equals("xaml", StringComparison.OrdinalIgnoreCase) ? DecoderMode.Xaml : DecoderMode.WinRt };
                    i++;
                    break;
                case "--interpolation" when next is not null:
                    options = options with { Interpolation = next.ToLowerInvariant() };
                    i++;
                    break;
                case "--timing-log" when next is not null:
                    options = options with { TimingLog = next };
                    i++;
                    break;
                case "--other-quicklook" when next is not null:
                    options = options with { OtherQuickLook = next.Equals("yield", StringComparison.OrdinalIgnoreCase) ? OtherQuickLookPolicy.Yield : OtherQuickLookPolicy.Prefer };
                    i++;
                    break;
                case "--settings" when next is not null:
                    options = options with { SettingsPath = next };
                    i++;
                    break;
                case "--no-idle-trim":
                    options = options with { IdleTrim = false };
                    break;
                case "--no-wic":
                    options = options with { UseWicForJpeg = false };
                    break;
                case "--no-prewarm":
                    options = options with { Prewarm = false };
                    break;
                case "--include-dialogs":
                    options = options with { IncludeFileDialogs = true };
                    break;
                case "--trace-shell":
                    options = options with { TraceShell = true };
                    break;
                case "--shutdown":
                    options = options with { Shutdown = true };
                    break;
                case "--quickview":
                    options = options with { QuickViewPaths = args.Skip(i + 1).ToArray() };
                    i = args.Count; // the rest are paths
                    break;
                case "--register":
                    options = options with { Registration = RegistrationCommand.Register };
                    break;
                case "--unregister":
                    options = options with { Registration = RegistrationCommand.Unregister };
                    break;
                case "--registration-status":
                    options = options with { Registration = RegistrationCommand.Status };
                    break;
                case "--no-startup":
                    options = options with { NoStartAtSignIn = true };
                    break;
                case "--verb" when next is not null:
                    options = options with { VerbName = next };
                    i++;
                    break;
            }
        }

        return options;
    }
}
