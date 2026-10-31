using Mavue.QuickView.Preview;

namespace Mavue.QuickView.Tests;

/// <summary>Which files the filmstrip shows and when Explorer's view is read again (docs/ARCHITECTURE.md §4.7.8).</summary>
[Trait("Category", "QuickView")]
public class FilmstripModelTests
{
    private static string P(int i) => $@"C:\photos\{i:D5}.jpg";

    /// <summary>The range <paramref name="first"/>..<paramref name="last"/> of a view, as Explorer reports it.</summary>
    private static List<FilmstripItem> Range(int first, int last) => [.. Enumerable.Range(first, last - first + 1).Select(i => new FilmstripItem(i, P(i)))];

    [Theory]
    [InlineData(500, 1000, 60, 440, 121)]
    [InlineData(0, 1000, 60, 0, 61)]
    [InlineData(999, 1000, 60, 939, 61)]
    [InlineData(5, 8, 60, 0, 8)]
    [InlineData(5000, 1000, 60, 939, 61)]     // focus beyond the view: clamped
    [InlineData(-3, 10, 2, 0, 3)]
    [InlineData(0, 0, 60, 0, 0)]
    public void RangeAround_IsBoundedAndClamped(int focused, int total, int radius, int first, int count)
    {
        Assert.Equal((first, count), FilmstripModel.RangeAround(focused, total, radius));
    }

    [Fact]
    public void Empty_AsksForARange()
    {
        var model = new FilmstripModel();
        Assert.Equal(FilmstripUpdate.NeedsReload, model.Track(P(0)));
    }

    [Fact]
    public void InsideTheRange_OnlyTheHighlightMoves()
    {
        var model = new FilmstripModel();
        model.LoadViewRange(Range(440, 560), 1000, P(500));
        Assert.Equal(60, model.CurrentIndex);

        Assert.Equal(FilmstripUpdate.Moved, model.Track(P(501)));
        Assert.Equal(61, model.CurrentIndex);
        Assert.Equal(FilmstripUpdate.Moved, model.Track(@"C:\PHOTOS\00470.JPG")); // case-insensitive
        Assert.Equal(30, model.CurrentIndex);
    }

    [Fact]
    public void NearAnEndWithMoreItems_ReadsAgain()
    {
        var model = new FilmstripModel();
        model.LoadViewRange(Range(440, 560), 1000, P(500));

        Assert.Equal(FilmstripUpdate.NeedsReload, model.Track(P(441)));   // near the start, items before exist
        Assert.Equal(FilmstripUpdate.NeedsReload, model.Track(P(555)));   // near the end, items after exist
        Assert.Equal(FilmstripUpdate.Moved, model.Track(P(440 + FilmstripModel.ReloadMargin)));
    }

    [Fact]
    public void AtTheEndsOfTheView_NoReload()
    {
        var model = new FilmstripModel();
        model.LoadViewRange(Range(0, 60), 61, P(0));

        Assert.Equal(FilmstripUpdate.Moved, model.Track(P(0)));
        Assert.Equal(FilmstripUpdate.Moved, model.Track(P(60)));
    }

    [Fact]
    public void FileOutsideTheRange_ReadsAgain()
    {
        var model = new FilmstripModel();
        model.LoadViewRange(Range(440, 560), 1000, P(500));

        Assert.Equal(FilmstripUpdate.NeedsReload, model.Track(P(900)));
        Assert.Equal(-1, model.CurrentIndex);
    }

    [Fact]
    public void ItemsWithoutPaths_LeaveGaps_ThatAreHandled()
    {
        // Explorer reported 0..9 but items 3 and 4 are virtual (no path): positions stay those of the view.
        List<FilmstripItem> items = [.. Range(0, 9).Where(i => i.ViewIndex is not (3 or 4))];
        var model = new FilmstripModel();
        model.LoadViewRange(items, 10, P(5));

        Assert.Equal(3, model.CurrentIndex);
        Assert.Equal(P(2), model.Neighbor(-1)!.Path);
        Assert.Equal(P(6), model.Neighbor(+1)!.Path);
        Assert.Equal(10, model.ViewCount);
    }

    [Fact]
    public void ItemsAreKeptInViewOrder()
    {
        var model = new FilmstripModel();
        model.LoadViewRange([new FilmstripItem(2, P(2)), new FilmstripItem(0, P(0)), new FilmstripItem(1, P(1))], 3, P(1));
        Assert.Equal([P(0), P(1), P(2)], model.Items.Select(i => i.Path));
        Assert.Equal(1, model.CurrentIndex);
    }

    [Fact]
    public void Selection_ShowsTheSelectedFiles_AndNeverReads()
    {
        var model = new FilmstripModel();
        model.LoadSelection([P(7), P(3), P(9)], P(3));

        Assert.True(model.IsSelection);
        Assert.Equal(1, model.CurrentIndex);
        Assert.Equal(FilmstripUpdate.Moved, model.Track(P(9)));
        Assert.Equal(2, model.CurrentIndex);
        Assert.Equal(FilmstripUpdate.Moved, model.Track(P(100))); // not in the selection: no highlight, no read
        Assert.Equal(-1, model.CurrentIndex);
    }

    [Fact]
    public void Neighbor_StopsAtTheEnds()
    {
        var model = new FilmstripModel();
        model.LoadViewRange(Range(0, 2), 3, P(0));

        Assert.Null(model.Neighbor(-1));
        Assert.Equal(P(1), model.Neighbor(+1)!.Path);
        model.Track(P(2));
        Assert.Null(model.Neighbor(+1));
        model.Track(P(99));
        Assert.Null(model.Neighbor(+1)); // nothing highlighted
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var model = new FilmstripModel();
        model.LoadSelection([P(1), P(2)], P(1));
        model.Clear();

        Assert.Empty(model.Items);
        Assert.Equal(-1, model.CurrentIndex);
        Assert.False(model.IsSelection);
        Assert.Equal(FilmstripUpdate.NeedsReload, model.Track(P(1)));
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        var model = new FilmstripModel();
        Assert.Throws<ArgumentNullException>(() => model.LoadViewRange(null!, 0, null));
        Assert.Throws<ArgumentNullException>(() => model.LoadSelection(null!, null));
        Assert.Throws<ArgumentException>(() => model.Track(string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => FilmstripModel.RangeAround(0, 10, -1));
    }
}
