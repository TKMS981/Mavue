namespace Mavue.QuickView.Trigger;

/// <summary>
/// Decides whether a Space key press in the shell should open Quick View.
/// <para>
/// Runs inside the WH_KEYBOARD_LL callback, so it must be allocation-free and fast: Windows silently
/// removes low-level hooks whose callbacks exceed LowLevelHooksTimeout. The Explorer window class names
/// used here are undocumented implementation details; they are deliberately confined to this type so a
/// Windows update that changes them needs a fix in one place (docs/WINDOWS-INTEGRATION.md §1).
/// </para>
/// </summary>
public sealed class SpaceKeyClassifier
{
    /// <summary>File Explorer top-level window (each window may host several tabs).</summary>
    public const string ExplorerWindowClass = "CabinetWClass";

    /// <summary>Desktop window classes (the desktop's file view lives under one of these).</summary>
    public const string DesktopProgmanClass = "Progman";

    public const string DesktopWorkerClass = "WorkerW";

    /// <summary>Common file dialogs.</summary>
    public const string DialogClass = "#32770";

    /// <summary>Shell folder view host; the parent of the focusable item view.</summary>
    public const string ShellViewClass = "SHELLDLL_DefView";

    /// <summary>Item view used by Explorer windows.</summary>
    public const string DirectUiViewClass = "DirectUIHWND";

    /// <summary>Item view used by the desktop.</summary>
    public const string ListViewClass = "SysListView32";

    private readonly SpaceKeyClassifierOptions _options;

    public SpaceKeyClassifier(SpaceKeyClassifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public SpaceKeyDecision Classify(in SpaceKeyContext context) => Classify(context, out _);

    public SpaceKeyDecision Classify(in SpaceKeyContext context, out PassThroughReason reason)
    {
        reason = Evaluate(context);
        return reason == PassThroughReason.None ? SpaceKeyDecision.OpenQuickView : SpaceKeyDecision.PassThrough;
    }

    private PassThroughReason Evaluate(in SpaceKeyContext context)
    {
        if (!_options.Enabled)
        {
            return PassThroughReason.Disabled;
        }

        // Ctrl+Space toggles selection in Explorer; other combinations belong to the user or other apps.
        if (context.HasModifiers)
        {
            return PassThroughReason.Modifiers;
        }

        if (context.ImeComposing)
        {
            return PassThroughReason.ImeComposing;
        }

        // Explorer type-ahead: typing "my file" selects by name; Space is part of the search text.
        if (context.SinceLastCharacterKey is { } since && since < _options.TypeAheadWindow)
        {
            return PassThroughReason.TypeAhead;
        }

        if (!IsShellTopLevel(context.ForegroundWindowClass))
        {
            return PassThroughReason.NotShellWindow;
        }

        // Rename edit boxes, the address bar and the search box (XAML islands in Windows 11) all have
        // different focus classes; only the item view itself qualifies.
        bool focusIsItemView =
            string.Equals(context.FocusParentWindowClass, ShellViewClass, StringComparison.Ordinal) &&
            (string.Equals(context.FocusWindowClass, DirectUiViewClass, StringComparison.Ordinal) ||
             string.Equals(context.FocusWindowClass, ListViewClass, StringComparison.Ordinal));

        return focusIsItemView ? PassThroughReason.None : PassThroughReason.FocusNotInFileView;
    }

    private bool IsShellTopLevel(string windowClass) =>
        string.Equals(windowClass, ExplorerWindowClass, StringComparison.Ordinal) ||
        string.Equals(windowClass, DesktopProgmanClass, StringComparison.Ordinal) ||
        string.Equals(windowClass, DesktopWorkerClass, StringComparison.Ordinal) ||
        (_options.IncludeFileDialogs && string.Equals(windowClass, DialogClass, StringComparison.Ordinal));
}

/// <summary>User-configurable trigger behavior.</summary>
public sealed record SpaceKeyClassifierOptions
{
    /// <summary>Space-key Quick View is on.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Also trigger inside Open/Save dialogs that host a shell view.</summary>
    public bool IncludeFileDialogs { get; init; }

    /// <summary>Space within this interval after a character key is treated as type-ahead text.</summary>
    public TimeSpan TypeAheadWindow { get; init; } = TimeSpan.FromMilliseconds(1000);
}
