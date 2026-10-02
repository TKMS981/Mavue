using Mavue.QuickView.Preview;

namespace Mavue.QuickView.Tests;

[Trait("Category", "QuickView")]
public class PreviewCacheTests
{
    private static readonly DateTime T0 = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Blob(string name, long size)
    {
        public string Name { get; } = name;

        public long Size { get; } = size;
    }

    private static PreviewKey Key(string path, long length = 10, DateTime? written = null, uint w = 1920, uint h = 1080) =>
        PreviewKey.Create(path, length, written ?? T0, w, h);

    private static (PreviewCache<Blob> Cache, List<string> Evicted) NewCache(long budget)
    {
        var evicted = new List<string>();
        return (new PreviewCache<Blob>(budget, b => b.Size, b => evicted.Add(b.Name)), evicted);
    }

    [Fact]
    public void Hit_ReturnsValue()
    {
        (var cache, _) = NewCache(100);
        var a = new Blob("a", 10);
        cache.Add(Key(@"C:\x\a.jpg"), a);

        Assert.True(cache.TryGet(Key(@"C:\x\a.jpg"), out Blob? hit));
        Assert.Same(a, hit);
    }

    [Fact]
    public void Keys_AreCaseInsensitiveForPaths()
    {
        (var cache, _) = NewCache(100);
        cache.Add(Key(@"C:\X\A.JPG"), new Blob("a", 10));
        Assert.True(cache.Contains(Key(@"c:\x\a.jpg")));
    }

    [Theory]
    [InlineData(11, 0, 1920u)]   // file size changed
    [InlineData(10, 1, 1920u)]   // file rewritten
    [InlineData(10, 0, 1280u)]   // window resized
    public void ChangedFileOrViewport_IsMiss(long length, int secondsLater, uint width)
    {
        (var cache, _) = NewCache(100);
        cache.Add(Key(@"C:\x\a.jpg"), new Blob("a", 10));
        Assert.False(cache.Contains(Key(@"C:\x\a.jpg", length, T0.AddSeconds(secondsLater), width)));
    }

    [Fact]
    public void OverBudget_EvictsLeastRecentlyUsed()
    {
        (var cache, var evicted) = NewCache(30);
        cache.Add(Key("a"), new Blob("a", 10));
        cache.Add(Key("b"), new Blob("b", 10));
        cache.Add(Key("c"), new Blob("c", 10));
        cache.TryGet(Key("a"), out _); // a becomes most recent; b is now the oldest

        cache.Add(Key("d"), new Blob("d", 10));

        Assert.Equal(["b"], evicted);
        Assert.Equal(30, cache.Bytes);
        Assert.True(cache.Contains(Key("a")));
        Assert.False(cache.Contains(Key("b")));
    }

    [Fact]
    public void ValueLargerThanBudget_IsNotKept()
    {
        (var cache, var evicted) = NewCache(30);
        cache.Add(Key("a"), new Blob("a", 10));
        cache.Add(Key("huge"), new Blob("huge", 31));

        Assert.Equal(["huge"], evicted);
        Assert.True(cache.Contains(Key("a")));
        Assert.Equal(10, cache.Bytes);
    }

    [Fact]
    public void Replacing_EvictsOldValue()
    {
        (var cache, var evicted) = NewCache(100);
        cache.Add(Key("a"), new Blob("old", 10));
        cache.Add(Key("a"), new Blob("new", 20));

        Assert.Equal(["old"], evicted);
        Assert.Equal(20, cache.Bytes);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void AddingSameInstanceAgain_DoesNotEvictIt()
    {
        (var cache, var evicted) = NewCache(100);
        var a = new Blob("a", 10);
        cache.Add(Key("a"), a);
        cache.Add(Key("a"), a);

        Assert.Empty(evicted);
        Assert.Equal(10, cache.Bytes);
    }

    [Fact]
    public void Clear_EvictsEverything()
    {
        (var cache, var evicted) = NewCache(100);
        cache.Add(Key("a"), new Blob("a", 10));
        cache.Add(Key("b"), new Blob("b", 10));

        cache.Clear();

        Assert.Equal(2, evicted.Count);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
    }
}
