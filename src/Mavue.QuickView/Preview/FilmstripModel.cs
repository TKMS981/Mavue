namespace Mavue.QuickView.Preview;

/// <summary>One file of the filmstrip and its position in Explorer's view (display order).</summary>
public sealed record FilmstripItem(int ViewIndex, string Path);

/// <summary>What the host should do after the shown item changed.</summary>
public enum FilmstripUpdate
{
    /// <summary>Nothing is loaded (the filmstrip is empty or off).</summary>
    None = 0,

    /// <summary>The current item is in the loaded range: only the highlight moves.</summary>
    Moved,

    /// <summary>Read a new range of Explorer's view around the current item.</summary>
    NeedsReload,
}

/// <summary>
/// The filmstrip's items (docs/ARCHITECTURE.md §4.7.8): either a window of Explorer's view around the shown file, in
/// Explorer's display order, or the multi-selection being stepped through. Only a bounded range of a folder is ever
/// read (folders can hold hundreds of thousands of items); it is read again when the shown file nears an end of the
/// range. Pure and UI-thread owned; paths compare case-insensitively.
/// </summary>
public sealed class FilmstripModel
{
    /// <summary>Items read on each side of the shown file.</summary>
    public const int DefaultRadius = 60;

    /// <summary>The range is read again when the shown file is this close to one of its ends (and more items exist there).</summary>
    public const int ReloadMargin = 15;

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private List<FilmstripItem> _items = [];

    public IReadOnlyList<FilmstripItem> Items => _items;

    /// <summary>Position of the shown file in <see cref="Items"/>, or -1.</summary>
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>The items are the multi-selection (not a range of the folder).</summary>
    public bool IsSelection { get; private set; }

    /// <summary>Items in Explorer's whole view (0 for a selection).</summary>
    public int ViewCount { get; private set; }

    /// <summary>The range of the view to read around <paramref name="focused"/> (clamped to the view).</summary>
    public static (int First, int Count) RangeAround(int focused, int total, int radius = DefaultRadius)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        if (total <= 0)
        {
            return (0, 0);
        }

        int center = Math.Clamp(focused, 0, total - 1);
        int first = Math.Max(0, center - radius);
        int last = Math.Min(total - 1, center + radius);
        return (first, last - first + 1);
    }

    /// <summary>Takes a range read from Explorer's view; items without a file-system path are already left out.</summary>
    public void LoadViewRange(IReadOnlyList<FilmstripItem> items, int viewCount, string? current)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items.OrderBy(i => i.ViewIndex)];
        IsSelection = false;
        ViewCount = Math.Max(viewCount, _items.Count == 0 ? 0 : _items[^1].ViewIndex + 1);
        CurrentIndex = current is null ? -1 : IndexOf(current);
    }

    /// <summary>Shows the multi-selection being stepped through.</summary>
    public void LoadSelection(IReadOnlyList<string> selection, string? current)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _items = [.. selection.Select((path, index) => new FilmstripItem(index, path))];
        IsSelection = true;
        ViewCount = 0;
        CurrentIndex = current is null ? -1 : IndexOf(current);
    }

    /// <summary>The shown file changed: moves the highlight, or asks for a new range of the view.</summary>
    public FilmstripUpdate Track(string current)
    {
        ArgumentException.ThrowIfNullOrEmpty(current);
        if (_items.Count == 0)
        {
            return IsSelection ? FilmstripUpdate.None : FilmstripUpdate.NeedsReload;
        }

        CurrentIndex = IndexOf(current);
        if (IsSelection)
        {
            return FilmstripUpdate.Moved;
        }

        if (CurrentIndex < 0)
        {
            return FilmstripUpdate.NeedsReload;
        }

        bool moreBefore = _items[0].ViewIndex > 0;
        bool moreAfter = _items[^1].ViewIndex < ViewCount - 1;
        bool nearStart = CurrentIndex < ReloadMargin && moreBefore;
        bool nearEnd = CurrentIndex >= _items.Count - ReloadMargin && moreAfter;
        return nearStart || nearEnd ? FilmstripUpdate.NeedsReload : FilmstripUpdate.Moved;
    }

    /// <summary>The loaded item <paramref name="delta"/> places from the shown one, or null at an end.</summary>
    public FilmstripItem? Neighbor(int delta)
    {
        int index = CurrentIndex + delta;
        return CurrentIndex >= 0 && index >= 0 && index < _items.Count ? _items[index] : null;
    }

    /// <summary>Quick View closed or the filmstrip was turned off.</summary>
    public void Clear()
    {
        _items = [];
        CurrentIndex = -1;
        IsSelection = false;
        ViewCount = 0;
    }

    private int IndexOf(string path)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (PathComparer.Equals(_items[i].Path, path))
            {
                return i;
            }
        }

        return -1;
    }
}
