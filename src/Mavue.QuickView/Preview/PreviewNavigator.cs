namespace Mavue.QuickView.Preview;

/// <summary>How Quick View relates to the Explorer selection.</summary>
public enum NavigationMode
{
    /// <summary>Nothing is shown.</summary>
    None = 0,

    /// <summary>
    /// One item selected: Quick View follows Explorer's selection. Arrow keys stay with Explorer
    /// (in Explorer's own layout order: list, details, icons, groups).
    /// </summary>
    SingleItem,

    /// <summary>
    /// Several items selected: Quick View steps through that selection with ←/→ without changing it
    /// (like Finder's Quick Look). ↑/↓ go to Explorer and usually collapse the selection to one item.
    /// </summary>
    MultipleItems,
}

/// <summary>What the host should do after a navigation input.</summary>
public enum NavigationAction
{
    /// <summary>Nothing changes on screen.</summary>
    None = 0,

    /// <summary>Show <see cref="NavigationResult.Path"/>.</summary>
    Show,

    /// <summary>Close Quick View (selection became empty).</summary>
    Hide,

    /// <summary>Single mode with Quick View focused: ask Explorer to move its selection by <see cref="NavigationResult.Delta"/>.</summary>
    MoveExplorerSelection,
}

/// <summary>Outcome of a navigation step.</summary>
public readonly record struct NavigationResult(NavigationAction Action, string? Path = null, int Delta = 0)
{
    public static NavigationResult Nothing => default;

    public static NavigationResult Hide => new(NavigationAction.Hide);
}

/// <summary>
/// Pure state machine deciding which item Quick View shows as the Explorer selection changes
/// (docs/QUICKVIEW-POC.md §7). Paths are compared case-insensitively (Windows file system).
/// Not thread-safe; owned by the UI thread.
/// </summary>
public sealed class PreviewNavigator
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private List<string> _items = [];
    private string? _explorerFocus;

    public NavigationMode Mode { get; private set; }

    /// <summary>Item currently shown, or null.</summary>
    public string? Current { get; private set; }

    /// <summary>Zero-based position of <see cref="Current"/> within <see cref="Items"/>.</summary>
    public int Index { get; private set; } = -1;

    /// <summary>Items Quick View can step through (the selection in display order).</summary>
    public IReadOnlyList<string> Items => _items;

    /// <summary>Starts from the selection at the moment Space was pressed.</summary>
    /// <param name="selection">Selected paths in display order.</param>
    /// <param name="focused">Path of the focused item, if any.</param>
    public NavigationResult Start(IReadOnlyList<string> selection, string? focused)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Reset();
        if (selection.Count == 0)
        {
            return NavigationResult.Nothing;
        }

        Adopt(selection, PreferredIndex(selection, focused, fallback: null));
        _explorerFocus = focused;
        return new NavigationResult(NavigationAction.Show, Current);
    }

    /// <summary>Explorer reported a selection change while Quick View is visible.</summary>
    public NavigationResult OnSelectionChanged(IReadOnlyList<string> selection, string? focused)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (Mode == NavigationMode.None)
        {
            return NavigationResult.Nothing;
        }

        if (selection.Count == 0)
        {
            Reset();
            return NavigationResult.Hide;
        }

        // Same multi-selection: keep the stepping position. Follow Explorer's focus only if Explorer
        // actually moved it (e.g. Ctrl+arrow); a repeated notification must not undo our ←/→ steps.
        if (Mode == NavigationMode.MultipleItems && selection.Count > 1 && SameSet(selection, _items))
        {
            bool focusMoved = focused is not null && !PathComparer.Equals(focused, _explorerFocus);
            _explorerFocus = focused ?? _explorerFocus;
            int focusedIndex = focusMoved ? IndexOf(_items, focused!) : -1;
            return focusedIndex >= 0 && focusedIndex != Index ? MoveTo(focusedIndex) : NavigationResult.Nothing;
        }

        string? previous = Current;
        _explorerFocus = focused;
        Adopt(selection, PreferredIndex(selection, focused, fallback: previous));
        return PathComparer.Equals(previous, Current)
            ? NavigationResult.Nothing
            : new NavigationResult(NavigationAction.Show, Current);
    }

    /// <summary>
    /// A step request (←/→ or ↑/↓ as -1/+1). In multiple mode moves within the selection (clamped);
    /// in single mode asks Explorer to move its selection, which comes back as a selection change.
    /// </summary>
    public NavigationResult Step(int delta)
    {
        if (delta == 0 || Mode == NavigationMode.None)
        {
            return NavigationResult.Nothing;
        }

        if (Mode == NavigationMode.SingleItem)
        {
            return new NavigationResult(NavigationAction.MoveExplorerSelection, Delta: Math.Sign(delta));
        }

        int target = Math.Clamp(Index + Math.Sign(delta), 0, _items.Count - 1);
        return target == Index ? NavigationResult.Nothing : MoveTo(target);
    }

    /// <summary>Quick View closed.</summary>
    public void Reset()
    {
        _items = [];
        _explorerFocus = null;
        Mode = NavigationMode.None;
        Current = null;
        Index = -1;
    }

    private NavigationResult MoveTo(int index)
    {
        Index = index;
        Current = _items[index];
        return new NavigationResult(NavigationAction.Show, Current);
    }

    private void Adopt(IReadOnlyList<string> selection, int index)
    {
        _items = [.. selection];
        Mode = _items.Count > 1 ? NavigationMode.MultipleItems : NavigationMode.SingleItem;
        Index = index;
        Current = _items[index];
    }

    /// <summary>Focused item if selected, else the previously shown item if still selected, else the first.</summary>
    private static int PreferredIndex(IReadOnlyList<string> selection, string? focused, string? fallback)
    {
        int index = focused is null ? -1 : IndexOf(selection, focused);
        if (index < 0 && fallback is not null)
        {
            index = IndexOf(selection, fallback);
        }

        return Math.Max(index, 0);
    }

    private static int IndexOf(IReadOnlyList<string> items, string path)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (PathComparer.Equals(items[i], path))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool SameSet(IReadOnlyList<string> a, List<string> b) =>
        a.Count == b.Count && new HashSet<string>(a, PathComparer).SetEquals(b);
}
