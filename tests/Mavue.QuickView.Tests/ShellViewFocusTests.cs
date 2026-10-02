using Mavue.QuickView.Trigger;
using static Mavue.QuickView.Trigger.SpaceKeyClassifier;

namespace Mavue.QuickView.Tests;

[Trait("Category", "QuickView")]
public class ShellViewFocusTests
{
    private const nint Owner = 0x100;
    private const nint CurrentView = 0x200;
    private const nint OtherView = 0x300;

    [Fact]
    public void FocusOnItemViewOfAnotherTab_IsSwitch()
    {
        Assert.True(ShellViewFocus.IsOtherItemView(DirectUiViewClass, ShellViewClass, OtherView, Owner, Owner, CurrentView));
    }

    [Fact]
    public void FocusMovingInsideTheFollowedView_IsNotSwitch()
    {
        // Arrow keys in the item view raise focus events for the same view.
        Assert.False(ShellViewFocus.IsOtherItemView(DirectUiViewClass, ShellViewClass, CurrentView, Owner, Owner, CurrentView));
    }

    [Theory]
    [InlineData("Edit", "DirectUIHWND")] // rename box
    [InlineData("SysTreeView32", "NamespaceTreeControl")] // navigation pane
    [InlineData("Microsoft.UI.Content.DesktopChildSiteBridge", "Microsoft.UI.Content.DesktopChildSiteBridge")] // address/search bar
    public void FocusOnOtherControls_IsNotSwitch(string focusClass, string parentClass)
    {
        Assert.False(ShellViewFocus.IsOtherItemView(focusClass, parentClass, OtherView, Owner, Owner, CurrentView));
    }

    [Fact]
    public void ItemViewInAnotherExplorerWindow_IsNotSwitch()
    {
        // Another window becoming active is handled by the foreground watcher, not by view switching.
        Assert.False(ShellViewFocus.IsOtherItemView(DirectUiViewClass, ShellViewClass, OtherView, rootWindow: 0x999, Owner, CurrentView));
    }

    [Fact]
    public void DesktopListView_IsRecognizedAsItemView()
    {
        Assert.True(ShellViewFocus.IsOtherItemView(ListViewClass, ShellViewClass, OtherView, Owner, Owner, CurrentView));
    }

    [Fact]
    public void NoOwner_IsNotSwitch()
    {
        Assert.False(ShellViewFocus.IsOtherItemView(DirectUiViewClass, ShellViewClass, OtherView, Owner, owner: 0, CurrentView));
    }
}
