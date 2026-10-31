using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

/// <summary>Performance tiers and their resource limits (ARCHITECTURE §4.7.4).</summary>
[Trait("Category", "QuickView")]
public class PreviewResourcePolicyTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

    [Theory]
    [InlineData(16, 64 * GiB, false, false, PerformanceTier.High)]      // development PC (7800X3D, 64 GB)
    [InlineData(8, 16 * GiB, false, false, PerformanceTier.High)]
    [InlineData(8, 12 * GiB, false, false, PerformanceTier.High)]       // exactly 12 GiB is enough
    [InlineData(8, (12 * GiB) - 1, false, false, PerformanceTier.Balanced)]
    [InlineData(8, 8 * GiB, false, false, PerformanceTier.Balanced)]
    [InlineData(8, 6 * GiB, false, false, PerformanceTier.Balanced)]    // exactly 6 GiB is not Low
    [InlineData(8, (6 * GiB) - 1, false, false, PerformanceTier.Low)]
    [InlineData(8, 4 * GiB, false, false, PerformanceTier.Low)]
    [InlineData(5, 64 * GiB, false, false, PerformanceTier.High)]
    [InlineData(4, 64 * GiB, false, false, PerformanceTier.Balanced)]
    [InlineData(3, 64 * GiB, false, false, PerformanceTier.Balanced)]
    [InlineData(2, 64 * GiB, false, false, PerformanceTier.Low)]
    [InlineData(1, 64 * GiB, false, false, PerformanceTier.Low)]
    [InlineData(16, 64 * GiB, true, false, PerformanceTier.Low)]        // energy saver wins over hardware
    [InlineData(16, 64 * GiB, false, true, PerformanceTier.Balanced)]   // remote desktop
    [InlineData(16, 64 * GiB, true, true, PerformanceTier.Low)]
    [InlineData(2, 4 * GiB, false, true, PerformanceTier.Low)]
    public void SelectTier_FollowsTheMachine(int processors, long memory, bool energySaver, bool remote, PerformanceTier expected)
    {
        Assert.Equal(expected, PreviewResourcePolicy.SelectTier(new SystemResourceSnapshot(processors, memory, energySaver, remote)));
    }

    [Theory]
    [InlineData(16, 0L, PerformanceTier.High)]     // memory unknown: not lowered
    [InlineData(0, 64 * GiB, PerformanceTier.High)] // processors unknown: not lowered
    [InlineData(0, 0L, PerformanceTier.High)]
    [InlineData(-1, -1L, PerformanceTier.High)]
    [InlineData(2, 0L, PerformanceTier.Low)]       // known facts still count
    public void SelectTier_UnknownFacts_DoNotLowerTheTier(int processors, long memory, PerformanceTier expected)
    {
        Assert.Equal(expected, PreviewResourcePolicy.SelectTier(new SystemResourceSnapshot(processors, memory, false, false)));
    }

    [Fact]
    public void High_KeepsTheMeasuredPocLimits()
    {
        // Behavior on capable machines must not change with the split (docs/QUICKVIEW-POC.md §10).
        PreviewResourcePolicy high = PreviewResourcePolicy.High;
        Assert.Equal(192 * MiB, high.CacheBudgetBytes);
        Assert.Equal(2, high.PrefetchNeighbors);
        Assert.Equal(64 * MiB, high.MaxPrefetchFileBytes);
        Assert.Equal(40_000_000, high.MaxPrefetchSourcePixels);
        Assert.Equal(16_000_000, high.MaxActualSizePrefetchPixels);
        Assert.Equal(8_000_000, high.MaxAnimatedGifPixels);
        Assert.Equal(64 * MiB, high.MaxWholeFileReadBytes);
    }

    [Theory]
    [InlineData(PerformanceTier.Low)]
    [InlineData(PerformanceTier.Balanced)]
    [InlineData(PerformanceTier.High)]
    public void For_ReturnsThatTiersLimits(PerformanceTier tier)
    {
        Assert.Equal(tier, PreviewResourcePolicy.For(tier).Tier);
    }

    [Fact]
    public void For_UnknownTier_UsesHigh()
    {
        Assert.Same(PreviewResourcePolicy.High, PreviewResourcePolicy.For((PerformanceTier)99));
    }

    [Fact]
    public void LowerTiers_NeverAllowMore()
    {
        PreviewResourcePolicy[] ordered = [PreviewResourcePolicy.Low, PreviewResourcePolicy.Balanced, PreviewResourcePolicy.High];
        for (int i = 1; i < ordered.Length; i++)
        {
            PreviewResourcePolicy lower = ordered[i - 1];
            PreviewResourcePolicy higher = ordered[i];
            Assert.True(lower.CacheBudgetBytes <= higher.CacheBudgetBytes);
            Assert.True(lower.PrefetchNeighbors <= higher.PrefetchNeighbors);
            Assert.True(lower.MaxPrefetchFileBytes <= higher.MaxPrefetchFileBytes);
            Assert.True(lower.MaxPrefetchSourcePixels <= higher.MaxPrefetchSourcePixels);
            Assert.True(lower.MaxActualSizePrefetchPixels <= higher.MaxActualSizePrefetchPixels);
            Assert.True(lower.MaxAnimatedGifPixels <= higher.MaxAnimatedGifPixels);
            Assert.True(lower.MaxWholeFileReadBytes <= higher.MaxWholeFileReadBytes);
        }
    }

    [Theory]
    [InlineData(PerformanceTier.Low)]
    [InlineData(PerformanceTier.Balanced)]
    [InlineData(PerformanceTier.High)]
    public void EveryTier_HasUsableLimits(PerformanceTier tier)
    {
        PreviewResourcePolicy policy = PreviewResourcePolicy.For(tier);
        Assert.True(policy.CacheBudgetBytes > 0); // PreviewCache rejects a budget of 0
        Assert.InRange(policy.PrefetchNeighbors, 1, 2);
        Assert.True(policy.MaxPrefetchFileBytes > 0);
        Assert.True(policy.MaxPrefetchSourcePixels > 0);
        Assert.True(policy.MaxActualSizePrefetchPixels > 0);
        Assert.True(policy.MaxAnimatedGifPixels > 0);
        Assert.True(policy.MaxWholeFileReadBytes > 0);
    }

    [Fact]
    public void Low_PrefetchesOnlyTheNextItem()
    {
        Assert.Equal(1, PreviewResourcePolicy.Low.PrefetchNeighbors);
    }
}
