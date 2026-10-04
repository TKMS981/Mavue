<#
.SYNOPSIS
  Builds the native shell components with the MSVC Build Tools:
  - native/Mavue.Shell.Native: the Windows 11 "Mavue Quick View" context-menu command (IExplorerCommand).
  - native/Mavue.Shell.Preview: the File Explorer preview handler and thumbnail provider.
.DESCRIPTION
  Output: artifacts/native/<rid>/Mavue.Shell.Native.dll and Mavue.Shell.Preview.dll. On the next build,
  Mavue.QuickView.Host copies Mavue.Shell.Native.dll (the identity package's external location) and Mavue.App copies
  Mavue.Shell.Preview.dll (registered by Mavue.exe --register) into their output folders. Requires Visual Studio Build
  Tools with the C++ workload (docs/BUILD.md section 2.2); the C# solution itself does not need them. The PDFium
  headers come from the restored bblanchon.PDFium.Win32 NuGet package (run dotnet restore first).
#>
param(
    [ValidateSet('x64', 'arm64', 'x86')] [string[]] $Architecture = @('x64'),
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio Build Tools not found (vswhere.exe missing). See docs/BUILD.md section 2.2.' }
$vs = & $vswhere -products * -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC (Microsoft.VisualStudio.Component.VC.Tools.x86.x64) not found. See docs/BUILD.md section 2.2.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'
$projects = @('Mavue.Shell.Native', 'Mavue.Shell.Preview')

# PDFium headers: the package version Mavue.Pdf uses (Directory.Packages.props), from the NuGet cache.
[xml] $packages = Get-Content (Join-Path $repo 'Directory.Packages.props')
$pdfiumVersion = ($packages.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq 'bblanchon.PDFium.Win32' }).Version
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$pdfiumInclude = Join-Path $nugetRoot "bblanchon.pdfium.win32\$pdfiumVersion\build\native\include"
if (-not (Test-Path (Join-Path $pdfiumInclude 'pdfium\fpdfview.h'))) { throw "PDFium headers not found in $pdfiumInclude (run 'dotnet restore' first)." }

foreach ($arch in $Architecture) {
    $vcArch = @{ x64 = 'x64'; arm64 = 'x64_arm64'; x86 = 'x64_x86' }[$arch]
    $processor = @{ x64 = 'AMD64'; arm64 = 'ARM64'; x86 = 'X86' }[$arch]
    $out = Join-Path $repo "artifacts\native\win-$arch"
    New-Item -ItemType Directory -Force $out | Out-Null
    # x86: only the preview/thumbnail DLL, for 32-bit applications (their file dialogs); Explorer itself is 64-bit.
    $archProjects = if ($arch -eq 'x86') { @('Mavue.Shell.Preview') } else { $projects }
    foreach ($project in $archProjects) {
        $source = Join-Path $repo "native\$project"
        $build = Join-Path $repo "artifacts\native-build\$project-$arch-$Configuration"
        New-Item -ItemType Directory -Force $build | Out-Null
        $configure = "cmake -S `"$source`" -B `"$build`" -G Ninja -DCMAKE_BUILD_TYPE=$Configuration -DCMAKE_SYSTEM_NAME=Windows -DCMAKE_SYSTEM_PROCESSOR=$processor -DPDFIUM_INCLUDE_DIR=`"$pdfiumInclude`""
        $compile = "cmake --build `"$build`""
        # vcvarsall.bat calls vswhere.exe through PATH; a shell started before the installation does not have it.
        $env:PATH = (Split-Path $vswhere) + ';' + $env:PATH
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue' # compiler output on stderr is not an error by itself
        cmd /c "call `"$vcvars`" $vcArch >nul && $configure && $compile" 2>&1 | ForEach-Object { "$_" }
        $exit = $LASTEXITCODE
        $ErrorActionPreference = $previous
        if ($exit -ne 0) { throw "native build of $project failed for $arch (exit $exit)" }
        Copy-Item (Join-Path $build "$project.dll") $out -Force
        Write-Host "Built $out\$project.dll"
    }

    if ($arch -eq 'x86') {
        # The 32-bit DLL loads pdfium.dll from its own folder: the x86 build of the same package.
        Copy-Item (Join-Path $nugetRoot "bblanchon.pdfium.win32\$pdfiumVersion\runtimes\win-x86\native\pdfium.dll") $out -Force
    }
}
