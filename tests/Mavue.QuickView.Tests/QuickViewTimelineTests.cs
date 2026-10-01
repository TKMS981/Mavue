using System.Text.Json;
using Mavue.QuickView.Diagnostics;

namespace Mavue.QuickView.Tests;

[Trait("Category", "QuickView")]
public sealed class QuickViewTimelineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mavue-timeline-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Marks_AreWrittenAsJsonLinesWithQpcAndDetails()
    {
        string path = Path.Combine(_dir, "timing.jsonl");
        long t = QuickViewTimeline.Now;
        await using (var timeline = new QuickViewTimeline(path))
        {
            timeline.Mark(1, "hook", t, new Dictionary<string, object?> { ["injected"] = true });
            timeline.Mark(1, "full-set", t + 10, new Dictionary<string, object?> { ["decodedWidth"] = 2025u, ["decodeMs"] = 12.5, ["decoder"] = "winrt", ["error"] = null });
        }

        string[] lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(2, lines.Length);

        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal(1, first.RootElement.GetProperty("request").GetInt64());
        Assert.Equal("hook", first.RootElement.GetProperty("mark").GetString());
        Assert.Equal(t, first.RootElement.GetProperty("qpc").GetInt64());
        Assert.Equal(System.Diagnostics.Stopwatch.Frequency, first.RootElement.GetProperty("freq").GetInt64());
        Assert.True(first.RootElement.GetProperty("injected").GetBoolean());

        using JsonDocument second = JsonDocument.Parse(lines[1]);
        Assert.Equal(2025, second.RootElement.GetProperty("decodedWidth").GetInt32());
        Assert.Equal(12.5, second.RootElement.GetProperty("decodeMs").GetDouble());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task NonAsciiDetails_StayReadable()
    {
        string path = Path.Combine(_dir, "unicode.jsonl");
        await using (var timeline = new QuickViewTimeline(path))
        {
            timeline.Mark(0, "note", QuickViewTimeline.Now, new Dictionary<string, object?> { ["text"] = "日本語" });
        }

        Assert.Contains("日本語", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutPath_MarksAreAcceptedAndNothingIsWritten()
    {
        await using var timeline = new QuickViewTimeline(null);
        timeline.Mark(1, "hook", QuickViewTimeline.Now);
        Assert.Empty(Directory.GetFiles(_dir));
    }
}
