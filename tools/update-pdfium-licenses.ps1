<#
.SYNOPSIS
  Copies the license texts of the PDFium build Mavue uses into licenses/pdfium, after checking that the NuGet
  package's pdfium.dll is byte-identical to the GitHub release of the same version.
.DESCRIPTION
  The bblanchon.PDFium.Win32 NuGet package contains no license files (docs/DEPENDENCIES.md §3.1); the release .tgz
  of the same version has them. This script:
    1. reads the version from Directory.Packages.props (e.g. 156.0.8076 → release tag chromium/8076),
    2. downloads pdfium-win-x64.tgz and pdfium-win-arm64.tgz of that release,
    3. compares SHA-256 of their pdfium.dll with the restored NuGet package (fails on any difference),
    4. replaces licenses/pdfium with LICENSE (build scripts), licenses/* (PDFium and bundled components), VERSION and args.gn.
  Run after changing the version, then review the diff of licenses/pdfium and update THIRD-PARTY-NOTICES.md.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content (Join-Path $repo 'Directory.Packages.props')
$version = ($props.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq 'bblanchon.PDFium.Win32' }).Version
if (-not $version) { throw 'bblanchon.PDFium.Win32 is not in Directory.Packages.props.' }
$build = $version.Split('.')[2]
$package = Join-Path $env:USERPROFILE ".nuget\packages\bblanchon.pdfium.win32\$version"
if (-not (Test-Path $package)) { throw "Restore the solution first ($package not found)." }

$work = Join-Path ([IO.Path]::GetTempPath()) "mavue-pdfium-$build"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $work | Out-Null
foreach ($arch in 'x64', 'arm64') {
    $tgz = Join-Path $work "pdfium-win-$arch.tgz"
    Invoke-WebRequest "https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/$build/pdfium-win-$arch.tgz" -OutFile $tgz
    $dir = Join-Path $work $arch
    New-Item -ItemType Directory $dir | Out-Null
    tar -xzf $tgz -C $dir
    $released = (Get-FileHash (Join-Path $dir 'bin\pdfium.dll') -Algorithm SHA256).Hash
    $restored = (Get-FileHash (Join-Path $package "runtimes\win-$arch\native\pdfium.dll") -Algorithm SHA256).Hash
    if ($released -ne $restored) { throw "pdfium.dll ($arch) differs between the NuGet package and the release: $restored vs $released" }
    Write-Host "win-$arch pdfium.dll matches the release ($released)"
}

$target = Join-Path $repo 'licenses\pdfium'
Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $target | Out-Null
$x64 = Join-Path $work 'x64'
Copy-Item (Join-Path $x64 'LICENSE') (Join-Path $target 'pdfium-binaries-build-scripts.txt')
Copy-Item (Join-Path $x64 'licenses\*') $target
Copy-Item (Join-Path $x64 'VERSION') (Join-Path $target 'VERSION.txt')
Copy-Item (Join-Path $x64 'args.gn') (Join-Path $target 'args.gn.txt')
Write-Host "Updated $target for PDFium $version (release chromium/$build)."
