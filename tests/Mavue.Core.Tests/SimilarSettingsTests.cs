using Mavue.Core.Settings;
using Mavue.Core.Similarity;

namespace Mavue.Core.Tests;

/// <summary>Similar-search settings, the cache file helpers and the export protocol (ARCHITECTURE §4.7.14–15).</summary>
[Trait("Category", "QuickView")]
public sealed class SimilarSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "similar-settings-" + Guid.NewGuid().ToString("N"));

    public SimilarSettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Sensitivities_MapToIncreasingThresholds()
    {
        Assert.True(SimilarSearchSettings.Threshold(SimilarSensitivity.Loose) < SimilarSearchSettings.Threshold(SimilarSensitivity.Normal));
        Assert.True(SimilarSearchSettings.Threshold(SimilarSensitivity.Normal) < SimilarSearchSettings.Threshold(SimilarSensitivity.Strict));
        Assert.Equal(72, SimilarSearchSettings.Threshold(SimilarSensitivity.Normal));
    }

    [Fact]
    public async Task Settings_DefaultsRoundTrip_AndBadValuesFallBack()
    {
        var defaults = new QuickViewSettings();
        Assert.Equal(SimilarSensitivity.Normal, defaults.SimilarSensitivity);
        Assert.False(defaults.SimilarIncludeSubfolders);
        Assert.True(defaults.SimilarDetectRotations);

        string path = Path.Combine(_folder, "settings.json");
        await (defaults with { SimilarSensitivity = SimilarSensitivity.Strict, SimilarIncludeSubfolders = true, SimilarDetectRotations = false }).SaveAsync(path, TestContext.Current.CancellationToken);
        QuickViewSettings loaded = QuickViewSettings.Load(path);
        Assert.Equal((SimilarSensitivity.Strict, true, false), (loaded.SimilarSensitivity, loaded.SimilarIncludeSubfolders, loaded.SimilarDetectRotations));

        File.WriteAllText(path, "{\"similarSensitivity\": 42}");
        Assert.Equal(SimilarSensitivity.Normal, QuickViewSettings.Load(path).SimilarSensitivity);
    }

    [Fact]
    public void CacheFile_SizeAndDelete()
    {
        string cache = Path.Combine(_folder, "signatures.bin");
        Assert.Equal(0, SimilarSearchSettings.CacheBytes(cache));
        Assert.True(SimilarSearchSettings.DeleteCache(cache)); // nothing to delete is fine
        File.WriteAllBytes(cache, new byte[1234]);
        File.WriteAllBytes(cache + ".99.tmp", new byte[10]);
        Assert.Equal(1234, SimilarSearchSettings.CacheBytes(cache));
        Assert.True(SimilarSearchSettings.DeleteCache(cache));
        Assert.False(File.Exists(cache));
        Assert.False(File.Exists(cache + ".99.tmp"));

        File.WriteAllBytes(cache, new byte[5]);
        using (File.Open(cache, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(SimilarSearchSettings.DeleteCache(cache)); // in use: reported, not thrown
        }
    }

    [Fact]
    public void Protocols_RoundTrip()
    {
        var request = new SimilarSearchRequest("a", "b", true, 10, 72, 5, 64, "c", 2, DetectRotations: true);
        string json = System.Text.Json.JsonSerializer.Serialize(request, SimilarSearchJson.Default.SimilarSearchRequest);
        Assert.Equal(request, System.Text.Json.JsonSerializer.Deserialize(json, SimilarSearchJson.Default.SimilarSearchRequest));

        var done = new Workers.CleanExportMessage { Type = Workers.CleanExportMessage.DoneType, Converted = true, Format = "Jpeg", RemovedBytes = 9 };
        Assert.Equal(done, Workers.CleanExportMessage.FromJson(done.ToJson()));
        Assert.Null(Workers.CleanExportMessage.FromJson("nope"));
    }
}
