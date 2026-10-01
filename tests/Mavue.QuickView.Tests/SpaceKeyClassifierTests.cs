using Mavue.QuickView.Trigger;
using static Mavue.QuickView.Trigger.SpaceKeyClassifier;

namespace Mavue.QuickView.Tests;

[Trait("Category", "QuickView")]
public class SpaceKeyClassifierTests
{
    private static readonly SpaceKeyClassifier Default = new(new SpaceKeyClassifierOptions());

    private static SpaceKeyContext ExplorerFileView(
        bool modifiers = false,
        bool ime = false,
        TimeSpan? sinceChar = null) =>
        new(ExplorerWindowClass, DirectUiViewClass, ShellViewClass, modifiers, ime, sinceChar);

    [Fact]
    public void ExplorerFileView_OpensQuickView()
    {
        Assert.Equal(SpaceKeyDecision.OpenQuickView, Default.Classify(ExplorerFileView()));
    }

    [Theory]
    [InlineData(DesktopProgmanClass)]
    [InlineData(DesktopWorkerClass)]
    public void DesktopListView_OpensQuickView(string desktopClass)
    {
        var context = new SpaceKeyContext(desktopClass, ListViewClass, ShellViewClass, false, false, null);
        Assert.Equal(SpaceKeyDecision.OpenQuickView, Default.Classify(context));
    }

    public static TheoryData<string, string, string?, PassThroughReason> NonFileViewFocus => new()
    {
        // Renaming a file: focus is an Edit control.
        { ExplorerWindowClass, "Edit", DirectUiViewClass, PassThroughReason.FocusNotInFileView },
        // Windows 11 address bar / search box are XAML islands.
        { ExplorerWindowClass, "Microsoft.UI.Content.DesktopChildSiteBridge", "Microsoft.UI.Content.DesktopChildSiteBridge", PassThroughReason.FocusNotInFileView },
        { ExplorerWindowClass, "Windows.UI.Input.InputSite.WindowClass", "Windows.UI.Composition.DesktopWindowContentBridge", PassThroughReason.FocusNotInFileView },
        // Navigation pane tree view.
        { ExplorerWindowClass, "SysTreeView32", "NamespaceTreeControl", PassThroughReason.FocusNotInFileView },
        // A DirectUIHWND that is not inside a shell view (e.g. ribbon or other DirectUI host).
        { ExplorerWindowClass, DirectUiViewClass, "CtrlNotifySink", PassThroughReason.FocusNotInFileView },
        // Other applications.
        { "Notepad", "RichEditD2DPT", "Notepad", PassThroughReason.NotShellWindow },
        { "Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", "Chrome_WidgetWin_1", PassThroughReason.NotShellWindow },
        // File dialogs are opt-in.
        { DialogClass, DirectUiViewClass, ShellViewClass, PassThroughReason.NotShellWindow },
    };

    [Theory]
    [MemberData(nameof(NonFileViewFocus))]
    public void PassesThrough_WhenFocusIsNotAFileView(string foreground, string focus, string? focusParent, PassThroughReason expectedReason)
    {
        var context = new SpaceKeyContext(foreground, focus, focusParent, false, false, null);

        Assert.Equal(SpaceKeyDecision.PassThrough, Default.Classify(context, out PassThroughReason reason));
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void FileDialogs_OpenQuickView_WhenEnabled()
    {
        var classifier = new SpaceKeyClassifier(new SpaceKeyClassifierOptions { IncludeFileDialogs = true });
        var context = new SpaceKeyContext(DialogClass, DirectUiViewClass, ShellViewClass, false, false, null);

        Assert.Equal(SpaceKeyDecision.OpenQuickView, classifier.Classify(context));
    }

    [Fact]
    public void Modifiers_PassThrough()
    {
        // Ctrl+Space toggles item selection in Explorer and must keep working.
        Assert.Equal(SpaceKeyDecision.PassThrough, Default.Classify(ExplorerFileView(modifiers: true), out var reason));
        Assert.Equal(PassThroughReason.Modifiers, reason);
    }

    [Fact]
    public void ImeComposition_PassesThrough()
    {
        Assert.Equal(SpaceKeyDecision.PassThrough, Default.Classify(ExplorerFileView(ime: true), out var reason));
        Assert.Equal(PassThroughReason.ImeComposing, reason);
    }

    [Fact]
    public void TypeAhead_PassesThrough_WithinWindow()
    {
        // User typed "my" and presses Space to continue typing "my file".
        Assert.Equal(SpaceKeyDecision.PassThrough, Default.Classify(ExplorerFileView(sinceChar: TimeSpan.FromMilliseconds(300)), out var reason));
        Assert.Equal(PassThroughReason.TypeAhead, reason);
    }

    [Fact]
    public void TypeAhead_Expired_OpensQuickView()
    {
        Assert.Equal(SpaceKeyDecision.OpenQuickView, Default.Classify(ExplorerFileView(sinceChar: TimeSpan.FromSeconds(3))));
    }

    [Fact]
    public void TypeAheadWindow_IsConfigurable()
    {
        var classifier = new SpaceKeyClassifier(new SpaceKeyClassifierOptions { TypeAheadWindow = TimeSpan.Zero });
        Assert.Equal(SpaceKeyDecision.OpenQuickView, classifier.Classify(ExplorerFileView(sinceChar: TimeSpan.FromMilliseconds(10))));
    }

    [Fact]
    public void Disabled_PassesThrough()
    {
        var classifier = new SpaceKeyClassifier(new SpaceKeyClassifierOptions { Enabled = false });
        Assert.Equal(SpaceKeyDecision.PassThrough, classifier.Classify(ExplorerFileView(), out var reason));
        Assert.Equal(PassThroughReason.Disabled, reason);
    }
}
