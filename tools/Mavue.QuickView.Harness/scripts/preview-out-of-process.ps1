param([string]$File)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @"
using System; using System.Runtime.InteropServices; using System.Runtime.InteropServices.ComTypes;
[ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandler { [PreserveSig] int SetWindow(IntPtr hwnd, ref RECT r); [PreserveSig] int SetRect(ref RECT r); [PreserveSig] int DoPreview(); [PreserveSig] int Unload(); }
[ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream { void Initialize(IStream s, uint mode); }
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
public static class P {
  [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
  [DllImport("shlwapi.dll", CharSet=CharSet.Unicode)] static extern int SHCreateStreamOnFileEx(string f, uint m, uint a, bool c, IStream t, out IStream s);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string n, uint st, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr pa);
  public static string Run(string file) {
    Guid clsid = new Guid("AB883DEA-90EE-4AF4-944A-45CEDD231E53"), unk = new Guid("00000000-0000-0000-C000-000000000046");
    object o; int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 4 /*CLSCTX_LOCAL_SERVER*/, ref unk, out o);
    if (hr != 0) return "CoCreateInstance 0x" + hr.ToString("X8");
    IStream s; SHCreateStreamOnFileEx(file, 0x40, 0, false, null, out s);
    ((IInitializeWithStream)o).Initialize(s, 0);
    IntPtr wnd = CreateWindowExW(0, "STATIC", "", 0x80000000, 0, 0, 640, 480, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    RECT r = new RECT { R = 640, B = 480 };
    var p = (IPreviewHandler)o; int a = p.SetWindow(wnd, ref r); int b = p.DoPreview();
    for (int i = 0; i < 40; i++) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(100); }
    string host = string.Join(",", System.Array.ConvertAll(System.Diagnostics.Process.GetProcessesByName("prevhost"), x => x.Id.ToString()));
    p.Unload(); Marshal.ReleaseComObject(o);
    return "SetWindow 0x" + a.ToString("X8") + " DoPreview 0x" + b.ToString("X8") + " prevhost pids " + host;
  }
}
"@
"32-bit: " + (-not [Environment]::Is64BitProcess); [P]::Run($File)
