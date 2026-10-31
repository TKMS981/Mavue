using Mavue.QuickView.Preview;

namespace Mavue.QuickView.Tests;

/// <summary>Preview pin and filmstrip navigation in <see cref="PreviewNavigator"/> (docs/ARCHITECTURE.md §4.7.8).</summary>
[Trait("Category", "QuickView")]
public class PreviewPinTests
{
    private const string A = @"C:\p\a.jpg";
    private const string B = @"C:\p\b.jpg";
    private const string C = @"C:\p\c.jpg";

    private static NavigationResult Show(string path) => new(NavigationAction.Show, path);

    [Fact]
    public void Pinned_IgnoresExplorerSelectionChanges()
    {
        var nav = new PreviewNavigator();
        nav.Start([A], A);
        Assert.True(nav.SetPinned(true));

        Assert.Equal(NavigationResult.Nothing, nav.OnSelectionChanged([B], B));
        Assert.Equal(NavigationResult.Nothing, nav.OnSelectionChanged([B, C], C));
        Assert.Equal(NavigationResult.Nothing, nav.OnSelectionChanged([], null)); // an empty selection does not close it either
        Assert.Equal(A, nav.Current);
        Assert.Equal(NavigationMode.SingleItem, nav.Mode);
    }

    [Fact]
    public void Unpinned_FollowsAgain()
    {
        var nav = new PreviewNavigator();
        nav.Start([A], A);
        nav.SetPinned(true);
        nav.OnSelectionChanged([B], B);

        Assert.True(nav.SetPinned(false));
        Assert.Equal(Show(B), nav.OnSelectionChanged([B], B));
    }

    [Fact]
    public void SetPinned_OnlyWhileSomethingIsShown_AndOnlyOnChange()
    {
        var nav = new PreviewNavigator();
        Assert.False(nav.SetPinned(true));
        Assert.False(nav.IsPinned);

        nav.Start([A], A);
        Assert.True(nav.SetPinned(true));
        Assert.False(nav.SetPinned(true));
        Assert.True(nav.IsPinned);
    }

    [Fact]
    public void Closing_OrANewSpace_Unpins()
    {
        var nav = new PreviewNavigator();
        nav.Start([A], A);
        nav.SetPinned(true);
        nav.Reset();
        Assert.False(nav.IsPinned);

        nav.Start([A], A);
        nav.SetPinned(true);
        nav.Start([B], B);
        Assert.False(nav.IsPinned);
    }

    [Fact]
    public void PinnedSingleItem_ArrowsDoNotMoveExplorer()
    {
        var nav = new PreviewNavigator();
        nav.Start([A], A);
        nav.SetPinned(true);
        Assert.Equal(NavigationResult.Nothing, nav.Step(+1));
    }

    [Fact]
    public void PinnedMultiSelection_StillSteps()
    {
        var nav = new PreviewNavigator();
        nav.Start([A, B, C], A);
        nav.SetPinned(true);
        Assert.Equal(Show(B), nav.Step(+1));
        Assert.Equal(NavigationResult.Nothing, nav.OnSelectionChanged([C], C));
        Assert.Equal(B, nav.Current);
    }

    [Fact]
    public void GoTo_InAMultiSelection_ShowsItDirectly()
    {
        var nav = new PreviewNavigator();
        nav.Start([A, B, C], A);
        Assert.Equal(Show(C), nav.GoTo(C));
        Assert.Equal(2, nav.Index);
        Assert.Equal(NavigationMode.MultipleItems, nav.Mode);
    }

    [Fact]
    public void GoTo_FollowingExplorer_AsksExplorerToSelect()
    {
        var nav = new PreviewNavigator();
        nav.Start([A], A);
        Assert.Equal(new NavigationResult(NavigationAction.SelectInExplorer, B), nav.GoTo(B));
        Assert.Equal(A, nav.Current); // shown once Explorer reports the new selection
    }

    [Fact]
    public void GoTo_WhilePinned_ShowsItWithoutExplorer_AndStaysPinned()
    {
        var nav = new PreviewNavigator();
        nav.Start([A], A);
        nav.SetPinned(true);
        Assert.Equal(Show(B), nav.GoTo(B));
        Assert.Equal(B, nav.Current);
        Assert.True(nav.IsPinned);
        Assert.Equal(NavigationMode.SingleItem, nav.Mode);
    }

    [Fact]
    public void GoTo_TheShownFile_OrNothingShown_DoesNothing()
    {
        var nav = new PreviewNavigator();
        Assert.Equal(NavigationResult.Nothing, nav.GoTo(A));
        nav.Start([A], A);
        Assert.Equal(NavigationResult.Nothing, nav.GoTo(@"C:\P\A.JPG"));
        Assert.Throws<ArgumentException>(() => nav.GoTo(string.Empty));
    }
}
