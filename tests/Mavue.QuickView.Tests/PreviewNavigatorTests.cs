using Mavue.QuickView.Preview;

namespace Mavue.QuickView.Tests;

[Trait("Category", "QuickView")]
public class PreviewNavigatorTests
{
    private const string A = @"C:\p\a.jpg";
    private const string B = @"C:\p\b.jpg";
    private const string C = @"C:\p\c.jpg";
    private const string D = @"C:\p\d.jpg";

    private static NavigationResult Show(string path) => new(NavigationAction.Show, path);

    [Fact]
    public void Start_EmptySelection_ShowsNothing()
    {
        var nav = new PreviewNavigator();
        Assert.Equal(NavigationResult.Nothing, nav.Start([], null));
        Assert.Equal(NavigationMode.None, nav.Mode);
    }

    [Fact]
    public void Start_Single()
    {
        var nav = new PreviewNavigator();
        Assert.Equal(Show(A), nav.Start([A], A));
        Assert.Equal(NavigationMode.SingleItem, nav.Mode);
    }

    [Fact]
    public void Start_Multiple_ShowsFocusedSelectedItem()
    {
        var nav = new PreviewNavigator();
        Assert.Equal(Show(C), nav.Start([A, B, C], C));
        Assert.Equal(NavigationMode.MultipleItems, nav.Mode);
        Assert.Equal(2, nav.Index);
    }

    [Fact]
    public void Start_Multiple_FocusOutsideSelection_ShowsFirst()
    {
        var nav = new PreviewNavigator();
        Assert.Equal(Show(A), nav.Start([A, B], D));
    }

    [Fact]
    public void Single_FollowsExplorerSelection()
    {
        // ↓ in Explorer moves the selection from A to B.
        var nav = new PreviewNavigator();
        nav.Start([A], A);

        Assert.Equal(Show(B), nav.OnSelectionChanged([B], B));
        Assert.Equal(NavigationMode.SingleItem, nav.Mode);
    }

    [Fact]
    public void Single_RepeatedEventForSameItem_DoesNothing()
    {
        var nav = new PreviewNavigator();
        nav.Start([A], A);
        Assert.Equal(NavigationResult.Nothing, nav.OnSelectionChanged([A], A));
    }

    [Fact]
    public void Single_ShiftExtendsSelection_SwitchesToMultipleAndShowsFocused()
    {
        var nav = new PreviewNavigator();
        nav.Start([A], A);

        Assert.Equal(Show(B), nav.OnSelectionChanged([A, B], B));
        Assert.Equal(NavigationMode.MultipleItems, nav.Mode);
    }

    [Fact]
    public void EmptySelection_Hides()
    {
        var nav = new PreviewNavigator();
        nav.Start([A], A);

        Assert.Equal(NavigationResult.Hide, nav.OnSelectionChanged([], null));
        Assert.Equal(NavigationMode.None, nav.Mode);
    }

    [Fact]
    public void Multiple_StepsWithinSelection_Clamped()
    {
        var nav = new PreviewNavigator();
        nav.Start([A, B, C], A);

        Assert.Equal(Show(B), nav.Step(+1));
        Assert.Equal(Show(C), nav.Step(+1));
        Assert.Equal(NavigationResult.Nothing, nav.Step(+1)); // last item: no wrap-around
        Assert.Equal(Show(B), nav.Step(-1));
        Assert.Equal(NavigationMode.MultipleItems, nav.Mode);
    }

    [Fact]
    public void Multiple_SameSelectionEvent_KeepsPosition()
    {
        var nav = new PreviewNavigator();
        nav.Start([A, B, C], A);
        nav.Step(+1);

        // Explorer re-reports the same selection with its focus still on A (our ←/→ never reached it):
        // the step position must survive.
        Assert.Equal(NavigationResult.Nothing, nav.OnSelectionChanged([A, B, C], A));
        Assert.Equal(B, nav.Current);
    }

    [Fact]
    public void Multiple_ExplorerMovesFocusWithinSelection_Follows()
    {
        // Ctrl+→ in Explorer moves only the focus rectangle onto C.
        var nav = new PreviewNavigator();
        nav.Start([A, B, C], A);

        Assert.Equal(Show(C), nav.OnSelectionChanged([A, B, C], C));
        Assert.Equal(2, nav.Index);
    }

    [Fact]
    public void Multiple_SameSelectionEventWithoutFocus_KeepsPosition()
    {
        var nav = new PreviewNavigator();
        nav.Start([A, B, C], A);
        nav.Step(+1);

        Assert.Equal(NavigationResult.Nothing, nav.OnSelectionChanged([C, B, A], null));
        Assert.Equal(B, nav.Current);
    }

    [Fact]
    public void Multiple_CollapseToSingle_SwitchesToSingle()
    {
        // ↓ in Explorer with several items selected selects just the next item.
        var nav = new PreviewNavigator();
        nav.Start([A, B, C], A);

        Assert.Equal(Show(B), nav.OnSelectionChanged([B], B));
        Assert.Equal(NavigationMode.SingleItem, nav.Mode);
    }

    [Fact]
    public void Multiple_SelectionChanges_KeepsShownItemIfStillSelected()
    {
        var nav = new PreviewNavigator();
        nav.Start([A, B, C], A);
        nav.Step(+1); // showing B

        Assert.Equal(NavigationResult.Nothing, nav.OnSelectionChanged([B, C, D], null));
        Assert.Equal(0, nav.Index); // B stays shown, now first of the new selection
        Assert.Equal(B, nav.Current);
        Assert.Equal([B, C, D], nav.Items);
    }

    [Fact]
    public void Single_StepAsksExplorerToMove()
    {
        // Quick View has keyboard focus (user clicked it): arrows move Explorer's selection.
        var nav = new PreviewNavigator();
        nav.Start([A], A);

        Assert.Equal(new NavigationResult(NavigationAction.MoveExplorerSelection, Delta: 1), nav.Step(+3));
        Assert.Equal(new NavigationResult(NavigationAction.MoveExplorerSelection, Delta: -1), nav.Step(-1));
    }

    [Fact]
    public void PathsCompareCaseInsensitively()
    {
        var nav = new PreviewNavigator();
        nav.Start([A], A);
        Assert.Equal(NavigationResult.Nothing, nav.OnSelectionChanged([A.ToUpperInvariant()], null));
    }

    [Fact]
    public void AfterReset_EventsAreIgnored()
    {
        var nav = new PreviewNavigator();
        nav.Start([A], A);
        nav.Reset();

        Assert.Equal(NavigationResult.Nothing, nav.OnSelectionChanged([B], B));
        Assert.Equal(NavigationResult.Nothing, nav.Step(1));
    }
}
