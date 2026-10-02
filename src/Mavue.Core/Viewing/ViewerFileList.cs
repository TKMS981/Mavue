namespace Mavue.Core.Viewing;

/// <summary>
/// The files the viewer steps through with ←/→: the files that were opened together, or, when one file was opened,
/// the viewable files of its folder in Explorer's name order. Stepping stops at the first and last file.
/// </summary>
public sealed class ViewerFileList
{
    private readonly List<string> _items;

    private ViewerFileList(List<string> items, int index)
    {
        _items = items;
        Index = items.Count == 0 ? -1 : Math.Clamp(index, 0, items.Count - 1);
    }

    public IReadOnlyList<string> Items => _items;

    /// <summary>Position of <see cref="Current"/>, or -1 when the list is empty.</summary>
    public int Index { get; private set; }

    public string? Current => Index >= 0 ? _items[Index] : null;

    public int Count => _items.Count;

    public bool CanStep(int delta) => delta != 0 && Index >= 0 && Index + delta >= 0 && Index + delta < _items.Count;

    /// <summary>Moves by <paramref name="delta"/> if the target exists; returns false (and does nothing) otherwise.</summary>
    public bool TryStep(int delta)
    {
        if (!CanStep(delta))
        {
            return false;
        }

        Index += delta;
        return true;
    }

    /// <summary>Files opened together, in the given order (duplicates removed).</summary>
    public static ViewerFileList FromFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var items = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ViewerFileList(items, 0);
    }

    /// <summary>
    /// <paramref name="file"/> among the viewable files of its folder (sorted like Explorer's name column).
    /// <paramref name="folderFiles"/> is the folder listing; the file itself is kept even if its extension is not
    /// in <paramref name="include"/>.
    /// </summary>
    public static ViewerFileList FromFolder(string file, IEnumerable<string> folderFiles, Func<string, bool> include)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(folderFiles);
        ArgumentNullException.ThrowIfNull(include);
        var items = folderFiles
            .Where(path => include(path) || string.Equals(path, file, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, NaturalStringComparer.Instance)
            .ToList();
        int index = items.FindIndex(path => string.Equals(path, file, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            items.Insert(0, file);
            index = 0;
        }

        return new ViewerFileList(items, index);
    }
}

/// <summary>
/// Name order close to Explorer's: runs of digits compare by value ("2" before "10"), everything else ordinally,
/// ignoring case (deterministic; Explorer additionally applies the user's locale).
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0;
        int j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int startX = i;
                int startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                ReadOnlySpan<char> a = x.AsSpan(startX, i - startX).TrimStart('0');
                ReadOnlySpan<char> b = y.AsSpan(startY, j - startY).TrimStart('0');
                int byValue = a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.SequenceCompareTo(b);
                if (byValue != 0)
                {
                    return byValue;
                }

                continue;
            }

            int byText = string.Compare(x, i, y, j, 1, StringComparison.OrdinalIgnoreCase);
            if (byText != 0)
            {
                return byText;
            }

            i++;
            j++;
        }

        int byLength = (x.Length - i).CompareTo(y.Length - j);
        return byLength != 0 ? byLength : string.Compare(x, y, StringComparison.Ordinal);
    }
}
