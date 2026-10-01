using Mavue.QuickView.Trigger;

namespace Mavue.QuickView.Tests;

[Trait("Category", "QuickView")]
public class TypingTrackerTests
{
    private const long Frequency = 1000; // timestamps in milliseconds
    private const int VkA = 0x41;
    private const int VkDigit1 = 0x31;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkBack = 0x08;
    private const int VkF5 = 0x74;
    private const int VkProcessKey = 0xE5;
    private static readonly nint ItemView = 100;

    [Fact]
    public void NoTyping_ReportsNothing()
    {
        var tracker = new TypingTracker(Frequency);

        Assert.Null(tracker.SinceLastCharacterKey(5000));
        Assert.Equal(0, tracker.PendingCharactersSinceCommit);
    }

    [Fact]
    public void CharacterKey_StartsTypeAheadTimer()
    {
        var tracker = new TypingTracker(Frequency);
        tracker.OnKeyDown(VkA, ctrlOrAltHeld: false, timestamp: 1000, ItemView);

        Assert.Equal(TimeSpan.FromMilliseconds(250), tracker.SinceLastCharacterKey(1250));
        Assert.Equal(1, tracker.PendingCharactersSinceCommit);
    }

    [Fact]
    public void Shortcuts_AreNotTyping()
    {
        // Ctrl+A (select all) or Alt+1 must not make the next Space look like type-ahead.
        var tracker = new TypingTracker(Frequency);
        tracker.OnKeyDown(VkA, ctrlOrAltHeld: true, timestamp: 1000, ItemView);
        tracker.OnKeyDown(VkDigit1, ctrlOrAltHeld: true, timestamp: 1001, ItemView);

        Assert.Null(tracker.SinceLastCharacterKey(1100));
        Assert.Equal(0, tracker.PendingCharactersSinceCommit);
    }

    [Fact]
    public void NonCharacterKeys_DoNotCountAsTyping()
    {
        var tracker = new TypingTracker(Frequency);
        tracker.OnKeyDown(VkF5, ctrlOrAltHeld: false, timestamp: 1000, ItemView);

        Assert.Null(tracker.SinceLastCharacterKey(1100));
    }

    [Theory]
    [InlineData(VkReturn)]
    [InlineData(VkEscape)]
    public void CommitKeys_EndPossibleImeComposition(int commitKey)
    {
        var tracker = new TypingTracker(Frequency);
        tracker.OnKeyDown(VkA, false, 1000, ItemView);
        tracker.OnKeyDown(VkA, false, 1100, ItemView);
        tracker.OnKeyDown(commitKey, false, 1200, ItemView);

        Assert.Equal(0, tracker.PendingCharactersSinceCommit);
    }

    [Fact]
    public void FocusChange_ResetsPendingComposition()
    {
        var tracker = new TypingTracker(Frequency);
        tracker.OnKeyDown(VkA, false, 1000, ItemView);
        tracker.OnKeyDown(VkA, false, 1100, focusWindow: 200);

        Assert.Equal(1, tracker.PendingCharactersSinceCommit);
    }

    [Fact]
    public void Backspace_KeepsCompositionAndRefreshesTimer()
    {
        var tracker = new TypingTracker(Frequency);
        tracker.OnKeyDown(VkA, false, 1000, ItemView);
        tracker.OnKeyDown(VkBack, false, 1500, ItemView);

        Assert.Equal(1, tracker.PendingCharactersSinceCommit);
        Assert.Equal(TimeSpan.FromMilliseconds(100), tracker.SinceLastCharacterKey(1600));
    }

    [Theory]
    [InlineData(0x30, true)]
    [InlineData(0x5A, true)]
    [InlineData(0x60, true)]
    [InlineData(0xBA, true)]
    [InlineData(0xDE, true)]
    [InlineData(0xE2, true)]
    [InlineData(VkProcessKey, true)]
    [InlineData(0x20, false)] // Space itself
    [InlineData(0x25, false)] // Left arrow
    [InlineData(0x70, false)] // F1
    [InlineData(0x10, false)] // Shift
    public void IsCharacterKey(int vk, bool expected)
    {
        Assert.Equal(expected, TypingTracker.IsCharacterKey(vk));
    }

    [Fact]
    public void TrackerAndClassifier_TypeAheadThenSpace_PassesThrough()
    {
        var tracker = new TypingTracker(Frequency);
        var classifier = new SpaceKeyClassifier(new SpaceKeyClassifierOptions());
        tracker.OnKeyDown(VkA, false, 10_000, ItemView);

        var context = new SpaceKeyContext(
            SpaceKeyClassifier.ExplorerWindowClass,
            SpaceKeyClassifier.DirectUiViewClass,
            SpaceKeyClassifier.ShellViewClass,
            HasModifiers: false,
            ImeComposing: false,
            SinceLastCharacterKey: tracker.SinceLastCharacterKey(10_300));

        Assert.Equal(SpaceKeyDecision.PassThrough, classifier.Classify(context, out PassThroughReason reason));
        Assert.Equal(PassThroughReason.TypeAhead, reason);
    }
}
