using Mavue.Core.Formats;
using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

/// <summary>What Quick View prepares ahead (ARCHITECTURE §4.7.4, and the removed wasted prefetches of §4.7.6).</summary>
[Trait("Category", "QuickView")]
public class PreviewPrefetchPlanTests
{
    private const long MiB = 1024L * 1024;

    private static FileFacts Facts(FileFormat format, long length = 1000, PreviewAccess access = PreviewAccess.Allowed) =>
        new(FileAttributes.Normal, length, format, access);

    [Fact]
    public void TwoNeighbors_NextFirst_ThenPrevious()
    {
        Assert.Equal([@"C:\n.jpg", @"C:\p.jpg"], PreviewPrefetchPlan.NeighborOrder(PreviewResourcePolicy.High, previous: @"C:\p.jpg", next: @"C:\n.jpg"));
        Assert.Equal([@"C:\n.jpg", @"C:\p.jpg"], PreviewPrefetchPlan.NeighborOrder(PreviewResourcePolicy.Balanced, @"C:\p.jpg", @"C:\n.jpg"));
    }

    [Fact]
    public void LowTier_OnlyTheNext()
    {
        Assert.Equal([@"C:\n.jpg"], PreviewPrefetchPlan.NeighborOrder(PreviewResourcePolicy.Low, @"C:\p.jpg", @"C:\n.jpg"));
    }

    [Fact]
    public void LowTier_AtTheEnd_PreparesNothing()
    {
        Assert.Empty(PreviewPrefetchPlan.NeighborOrder(PreviewResourcePolicy.Low, @"C:\p.jpg", next: null));
    }

    [Fact]
    public void NoNeighborsAllowed_PreparesNothing()
    {
        PreviewResourcePolicy none = PreviewResourcePolicy.High with { PrefetchNeighbors = 0 };
        Assert.Empty(PreviewPrefetchPlan.NeighborOrder(none, @"C:\p.jpg", @"C:\n.jpg"));
    }

    [Fact]
    public void MissingNeighbors_AreSkipped()
    {
        Assert.Equal([@"C:\p.jpg"], PreviewPrefetchPlan.NeighborOrder(PreviewResourcePolicy.High, @"C:\p.jpg", null));
        Assert.Empty(PreviewPrefetchPlan.NeighborOrder(PreviewResourcePolicy.High, null, null));
    }

    [Fact]
    public void SameFileOnBothSides_IsPreparedOnce()
    {
        // Two-item multi-selection wrapping or a view that reports the same neighbor twice.
        Assert.Equal([@"C:\x\A.jpg"], PreviewPrefetchPlan.NeighborOrder(PreviewResourcePolicy.High, @"C:\x\a.jpg", @"C:\x\A.jpg"));
    }

    [Fact]
    public void MayPrefetch_RegularFileWithinLimit()
    {
        Assert.True(PreviewPrefetchPlan.MayPrefetch(Facts(FileFormat.Jpeg, 10 * MiB), PreviewResourcePolicy.High));
        Assert.True(PreviewPrefetchPlan.MayPrefetch(Facts(FileFormat.Jpeg, 64 * MiB), PreviewResourcePolicy.High)); // at the limit
    }

    [Fact]
    public void MayPrefetch_LargerThanTheTierAllows_No()
    {
        Assert.False(PreviewPrefetchPlan.MayPrefetch(Facts(FileFormat.Jpeg, (64 * MiB) + 1), PreviewResourcePolicy.High));
        Assert.False(PreviewPrefetchPlan.MayPrefetch(Facts(FileFormat.Jpeg, 40 * MiB), PreviewResourcePolicy.Low));
    }

    [Theory]
    [InlineData(PreviewAccess.CloudPlaceholder)] // would download the file
    [InlineData(PreviewAccess.NotAFile)]
    [InlineData(PreviewAccess.TooLarge)]
    public void MayPrefetch_NotARegularReadableFile_No(PreviewAccess access)
    {
        Assert.False(PreviewPrefetchPlan.MayPrefetch(Facts(FileFormat.Jpeg, 1000, access), PreviewResourcePolicy.High));
    }

    [Theory]
    [InlineData(PreviewContentKind.Raster, true)]
    [InlineData(PreviewContentKind.PdfDocument, false)]
    [InlineData(PreviewContentKind.Svg, false)]
    [InlineData(PreviewContentKind.Media, false)]
    [InlineData(PreviewContentKind.Universal, false)]
    public void OnlyRasterContent_DecodesNeighborsAhead(PreviewContentKind kind, bool expected)
    {
        Assert.Equal(expected, PreviewPrefetchPlan.PreparesNeighbors(kind));
    }

    [Theory]
    [InlineData(FileFormat.Jpeg, true, true)]
    [InlineData(FileFormat.Png, true, true)]
    [InlineData(FileFormat.Gif, true, true)]
    [InlineData(FileFormat.Pdf, true, false)]   // PDFium document view renders on demand: no Windows.Data.Pdf page image ahead
    [InlineData(FileFormat.Pdf, false, true)]   // without PDFium, PDF pages are images and are prepared like images
    [InlineData(FileFormat.Svg, true, false)]   // WIC cannot decode SVG: no failing decode ahead
    [InlineData(FileFormat.Svg, false, false)]
    [InlineData(FileFormat.Mp4, true, false)]
    [InlineData(FileFormat.Mp3, true, false)]
    public void NeighborFile_IsDecodedAheadOnlyWhenItsContentIsRaster(FileFormat format, bool pdfium, bool expected)
    {
        FileFacts facts = Facts(format);
        bool prepared = PreviewPrefetchPlan.MayPrefetch(facts, PreviewResourcePolicy.High) &&
            PreviewPrefetchPlan.PreparesNeighbors(PreviewContentRouting.Choose(facts, pdfium));
        Assert.Equal(expected, prepared);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => PreviewPrefetchPlan.NeighborOrder(null!, null, null));
        Assert.Throws<ArgumentNullException>(() => PreviewPrefetchPlan.MayPrefetch(null!, PreviewResourcePolicy.High));
        Assert.Throws<ArgumentNullException>(() => PreviewPrefetchPlan.MayPrefetch(Facts(FileFormat.Jpeg), null!));
    }
}
