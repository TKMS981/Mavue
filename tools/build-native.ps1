<#
.SYNOPSIS
  Builds native/Mavue.Shell.Native (the Windows 11 "Mavue Quick View" context-menu command) with the MSVC Build Tools.
.DESCRIPTION
  Output: artifacts/native/<rid>/Mavue.Shell.Native.dll. Mavue.QuickView.Host copies it into its output folder on the
  next build (the identity package's external location). Requires Visual Studio Build Tools with the C++ workload
  (docs/BUILD.md section 2.2); the C# solution itself does not need them.
#>
param(
    [ValidateSet('x64', 'arm64')] [string[]] $Architecture = @('x64'),
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio Build Tools not found (vswhere.exe missing). See docs/BUILD.md section 2.2.' }
$vs = & $vswhere -products * -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC (Microsoft.VisualStudio.Component.VC.Tools.x86.x64) not found. See docs/BUILD.md section 2.2.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'
$source = Join-Path $repo 'native\Mavue.Shell.Native'

foreach ($arch in $Architecture) {
    $vcArch = if ($arch -eq 'x64') { 'x64' } else { 'x64_arm64' }
    $processor = if ($arch -eq 'x64') { 'AMD64' } else { 'ARM64' }
    $build = Join-Path $repo "artifacts\native-build\$arch-$Configuration"
    $out = Join-Path $repo "artifacts\native\win-$arch"
    New-Item -ItemType Directory -Force $build, $out | Out-Null
    $configure = "cmake -S `"$source`" -B `"$build`" -G Ninja -DCMAKE_BUILD_TYPE=$Configuration -DCMAKE_SYSTEM_NAME=Windows -DCMAKE_SYSTEM_PROCESSOR=$processor"
    $compile = "cmake --build `"$build`""
    # vcvarsall.bat calls vswhere.exe through PATH; a shell started before the installation does not have it.
    $env:PATH = (Split-Path $vswhere) + ';' + $env:PATH
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue' # compiler output on stderr is not an error by itself
    cmd /c "call `"$vcvars`" $vcArch >nul && $configure && $compile" 2>&1 | ForEach-Object { "$_" }
    $exit = $LASTEXITCODE
    $ErrorActionPreference = $previous
    if ($exit -ne 0) { throw "native build failed for $arch (exit $exit)" }
    Copy-Item (Join-Path $build 'Mavue.Shell.Native.dll') $out -Force
    Write-Host "Built $out\Mavue.Shell.Native.dll"
}
