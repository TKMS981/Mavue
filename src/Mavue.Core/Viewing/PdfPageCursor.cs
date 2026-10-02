namespace Mavue.Core.Viewing;

/// <summary>
/// Current page of the PDF shown in Quick View. Page steps never leave the document: a step past the first
/// or last page is refused (no wrap-around), mirroring how stepping past the end of a multi-selection behaves.
/// The page count is learned from the first render; until then (Count = 0) no step is possible.
/// </summary>
public sealed class PdfPageCursor
{
    /// <summary>Zero-based page index.</summary>
    public int Index { get; private set; }

    /// <summary>Number of pages, or 0 while unknown.</summary>
    public int Count { get; private set; }

    /// <summary>True when there is more than one page to step through.</summary>
    public bool IsMultiPage => Count > 1;

    public bool CanStep(int delta) => IsMultiPage && delta != 0 && Index + delta >= 0 && Index + delta < Count;

    /// <summary>Moves by <paramref name="delta"/> pages if the target exists; returns false (and does nothing) otherwise.</summary>
    public bool TryStep(int delta)
    {
        if (!CanStep(delta))
        {
            return false;
        }

        Index += delta;
        return true;
    }

    /// <summary>Records the page count reported by the renderer; keeps the index inside the document.</summary>
    public void SetCount(int count)
    {
        Count = Math.Max(0, count);
        if (Count > 0 && Index >= Count)
        {
            Index = Count - 1;
        }
    }

    /// <summary>Back to the first page of an unknown document (another file is shown, or Quick View closed).</summary>
    public void Reset()
    {
        Index = 0;
        Count = 0;
    }
}
