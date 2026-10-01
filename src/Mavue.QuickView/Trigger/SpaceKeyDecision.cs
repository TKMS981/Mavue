namespace Mavue.QuickView.Trigger;

/// <summary>Outcome of evaluating a Space key press.</summary>
public enum SpaceKeyDecision
{
    /// <summary>Let the key through to the focused window unchanged.</summary>
    PassThrough = 0,

    /// <summary>Swallow the key and open Quick View for the current shell selection.</summary>
    OpenQuickView,
}

/// <summary>Why a press was passed through. Used for diagnostics only; never logs key content.</summary>
public enum PassThroughReason
{
    None = 0,
    Disabled,
    Modifiers,
    ImeComposing,
    TypeAhead,
    NotShellWindow,
    FocusNotInFileView,
}
