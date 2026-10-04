<#
.SYNOPSIS
  Builds and signs the full MSIX package of Mavue (Mavue.exe + the Quick View host), see docs/PACKAGING.md.
.DESCRIPTION
  1. Takes the release folder (-AppDirectory; tools/build-release.ps1 makes it and calls this script): Mavue.exe and
     Mavue.QuickView.Host.exe self-contained in one folder, native DLLs, licenses\, binaries already signed. It goes into
     the package as Mavue\ (the ZIP-only identity package and install scripts are left out).
     Without -AppDirectory, tools/build-release.ps1 -SkipMsix is run first to make it.
  2. Writes the logos (Assets\) from the Quick View logo and AppxManifest.xml (--write-package-manifest: the same
     file type lists as the unpackaged registration).
  3. Packs with MakeAppx and signs with SignTool (Microsoft.Windows.SDK.BuildTools NuGet package) using the certificate
     in Cert:\CurrentUser\My whose subject is -Publisher (development: tools/new-dev-certificate.ps1) or -Thumbprint.
  Install for a test with Add-AppxPackage <msix>; remove with Get-AppxPackage Mavue | Remove-AppxPackage.
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Architecture = 'x64',
    [string] $AppDirectory,
    [string] $Publisher = 'CN=Mavue Dev',
    [string] $Thumbprint,
    [string] $TimestampUrl,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')] [string] $Version,
    [string] $Output,
    [switch] $NoSign
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo 'artifacts\msix'
$layout = Join-Path $out 'layout'
Remove-Item $layout -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $layout | Out-Null

if (-not $AppDirectory) {
    $buildArguments = @{ Architecture = $Architecture; SkipMsix = $true; Subject = $Publisher }
    if ($NoSign) { $buildArguments.NoSign = $true }
    if ($Version) { $buildArguments.Version = ($Version.Split('.')[0..2] -join '.') }
    & (Join-Path $PSScriptRoot 'build-release.ps1') @buildArguments
    $AppDirectory = Join-Path $repo 'artifacts\release\Mavue'
}
foreach ($file in 'Mavue.exe', 'Mavue.QuickView.Host.exe', 'Mavue.Shell.Preview.dll', 'Mavue.Shell.Native.dll', 'licenses\THIRD-PARTY-NOTICES.txt') {
    if (-not (Test-Path (Join-Path $AppDirectory $file))) { throw "$file is not in $AppDirectory (make it with tools/build-release.ps1)." }
}
$appLayout = Join-Path $layout 'Mavue'
Copy-Item $AppDirectory $appLayout -Recurse
Get-ChildItem $appLayout -File | Where-Object { $_.Extension -in '.msix', '.ps1', '.cmd' } | Remove-Item

# Logos from the 150x150 Quick View logo.
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $layout 'Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$source = [System.Drawing.Image]::FromFile((Join-Path $repo 'src\Mavue.QuickView.Host\Assets\QuickViewLogo.png'))
foreach ($logo in @(@{ Name = 'Square150x150Logo.png'; Size = 150 }, @{ Name = 'Square44x44Logo.png'; Size = 44 }, @{ Name = 'StoreLogo.png'; Size = 50 })) {
    $bitmap = New-Object System.Drawing.Bitmap $logo.Size, $logo.Size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = 'HighQualityBicubic'
    $graphics.DrawImage($source, 0, 0, $logo.Size, $logo.Size)
    $bitmap.Save((Join-Path $assets $logo.Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}
$source.Dispose()

$hostExe = Join-Path $appLayout 'Mavue.QuickView.Host.exe'
$arguments = @('--write-package-manifest', "`"$layout\AppxManifest.xml`"", '--publisher', "`"$Publisher`"", '--architecture', $Architecture)
if ($Version) { $arguments += @('--package-version', $Version) }
$process = Start-Process $hostExe -ArgumentList $arguments -Wait -PassThru -NoNewWindow
if ($process.ExitCode -ne 0) { throw "--write-package-manifest failed (exit $($process.ExitCode))" }

$tools = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') -Recurse -Filter makeappx.exe |
    Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $tools) { throw 'makeappx.exe not found (restore the solution first).' }
$package = if ($Output) { $Output } else { Join-Path $out "Mavue_$Architecture.msix" }
& $tools.FullName pack /o /d $layout /p $package | Out-Null
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed (exit $LASTEXITCODE)" }

if (-not $NoSign) {
    # The files inside were signed by tools/sign-release.ps1 (build-release.ps1); this signs the package.
    $signArguments = @{ Path = $package; Subject = $Publisher }
    if ($Thumbprint) { $signArguments.Thumbprint = $Thumbprint }
    if ($TimestampUrl) { $signArguments.TimestampUrl = $TimestampUrl }
    & (Join-Path $PSScriptRoot 'sign-release.ps1') @signArguments
}

$size = [math]::Round((Get-Item $package).Length / 1MB, 1)
Write-Host "Package: $package ($size MB)"
