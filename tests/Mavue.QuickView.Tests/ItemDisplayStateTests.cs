using Mavue.QuickView.Preview;

namespace Mavue.QuickView.Tests;

/// <summary>
/// What an item has put on screen decides whether "loading" or a late thumbnail may still appear
/// (ARCHITECTURE §4.7.6 item 3): an item's own message such as "unsupported" is never replaced by "loading".
/// </summary>
[Trait("Category", "QuickView")]
public class ItemDisplayStateTests
{
    [Fact]
    public void NewItem_ShowingNothingYet_ClearsThePreviousPicture()
    {
        var state = new ItemDisplayState();
        state.MarkContent(1); // previous item's image

        Assert.True(state.NeedsStaleClear(currentRequest: 2, requestId: 2));
    }

    [Fact]
    public void NewItem_WithItsContent_KeepsIt()
    {
        var state = new ItemDisplayState();
        state.MarkContent(2);

        Assert.False(state.NeedsStaleClear(2, 2));
        Assert.Equal(2, state.ShownRequest);
    }

    [Fact]
    public void SupersededRequest_DoesNotClear()
    {
        // The user moved on again before the grace period ended: the newer request handles its own clearing.
        var state = new ItemDisplayState();
        Assert.False(state.NeedsStaleClear(currentRequest: 3, requestId: 2));
    }

    [Fact]
    public void UnsupportedMessage_IsNotReplacedByLoading_WhenTheThumbnailLookupEndsLater()
    {
        // Unknown file: the decoder reports "unsupported" before the shell thumbnail lookup (none) completes.
        var state = new ItemDisplayState();
        state.MarkMessage(5);

        Assert.False(state.MayShowThumbnail(5));
        Assert.False(state.MayShowLoading(5));
    }

    [Fact]
    public void UnsupportedMessage_IsNotReplacedByLoading_WhenTheStaleImageGraceEndsLater()
    {
        // The message appeared within 120 ms but the item's work (thumbnail lookup) was still running.
        var state = new ItemDisplayState();
        state.MarkContent(4); // previous item
        state.MarkMessage(5);

        Assert.False(state.NeedsStaleClear(5, 5));
    }

    [Fact]
    public void FinishingAfterAMessage_KeepsTheMessageProtected()
    {
        var state = new ItemDisplayState();
        state.MarkMessage(5);
        state.MarkContent(5); // the controller marks the item finished

        Assert.False(state.MayShowLoading(5));
        Assert.False(state.MayShowThumbnail(5));
    }

    [Fact]
    public void CloudNotice_AllowsTheCachedThumbnail_ButNotLoading()
    {
        // Cloud-only file: nothing will be decoded, so "loading" would stay forever; a cached thumbnail is welcome.
        var state = new ItemDisplayState();
        state.MarkNotice(6);

        Assert.True(state.MayShowThumbnail(6));
        Assert.False(state.MayShowLoading(6));
        Assert.False(state.NeedsStaleClear(6, 6));
    }

    [Fact]
    public void NormalDecode_AllowsThumbnailAndLoading()
    {
        var state = new ItemDisplayState();
        Assert.True(state.MayShowThumbnail(7));
        Assert.True(state.MayShowLoading(7));

        state.MarkContent(7); // the thumbnail is on screen; the full image follows
        Assert.True(state.MayShowLoading(7));
    }

    [Fact]
    public void MessagesOfAnEarlierItem_DoNotAffectTheNextItem()
    {
        var state = new ItemDisplayState();
        state.MarkMessage(8);
        state.MarkNotice(9);

        Assert.True(state.MayShowThumbnail(10));
        Assert.True(state.MayShowLoading(10));
        Assert.True(state.NeedsStaleClear(10, 10));
    }

    [Fact]
    public void ReloadingTheSameFile_UsesANewRequest_AndStartsClean()
    {
        // Re-showing the current file (resize, zoom, page) gets a new request id; an old message does not stick.
        var state = new ItemDisplayState();
        state.MarkMessage(11);

        Assert.True(state.MayShowLoading(12));
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var state = new ItemDisplayState();
        state.MarkMessage(13);
        state.MarkNotice(13);
        state.Clear();

        Assert.Equal(0, state.ShownRequest);
        Assert.True(state.MayShowLoading(13));
        Assert.True(state.MayShowThumbnail(13));
        Assert.True(state.NeedsStaleClear(13, 13));
    }

    [Fact]
    public void ShownRequest_FollowsTheLastMark()
    {
        var state = new ItemDisplayState();
        Assert.Equal(0, state.ShownRequest);
        state.MarkContent(1);
        Assert.Equal(1, state.ShownRequest);
        state.MarkMessage(2);
        Assert.Equal(2, state.ShownRequest);
        state.MarkNotice(3);
        Assert.Equal(3, state.ShownRequest);
    }
}
