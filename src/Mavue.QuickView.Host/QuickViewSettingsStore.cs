using Mavue.Core.Settings;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Microsoft.UI.Dispatching;

namespace Mavue.QuickView.Host;

/// <summary>
/// Quick View's settings file as the resident process sees it. The image scale is chosen in settings (written by the
/// settings UI or by hand), not in the Quick View window; the file is re-read when Quick View opens and the file has
/// changed, so a change applies from the next Space without restarting (one timestamp check per open).
/// PDF choices made in the window (layout, sidebar) are written back.
/// </summary>
internal sealed class QuickViewSettingsStore
{
    private readonly string _path;
    private readonly QuickViewTimeline _timeline;
    private readonly DispatcherQueue _dispatcher;
    private DateTime _stamp;

    public QuickViewSettingsStore(string path, QuickViewTimeline timeline, DispatcherQueue dispatcher)
    {
        _path = path;
        _timeline = timeline;
        _dispatcher = dispatcher;
    }

    public ImageScaleMode ScaleMode { get; private set; }

    /// <summary>How PDF pages are arranged (the last choice is kept).</summary>
    public PdfLayoutMode PdfLayout { get; set; } = PdfLayoutMode.Continuous;

    /// <summary>The PDF sidebar is open.</summary>
    public bool PdfSidebarOpen { get; set; }

    /// <summary>The filmstrip is shown (the last choice is kept).</summary>
    public bool FilmstripVisible { get; set; }

    /// <summary>The pixel inspector is on (the last choice is kept).</summary>
    public bool PixelInspectorOn { get; set; }

    /// <summary>A checkerboard is shown behind transparent images (on by default).</summary>
    public bool TransparencyGridOn { get; set; } = true;

    /// <summary>Plain text wraps long lines (on by default).</summary>
    public bool WrapPlainText { get; set; } = true;

    /// <summary>Source code wraps long lines (off by default).</summary>
    public bool WrapCode { get; set; }

    /// <summary>The image scale changed on a reload (decoded previews were made for the other mode's box).</summary>
    public event Action? ScaleModeChanged;

    public void ReloadIfChanged(bool force = false)
    {
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(_path); // 1601-01-01 when the file does not exist
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return;
        }

        if (!force && stamp == _stamp)
        {
            return;
        }

        _stamp = stamp;
        ImageScaleMode previous = ScaleMode;
        QuickViewSettings settings = QuickViewSettings.Load(_path);
        ScaleMode = settings.ImageScale;
        PdfLayout = settings.PdfLayout;
        PdfSidebarOpen = settings.ShowPdfSidebar;
        FilmstripVisible = settings.ShowFilmstrip;
        PixelInspectorOn = settings.ShowPixelInspector;
        TransparencyGridOn = settings.ShowTransparencyGrid;
        WrapPlainText = settings.WrapPlainText;
        WrapCode = settings.WrapCode;
        if (!force && previous != ScaleMode)
        {
            ScaleModeChanged?.Invoke();
        }

        _timeline.Mark(0, "settings-loaded", QuickViewTimeline.Now, new Dictionary<string, object?> { ["imageScale"] = ScaleMode.ToString(), ["pdfLayout"] = PdfLayout.ToString() });
    }

    /// <summary>Remembers a choice in the settings file (re-read before writing; off the UI thread).</summary>
    public void Save(Func<QuickViewSettings, QuickViewSettings> change)
    {
        string path = _path;
        _ = Task.Run(async () =>
        {
            try
            {
                await change(QuickViewSettings.Load(path)).SaveAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The choice applies to this session only.
            }
        }).ContinueWith(_ => _dispatcher.TryEnqueue(() =>
        {
            try
            {
                _stamp = File.GetLastWriteTimeUtc(path); // our own write is not a change to re-read
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Re-read next time; harmless.
            }
        }), TaskScheduler.Default);
    }
}
