using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Host.Content.Html;

/// <summary>What the locked-down browser refused while showing a document (diagnostics and tests).</summary>
internal sealed record HtmlBlockReport(int Requests, int Navigations, int NewWindows, int Downloads, int ExternalLaunches, int Permissions);

/// <summary>
/// A WebView2 locked down for previewing untrusted HTML (SPEC §3.1 requirement 4; docs/ARCHITECTURE.md §4.7.12).
/// The only class that touches WebView2 types, so the WebView2 assemblies and the browser processes load only when
/// the first HTML file is shown; <see cref="Close"/> ends them.
/// <list type="bullet">
/// <item>Scripts are off (<c>IsScriptEnabled = false</c>), as are web messages, host objects, dev tools, the context
/// menu, browser shortcut keys (print, save, find, reload), autofill, password saving, the status bar, swipe
/// navigation and SmartScreen reputation checks (which would send addresses to a service).</item>
/// <item>The document is served from memory at <c>https://quickview.mavue.invalid/…</c> (a reserved name that never
/// resolves) with a Content-Security-Policy of <c>sandbox; default-src 'none'</c> plus inline styles and data: images
/// and fonts. Every other request — images, styles, scripts, fonts, frames, prefetches, local files — gets an empty
/// 403 answer from <see cref="CoreWebView2.WebResourceRequested"/> before it reaches the network.</item>
/// <item>Every navigation other than the document itself (link clicks, refresh, forms, frames) is cancelled;
/// new windows, downloads, external protocol launches and permission requests are refused.</item>
/// <item>The browser is started with a proxy that does not exist for all hosts including loopback, as a second
/// barrier, and without background networking or pings; the profile is InPrivate (nothing is kept on disk).</item>
/// </list>
/// </summary>
internal sealed class HtmlBrowser
{
    public const string DocumentHost = "quickview.mavue.invalid";

    private const string ContentSecurityPolicy =
        "sandbox; default-src 'none'; img-src data:; style-src 'unsafe-inline' data:; font-src data:; media-src data:; " +
        "frame-src 'none'; child-src 'none'; worker-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'; connect-src 'none'";

    private const string BrowserArguments =
        "--proxy-server=\"http://127.0.0.1:9\" --proxy-bypass-list=\"<-loopback>\" --no-pings --disable-background-networking --disable-component-update --disable-sync --disable-domain-reliability";

    private static CoreWebView2Environment? s_environment;

    private WebView2? _web;
    private Uri? _document;
    private byte[] _content = [];
    private string _contentType = "text/html";
    private int _navigation;
    private TaskCompletionSource<bool>? _loaded;
    private int _blockedRequests;
    private int _blockedNavigations;
    private int _newWindows;
    private int _downloads;
    private int _launches;
    private int _permissions;

    /// <summary>The element to put on the stage (null until <see cref="EnsureAsync"/> succeeded).</summary>
    public UIElement? Element => _web;

    /// <summary>The browser process went away (it is recreated for the next document).</summary>
    public event Action<string>? Failed;

    /// <summary>A request or navigation was refused (its kind only; addresses from documents are not logged).</summary>
    public event Action<string, string>? Blocked;

    /// <summary>The installed WebView2 runtime version, or null when there is none.</summary>
    public static string? RuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Creates and configures the WebView2 once. False when WebView2 is not available on this computer.</summary>
    public async Task<bool> EnsureAsync()
    {
        if (_web?.CoreWebView2 is not null)
        {
            return true;
        }

        WebView2? created = null;
        try
        {
            if (s_environment is null)
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mavue", "QuickView", "WebView2");
                Directory.CreateDirectory(folder);
                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = BrowserArguments,
                    AllowSingleSignOnUsingOSPrimaryAccount = false,
                    AreBrowserExtensionsEnabled = false,
                    EnableTrackingPrevention = true,
                };
                s_environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, options);
            }

            created = new WebView2 { DefaultBackgroundColor = Microsoft.UI.Colors.White, IsTabStop = false };
            AutomationProperties.SetAutomationId(created, "QuickViewHtml");
            CoreWebView2ControllerOptions controller = s_environment.CreateCoreWebView2ControllerOptions();
            controller.IsInPrivateModeEnabled = true;
            controller.ProfileName = "QuickViewHtml";
            await created.EnsureCoreWebView2Async(s_environment, controller);
            Configure(created.CoreWebView2); // before the first navigation: nothing runs unprotected
            _web = created;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failed?.Invoke(ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                created?.Close();
            }
            catch (Exception closing) when (closing is COMException or InvalidOperationException)
            {
                // Nothing more to release.
            }

            _web = null;
            return false;
        }
    }

    /// <summary>Shows <paramref name="content"/> (the file's bytes) as an HTML document; true when it finished loading.</summary>
    /// <param name="charset">The encoding to declare, or null when the file has a byte order mark.</param>
    public async Task<bool> ShowAsync(byte[] content, string? charset, CancellationToken cancellation)
    {
        if (_web?.CoreWebView2 is not { } core)
        {
            return false;
        }

        ResetReport();
        _content = content;
        _contentType = charset is null ? "text/html" : "text/html; charset=" + charset;
        _document = new Uri($"https://{DocumentHost}/{++_navigation}/document.html");
        _loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> loaded = _loaded;
        core.Navigate(_document.AbsoluteUri);
        using (cancellation.Register(() => loaded.TrySetCanceled()))
        {
            Task finished = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(15), cancellation));
            cancellation.ThrowIfCancellationRequested();
            return finished == loaded.Task && await loaded.Task;
        }
    }

    /// <summary>Drops the shown document (an empty page), keeping the browser for the next one.</summary>
    public void Blank()
    {
        _content = [];
        _document = null;
        _loaded?.TrySetCanceled();
        try
        {
            _web?.CoreWebView2?.Navigate("about:blank");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // The browser is gone; it is recreated for the next document.
        }
    }

    /// <summary>Ends the browser (its processes exit); a later document creates a new one.</summary>
    public void Close()
    {
        _loaded?.TrySetCanceled();
        _content = [];
        _document = null;
        try
        {
            _web?.Close();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // Already gone.
        }

        _web = null;
    }

    public HtmlBlockReport Report() => new(_blockedRequests, _blockedNavigations, _newWindows, _downloads, _launches, _permissions);

    private void ResetReport() => _blockedRequests = _blockedNavigations = _newWindows = _downloads = _launches = _permissions = 0;

    private void Configure(CoreWebView2 core)
    {
        CoreWebView2Settings settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsReputationCheckingRequired = false;
        settings.IsZoomControlEnabled = true; // Ctrl+wheel zooms the page, as everywhere in Quick View

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += (_, e) =>
        {
            if (!IsAllowedNavigation(e.Uri))
            {
                e.Cancel = true;
                _blockedNavigations++;
                Blocked?.Invoke("navigation", (e.IsUserInitiated ? "user:" : string.Empty) + SchemeOf(e.Uri));
            }
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            e.Cancel = true;
            _blockedNavigations++;
            Blocked?.Invoke("frame", SchemeOf(e.Uri));
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (_document is not null && core.Source == _document.AbsoluteUri)
            {
                _loaded?.TrySetResult(e.IsSuccess);
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true; // no window is opened, nothing is navigated
            _newWindows++;
            Blocked?.Invoke("new-window", SchemeOf(e.Uri));
        };
        core.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
            e.Handled = true;
            _downloads++;
            Blocked?.Invoke("download", SchemeOf(e.DownloadOperation?.Uri));
        };
        core.LaunchingExternalUriScheme += (_, e) =>
        {
            e.Cancel = true;
            _launches++;
            Blocked?.Invoke("external-scheme", SchemeOf(e.Uri));
        };
        core.PermissionRequested += (_, e) =>
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
            _permissions++;
            Blocked?.Invoke("permission", e.PermissionKind.ToString());
        };
        core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
        core.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        core.ProcessFailed += (_, e) =>
        {
            _loaded?.TrySetResult(false);
            Failed?.Invoke("process-" + e.ProcessFailedKind);
        };
    }

    private bool IsAllowedNavigation(string? uri) =>
        uri is "about:blank" || (_document is not null && string.Equals(uri, _document.AbsoluteUri, StringComparison.Ordinal));

    private void OnResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string uri = e.Request.Uri;
        if (_document is not null && string.Equals(uri, _document.AbsoluteUri, StringComparison.Ordinal) && e.ResourceContext == CoreWebView2WebResourceContext.Document)
        {
            _ = ServeDocumentAsync(sender, e, e.GetDeferral(), _content, _contentType);
            return;
        }

        // Anything else the document asks for never leaves the process.
        e.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Cache-Control: no-store");
        _blockedRequests++;
        Blocked?.Invoke("request", e.ResourceContext + ":" + SchemeOf(uri));
    }

    /// <summary>The document's bytes from memory, with the headers that lock it down.</summary>
    private static async Task ServeDocumentAsync(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, Windows.Foundation.Deferral deferral, byte[] content, string contentType)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(content.AsBuffer());
            stream.Seek(0);
            string headers =
                $"Content-Type: {contentType}\r\n" +
                $"Content-Security-Policy: {ContentSecurityPolicy}\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "Referrer-Policy: no-referrer\r\n" +
                "Cache-Control: no-store";
            e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // The browser went away while the document was prepared.
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static string SchemeOf(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return "none";
        }

        int colon = uri.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && colon < 16 ? uri[..colon].ToLowerInvariant() : "relative";
    }
}
