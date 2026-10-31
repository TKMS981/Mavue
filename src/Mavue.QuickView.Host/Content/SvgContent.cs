using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Presentation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// SVG through XAML's SvgImageSource (Direct2D), drawn at the element's size: 100 % is the drawing's own size at
/// 96 dpi on this monitor; small drawings are not enlarged. Never for cloud placeholders (reading would download).
/// The document type and external entities are not processed (<see cref="SvgDimensions"/>).
/// </summary>
internal sealed class SvgContent : IPreviewContent, IViewTransformContent
{
    private readonly PreviewServices _s;
    private readonly IPreviewHost _host;
    private readonly SurfaceImageView _view;
    private IRandomAccessStream? _stream;

    public SvgContent(PreviewServices services, IPreviewHost host)
    {
        _s = services;
        _host = host;
        _view = services.ImageView;
    }

    public PreviewContentKind Kind => PreviewContentKind.Svg;

    public async Task ShowAsync(PreviewItem item, CancellationToken cancellation)
    {
        (long requestId, string path, FileFacts facts) = (item.RequestId, item.Path, item.Facts);
        if (facts.Access != PreviewAccess.Allowed)
        {
            _s.Window.Surface.ShowStatusOnly(_s.Strings.GetString("QuickView_Error_CloudPlaceholder"));
            _host.Display.MarkMessage(requestId);
            return;
        }

        // Read whole (XAML loads SVG from a stream): bounded.
        if (facts.Length > _s.Resources.Policy.MaxWholeFileReadBytes)
        {
            _s.Window.Surface.ShowStatusOnly(_s.Strings.GetString("QuickView_Error_TooLarge"));
            _host.Display.MarkMessage(requestId);
            return;
        }

        (double Width, double Height)? intrinsic = await Task.Run(() => SvgDimensions.TryRead(path), cancellation);
        byte[] content = await File.ReadAllBytesAsync(path, cancellation);
        if (intrinsic is not { } size)
        {
            _s.Window.Surface.ShowStatusOnly(_s.Strings.GetString("QuickView_Error_Failed"));
            _host.Display.MarkMessage(requestId);
            _s.Timeline.Mark(requestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = "svg-invalid" });
            return;
        }

        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(content.AsBuffer());
        stream.Seek(0);
        var svg = new SvgImageSource();
        SvgImageSourceLoadStatus status = await svg.SetSourceAsync(stream);
        if (cancellation.IsCancellationRequested || status != SvgImageSourceLoadStatus.Success)
        {
            stream.Dispose();
            cancellation.ThrowIfCancellationRequested();
            _s.Window.Surface.ShowStatusOnly(_s.Strings.GetString("QuickView_Error_Failed"));
            _host.Display.MarkMessage(requestId);
            _s.Timeline.Mark(requestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = "svg-" + status });
            return;
        }

        _s.Window.Surface.ImageMayHaveTransparency = true; // drawings usually have a transparent background
        double scale = _host.DisplayScale();
        (double sourceWidth, double sourceHeight) = (size.Width * scale, size.Height * scale);
        double factor = ViewerZoom.FactorFor(_view.ViewZoom ?? ZoomSetting.For(_s.Settings.ScaleMode), sourceWidth, sourceHeight, item.AreaWidth, item.AreaHeight);
        (uint displayWidth, uint displayHeight) = ViewerZoom.DisplaySize(sourceWidth, sourceHeight, factor);
        (double, double) element = DisplaySizing.ElementSize(displayWidth, displayHeight, scale);
        _s.Window.Surface.SetImageBox(element, displayWidth > item.AreaWidth + 1 || displayHeight > item.AreaHeight + 1);
        _s.Window.Surface.SetFullImage(svg);
        _view.ReleaseDisplayedBitmap();
        ReplaceStream(stream);
        _host.Display.MarkContent(requestId);
        _view.RecordShown(SurfaceImageKind.Svg, (sourceWidth, sourceHeight), element.Item1, scale);
        _host.SetInfo(PreviewInfoText.Image(_s.Strings, facts, size.Width, size.Height));
        _s.Timeline.Mark(requestId, "full-set", QuickViewTimeline.Now, new Dictionary<string, object?> { ["decoder"] = "svg", ["sourceWidth"] = size.Width, ["sourceHeight"] = size.Height });
        await QuickViewPresence.NextFrameAsync();
        if (!cancellation.IsCancellationRequested)
        {
            _s.Timeline.Mark(requestId, "full-visible", QuickViewTimeline.Now);
        }
    }

    public void Reset(ContentReset scope, string reason)
    {
        if (scope != ContentReset.Reload)
        {
            ReplaceStream(null);
            _view.Reset();
        }
    }

    public void Dispose() => ReplaceStream(null);

    /// <summary>Vector drawings resize without decoding again.</summary>
    public void Zoom(QuickViewZoomCommand command, int wheelDelta, Point? anchor)
    {
        if (_host.IsVisible && _host.CurrentPath is not null)
        {
            _ = _view.Zoom(command, wheelDelta, anchor, _s.Settings.ScaleMode, _host.ImageArea(), _host.DisplayScale(), _host.CurrentRequest);
        }
    }

    /// <summary>Rotation of SVG is not supported yet (FEATURES F04.11).</summary>
    public void Rotate(int quarterTurns)
    {
    }

    private void ReplaceStream(IRandomAccessStream? stream)
    {
        IRandomAccessStream? previous = _stream;
        _stream = stream;
        if (!ReferenceEquals(previous, stream))
        {
            previous?.Dispose();
        }
    }
}
