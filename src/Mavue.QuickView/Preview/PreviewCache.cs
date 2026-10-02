namespace Mavue.QuickView.Preview;

/// <summary>
/// Identifies a decoded preview. Size and last-write time make a changed file a cache miss;
/// the viewport makes a resized window decode again. Paths compare case-insensitively.
/// </summary>
public readonly record struct PreviewKey
{
    private PreviewKey(string normalizedPath, long length, DateTime lastWriteUtc, uint viewportWidth, uint viewportHeight)
    {
        NormalizedPath = normalizedPath;
        Length = length;
        LastWriteUtc = lastWriteUtc;
        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;
    }

    public string NormalizedPath { get; }

    public long Length { get; }

    public DateTime LastWriteUtc { get; }

    public uint ViewportWidth { get; }

    public uint ViewportHeight { get; }

    public static PreviewKey Create(string path, long length, DateTime lastWriteUtc, uint viewportWidth, uint viewportHeight)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new PreviewKey(path.ToUpperInvariant(), length, lastWriteUtc, viewportWidth, viewportHeight);
    }
}

/// <summary>
/// Least-recently-used cache of decoded previews with a byte budget, used to show the neighbor items
/// instantly when the user steps through Explorer. Owns its values: evicted or replaced values are passed
/// to the eviction callback (to dispose native bitmaps). Not thread-safe; used from the UI thread.
/// </summary>
public sealed class PreviewCache<T> : IDisposable
    where T : class
{
    private readonly long _budgetBytes;
    private readonly Func<T, long> _sizeOf;
    private readonly Action<T>? _onEvicted;
    private readonly Dictionary<PreviewKey, LinkedListNode<(PreviewKey Key, T Value, long Bytes)>> _map = [];
    private readonly LinkedList<(PreviewKey Key, T Value, long Bytes)> _lru = new(); // first = most recent

    /// <param name="budgetBytes">Maximum total size of cached values.</param>
    /// <param name="sizeOf">Size of a value in bytes.</param>
    /// <param name="onEvicted">Called for values that leave the cache (including on <see cref="Dispose"/>).</param>
    public PreviewCache(long budgetBytes, Func<T, long> sizeOf, Action<T>? onEvicted = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        ArgumentNullException.ThrowIfNull(sizeOf);
        _budgetBytes = budgetBytes;
        _sizeOf = sizeOf;
        _onEvicted = onEvicted;
    }

    public int Count => _map.Count;

    public long Bytes { get; private set; }

    public bool Contains(PreviewKey key) => _map.ContainsKey(key);

    /// <summary>Returns the value and marks it most recently used.</summary>
    public bool TryGet(PreviewKey key, out T? value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Adds or replaces a value, then evicts least recently used values until within budget.
    /// A value larger than the whole budget is not kept (it is evicted immediately).
    /// </summary>
    public void Add(PreviewKey key, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_map.TryGetValue(key, out var existing))
        {
            if (ReferenceEquals(existing.Value.Value, value))
            {
                TryGet(key, out _);
                return;
            }

            Remove(existing);
        }

        long bytes = Math.Max(0, _sizeOf(value));
        if (bytes > _budgetBytes)
        {
            _onEvicted?.Invoke(value);
            return;
        }

        _map[key] = _lru.AddFirst((key, value, bytes));
        Bytes += bytes;
        while (Bytes > _budgetBytes && _lru.Last is { } oldest)
        {
            Remove(oldest);
        }
    }

    /// <summary>Removes everything (e.g. when Quick View closes, to release memory while idle).</summary>
    public void Clear()
    {
        while (_lru.First is { } node)
        {
            Remove(node);
        }
    }

    public void Dispose() => Clear();

    private void Remove(LinkedListNode<(PreviewKey Key, T Value, long Bytes)> node)
    {
        _lru.Remove(node);
        _map.Remove(node.Value.Key);
        Bytes -= node.Value.Bytes;
        _onEvicted?.Invoke(node.Value.Value);
    }
}
