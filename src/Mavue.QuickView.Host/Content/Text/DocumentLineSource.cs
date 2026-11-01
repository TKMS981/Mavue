using Mavue.Core.Text;

namespace Mavue.QuickView.Host.Content.Text;

/// <summary>
/// Lines of a <see cref="TextDocument"/> for <see cref="TextLinesView"/>: read off the UI thread, one read at a time
/// (a newer request replaces a waiting one), kept in a bounded cache around what is visible. Line counts grow while
/// the document's index is built (<see cref="Refresh"/>).
/// </summary>
internal sealed class DocumentLineSource : ITextLineSource, IDisposable
{
    private readonly TextDocument _document;
    private readonly Dictionary<int, TextLine> _cache = [];
    private readonly CancellationTokenSource _life = new();
    private (int First, int Last)? _waiting;
    private bool _reading;
    private bool _disposed;

    public DocumentLineSource(TextDocument document)
    {
        _document = document;
        LineCount = document.Index.LineCount;
    }

    public event Action? Changed;

    /// <summary>A read failed (the file went away or became unreadable); the view keeps what it has.</summary>
    public event Action<Exception>? Failed;

    public int LineCount { get; private set; }

    public TextDocument Document => _document;

    public TextLine? TryGet(int line) => _cache.GetValueOrDefault(line);

    public void Request(int first, int last)
    {
        if (_disposed || last < first)
        {
            return;
        }

        if (_reading)
        {
            _waiting = (first, last);
            return;
        }

        _ = ReadAsync(first, last);
    }

    /// <summary>Reads lines now and returns when they are in the cache (the first screen).</summary>
    public async Task LoadAsync(int first, int last, CancellationToken cancellation)
    {
        IReadOnlyList<TextLine> lines = await Task.Run(() => _document.ReadLines(first, last - first + 1, cancellation), cancellation);
        Store(first, lines);
    }

    /// <summary>More lines are known (index progress); call on the UI thread.</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        int count = _document.Index.LineCount;
        if (count != LineCount)
        {
            LineCount = count;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _life.Cancel();
        _life.Dispose();
        _cache.Clear();
    }

    private async Task ReadAsync(int first, int last)
    {
        _reading = true;
        try
        {
            CancellationToken cancellation = _life.Token;
            IReadOnlyList<TextLine> lines = await Task.Run(() => _document.ReadLines(first, last - first + 1, cancellation), cancellation);
            if (_disposed)
            {
                return;
            }

            Store(first, lines);
            Changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (!_disposed)
            {
                Failed?.Invoke(ex);
            }
        }
        finally
        {
            _reading = false;
        }

        if (!_disposed && _waiting is { } next)
        {
            _waiting = null;
            if (!Covered(next.First, next.Last))
            {
                _ = ReadAsync(next.First, next.Last);
            }
        }
    }

    private bool Covered(int first, int last)
    {
        for (int line = first; line <= last; line++)
        {
            if (!_cache.ContainsKey(line))
            {
                return false;
            }
        }

        return true;
    }

    private void Store(int first, IReadOnlyList<TextLine> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            _cache[first + i] = lines[i];
        }

        // Bounded: forget the lines farthest from what was just read.
        int max = _document.Limits.MaxCachedLines;
        if (_cache.Count > max)
        {
            int center = first + (lines.Count / 2);
            foreach (int line in _cache.Keys.OrderByDescending(l => Math.Abs(l - center)).Take(_cache.Count - (max * 3 / 4)).ToArray())
            {
                _cache.Remove(line);
            }
        }
    }
}
