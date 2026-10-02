namespace Mavue.QuickView.Trigger;

/// <summary>
/// Decides whether a focus change inside Explorer means "the user is now looking at another shell view"
/// (typically another Windows 11 tab of the same window), so Quick View should follow that view's
/// selection. Pure logic over window classes and handles, testable without Explorer.
/// </summary>
public static class ShellViewFocus
{
    /// <summary>
    /// True if the newly focused window is the item view of a shell view other than
    /// <paramref name="currentView"/>, inside the Explorer window <paramref name="owner"/>.
    /// </summary>
    /// <param name="focusClass">Class of the window that received focus.</param>
    /// <param name="parentClass">Class of its parent.</param>
    /// <param name="parentWindow">Its parent (the SHELLDLL_DefView of that view).</param>
    /// <param name="rootWindow">Its top-level window.</param>
    /// <param name="owner">Explorer window Quick View belongs to.</param>
    /// <param name="currentView">SHELLDLL_DefView Quick View currently follows.</param>
    public static bool IsOtherItemView(string focusClass, string parentClass, nint parentWindow, nint rootWindow, nint owner, nint currentView)
    {
        ArgumentNullException.ThrowIfNull(focusClass);
        ArgumentNullException.ThrowIfNull(parentClass);
        bool itemView =
            string.Equals(parentClass, SpaceKeyClassifier.ShellViewClass, StringComparison.Ordinal) &&
            (string.Equals(focusClass, SpaceKeyClassifier.DirectUiViewClass, StringComparison.Ordinal) ||
             string.Equals(focusClass, SpaceKeyClassifier.ListViewClass, StringComparison.Ordinal));

        return itemView && owner != 0 && rootWindow == owner && parentWindow != 0 && parentWindow != currentView;
    }
}
