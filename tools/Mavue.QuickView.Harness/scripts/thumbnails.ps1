param([string]$Folder)
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap); }
[StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx; public int cy; }
[StructLayout(LayoutKind.Sequential)] public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
public static class T {
  [DllImport("shell32.dll", CharSet=CharSet.Unicode, PreserveSig=false)] static extern void SHCreateItemFromParsingName(string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
  [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bm);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
  public static string Get(string path, int size) {
    IShellItemImageFactory f; SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    IntPtr bmp; int hr = f.GetImage(new SIZE{cx=size, cy=size}, 0x8 /*SIIGBF_THUMBNAILONLY*/, out bmp);
    double ms = sw.Elapsed.TotalMilliseconds;
    if (hr != 0) return string.Format("hr=0x{0:X8} {1:0}ms", hr, ms);
    BITMAP bm; GetObject(bmp, Marshal.SizeOf(typeof(BITMAP)), out bm); DeleteObject(bmp);
    return string.Format("{0}x{1} {2:0}ms", bm.bmWidth, bm.bmHeight, ms);
  }
}
"@
Get-ChildItem $Folder -File | Where-Object { $_.Extension -ne '.jsonl' } | ForEach-Object { "{0,-14} {1}" -f $_.Name, [T]::Get($_.FullName, 256) }
