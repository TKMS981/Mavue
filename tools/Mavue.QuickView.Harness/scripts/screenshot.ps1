param([string]$Out, [int]$Hwnd = 0, [string]$Process = '')
Add-Type -Namespace Dpi -Name N -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr v);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
public struct RECT { public int L, T, R, B; }
'@
[void][Dpi.N]::SetProcessDpiAwarenessContext([IntPtr](-4))
Add-Type -AssemblyName System.Drawing
if ($Hwnd -eq 0 -and $Process) { $Hwnd = (Get-Process $Process | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle }
if ($Hwnd -eq 0) { $Hwnd = [Dpi.N]::GetForegroundWindow() }
$r = New-Object Dpi.N+RECT; [void][Dpi.N]::GetWindowRect([IntPtr]$Hwnd, [ref]$r)
$w = $r.R - $r.L; $h = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
$scale = [Math]::Min(1.0, 1600.0 / $w)
$small = New-Object System.Drawing.Bitmap ([int]($w*$scale)), ([int]($h*$scale))
$g2 = [System.Drawing.Graphics]::FromImage($small); $g2.InterpolationMode = 'HighQualityBicubic'; $g2.DrawImage($bmp, 0, 0, $small.Width, $small.Height)
$small.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); "$Out ${w}x${h}"
