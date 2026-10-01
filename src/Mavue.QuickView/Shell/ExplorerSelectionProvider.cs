using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Mavue.QuickView.Interop;
using IServiceProvider = Mavue.QuickView.Interop.IServiceProvider;

namespace Mavue.QuickView.Shell;

/// <summary>Items selected in one shell view.</summary>
/// <param name="TopLevelWindow">Explorer frame (CabinetWClass) or desktop window.</param>
/// <param name="ShellViewWindow">The view's SHELLDLL_DefView window (identifies the tab).</param>
/// <param name="Paths">File-system paths of the selected items, in selection order.</param>
/// <param name="NonFileSystemItemCount">Selected items without a file-system path (e.g. inside ZIP folders, libraries roots).</param>
public sealed record ExplorerSelection(nint TopLevelWindow, nint ShellViewWindow, IReadOnlyList<string> Paths, int NonFileSystemItemCount);

/// <summary>
/// Reads the selection of Explorer windows (including Windows 11 tabs) and the desktop through the
/// documented Shell automation objects: IShellWindows → SID_STopLevelBrowser → IShellBrowser →
/// IShellView → IFolderView2::GetSelection (docs/WINDOWS-INTEGRATION.md §1).
/// <para>
/// Calls are cross-process COM calls into explorer.exe. Use from a single <b>STA</b> thread, never from
/// the keyboard hook callback. Measured on Windows 11 25H2 (docs/QUICKVIEW-POC.md): from an MTA thread
/// IShellBrowser::GetWindow fails with RPC_E_CANTCALLOUT_ININPUTSYNCCALL (0x8001010D), from an STA
/// thread it succeeds. The provider therefore refuses to run on non-STA threads.
/// </para>
/// </summary>
public sealed class ExplorerSelectionProvider : IDisposable
{
    private const int RpcDisconnected = unchecked((int)0x80010108);
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);
    private const int RpcCallFailed = unchecked((int)0x800706BE);

    private IShellWindows? _shellWindows;

    /// <summary>Optional diagnostics sink (HRESULTs and window handles only; never paths).</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// Connects to the ShellWindows object ahead of the first Space press so that cost is not paid
    /// on the hot path.
    /// </summary>
    public void Warm() => WithReconnect(() => ShellWindows().get_Count(out _));

    /// <summary>
    /// Returns the selection of the shell view identified by <paramref name="shellViewWindow"/>
    /// (the focused item view's parent), or null if no matching view is found.
    /// </summary>
    /// <param name="topLevelWindow">Foreground top-level window (Explorer frame or desktop).</param>
    /// <param name="shellViewWindow">SHELLDLL_DefView window that contains the focused item view.</param>
    public ExplorerSelection? GetSelection(nint topLevelWindow, nint shellViewWindow)
    {
        return WithReconnect(() =>
        {
            string topClass = Win32.GetClassName(topLevelWindow);
            Trace?.Invoke($"GetSelection top=0x{topLevelWindow:X} ({topClass}) view=0x{shellViewWindow:X} ({Win32.GetClassName(shellViewWindow)})");
            if (topClass is "Progman" or "WorkerW")
            {
                return ReadDesktop(shellViewWindow);
            }

            foreach (ExplorerSelection? candidate in EnumerateCore(topLevelWindow, shellViewWindow, stopAtFirst: true))
            {
                if (candidate is not null)
                {
                    return candidate;
                }
            }

            return null;
        });
    }

    /// <summary>Selections of all open Explorer views (diagnostics and test harness).</summary>
    public IReadOnlyList<ExplorerSelection> EnumerateWindows() =>
        WithReconnect(() => EnumerateCore(0, 0, stopAtFirst: false).OfType<ExplorerSelection>().ToList());

    public void Dispose()
    {
        ShellNative.Release(_shellWindows);
        _shellWindows = null;
    }

    private T WithReconnect<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("ExplorerSelectionProvider must be used from an STA thread.");
        }

        try
        {
            return action();
        }
        catch (COMException ex) when (ex.HResult is RpcDisconnected or RpcServerUnavailable or RpcCallFailed)
        {
            // explorer.exe restarted: the cached ShellWindows proxy is dead. Reconnect once.
            Dispose();
            return action();
        }
    }

    private IShellWindows ShellWindows()
    {
        if (_shellWindows is null)
        {
            Marshal.ThrowExceptionForHR(ShellNative.CoCreateInstance(
                ShellNative.ClsidShellWindows, 0, ShellNative.CLSCTX_ALL, ShellNative.IidOf<IShellWindows>(), out nint pointer));
            _shellWindows = ShellNative.Wrap<IShellWindows>(pointer);
        }

        return _shellWindows;
    }

    private IEnumerable<ExplorerSelection?> EnumerateCore(nint topLevelWindow, nint shellViewWindow, bool stopAtFirst)
    {
        IShellWindows windows = ShellWindows();
        int hr = windows.get_Count(out int count);
        Trace?.Invoke($"IShellWindows.Count hr=0x{hr:X8} count={count}");
        Marshal.ThrowExceptionForHR(hr);

        for (int i = 0; i < count; i++)
        {
            hr = windows.Item(ComVariant.Create(i), out nint dispatch);
            Trace?.Invoke($"  Item({i}) hr=0x{hr:X8} dispatch={(dispatch != 0)}");
            if (hr < 0 || dispatch == 0)
            {
                continue;
            }

            ExplorerSelection? selection = ReadBrowser(dispatch, topLevelWindow, shellViewWindow, Trace);
            yield return selection;
            if (stopAtFirst && selection is not null)
            {
                yield break;
            }
        }
    }

    private ExplorerSelection? ReadDesktop(nint shellViewWindow)
    {
        ComVariant location = ComVariant.Create(ShellNative.CSIDL_DESKTOP);
        ComVariant root = default;
        int hr = ShellWindows().FindWindowSW(location, root, ShellNative.SWC_DESKTOP, out int desktopHwnd, ShellNative.SWFO_NEEDDISPATCH, out nint dispatch);
        if (hr != 0 || dispatch == 0)
        {
            return null;
        }

        return ReadBrowser(dispatch, (nint)desktopHwnd, shellViewWindow, Trace, requireTopLevelMatch: false);
    }

    /// <summary>Reads one browser's selection. Takes ownership of <paramref name="dispatch"/>.</summary>
    private static ExplorerSelection? ReadBrowser(nint dispatch, nint topLevelWindow, nint shellViewWindow, Action<string>? trace, bool requireTopLevelMatch = true)
    {
        object? browserObject = null;
        object? viewObject = null;
        object? arrayObject = null;
        object dispatchObject = ShellNative.Wrap<object>(dispatch);
        try
        {
            if (dispatchObject is not IServiceProvider services)
            {
                trace?.Invoke("    no IServiceProvider");
                return null;
            }

            int qs = services.QueryService(ShellNative.SidTopLevelBrowser, ShellNative.IidOf<IShellBrowser>(), out nint browserPointer);
            if (qs < 0)
            {
                trace?.Invoke($"    QueryService hr=0x{qs:X8}");
                return null;
            }

            browserObject = ShellNative.Wrap<object>(browserPointer);
            var browser = (IShellBrowser)browserObject;
            int gw = browser.GetWindow(out nint browserWindow);
            nint frame = Win32.GetAncestor(browserWindow, Win32.GA_ROOT);
            trace?.Invoke($"    browser.GetWindow hr=0x{gw:X8} hwnd=0x{browserWindow:X} ({Win32.GetClassName(browserWindow)}) frame=0x{frame:X}");
            if (gw < 0)
            {
                return null;
            }

            if (requireTopLevelMatch && topLevelWindow != 0 && frame != topLevelWindow)
            {
                return null;
            }

            int qv = browser.QueryActiveShellView(out nint viewPointer);
            if (qv < 0 || viewPointer == 0)
            {
                trace?.Invoke($"    QueryActiveShellView hr=0x{qv:X8}");
                return null;
            }

            viewObject = ShellNative.Wrap<object>(viewPointer);
            int vw = ((IShellView)viewObject).GetWindow(out nint viewWindow);
            trace?.Invoke($"    view.GetWindow hr=0x{vw:X8} hwnd=0x{viewWindow:X} ({Win32.GetClassName(viewWindow)})");
            if (vw < 0)
            {
                return null;
            }

            // Windows 11 tabs share one frame; the view window identifies the active tab precisely.
            if (shellViewWindow != 0 && viewWindow != shellViewWindow)
            {
                return null;
            }

            if (viewObject is not IFolderView2 folderView)
            {
                trace?.Invoke("    no IFolderView2");
                return new ExplorerSelection(frame, viewWindow, [], 0);
            }

            int gs = folderView.GetSelection(0, out nint arrayPointer);
            trace?.Invoke($"    GetSelection hr=0x{gs:X8}");
            if (gs < 0 || arrayPointer == 0)
            {
                return new ExplorerSelection(frame, viewWindow, [], 0);
            }

            arrayObject = ShellNative.Wrap<object>(arrayPointer);
            (List<string> paths, int nonFileSystem) = ReadPaths((IShellItemArray)arrayObject);
            return new ExplorerSelection(frame, viewWindow, paths, nonFileSystem);
        }
        finally
        {
            ShellNative.Release(arrayObject);
            ShellNative.Release(viewObject);
            ShellNative.Release(browserObject);
            ShellNative.Release(dispatchObject);
        }
    }

    private static (List<string> Paths, int NonFileSystem) ReadPaths(IShellItemArray array)
    {
        var paths = new List<string>();
        int nonFileSystem = 0;
        if (array.GetCount(out uint count) < 0)
        {
            return (paths, 0);
        }

        for (uint i = 0; i < count; i++)
        {
            if (array.GetItemAt(i, out nint itemPointer) < 0 || itemPointer == 0)
            {
                continue;
            }

            object itemObject = ShellNative.Wrap<object>(itemPointer);
            try
            {
                if (((IShellItem)itemObject).GetDisplayName(ShellNative.SIGDN_FILESYSPATH, out nint name) >= 0 && name != 0)
                {
                    paths.Add(Marshal.PtrToStringUni(name)!);
                    ShellNative.CoTaskMemFree(name);
                }
                else
                {
                    nonFileSystem++;
                }
            }
            finally
            {
                ShellNative.Release(itemObject);
            }
        }

        return (paths, nonFileSystem);
    }
}
