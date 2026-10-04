using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

[Trait("Category", "Viewer")]
public sealed class PdfPageLayoutTests
{
    // A4 portrait at 100 % in DIPs (595 × 842 pt × 96/72).
    private static readonly (double, double) A4 = (793.3, 1122.7);

    private static List<(double Width, double Height)> Pages(int count) => Enumerable.Repeat(A4, count).ToList();

    [Fact]
    public void Continuous_StacksPages_CenteredHorizontally()
    {
        PdfPageLayout layout = PdfPageLayout.Create(Pages(3), PdfLayoutMode.Continuous, 0.5, 1000, 800);
        Assert.Equal(3, layout.Pages.Count);
        PlacedPage first = layout.Pages[0];
        Assert.Equal(PdfPageLayout.Padding, first.Y, 6);
        Assert.Equal((1000 - first.Width) / 2, first.X, 6);
        Assert.Equal(first.Bottom + PdfPageLayout.RowGap, layout.Pages[1].Y, 6);
        Assert.Equal(layout.Pages[2].Bottom + PdfPageLayout.Padding, layout.ExtentHeight, 6);
    }

    [Fact]
    public void TwoPages_PutsPairsSideBySide_LastPageAlone()
    {
        PdfPageLayout layout = PdfPageLayout.Create(Pages(5), PdfLayoutMode.TwoPages, 0.5, 2000, 800);
        Assert.Equal(layout.Pages[0].Y, layout.Pages[1].Y);
        Assert.Equal(layout.Pages[0].Right + PdfPageLayout.SpreadGap, layout.Pages[1].X, 6);
        Assert.True(layout.Pages[2].Y > layout.Pages[1].Y);
        Assert.Equal((2000 - layout.Pages[4].Width) / 2, layout.Pages[4].X, 6);
        Assert.Equal(2, PdfPageLayout.StepPage(1, +1, 5, PdfLayoutMode.TwoPages));
        Assert.Equal(0, PdfPageLayout.StepPage(3, -1, 5, PdfLayoutMode.TwoPages));
        Assert.Equal(4, PdfPageLayout.StepPage(4, +1, 5, PdfLayoutMode.TwoPages));
    }

    [Fact]
    public void TwoPagesCover_PutsTheCoverAlone_ThenPairs()
    {
        PdfPageLayout layout = PdfPageLayout.Create(Pages(6), PdfLayoutMode.TwoPagesCover, 0.5, 2000, 800);
        Assert.Equal((2000 - layout.Pages[0].Width) / 2, layout.Pages[0].X, 6); // cover centered, alone
        Assert.True(layout.Pages[1].Y > layout.Pages[0].Y);
        Assert.Equal(layout.Pages[1].Y, layout.Pages[2].Y); // 2–3
        Assert.Equal(layout.Pages[3].Y, layout.Pages[4].Y); // 4–5
        Assert.True(layout.Pages[5].Y > layout.Pages[4].Y); // 6 alone at the end
        Assert.Equal(1, PdfPageLayout.StepPage(0, +1, 6, PdfLayoutMode.TwoPagesCover));
        Assert.Equal(3, PdfPageLayout.StepPage(1, +1, 6, PdfLayoutMode.TwoPagesCover));
        Assert.Equal(3, PdfPageLayout.StepPage(2, +1, 6, PdfLayoutMode.TwoPagesCover));
        Assert.Equal(1, PdfPageLayout.StepPage(4, -1, 6, PdfLayoutMode.TwoPagesCover));
        Assert.Equal(0, PdfPageLayout.StepPage(2, -1, 6, PdfLayoutMode.TwoPagesCover));
        Assert.Equal(5, PdfPageLayout.StepPage(4, +1, 6, PdfLayoutMode.TwoPagesCover));
        Assert.Equal(5, PdfPageLayout.StepPage(5, +1, 6, PdfLayoutMode.TwoPagesCover));
        double fit = PdfPageLayout.FitFactor(Pages(6), PdfLayoutMode.TwoPagesCover, ZoomSetting.FitWidth, 1000, 800, 0);
        Assert.Equal((1000 - (2 * PdfPageLayout.Padding) - PdfPageLayout.SpreadGap) / (2 * 793.3), fit, 4); // the widest row is a pair
    }

    [Fact]
    public void SinglePage_ShowsOnlyThatPage_CenteredVertically()
    {
        PdfPageLayout layout = PdfPageLayout.Create(Pages(10), PdfLayoutMode.SinglePage, 0.25, 1000, 1000, singlePage: 7);
        PlacedPage page = Assert.Single(layout.Pages);
        Assert.Equal(7, page.Index);
        Assert.Equal((1000 - page.Height) / 2, page.Y, 1);
        Assert.Equal(7, layout.CurrentPage(0, 1000));
        Assert.Null(layout.Find(6));
    }

    [Fact]
    public void Fit_ShowsTheWholePage_FitWidthFillsTheWidth()
    {
        List<(double, double)> pages = Pages(3);
        double fit = PdfPageLayout.FitFactor(pages, PdfLayoutMode.Continuous, ZoomSetting.Fit, 1000, 800, 0);
        Assert.Equal((800 - (2 * PdfPageLayout.Padding)) / 1122.7, fit, 4);
        double width = PdfPageLayout.FitFactor(pages, PdfLayoutMode.Continuous, ZoomSetting.FitWidth, 1000, 800, 0);
        Assert.Equal((1000 - (2 * PdfPageLayout.Padding)) / 793.3, width, 4);
        double spread = PdfPageLayout.FitFactor(pages, PdfLayoutMode.TwoPages, ZoomSetting.FitWidth, 1000, 800, 0);
        Assert.Equal((1000 - (2 * PdfPageLayout.Padding) - PdfPageLayout.SpreadGap) / (2 * 793.3), spread, 4);
        Assert.Equal(1.5, PdfPageLayout.FitFactor(pages, PdfLayoutMode.Continuous, new ZoomSetting(ZoomMode.Custom, 1.5), 1000, 800, 0));
    }

    [Fact]
    public void VisiblePages_And_CurrentPage_FollowTheScrollPosition()
    {
        PdfPageLayout layout = PdfPageLayout.Create(Pages(100), PdfLayoutMode.Continuous, 0.5, 1000, 800);
        double pitch = layout.Pages[1].Y - layout.Pages[0].Y;
        IReadOnlyList<PlacedPage> visible = layout.VisiblePages(pitch * 50, (pitch * 50) + 800);
        Assert.Equal([50, 51], visible.Select(p => p.Index));
        Assert.Equal(50, layout.CurrentPage(pitch * 50, 800));
        Assert.Equal(99, layout.CurrentPage(layout.ExtentHeight - 800, 800));
        Assert.Equal(0, layout.CurrentPage(0, 800));
        Assert.Equal(3, layout.PageAt(500, layout.Pages[3].Y + 10)?.Index);
        Assert.Null(layout.PageAt(1, 1));
    }

    [Fact]
    public void LargeDocuments_LayOutWithoutPerPageObjects()
    {
        PdfPageLayout layout = PdfPageLayout.Create(Pages(20_000), PdfLayoutMode.Continuous, 0.2, 1000, 800);
        Assert.Equal(20_000, layout.Pages.Count);
        Assert.InRange(layout.VisiblePages(layout.ExtentHeight / 2, (layout.ExtentHeight / 2) + 800).Count, 1, 6);
    }

    [Fact]
    public void Empty_AndBadFactors_AreSafe()
    {
        PdfPageLayout empty = PdfPageLayout.Create([], PdfLayoutMode.Continuous, 1, 100, 100);
        Assert.Empty(empty.Pages);
        Assert.Equal(0, empty.CurrentPage(0, 100));
        Assert.Equal(0, PdfPageLayout.StepPage(0, 1, 0, PdfLayoutMode.Continuous));
        PdfPageLayout nan = PdfPageLayout.Create(Pages(1), PdfLayoutMode.Continuous, double.NaN, 100, 100);
        Assert.Equal(1, nan.Factor);
    }
}
