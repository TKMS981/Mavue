using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mavue.QuickView.Host.Presentation;

/// <summary>
/// Quick View's see-through background (docs/ARCHITECTURE.md §4.7.9): desktop acrylic tinted with the stage color,
/// with the tint and luminosity opacities set to the chosen opacity, so the whole window — Windows' title bar, the
/// tool bar and the stage — is one calm, frosted material whose see-through share is the setting. XAML's
/// DesktopAcrylicBackdrop has no tint settings, hence this small SystemBackdrop.
/// The window counts as active even while shown passively (Explorer keeps the keyboard), so the material does not
/// switch to its solid fallback every time Quick View opens; Windows still shows the solid fallback (the stage color)
/// when transparency effects are off or energy saver is on.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The controller lives from OnTargetConnected to OnTargetDisconnected, which disposes it (the SystemBackdrop pattern).")]
internal sealed partial class TintedAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private Color _tint;
    private float _opacity;
    private bool _dark;

    public TintedAcrylicBackdrop(Color tint, float opacity, bool dark)
    {
        _tint = tint;
        _opacity = opacity;
        _dark = dark;
    }

    public static bool IsSupported => DesktopAcrylicController.IsSupported();

    /// <summary>Changes the tint (theme change) or the opacity (setting change) in place.</summary>
    public void Update(Color tint, float opacity, bool dark)
    {
        if (tint == _tint && opacity == _opacity && dark == _dark)
        {
            return; // every show asks again; nothing to do unless the theme or the setting changed
        }

        (_tint, _opacity, _dark) = (tint, opacity, dark);
        if (_configuration is not null)
        {
            _configuration.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        }

        if (_controller is not null)
        {
            Apply(_controller);
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _configuration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        };
        _controller = new DesktopAcrylicController();
        Apply(_controller);
        _controller.SetSystemBackdropConfiguration(_configuration);
        _controller.AddSystemBackdropTarget(connectedTarget);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (_controller is not null)
        {
            _controller.RemoveSystemBackdropTarget(disconnectedTarget);
            _controller.Dispose();
            _controller = null;
        }

        _configuration = null;
    }

    private void Apply(DesktopAcrylicController controller)
    {
        controller.TintColor = _tint;
        controller.TintOpacity = _opacity;
        controller.LuminosityOpacity = _opacity;
        controller.FallbackColor = _tint;
    }
}
