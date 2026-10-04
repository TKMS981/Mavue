using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

[Trait("Category", "Viewer")]
public sealed class ViewerZoomTests
{
    [Fact]
    public void Fit_ReducesLargeContent_NeverEnlargesSmallImages()
    {
        Assert.Equal(0.5, ViewerZoom.FactorFor(ZoomSetting.Fit, 4000, 2000, 2000, 1500), 6);
        Assert.Equal(1, ViewerZoom.FactorFor(ZoomSetting.Fit, 200, 100, 2000, 1500));
        Assert.Equal(10, ViewerZoom.FactorFor(ZoomSetting.Fit, 200, 100, 2000, 1500, allowEnlarge: true), 6); // vector pages
    }

    [Fact]
    public void FitWidth_UsesTheWidthOnly()
    {
        Assert.Equal(2, ViewerZoom.FactorFor(ZoomSetting.FitWidth, 1000, 5000, 2000, 500), 6);
    }

    [Fact]
    public void Custom_IsClampedToLimits()
    {
        Assert.Equal(1, ViewerZoom.FactorFor(ZoomSetting.ActualSize, 6000, 4000, 100, 100));
        Assert.Equal(ViewerZoom.MaxFactor, ViewerZoom.FactorFor(new ZoomSetting(ZoomMode.Custom, 1000), 10, 10, 100, 100));
        Assert.Equal(ViewerZoom.MinFactor, ViewerZoom.FactorFor(new ZoomSetting(ZoomMode.Custom, 0.0001), 10, 10, 100, 100));

        // A huge source cannot be zoomed beyond MaxDisplaySide on screen.
        Assert.Equal(ViewerZoom.MaxDisplaySide / 100_000, ViewerZoom.Clamp(32, 100_000, 50_000), 6);
        Assert.Equal(1, ViewerZoom.Clamp(double.NaN, 10, 10));
    }

    [Theory]
    [InlineData(1, 1.25)]
    [InlineData(0.79, 1)]
    [InlineData(32, 32)]
    [InlineData(0.01, 0.05)]
    public void StepIn_GoesToTheNextStepAbove(double factor, double expected) =>
        Assert.Equal(expected, ViewerZoom.StepIn(factor), 6);

    [Theory]
    [InlineData(1, 0.75)]
    [InlineData(1.1, 1)]
    [InlineData(0.05, 0.02)]
    public void StepOut_GoesToTheNextStepBelow(double factor, double expected) =>
        Assert.Equal(expected, ViewerZoom.StepOut(factor), 6);

    [Fact]
    public void Wheel_MultipliesPerNotch()
    {
        Assert.Equal(1.2, ViewerZoom.Wheel(1, 120), 6);
        Assert.Equal(1 / 1.44, ViewerZoom.Wheel(1, -240), 6);
    }

    [Fact]
    public void DecodeSize_IsDisplayWhenReduced_SourceWhenEnlarged_WithinBudget()
    {
        Assert.Equal((1500u, 1000u), ViewerZoom.DecodeSize(6000, 4000, 1500, 1000, 50_000_000));
        Assert.Equal((600u, 400u), ViewerZoom.DecodeSize(600, 400, 2400, 1600, 50_000_000)); // the screen stretches it
        (uint w, uint h) = ViewerZoom.DecodeSize(20000, 10000, 20000, 10000, 50_000_000);
        Assert.True((long)w * h <= 50_000_000);
        Assert.Equal(2.0, w / (double)h, 2);
    }

    [Fact]
    public void AnchoredOffset_KeepsThePointUnderThePointer()
    {
        // Point 100 px into the viewport at offset 300 (content 400) → content doubles → 800 - 100 = 700.
        Assert.Equal(700, ViewerZoom.AnchoredOffset(300, 100, 2, 10_000, 1000));
        Assert.Equal(0, ViewerZoom.AnchoredOffset(0, 0, 0.5, 400, 1000)); // smaller than the viewport
        Assert.Equal(9000, ViewerZoom.AnchoredOffset(8000, 500, 1.5, 10_000, 1000)); // clamped to the end
    }

    [Fact]
    public void ForScaleMode_MapsTheUserSetting()
    {
        Assert.Equal(ZoomSetting.Fit, ZoomSetting.For(ImageScaleMode.FitNoUpscale));
        Assert.Equal(ZoomSetting.ActualSize, ZoomSetting.For(ImageScaleMode.ActualSize));
    }

    [Fact]
    public void DisplaySize_IsAtLeastOnePixel()
    {
        Assert.Equal((1u, 1u), ViewerZoom.DisplaySize(10, 10, 0.001));
        Assert.Equal((3000u, 2000u), ViewerZoom.DisplaySize(6000, 4000, 0.5));
    }
}

[Trait("Category", "Viewer")]
public sealed class ViewOrientationTests
{
    [Fact]
    public void FourQuarterTurns_AreIdentity()
    {
        ViewOrientation o = ViewOrientation.Identity;
        for (int i = 0; i < 4; i++)
        {
            o = o.RotateClockwise();
        }

        Assert.True(o.IsIdentity);
        Assert.Equal(ViewOrientation.Identity.RotateCounterClockwise(), new ViewOrientation(3, false));
    }

    [Fact]
    public void Flips_UndoThemselves_AndTogetherAreAHalfTurn()
    {
        ViewOrientation o = new ViewOrientation(1, false);
        Assert.Equal(o, o.FlipHorizontal().FlipHorizontal());
        Assert.Equal(o, o.FlipVertical().FlipVertical());
        Assert.Equal(new ViewOrientation(2, false), ViewOrientation.Identity.FlipVertical().FlipHorizontal());
    }

    [Fact]
    public void SwapsAxes_ForQuarterAndThreeQuarterTurns()
    {
        Assert.Equal((3, 4), new ViewOrientation(1, false).Oriented(4, 3));
        Assert.Equal((4, 3), new ViewOrientation(2, true).Oriented(4, 3));
        Assert.Equal(270, new ViewOrientation(3, false).Degrees);
    }

    /// <summary>The corners of a 4 × 3 image land where the screen operation says (checked against the definition).</summary>
    [Fact]
    public void MapPixel_MatchesScreenOperations()
    {
        // Clockwise: top-left goes to top-right.
        Assert.Equal((2, 0), new ViewOrientation(1, false).MapPixel(0, 0, 4, 3));

        // Flip horizontal of the upright image: top-left goes to top-right.
        Assert.Equal((3, 0), ViewOrientation.Identity.FlipHorizontal().MapPixel(0, 0, 4, 3));

        // Flip vertical of the upright image: top-left goes to bottom-left.
        Assert.Equal((0, 2), ViewOrientation.Identity.FlipVertical().MapPixel(0, 0, 4, 3));

        // Rotate clockwise, then flip horizontally on screen: the screen's top-right corner (where top-left went) moves to the top-left.
        Assert.Equal((0, 0), new ViewOrientation(1, false).FlipHorizontal().MapPixel(0, 0, 4, 3));
    }
}
