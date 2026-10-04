<#
.SYNOPSIS
  Builds and signs the identity package that adds "Mavue Quick View" to the Windows 11 File Explorer context menu.
.DESCRIPTION
  1. Writes AppxManifest.xml with the Quick View host (--write-identity-manifest; the file types come from the same
     list as the classic context menu).
  2. Packs it with MakeAppx (/nv: the referenced files live in the external location, not in the package).
  3. Signs it with SignTool using the certificate in Cert:\CurrentUser\My whose subject is -Publisher.
  MakeAppx and SignTool come from the Microsoft.Windows.SDK.BuildTools NuGet package (no Visual Studio needed).
  Register the result with:  Mavue.QuickView.Host.exe --register-modern-menu <package.msix>
#>
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')] [string] $RuntimeIdentifier = 'win-x64',
    [string] $Publisher = 'CN=Mavue Dev',
    [string] $Version,
    # The folder with Mavue.QuickView.Host.exe (default: the build output; tools/build-release.ps1 passes the release folder).
    [string] $HostDirectory,
    [string] $Thumbprint,
    [string] $TimestampUrl
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$hostDir = if ($HostDirectory) { $HostDirectory } else { Join-Path $repo "src\Mavue.QuickView.Host\bin\$Configuration\net10.0-windows10.0.26100.0\$RuntimeIdentifier" }
$hostExe = Join-Path $hostDir 'Mavue.QuickView.Host.exe'
foreach ($required in $hostExe, (Join-Path $hostDir 'Mavue.Shell.Native.dll'), (Join-Path $hostDir 'Assets\QuickViewLogo.png')) {
    if (-not (Test-Path $required)) { throw "Missing $required (build the host after tools/build-native.ps1)." }
}

$tools = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') -Recurse -Filter makeappx.exe |
    Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $tools) { throw 'makeappx.exe not found (restore the solution first).' }
$makeAppx = $tools.FullName
$signTool = Join-Path $tools.DirectoryName 'signtool.exe'

$certificate = if ($Thumbprint) { Get-Item "Cert:\CurrentUser\My\$Thumbprint" } else {
    Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1 }
if (-not $certificate) { throw "No signing certificate with subject '$Publisher' in Cert:\CurrentUser\My (development: tools/new-dev-certificate.ps1)." }

$out = Join-Path $repo 'artifacts\identity'
$stage = Join-Path $out 'stage'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

$arguments = @('--write-identity-manifest', "`"$stage\AppxManifest.xml`"", '--publisher', "`"$Publisher`"")
if ($Version) { $arguments += @('--package-version', $Version) }
$process = Start-Process $hostExe -ArgumentList $arguments -Wait -PassThru -NoNewWindow
if ($process.ExitCode -ne 0) { throw "--write-identity-manifest failed (exit $($process.ExitCode))" }

$package = Join-Path $out 'Mavue.QuickView.Identity.msix'
& $makeAppx pack /o /d $stage /nv /p $package
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed (exit $LASTEXITCODE)" }
$timestamp = if ($TimestampUrl) { @('/tr', $TimestampUrl, '/td', 'SHA256') } else { @() }
& $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint /s My @timestamp $package
if ($LASTEXITCODE -ne 0) { throw "SignTool failed (exit $LASTEXITCODE)" }
Write-Host "Signed package: $package"
$package
