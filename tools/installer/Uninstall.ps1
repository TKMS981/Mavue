<#
.SYNOPSIS
  Removes Mavue installed by Install.ps1 (or only unregisters an unzipped copy), for the current user.
.DESCRIPTION
  Ends Quick View, removes every registration (Mavue.exe --unregister: Open with, Default apps candidates, preview
  pane, thumbnails, restoring what Mavue replaced; Mavue.QuickView.Host.exe --unregister: context menus, identity
  package, start at sign-in), the Start menu shortcut, the Installed apps entry and %LOCALAPPDATA%\Programs\Mavue.
  Files that File Explorer still has loaded are removed at the next sign-in. Settings (%LOCALAPPDATA%\Mavue) are kept
  unless -RemoveSettings.
  Run from a folder that was not installed (an unzipped copy), it only removes that copy's registrations.
#>
param([switch] $Quiet, [switch] $RemoveSettings)
$ErrorActionPreference = 'Stop'
# The user's first preferred language (Settings › Time & language), which Mavue's UI follows too; PowerShell's UI culture
# can differ from it.
$japanese = try { (Get-WinUserLanguageList)[0].LanguageTag -like 'ja*' } catch { (Get-UICulture).TwoLetterISOLanguageName -eq 'ja' }
function Say([string] $ja, [string] $en) { Write-Host $(if ($japanese) { $ja } else { $en }) }

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Mavue'
$entry = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
$installed = $entry -and $entry.InstallLocation -and ($PSScriptRoot -eq $entry.InstallLocation -or $PSScriptRoot -like "$($entry.InstallLocation)\*")
if ($installed) {
    $root = $entry.InstallLocation
    $app = $entry.AppFolder
} elseif ($entry) {
    # The registrations are per user, not per folder: unregistering here would break the installed copy.
    Say "Mavue は $($entry.InstallLocation) にインストールされています。設定 > アプリ から削除してください。" "Mavue is installed in $($entry.InstallLocation); remove it in Settings > Apps."
    exit 1
} else {
    $root = $null
    $app = $PSScriptRoot
}
if (-not (Test-Path (Join-Path $app 'Mavue.exe'))) {
    Say "Mavue.exe が見つかりません: $app" "Mavue.exe was not found: $app"
    exit 1
}
$scope = if ($root) { $root } else { $app }

function Invoke-Program([string] $path, [string[]] $arguments) {
    if (-not (Test-Path $path)) { return -1 }
    # Waits for this process only (Start-Process -Wait would also wait for processes it starts).
    $start = New-Object Diagnostics.ProcessStartInfo $path, ($arguments -join ' ')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    $process.WaitForExit()
    return $process.ExitCode
}

# Mavue windows may hold unsaved edits: ask to close them instead of ending them.
while (Get-Process Mavue -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$scope\*" }) {
    if ($Quiet) { Say 'Mavue が実行中のため削除できません。' 'Mavue is running; it was not removed.'; exit 1 }
    Say 'Mavue のウィンドウを閉じてから Enter を押してください（中止: Ctrl+C）。' 'Close the Mavue windows, then press Enter (Ctrl+C cancels).'
    [void](Read-Host)
}
foreach ($process in Get-Process Mavue.QuickView.Host -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$scope\*" }) {
    Invoke-Program $process.Path @('--shutdown') | Out-Null
    if (-not $process.WaitForExit(5000)) { Stop-Process $process -Force }
}

Invoke-Program (Join-Path $app 'Mavue.QuickView.Host.exe') @('--unregister') | Out-Null
Invoke-Program (Join-Path $app 'Mavue.exe') @('--unregister') | Out-Null

if (-not $root) {
    Say "登録を削除しました（ファイルは残っています）: $app" "Registrations removed (the files were left in place): $app"
    exit 0
}

Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Mavue.lnk') -ErrorAction SilentlyContinue
Remove-Item $uninstallKey -Recurse -ErrorAction SilentlyContinue
if ($RemoveSettings) { Remove-Item (Join-Path $env:LOCALAPPDATA 'Mavue') -Recurse -Force -ErrorAction SilentlyContinue }

# The preview/thumbnail hosts keep Mavue.Shell.Preview.dll loaded for a while after unregistering.
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $root) {
    $command = "cmd.exe /c rd /s /q `"$root`""
    New-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce' -Force | Out-Null
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce' 'MavueCleanup' $command
    Say "使用中のファイルは次回サインイン時に削除されます: $root" "Files in use are removed at the next sign-in: $root"
}
Say 'Mavue を削除しました。' 'Mavue was removed.'
