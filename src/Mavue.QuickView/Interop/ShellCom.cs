using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Mavue.QuickView.Interop;

// Minimal Windows Shell COM declarations (shldisp.h, shobjidl_core.h, servprov.h).
// Vtable order matters: methods that Mavue never calls are declared with placeholder signatures
// solely to keep later slots aligned. Do not reorder.

[GeneratedComInterface]
[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
internal partial interface IShellWindows
{
    // IDispatch
    void GetTypeInfoCount(out uint count);
    void GetTypeInfo(uint index, uint lcid, out nint typeInfo);
    void GetIDsOfNames(in Guid riid, nint names, uint count, uint lcid, nint dispIds);
    void Invoke(int dispId, in Guid riid, uint lcid, ushort flags, nint parameters, nint result, nint excepInfo, nint argErr);

    // IShellWindows
    [PreserveSig]
    int get_Count(out int count);

    [PreserveSig]
    int Item(ComVariant index, out nint folder);

    void _NewEnum(out nint enumerator);
    void Register(nint disp, int hwnd, int swClass, out int cookie);
    void RegisterPending(int threadId, nint loc, nint locRoot, int swClass, out int cookie);
    void Revoke(int cookie);
    void OnNavigate(int cookie, nint loc);
    void OnActivated(int cookie, short active);

    [PreserveSig]
    int FindWindowSW(in ComVariant loc, in ComVariant locRoot, int swClass, out int hwnd, int options, out nint disp);
}

[GeneratedComInterface]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal partial interface IServiceProvider
{
    [PreserveSig]
    int QueryService(in Guid service, in Guid riid, out nint obj);
}

[GeneratedComInterface]
[Guid("000214E2-0000-0000-C000-000000000046")]
internal partial interface IShellBrowser
{
    // IOleWindow
    [PreserveSig]
    int GetWindow(out nint hwnd);

    void ContextSensitiveHelp(int enterMode);

    // IShellBrowser
    void InsertMenusSB(nint menu, nint widths);
    void SetMenuSB(nint menu, nint olemenu, nint activeObject);
    void RemoveMenusSB(nint menu);
    void SetStatusTextSB(nint text);
    void EnableModelessSB(int enable);
    void TranslateAcceleratorSB(nint msg, ushort id);
    void BrowseObject(nint pidl, uint flags);
    void GetViewStateStream(uint mode, out nint stream);
    void GetControlWindow(uint id, out nint hwnd);
    void SendControlMsg(uint id, uint msg, nint wParam, nint lParam, nint result);

    [PreserveSig]
    int QueryActiveShellView(out nint shellView);
}

[GeneratedComInterface]
[Guid("000214E3-0000-0000-C000-000000000046")]
internal partial interface IShellView
{
    // IOleWindow
    [PreserveSig]
    int GetWindow(out nint hwnd);
}

[GeneratedComInterface]
[Guid("1AF3A467-214F-4298-908E-06B03E0B39F9")]
internal partial interface IFolderView2
{
    // IFolderView
    void GetCurrentViewMode(out uint mode);
    void SetCurrentViewMode(uint mode);
    void GetFolder(in Guid riid, out nint folder);
    void Item(int index, out nint pidl);
    void ItemCount(uint flags, out int count);

    [PreserveSig]
    int Items(uint flags, in Guid riid, out nint items);

    void GetSelectionMarkedItem(out int item);

    [PreserveSig]
    int GetFocusedItem(out int item);

    void GetItemPosition(nint pidl, out long point);
    void GetSpacing(nint point);
    void GetDefaultSpacing(nint point);
    void GetAutoArrange();
    void SelectItem(int item, uint flags);
    void SelectAndPositionItems(uint count, nint pidls, nint points, uint flags);

    // IFolderView2
    void SetGroupBy(nint key, int ascending);
    void GetGroupBy(nint key, nint ascending);
    void SetViewProperty(nint pidl, nint key, nint value);
    void GetViewProperty(nint pidl, nint key, nint value);
    void SetTileViewProperties(nint pidl, nint list);
    void SetExtendedTileViewProperties(nint pidl, nint list);
    void SetText(int type, nint text);
    void SetCurrentFolderFlags(uint mask, uint flags);
    void GetCurrentFolderFlags(out uint flags);
    void GetSortColumnCount(out int count);
    void SetSortColumns(nint columns, int count);
    void GetSortColumns(nint columns, int count);

    [PreserveSig]
    int GetItem(int index, in Guid riid, out nint item);

    void GetVisibleItem(int start, int previous, out int item);
    void GetSelectedItem(int start, out int item);

    [PreserveSig]
    int GetSelection(int noneImpliesFolder, out nint items);
}

[GeneratedComInterface]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
internal partial interface IShellItemArray
{
    void BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result);
    void GetPropertyStore(int flags, in Guid riid, out nint store);
    void GetPropertyDescriptionList(nint keyType, in Guid riid, out nint list);
    void GetAttributes(int attribFlags, uint mask, out uint attributes);

    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetItemAt(uint index, out nint item);
}

[GeneratedComInterface]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
internal partial interface IShellItem
{
    void BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result);
    void GetParent(out nint parent);

    [PreserveSig]
    int GetDisplayName(uint sigdn, out nint name);

    [PreserveSig]
    int GetAttributes(uint mask, out uint attributes);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSize
{
    public int Width;
    public int Height;
}

[GeneratedComInterface]
[Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
internal partial interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(NativeSize size, int flags, out nint bitmap);
}

internal static partial class ShellNative
{
    public const uint SIGDN_FILESYSPATH = 0x80058000;
    public const int SIIGBF_BIGGERSIZEOK = 0x01;
    public const int SIIGBF_THUMBNAILONLY = 0x08;
    public const int SIIGBF_INCACHEONLY = 0x10;
    public const int CSIDL_DESKTOP = 0x0000;
    public const int SWC_DESKTOP = 0x08;
    public const int SWFO_NEEDDISPATCH = 0x01;
    public const uint CLSCTX_LOCAL_SERVER = 0x4;
    public const uint CLSCTX_ALL = 0x17;

    public static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint obj);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint ptr);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid riid, out nint item);

    public static readonly StrategyBasedComWrappers ComWrappers = new();

    /// <summary>Wraps a raw interface pointer (taking ownership of the reference) as a source-generated RCW.</summary>
    public static T Wrap<T>(nint pointer)
        where T : class
    {
        try
        {
            return (T)ComWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>Releases an RCW created by <see cref="Wrap{T}"/> immediately instead of at GC time.</summary>
    public static void Release(object? rcw)
    {
        if (rcw is ComObject com)
        {
            com.FinalRelease();
        }
    }

    public static Guid IidOf<T>() => typeof(T).GUID;
}
