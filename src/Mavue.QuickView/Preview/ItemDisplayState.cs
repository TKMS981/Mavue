namespace Mavue.QuickView.Preview;

/// <summary>
/// What the current Quick View item has put on screen, by request id. Decides whether a placeholder (the cached
/// shell thumbnail or "loading") may still be shown, so that an item's own result — its content, or a message such
/// as "unsupported" — is never replaced by a placeholder that arrives later, and the previous item's picture is
/// never left under the new file name. UI thread only.
/// </summary>
public sealed class ItemDisplayState
{
    private long _shown;   // request whose content, thumbnail or message is on screen
    private long _final;   // request whose final message is on screen (nothing else of it follows)
    private long _notice;  // request whose notice is on screen (a cached thumbnail may still be added)

    /// <summary>Request whose own content or message is on screen; 0 when none (as opposed to the previous item's).</summary>
    public long ShownRequest => _shown;

    /// <summary>The request's content (thumbnail, image, player, document) is on screen.</summary>
    public void MarkContent(long requestId) => _shown = requestId;

    /// <summary>
    /// A final message for the request is on screen (unsupported, damaged, too large, failed): no thumbnail or
    /// "loading" of that request may replace it.
    /// </summary>
    public void MarkMessage(long requestId)
    {
        _shown = requestId;
        _final = requestId;
    }

    /// <summary>
    /// A notice for the request is on screen and nothing more will be loaded (e.g. a cloud-only file): a cached
    /// thumbnail may still be added, but "loading" must not replace the notice.
    /// </summary>
    public void MarkNotice(long requestId)
    {
        _shown = requestId;
        _notice = requestId;
    }

    /// <summary>Quick View closed: nothing is on screen.</summary>
    public void Clear()
    {
        _shown = 0;
        _final = 0;
        _notice = 0;
    }

    /// <summary>
    /// After navigation: true when <paramref name="requestId"/> is still the current request and has shown nothing
    /// yet, so the previous item's picture must be replaced with "loading".
    /// </summary>
    public bool NeedsStaleClear(long currentRequest, long requestId) => requestId == currentRequest && _shown != requestId;

    /// <summary>A cached thumbnail of <paramref name="requestId"/> may be shown (no final message is on screen).</summary>
    public bool MayShowThumbnail(long requestId) => _final != requestId;

    /// <summary>"Loading" may be shown for <paramref name="requestId"/> (neither a final message nor a notice is on screen).</summary>
    public bool MayShowLoading(long requestId) => _final != requestId && _notice != requestId;
}
