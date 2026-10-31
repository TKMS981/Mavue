using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

/// <summary>The pixel inspector's coordinate mapping and formatting (docs/ARCHITECTURE.md §4.7.8).</summary>
[Trait("Category", "QuickView")]
public class PixelInspectionTests
{
    public static TheoryData<int, bool> AllViewOrientations()
    {
        var data = new TheoryData<int, bool>();
        foreach (bool mirrored in new[] { false, true })
        {
            for (int turns = 0; turns < 4; turns++)
            {
                data.Add(turns, mirrored);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllViewOrientations))]
    public void Unmap_IsTheInverseOfMapPixel(int turns, bool mirrored)
    {
        var view = new ViewOrientation(turns, mirrored);
        const int Width = 7;
        const int Height = 4; // not square: catches swapped axes
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                (int sx, int sy) = view.MapPixel(x, y, Width, Height);
                Assert.Equal((x, y), PixelInspection.Unmap(view, sx, sy, Width, Height));
            }
        }
    }

    [Fact]
    public void ImagePixelAt_ScalesFromTheElementToTheImage()
    {
        // A 4000 × 3000 image shown 400 × 300 (10 %): the element's point (10.5, 20.5) is pixel (105, 205).
        Assert.Equal((105, 205), PixelInspection.ImagePixelAt(10.5, 20.5, 400, 300, 4000, 3000, ViewOrientation.Identity));

        // Zoomed in 800 % on a 10 × 10 image: every 8 element units is one pixel.
        Assert.Equal((3, 9), PixelInspection.ImagePixelAt(31.9, 79.9, 80, 80, 10, 10, ViewOrientation.Identity));
    }

    [Fact]
    public void ImagePixelAt_Edges_StayInside()
    {
        Assert.Equal((0, 0), PixelInspection.ImagePixelAt(0, 0, 400, 300, 4000, 3000, ViewOrientation.Identity));
        Assert.Equal((3999, 2999), PixelInspection.ImagePixelAt(399.999, 299.999, 400, 300, 4000, 3000, ViewOrientation.Identity));
    }

    [Theory]
    [InlineData(-0.1, 10)]
    [InlineData(10, -0.1)]
    [InlineData(400, 10)]
    [InlineData(10, 300)]
    [InlineData(double.NaN, 10)]
    public void ImagePixelAt_OutsideTheElement_IsNull(double x, double y)
    {
        Assert.Null(PixelInspection.ImagePixelAt(x, y, 400, 300, 4000, 3000, ViewOrientation.Identity));
    }

    [Fact]
    public void ImagePixelAt_EmptySizes_AreNull()
    {
        Assert.Null(PixelInspection.ImagePixelAt(1, 1, 0, 300, 4000, 3000, ViewOrientation.Identity));
        Assert.Null(PixelInspection.ImagePixelAt(1, 1, 400, 300, 0, 3000, ViewOrientation.Identity));
    }

    [Fact]
    public void ImagePixelAt_TurnedView_FindsTheImagesPixel()
    {
        // A 400 × 200 image turned a quarter clockwise is shown 200 × 400: its top-right corner on screen is the
        // image's top-left pixel.
        var turned = new ViewOrientation(1, false);
        Assert.Equal((0, 0), PixelInspection.ImagePixelAt(199.5, 0.5, 200, 400, 400, 200, turned));

        // The bottom-left on screen is the image's bottom-right pixel.
        Assert.Equal((399, 199), PixelInspection.ImagePixelAt(0.5, 399.5, 200, 400, 400, 200, turned));
    }

    [Theory]
    [InlineData(1, 120, 80)]
    [InlineData(2, 120, 80)]
    [InlineData(3, 120, 80)]
    [InlineData(4, 120, 80)]
    [InlineData(5, 120, 80)]
    [InlineData(6, 120, 80)]
    [InlineData(7, 120, 80)]
    [InlineData(8, 120, 80)]
    public void StoredPixel_IsInsideTheStoredImage_AndOneToOne(int orientation, int storedWidth, int storedHeight)
    {
        (int uw, int uh) = PixelInspection.UprightSize(storedWidth, storedHeight, orientation);
        var seen = new HashSet<(int, int)>();
        for (int y = 0; y < uh; y++)
        {
            for (int x = 0; x < uw; x++)
            {
                (int sx, int sy) = PixelInspection.StoredPixel(x, y, storedWidth, storedHeight, orientation);
                Assert.InRange(sx, 0, storedWidth - 1);
                Assert.InRange(sy, 0, storedHeight - 1);
                Assert.True(seen.Add((sx, sy)));
            }
        }
    }

    [Theory]
    [InlineData(1, 0, 0)]       // upright top-left = stored top-left
    [InlineData(2, 119, 0)]     // mirrored: stored top-right
    [InlineData(3, 119, 79)]    // half turn: stored bottom-right
    [InlineData(4, 0, 79)]      // flipped: stored bottom-left
    [InlineData(5, 0, 0)]       // transposed
    [InlineData(6, 0, 79)]      // shown turned clockwise: the stored bottom-left comes to the top-left
    [InlineData(7, 119, 79)]    // transversed
    [InlineData(8, 119, 0)]     // shown turned counter-clockwise: the stored top-right comes to the top-left
    public void StoredPixel_UprightTopLeftCorner(int orientation, int expectedX, int expectedY)
    {
        Assert.Equal((expectedX, expectedY), PixelInspection.StoredPixel(0, 0, 120, 80, orientation));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-3)]
    public void StoredPixel_UnknownOrientation_IsUpright(int orientation)
    {
        Assert.Equal((5, 6), PixelInspection.StoredPixel(5, 6, 120, 80, orientation));
        Assert.Equal((120, 80), PixelInspection.UprightSize(120, 80, orientation));
    }

    [Fact]
    public void UprightSize_SwapsForQuarterTurns()
    {
        Assert.Equal((80, 120), PixelInspection.UprightSize(120, 80, 6));
        Assert.Equal((80, 120), PixelInspection.UprightSize(120, 80, 5));
        Assert.Equal((120, 80), PixelInspection.UprightSize(120, 80, 3));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 256, 256)]
    [InlineData(255, 255, 0, 0, 256, 256)]
    [InlineData(256, 10, 256, 0, 256, 256)]
    [InlineData(999, 700, 768, 512, 232, 188)]      // last tiles are clipped to 1000 × 700
    [InlineData(5000, -5, 768, 0, 232, 256)]        // outside: clamped to the image
    public void TileAround_IsAlignedAndClipped(int x, int y, int left, int top, int width, int height)
    {
        Assert.Equal(new PixelRect(left, top, width, height), PixelInspection.TileAround(x, y, 1000, 700));
    }

    [Fact]
    public void TileAround_ContainsThePixel()
    {
        for (int x = 0; x < 1000; x += 37)
        {
            for (int y = 0; y < 700; y += 41)
            {
                Assert.True(PixelInspection.TileAround(x, y, 1000, 700).Contains(x, y));
            }
        }
    }

    [Fact]
    public void TileAround_RejectsEmptyImagesAndTiles()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelInspection.TileAround(0, 0, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelInspection.TileAround(0, 0, 10, 10, 0));
    }

    [Fact]
    public void Formatting()
    {
        var color = new PixelColor(255, 128, 0, 128);
        Assert.Equal("#FF8000", color.Hex);
        Assert.Equal("#FF800080", color.HexWithAlpha);
        Assert.Equal(50, color.AlphaPercent);
        Assert.Equal("R 255  G 128  B 0", PixelInspection.RgbText(color));
        Assert.Equal("A 128 (50%)", PixelInspection.AlphaText(color));
        Assert.Equal("X 12  Y 3400", PixelInspection.PositionText(12, 3400));
        Assert.Equal(0, new PixelColor(0, 0, 0, 0).AlphaPercent);
        Assert.Equal(100, new PixelColor(0, 0, 0, 255).AlphaPercent);
    }

    [Fact]
    public void FromBgra_ReadsMemoryOrder()
    {
        Assert.Equal(new PixelColor(3, 2, 1, 4), PixelColor.FromBgra([1, 2, 3, 4]));
    }
}
