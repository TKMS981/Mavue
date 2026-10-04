<#
.SYNOPSIS
  Checks an installed Mavue MSIX package (docs/TESTING.md, MSIX): activation of both applications, that .NET and the
  Windows App SDK load from the package (self-contained), the package's COM classes (preview handler, thumbnail
  provider, Windows 11 context-menu command), thumbnails through the shell, the packaged guards of --register, Quick
  View through its client, and where settings are written.
.PARAMETER Pdf
  A PDF to copy (fresh name, so no cached thumbnail is used).
#>
param([Parameter(Mandatory)] [string] $Pdf)
$ErrorActionPreference = 'Stop'
$results = [ordered]@{}
function Check([string] $name, [bool] $ok, [string] $observed) { $results[$name] = @{ ok = $ok; observed = $observed }; Write-Host ('{0,-5} {1}: {2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $observed) }

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IApplicationActivationManager { int ActivateApplication(string aumid, string args, int options, out uint pid); }
[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] public class ApplicationActivationManager { }
[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemImageFactory { [PreserveSig] int GetImage(Size size, int flags, out IntPtr bitmap); }
[StructLayout(LayoutKind.Sequential)] public struct Size { public int X, Y; }
public static class Probe {
  [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
  [DllImport("shell32.dll", CharSet=CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid iid, out IShellItemImageFactory item);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
  public static uint Activate(string aumid, string args) { uint pid; ((IApplicationActivationManager)new ApplicationActivationManager()).ActivateApplication(aumid, args, 0, out pid); return pid; }
  public static string Create(string clsid) {
    Guid c = new Guid(clsid), unk = new Guid("00000000-0000-0000-C000-000000000046"); object o;
    int hr = CoCreateInstance(ref c, IntPtr.Zero, 4 /* CLSCTX_LOCAL_SERVER */, ref unk, out o);
    if (o != null) Marshal.ReleaseComObject(o);
    return "0x" + hr.ToString("X8");
  }
  public static string Thumbnail(string path, int size) {
    Guid iid = typeof(IShellItemImageFactory).GUID; IShellItemImageFactory f;
    int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out f); if (hr != 0) return "item 0x" + hr.ToString("X8");
    IntPtr b; hr = f.GetImage(new Size { X = size, Y = size }, 0x8 /* SIIGBF_THUMBNAILONLY */, out b); Marshal.ReleaseComObject(f);
    if (hr != 0) return "0x" + hr.ToString("X8");
    using (var bmp = System.Drawing.Image.FromHbitmap(b)) { DeleteObject(b); return "ok " + bmp.Width + "x" + bmp.Height; }
  }
}
"@ -ReferencedAssemblies System.Drawing

function Modules([int] $id) {
    try { (Get-Process -Id $id).Modules | Select-Object -Expand FileName } catch { @() }
}
function Origin([string[]] $modules, [string] $name) {
    $module = $modules | Where-Object { (Split-Path $_ -Leaf) -eq $name } | Select-Object -First 1
    if ($module) { $module } else { "$name not loaded" }
}

$package = Get-AppxPackage -Name Mavue
Check 'package' ($null -ne $package -and $package.Status -eq 'Ok') "$($package.PackageFullName) status $($package.Status) signature $($package.SignatureKind)"
if (-not $package) { $results | ConvertTo-Json -Depth 4; exit 1 }
$install = $package.InstallLocation
$app = Join-Path $install 'Mavue'
$family = $package.PackageFamilyName

# Test files (fresh names: nothing cached).
$folder = Join-Path $env:TEMP "Mavue.MsixProbe\$(Get-Date -Format 'MMdd-HHmmss')"
New-Item -ItemType Directory -Force $folder | Out-Null
$png = Join-Path $folder 'probe.png'
$bitmap = New-Object System.Drawing.Bitmap 320, 200
$graphics = [System.Drawing.Graphics]::FromImage($bitmap); $graphics.Clear([System.Drawing.Color]::FromArgb(30, 120, 220)); $graphics.Dispose()
$bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
$svg = Join-Path $folder 'probe.svg'
Set-Content $svg '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="120"><rect width="200" height="120" fill="#2a8"/><circle cx="100" cy="60" r="40" fill="#fff"/></svg>' -Encoding utf8
$pdfCopy = Join-Path $folder 'probe.pdf'
Copy-Item $Pdf $pdfCopy

$realSettings = Join-Path $env:LOCALAPPDATA 'Mavue\settings.json'
$realBefore = if (Test-Path $realSettings) { (Get-Item $realSettings).LastWriteTimeUtc } else { $null }

# 1. Mavue with a file (activation with arguments, as File Explorer's "Open with" does for the package).
$mavuePid = [Probe]::Activate("$family!Mavue", "`"$png`"")
Start-Sleep -Seconds 6
$mavue = Get-Process -Id $mavuePid -ErrorAction SilentlyContinue
$modules = Modules $mavuePid
$coreclr = Origin $modules 'coreclr.dll'; $xaml = Origin $modules 'Microsoft.UI.Xaml.dll'
Check 'mavue-activation' ($null -ne $mavue -and $mavue.Path -like "$app\*" -and $mavue.MainWindowTitle -like '*probe.png*') "pid $mavuePid, path $($mavue.Path), window '$($mavue.MainWindowTitle)'"
Check 'mavue-self-contained' ($coreclr -like "$app\*" -and $xaml -like "$app\*" -and -not ($modules -like "$env:ProgramFiles\dotnet\*")) "coreclr: $coreclr; Microsoft.UI.Xaml: $xaml; modules from Program Files\dotnet: $(@($modules -like "$env:ProgramFiles\dotnet\*").Count)"

# 2. Quick View: the packaged application (resident), then its client by path (as the context-menu command starts it).
Get-Process Mavue.QuickView.Host -ErrorAction SilentlyContinue | Where-Object { $_.Path -notlike "$install\*" } | ForEach-Object { Write-Host "note: unpackaged Quick View running: $($_.Path)" }
$qvPid = [Probe]::Activate("$family!QuickView", '')
Start-Sleep -Seconds 4
$qv = Get-Process -Id $qvPid -ErrorAction SilentlyContinue
$qvModules = Modules $qvPid
Check 'quickview-activation' ($null -ne $qv -and $qv.Path -like "$app\*" -and (Origin $qvModules 'coreclr.dll') -like "$app\*") "pid $qvPid, path $($qv.Path), coreclr $(Origin $qvModules 'coreclr.dll')"
$hostExe = Join-Path $app 'Mavue.QuickView.Host.exe'
$client = Start-Process $hostExe -ArgumentList @('--quickview', "`"$svg`"") -PassThru -Wait
$qvWindows = ''
for ($i = 0; $i -lt 25 -and $qvWindows -notlike '*probe.svg*'; $i++) {
    Start-Sleep -Milliseconds 200
    $qvWindows = (Get-Process Mavue.QuickView.Host | Where-Object { $_.MainWindowHandle -ne 0 } | ForEach-Object { "$($_.Id):'$($_.MainWindowTitle)'" }) -join ', '
}
Check 'quickview-client' ($client.ExitCode -eq 0 -and $qvWindows -like '*probe.svg*') "client exit $($client.ExitCode); windows $qvWindows"

# 3. Packaged guards: --register does nothing inside the package.
$out = Join-Path $folder 'register.txt'
$guard = Start-Process $hostExe -ArgumentList '--register' -PassThru -Wait -RedirectStandardOutput $out
$text = (Get-Content $out -Raw -ErrorAction SilentlyContinue)
Check 'packaged-register-guard' ($guard.ExitCode -eq 0 -and $text -like '*installed as a package*') "exit $($guard.ExitCode): $($text.Trim())"

# 4. COM classes declared by the package.
foreach ($class in @(@{ Name = 'preview-handler'; Id = 'AB883DEA-90EE-4AF4-944A-45CEDD231E53' }, @{ Name = 'thumbnail-provider'; Id = 'B4E9FA4B-4DA4-4A1A-9DC7-DE422F056135' }, @{ Name = 'context-menu-command'; Id = '3C34DBCC-2B28-45D3-A949-A83B0EC298EB' })) {
    $hr = [Probe]::Create($class.Id)
    Check "com-$($class.Name)" ($hr -eq '0x00000000') "CoCreateInstance(LOCAL_SERVER) $hr"
}
$surrogates = Get-Process dllhost -ErrorAction SilentlyContinue | Where-Object { (Modules $_.Id) -like "$app\Mavue.Shell.*" }
Check 'com-surrogate-from-package' (@($surrogates).Count -gt 0) "dllhost with the package's Mavue DLLs: $(@($surrogates | ForEach-Object { $_.Id }) -join ', ')"

# 5. Thumbnails through the shell (Windows decides which provider; the package's ThumbnailHandler is on its ProgIDs).
$svgThumb = [Probe]::Thumbnail($svg, 256); $pdfThumb = [Probe]::Thumbnail($pdfCopy, 256)
$thumbHosts = Get-Process dllhost -ErrorAction SilentlyContinue | Where-Object { (Modules $_.Id) -like "$app\Mavue.Shell.Preview.dll" }
Check 'thumbnail-pdf' ($pdfThumb -like 'ok*') "pdf $pdfThumb; processes with the package's preview DLL: $(@($thumbHosts).Count)"
# Known platform behavior (docs/PACKAGING.md §5.3): Windows uses a package's thumbnail/preview handler for a type only
# when no other package also declares it; .svg is also declared by Photos on a standard Windows 11.
$svgOwners = @((Get-Item 'Registry::HKEY_CLASSES_ROOT\.svg\OpenWithProgids' -ErrorAction SilentlyContinue).Property) | Where-Object { $_ -like 'AppX*' } |
    ForEach-Object { (Get-ItemProperty "Registry::HKEY_CLASSES_ROOT\$_\Application" -ErrorAction SilentlyContinue).AppUserModelID }
$otherSvgPackages = @($svgOwners | Where-Object { $_ -and $_ -notlike 'Mavue_*' })
$label = if ($svgThumb -like 'ok*') { 'PASS' } elseif ($otherSvgPackages.Count -gt 0) { 'KNOWN' } else { 'FAIL' }
$results['thumbnail-svg'] = @{ ok = ($label -ne 'FAIL'); known = ($label -eq 'KNOWN'); observed = "svg $svgThumb; other packages declaring .svg: $($otherSvgPackages -join ', ')" }
Write-Host ('{0,-5} thumbnail-svg: svg {1}; other packages declaring .svg: {2}' -f $label, $svgThumb, ($otherSvgPackages -join ', '))

# 6. Settings: where the packaged app writes (MSIX may redirect %LOCALAPPDATA% writes into the package's data).
Start-Sleep -Seconds 1
$realAfter = if (Test-Path $realSettings) { (Get-Item $realSettings).LastWriteTimeUtc } else { $null }
$redirected = Get-ChildItem (Join-Path $env:LOCALAPPDATA "Packages\$family") -Recurse -Filter settings.json -ErrorAction SilentlyContinue | Select-Object -Expand FullName
Check 'settings-location' $true "real %LOCALAPPDATA%\Mavue\settings.json changed: $($realBefore -ne $realAfter); package copies: $($redirected -join ', ')"

# Clean up: close Mavue, end Quick View.
if ($mavue) { [void]$mavue.CloseMainWindow(); if (-not $mavue.WaitForExit(5000)) { Stop-Process $mavue -Force } }
Start-Process $hostExe -ArgumentList '--shutdown' -Wait
$results['folder'] = $folder
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $folder 'msix-probe.json') -Encoding utf8
Write-Host "Report: $(Join-Path $folder 'msix-probe.json')"
