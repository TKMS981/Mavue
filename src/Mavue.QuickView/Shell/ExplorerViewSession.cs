using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Mavue.QuickView.Interop;

namespace Mavue.QuickView.Shell;

/// <summary>Selection of one shell view in view order, plus the item that has the focus rectangle.</summary>
/// <param name="Paths">File-system paths of selected items in the view's display order.</param>
/// <param name="FocusedPath">Path of the focused item, or null if none / not a file-system item.</param>
/// <param name="NonFileSystemItemCount">Selected items without a file-system path.</param>
public sealed record ViewSelection(IReadOnlyList<string> Paths, string? FocusedPath, int NonFileSystemItemCount)
{
    public static ViewSelection Empty { get; } = new([], null, 0);
}

/// <summary>Events raised by <see cref="ExplorerViewSession"/>.</summary>
public enum ViewEvent
{
    /// <summary>DShellFolderViewEvents.SelectionChanged: selection or focus changed in the view.</summary>
    SelectionChanged,

    /// <summary>DWebBrowserEvents2.NavigateComplete2: the window navigated to another folder (new view).</summary>
    Navigated,

    /// <summary>DWebBrowserEvents2.OnQuit: the Explorer window/tab is closing.</summary>
    Closed,
}

/// <summary>
/// Subscribes to one Explorer view's automation events so Quick View can follow selection changes
/// without polling (DShellFolderViewEvents on the view's ShellFolderView object, DWebBrowserEvents2 on
/// the browser for navigation and close). Must be created, used and disposed on the same STA thread,
/// and that thread must pump messages: Explorer delivers the events as cross-process COM calls.
/// </summary>
public sealed class ExplorerViewSession : IDisposable
{
    private const int DispidSelectionChanged = 200;
    private const int DispidNavigateComplete2 = 252;
    private const int DispidOnQuit = 253;
    private const uint SvgioBackground = 0x00000000;
    private const uint SvgioSelection = 0x00000001;
    private const uint SvgioAllView = 0x00000002;
    private const uint SvgioFlagViewOrder = 0x80000000;
    private const uint SvsiSelect = 0x1;
    private const uint SvsiDeselectOthers = 0x4;
    private const uint SvsiEnsureVisible = 0x8;
    private const uint SvsiFocused = 0x10;

    private static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");
    private static readonly Guid DiidShellFolderViewEvents = typeof(IShellFolderViewEventsSink).GUID;
    private static readonly Guid DiidWebBrowserEvents2 = typeof(IWebBrowserEventsSink).GUID;

    private readonly object _browserDispatch;
    private readonly object _browser;
    private readonly DispatchEventSink _sink;
    private readonly nint _sinkUnknown;
    private readonly Action<ViewEvent> _onEvent;
    private Connection? _browserConnection;
    private Connection? _viewConnection;
    private object? _view;
    private object? _viewAutomation;
    private bool _disposed;

    internal ExplorerViewSession(object browserDispatch, object browser, nint topLevelWindow, Action<ViewEvent> onEvent)
    {
        _browserDispatch = browserDispatch;
        _browser = browser;
        _onEvent = onEvent;
        TopLevelWindow = topLevelWindow;
        _sink = new DispatchEventSink(OnDispatchEvent);
        _sinkUnknown = ShellNative.ComWrappers.GetOrCreateComInterfaceForObject(_sink, CreateComInterfaceFlags.None);
        _browserConnection = Connection.Advise(_browserDispatch, DiidWebBrowserEvents2, _sinkUnknown);
        AttachToActiveView();
    }

    /// <summary>Explorer frame window the session belongs to.</summary>
    public nint TopLevelWindow { get; }

    /// <summary>SHELLDLL_DefView of the currently observed view (changes after navigation).</summary>
    public nint ShellViewWindow { get; private set; }

    /// <summary>True when both view and browser events are connected.</summary>
    public bool IsListening => _viewConnection is not null && _browserConnection is not null;

    /// <summary>Reads the current selection in display order and the focused item.</summary>
    public ViewSelection ReadSelection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_view is not IFolderView2 folderView)
        {
            return ViewSelection.Empty;
        }

        var paths = new List<string>();
        int nonFileSystem = 0;
        if (folderView.Items(SvgioSelection | SvgioFlagViewOrder, ShellNative.IidOf<IShellItemArray>(), out nint arrayPointer) >= 0 && arrayPointer != 0)
        {
            object arrayObject = ShellNative.Wrap<object>(arrayPointer);
            try
            {
                var array = (IShellItemArray)arrayObject;
                if (array.GetCount(out uint count) >= 0)
                {
                    for (uint i = 0; i < count; i++)
                    {
                        if (array.GetItemAt(i, out nint item) >= 0 && item != 0 && PathOf(item) is { } path)
                        {
                            paths.Add(path);
                        }
                        else
                        {
                            nonFileSystem++;
                        }
                    }
                }
            }
            finally
            {
                ShellNative.Release(arrayObject);
            }
        }

        string? focused = null;
        if (folderView.GetFocusedItem(out int focusedIndex) >= 0 && focusedIndex >= 0 &&
            folderView.GetItem(focusedIndex, ShellNative.IidOf<IShellItem>(), out nint focusedItem) >= 0 && focusedItem != 0)
        {
            focused = PathOf(focusedItem);
        }

        return new ViewSelection(paths, focused, nonFileSystem);
    }

    /// <summary>
    /// Paths of the items before and after the focused item in display order (for prefetching).
    /// Null where there is no neighbor or it has no file-system path.
    /// </summary>
    public (string? Previous, string? Next) ReadNeighbors()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_view is not IFolderView2 folderView || folderView.GetFocusedItem(out int focused) < 0 || focused < 0)
        {
            return (null, null);
        }

        folderView.ItemCount(SvgioAllView, out int count);
        return (PathAt(folderView, focused - 1, count), PathAt(folderView, focused + 1, count));
    }

    private static string? PathAt(IFolderView2 folderView, int index, int count)
    {
        if (index < 0 || index >= count || folderView.GetItem(index, ShellNative.IidOf<IShellItem>(), out nint item) < 0 || item == 0)
        {
            return null;
        }

        return PathOf(item);
    }

    /// <summary>
    /// Moves Explorer's selection to the item <paramref name="delta"/> positions from the focused item in
    /// display order (used when Quick View itself has keyboard focus). Clamped to the view's bounds.
    /// </summary>
    /// <returns>True if the selection moved.</returns>
    public bool MoveSelection(int delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_view is not IFolderView2 folderView || folderView.GetFocusedItem(out int focused) < 0)
        {
            return false;
        }

        folderView.ItemCount(SvgioAllView, out int count);
        int target = Math.Clamp(Math.Max(focused, 0) + delta, 0, Math.Max(0, count - 1));
        if (count == 0 || target == focused)
        {
            return false;
        }

        folderView.SelectItem(target, SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewConnection?.Dispose();
        _browserConnection?.Dispose();
        _viewConnection = null;
        _browserConnection = null;
        ShellNative.Release(_viewAutomation);
        ShellNative.Release(_view);
        ShellNative.Release(_browser);
        ShellNative.Release(_browserDispatch);
        if (_sinkUnknown != 0)
        {
            Marshal.Release(_sinkUnknown);
        }
    }

    private void OnDispatchEvent(int dispId)
    {
        if (_disposed)
        {
            return;
        }

        switch (dispId)
        {
            case DispidSelectionChanged:
                _onEvent(ViewEvent.SelectionChanged);
                break;
            case DispidNavigateComplete2:
                // A navigation replaces the view object; follow the new one.
                AttachToActiveView();
                _onEvent(ViewEvent.Navigated);
                break;
            case DispidOnQuit:
                _onEvent(ViewEvent.Closed);
                break;
        }
    }

    private void AttachToActiveView()
    {
        _viewConnection?.Dispose();
        _viewConnection = null;
        ShellNative.Release(_viewAutomation);
        _viewAutomation = null;
        ShellNative.Release(_view);
        _view = null;
        ShellViewWindow = 0;

        if (((IShellBrowser)_browser).QueryActiveShellView(out nint viewPointer) < 0 || viewPointer == 0)
        {
            return;
        }

        _view = ShellNative.Wrap<object>(viewPointer);
        var view = (IShellView)_view;
        if (view.GetWindow(out nint viewWindow) >= 0)
        {
            ShellViewWindow = viewWindow;
        }

        // The ShellFolderView automation object raises DShellFolderViewEvents.
        // Keep the automation object alive for the lifetime of the subscription.
        if (view.GetItemObject(SvgioBackground, IidIDispatch, out nint automation) >= 0 && automation != 0)
        {
            _viewAutomation = ShellNative.Wrap<object>(automation);
            _viewConnection = Connection.Advise(_viewAutomation, DiidShellFolderViewEvents, _sinkUnknown);
        }
    }

    private static string? PathOf(nint itemPointer)
    {
        object itemObject = ShellNative.Wrap<object>(itemPointer);
        try
        {
            if (((IShellItem)itemObject).GetDisplayName(ShellNative.SIGDN_FILESYSPATH, out nint name) >= 0 && name != 0)
            {
                string path = Marshal.PtrToStringUni(name)!;
                ShellNative.CoTaskMemFree(name);
                return path;
            }

            return null;
        }
        finally
        {
            ShellNative.Release(itemObject);
        }
    }

    /// <summary>One IConnectionPoint advise, undone on dispose.</summary>
    private sealed class Connection : IDisposable
    {
        private readonly object _point;
        private readonly uint _cookie;

        private Connection(object point, uint cookie)
        {
            _point = point;
            _cookie = cookie;
        }

        public static Connection? Advise(object source, in Guid eventsIid, nint sink)
        {
            if (source is not IConnectionPointContainer container ||
                container.FindConnectionPoint(eventsIid, out nint pointPointer) < 0 || pointPointer == 0)
            {
                return null;
            }

            object point = ShellNative.Wrap<object>(pointPointer);
            if (((IConnectionPoint)point).Advise(sink, out uint cookie) < 0)
            {
                ShellNative.Release(point);
                return null;
            }

            return new Connection(point, cookie);
        }

        public void Dispose()
        {
            try
            {
                ((IConnectionPoint)_point).Unadvise(_cookie);
            }
            catch (COMException)
            {
                // Explorer window already gone.
            }

            ShellNative.Release(_point);
        }
    }
}
