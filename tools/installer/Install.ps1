<#
.SYNOPSIS
  Installs (or updates) Mavue for the current user from the release ZIP. No administrator rights.
.DESCRIPTION
  - Copies the Mavue folder next to this script to %LOCALAPPDATA%\Programs\Mavue\app-<version>. Each version gets
    its own folder, so an update never has to overwrite a DLL that File Explorer's preview/thumbnail host still has
    loaded; older version folders are removed when nothing uses them any more (otherwise at the next install).
  - Registers it: Mavue.exe --register --keep-preview-choices (Open with, Default apps candidates, preview pane,
    thumbnails; the user's PDF preview choice is kept), the Windows 11 context menu (signed identity package, when
    this PC trusts its certificate; otherwise the classic menu) and Quick View at sign-in.
  - Start menu shortcut, an entry in Settings > Apps > Installed apps (uninstall), and starts Quick View.
  Settings (%LOCALAPPDATA%\Mavue) are kept across updates. See docs/PACKAGING.md.
.PARAMETER NoStart
  Do not start Quick View now (it still starts at the next sign-in).
.PARAMETER Force
  Install even though the MSIX package of Mavue is installed (both would add the same commands twice).
#>
param([switch] $NoStart, [switch] $Force)
$ErrorActionPreference = 'Stop'
# The user's first preferred language (Settings › Time & language), which Mavue's UI follows too; PowerShell's UI culture
# can differ from it.
$japanese = try { (Get-WinUserLanguageList)[0].LanguageTag -like 'ja*' } catch { (Get-UICulture).TwoLetterISOLanguageName -eq 'ja' }
function Say([string] $ja, [string] $en) { Write-Host $(if ($japanese) { $ja } else { $en }) }
function Fail([string] $ja, [string] $en) { Say $ja $en; exit 1 }

$source = Join-Path $PSScriptRoot 'Mavue'
$sourceExe = Join-Path $source 'Mavue.exe'
if (-not (Test-Path $sourceExe)) { Fail 'Mavue フォルダーが見つかりません（ZIP を展開してから実行してください）。' 'The Mavue folder was not found (extract the ZIP first).' }
if ([Environment]::OSVersion.Version.Build -lt 19041) { Fail 'Windows 10 2004 (19041) 以降が必要です。' 'Windows 10 2004 (19041) or later is required.' }

# Architecture of the package (PE machine of Mavue.exe) against the PC: x64 runs on x64 (and x64 emulation on ARM64).
$bytes = [IO.File]::ReadAllBytes($sourceExe)
$machine = [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3C) + 4)
if ($machine -eq 0xAA64 -and $env:PROCESSOR_ARCHITECTURE -ne 'ARM64') { Fail 'この ZIP は ARM64 版です。x64 版を使ってください。' 'This ZIP is for ARM64; use the x64 one.' }

if ((Get-AppxPackage -Name Mavue -ErrorAction SilentlyContinue) -and -not $Force) {
    Fail 'Mavue の MSIX パッケージがインストールされています。先に 設定 > アプリ から削除してください（両方あるとメニューが重複します）。' 'The MSIX package of Mavue is installed. Remove it first in Settings > Apps (both would add the same commands twice).'
}

$version = (Get-Item $sourceExe).VersionInfo.ProductVersion.Split('+')[0]
$root = Join-Path $env:LOCALAPPDATA 'Programs\Mavue'
$target = Join-Path $root "app-$version"
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Mavue'
New-Item -ItemType Directory -Force $root | Out-Null
# A pending clean-up of an earlier uninstall (files that were in use) must not remove this installation.
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce' 'MavueCleanup' -ErrorAction SilentlyContinue

function Invoke-Program([string] $path, [string[]] $arguments) {
    # Waits for this process only (Start-Process -Wait would also wait for a resident Quick View it starts).
    $start = New-Object Diagnostics.ProcessStartInfo $path, ($arguments -join ' ')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    $process.WaitForExit()
    return $process.ExitCode
}

# End the resident Quick View of an earlier install (it is a viewer: nothing to save). Mavue windows of another
# version may stay open; they keep their own folder until they are closed.
foreach ($process in Get-Process Mavue.QuickView.Host -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\*" }) {
    Invoke-Program $process.Path @('--shutdown') | Out-Null
    if (-not $process.WaitForExit(5000)) { Stop-Process $process -Force }
}

if (Test-Path $target) {
    # The same version again (repair): its programs must not be running.
    if (Get-Process Mavue -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target\*" }) {
        Fail "Mavue $version が実行中です。ウィンドウを閉じてから、もう一度実行してください。" "Mavue $version is running. Close its windows and run this again."
    }
    try { Remove-Item $target -Recurse -Force } catch {
        Fail "$target を置き換えられません（エクスプローラーのプレビューが使用中の可能性があります）。サインアウト後にもう一度実行してください。" "Cannot replace $target (File Explorer's preview may be using it). Sign out and run this again."
    }
}
$partial = "$target.partial"
Remove-Item $partial -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item $source $partial -Recurse
# Copies of a downloaded ZIP carry its mark of the web; the user chose to install these files.
Get-ChildItem $partial -Recurse -File | Unblock-File
Rename-Item $partial (Split-Path $target -Leaf)
Copy-Item (Join-Path $target 'Uninstall.ps1') (Join-Path $root 'Uninstall.ps1') -Force

$mavue = Join-Path $target 'Mavue.exe'
$quickView = Join-Path $target 'Mavue.QuickView.Host.exe'
$exit = Invoke-Program $mavue @('--register', '--keep-preview-choices')
if ($exit -ne 0) { Fail "Mavue の登録に失敗しました（終了コード $exit）。" "Registering Mavue failed (exit code $exit)." }

# Windows 11 context menu: the identity package must be re-registered for the new folder; when this PC does not trust
# its certificate (or there is none), the classic menu is used instead.
Invoke-Program $quickView @('--unregister-modern-menu') | Out-Null
$identity = Join-Path $target 'Mavue.QuickView.Identity.msix'
$modern = (Test-Path $identity) -and ((Invoke-Program $quickView @('--register-modern-menu', "`"$identity`"")) -eq 0)
if (-not $modern) { Invoke-Program $quickView @('--unregister-modern-menu') | Out-Null }
$exit = Invoke-Program $quickView @('--register')
if ($exit -ne 0) { Fail "Quick View の登録に失敗しました（終了コード $exit）。" "Registering Quick View failed (exit code $exit)." }

# Start menu shortcut.
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Mavue.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $mavue
$shortcut.WorkingDirectory = $target
$shortcut.Save()

# Settings > Apps > Installed apps.
$size = [int]((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
New-Item $uninstallKey -Force | Out-Null
$values = @{
    DisplayName = 'Mavue'; DisplayVersion = $version; Publisher = 'Mavue'; DisplayIcon = "`"$mavue`",0"
    InstallLocation = $root; AppFolder = $target
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$root\Uninstall.ps1`""
    QuietUninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$root\Uninstall.ps1`" -Quiet"
}
foreach ($name in $values.Keys) { Set-ItemProperty $uninstallKey $name $values[$name] }
Set-ItemProperty $uninstallKey EstimatedSize $size -Type DWord
Set-ItemProperty $uninstallKey NoModify 1 -Type DWord
Set-ItemProperty $uninstallKey NoRepair 1 -Type DWord

# Older versions: removed now when nothing has them open, otherwise at the next install.
$kept = @()
foreach ($old in Get-ChildItem $root -Directory -Filter 'app-*' | Where-Object { $_.FullName -ne $target }) {
    try { Remove-Item $old.FullName -Recurse -Force } catch { $kept += $old.Name }
}

if (-not $NoStart) { Start-Process $quickView }
Say "Mavue $version をインストールしました: $target" "Installed Mavue ${version}: $target"
Say "Windows 11 の上段メニュー: $(if ($modern) { 'あり' } else { 'なし（従来のメニュー「その他のオプションを確認」に表示）' })" "Windows 11 context menu: $(if ($modern) { 'yes' } else { 'no (classic menu, Show more options)' })"
Say "利用条件: $target\licenses\EULA.txt（ライセンス: 同じフォルダー）" "Terms of use: $target\licenses\EULA.txt (licenses in the same folder)"
if ($kept) { Say "使用中のため残した古いフォルダー（次回のインストールで削除）: $($kept -join ', ')" "Older folders still in use (removed at the next install): $($kept -join ', ')" }
