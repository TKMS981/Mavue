<#
.SYNOPSIS
  Builds Mavue's release artifacts: the per-user ZIP (with Install.cmd), the MSIX package, symbols and checksums.
.DESCRIPTION
  Runtime decision (docs/PACKAGING.md section 2): .NET and the Windows App SDK are self-contained, and Mavue.exe and
  Mavue.QuickView.Host.exe share one folder (their common files are identical, so one runtime serves both). Nothing has
  to be installed on the user's PC first.

  1. Publishes Mavue.App and Mavue.QuickView.Host (Release, self-contained, -p:Version) each into its own folder under
     artifacts\release\build (never touching the normal bin\ outputs) and merges them into artifacts\release\Mavue;
     a file present in both with different contents stops the build.
  2. Adds the native DLLs (tools/build-native.ps1: x64 or arm64, and for x64 the x86 preview DLL for 32-bit apps).
  3. Moves the .pdb files into the symbols ZIP.
  4. Adds the license files (licenses\): Mavue's LICENSE, NOTICE and THIRD-PARTY-NOTICES, PDFium's (already there),
     and the license/notice files of the .NET runtime, Windows App SDK and WebView2 packages actually in the output,
     taken from the NuGet packages at the versions recorded in Mavue.deps.json.
  5. Signs Mavue's binaries and pdfium.dll (tools/sign-release.ps1) unless -NoSign.
  6. Builds the identity package for the Windows 11 context menu (ZIP install), the ZIP and the MSIX
     (tools/package-msix.ps1 from the same folder), and SHA256SUMS.txt.
  7. Checks that the ZIP and the MSIX hold exactly the release folder's files (SHA-256), with the install and license
     files, and that the symbols match the shipped assemblies; stops otherwise.
.EXAMPLE
  tools/build-release.ps1 -Version 0.1.0                       # development certificate (CN=Mavue Dev)
  tools/build-release.ps1 -Version 0.1.0 -Subject 'CN=...' -TimestampUrl http://timestamp.example
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Architecture = 'x64',
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version = '0.1.0',
    [string] $Subject = 'CN=Mavue Dev',
    [string] $Thumbprint,
    [string] $TimestampUrl,
    [switch] $NoSign,
    [switch] $SkipMsix
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$rid = "win-$Architecture"
$out = Join-Path $repo 'artifacts\release'
$build = Join-Path $out 'build'
$app = Join-Path $out 'Mavue'
$name = "Mavue-$Version-$rid"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $app | Out-Null

# 1. Publish both programs and merge them into one folder.
$dotnet = if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { 'dotnet' }
$published = @()
foreach ($project in 'src\Mavue.App\Mavue.App.csproj', 'src\Mavue.QuickView.Host\Mavue.QuickView.Host.csproj') {
    $folder = Join-Path $build ('publish-' + [IO.Path]::GetFileNameWithoutExtension($project))
    & $dotnet publish (Join-Path $repo $project) -c Release -r $rid --self-contained -p:Platform=$Architecture -p:Version=$Version `
        --artifacts-path (Join-Path $build 'obj') -o $folder -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
    $published += $folder
}
foreach ($folder in $published) {
    foreach ($file in Get-ChildItem $folder -Recurse -File) {
        $relative = $file.FullName.Substring($folder.Length + 1)
        $target = Join-Path $app $relative
        if (Test-Path $target) {
            if ((Get-FileHash $target).Hash -ne (Get-FileHash $file.FullName).Hash) {
                throw "$relative differs between Mavue and the Quick View host; they cannot share one folder (align the package versions)."
            }
            continue
        }
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item $file.FullName $target
    }
}
if (Get-ChildItem $app -Filter 'Microsoft.WindowsDesktop.App*' -Recurse) { throw 'Unexpected WindowsDesktop runtime files in the output.' }

# 2. Native DLLs (copied by the projects when they existed at build time; checked here).
$native = Join-Path $repo "artifacts\native\$rid"
$required = @(
    @{ From = Join-Path $native 'Mavue.Shell.Preview.dll'; To = 'Mavue.Shell.Preview.dll' },
    @{ From = Join-Path $native 'Mavue.Shell.Native.dll'; To = 'Mavue.Shell.Native.dll' })
if ($Architecture -eq 'x64') {
    $x86 = Join-Path $repo 'artifacts\native\win-x86'
    $required += @{ From = Join-Path $x86 'Mavue.Shell.Preview.dll'; To = 'x86\Mavue.Shell.Preview.dll' }
    $required += @{ From = Join-Path $x86 'pdfium.dll'; To = 'x86\pdfium.dll' }
}
foreach ($file in $required) {
    $target = Join-Path $app $file.To
    if (-not (Test-Path $file.From)) { throw "Missing $($file.From) (tools/build-native.ps1 -Architecture $(if ($file.To -like 'x86*') { 'x86' } else { $Architecture }))." }
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item $file.From $target -Force
}
foreach ($file in 'Mavue.exe', 'Mavue.QuickView.Host.exe', 'pdfium.dll', 'Mavue.Shell.Preview.dll', 'Mavue.Shell.Native.dll', 'Mavue.pri', 'Mavue.QuickView.Host.pri') {
    if (-not (Test-Path (Join-Path $app $file))) { throw "Missing $file in the release folder." }
}

# 3. Symbols go into their own ZIP (not shipped to users).
$symbols = Join-Path $build 'symbols'
foreach ($pdb in Get-ChildItem $app -Recurse -Filter *.pdb) {
    $target = Join-Path $symbols $pdb.FullName.Substring($app.Length + 1)
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Move-Item $pdb.FullName $target
}

# 4. License files.
$licenses = Join-Path $app 'licenses'
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $licenses 'LICENSE.txt')
Copy-Item (Join-Path $repo 'NOTICE') (Join-Path $licenses 'NOTICE.txt')
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') (Join-Path $licenses 'THIRD-PARTY-NOTICES.txt')
Copy-Item (Join-Path $repo 'licenses\EULA.txt') (Join-Path $licenses 'EULA.txt') # terms for the binaries (draft until legal review, docs/EULA-DRAFT.md)
$deps = Get-Content (Join-Path $app 'Mavue.deps.json') -Raw | ConvertFrom-Json
$libraries = @{}
foreach ($property in $deps.libraries.PSObject.Properties) {
    $parts = $property.Name.Split('/')
    $libraries[$parts[0]] = $parts[1]
}
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$packageLicenses = @(
    @{ Id = "runtimepack.Microsoft.NETCore.App.Runtime.$rid"; Package = "microsoft.netcore.app.runtime.$rid"; Folder = 'dotnet'; Files = 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT' },
    @{ Id = 'Microsoft.WindowsAppSDK.WinUI'; Folder = 'windowsappsdk'; Files = 'license.txt', 'NOTICE.txt'; Prefix = 'WinUI-' },
    @{ Id = 'Microsoft.WindowsAppSDK.Foundation'; Folder = 'windowsappsdk'; Files = 'license.txt'; Prefix = 'Foundation-' },
    @{ Id = 'Microsoft.WindowsAppSDK.InteractiveExperiences'; Folder = 'windowsappsdk'; Files = 'license.txt'; Prefix = 'InteractiveExperiences-' },
    @{ Id = 'Microsoft.Web.WebView2'; Folder = 'webview2'; Files = 'LICENSE.txt', 'NOTICE.txt' })
foreach ($entry in $packageLicenses) {
    $packageVersion = $libraries[$entry.Id]
    if (-not $packageVersion) { throw "$($entry.Id) is not in Mavue.deps.json; update the license list in tools/build-release.ps1." }
    $packageId = if ($entry.Package) { $entry.Package } else { $entry.Id.ToLowerInvariant() }
    $folder = Join-Path $licenses $entry.Folder
    New-Item -ItemType Directory -Force $folder | Out-Null
    foreach ($file in $entry.Files) {
        $source = Join-Path $nuget "$packageId\$packageVersion\$file"
        if (-not (Test-Path $source)) { throw "Missing $source." }
        Copy-Item $source (Join-Path $folder ($entry.Prefix + $file))
    }
    Add-Content (Join-Path $folder 'VERSIONS.txt') "$($entry.Id) $packageVersion" -Encoding utf8
}
if (-not (Test-Path (Join-Path $licenses 'pdfium\pdfium.txt'))) { throw 'licenses\pdfium is missing (Mavue.Pdf copies it).' }

# 5. Sign Mavue's binaries (Microsoft's are already signed).
$signArguments = @{ Path = $app; Subject = $Subject }
if ($Thumbprint) { $signArguments.Thumbprint = $Thumbprint }
if ($TimestampUrl) { $signArguments.TimestampUrl = $TimestampUrl }
if (-not $NoSign) { & (Join-Path $PSScriptRoot 'sign-release.ps1') @signArguments }

# 6a. Identity package for the Windows 11 context menu of the ZIP install (signed; the installer falls back to the
#     classic menu when the PC does not trust the certificate).
if (-not $NoSign) {
    $identity = & (Join-Path $PSScriptRoot 'package-identity.ps1') -HostDirectory $app -Publisher $Subject -Thumbprint $Thumbprint -TimestampUrl $TimestampUrl |
        Select-Object -Last 1
    Copy-Item $identity (Join-Path $app 'Mavue.QuickView.Identity.msix')
}

# 6b. ZIP: Install.cmd / Install.ps1 / Uninstall.ps1 / README.txt and the Mavue folder.
$zipStage = Join-Path $build 'zip'
New-Item -ItemType Directory -Force $zipStage | Out-Null
Copy-Item $app (Join-Path $zipStage 'Mavue') -Recurse
Copy-Item (Join-Path $PSScriptRoot 'installer\*') $zipStage
Copy-Item (Join-Path $PSScriptRoot 'installer\Uninstall.ps1') (Join-Path $zipStage 'Mavue\Uninstall.ps1')
$zip = Join-Path $out "$name.zip"
# ZipFile, not Compress-Archive: Windows PowerShell's Compress-Archive writes entry names with backslashes, which the ZIP
# format does not allow (some tools then extract "Mavue\Mavue.exe" as one file name).
# (ZipFile.CreateFromDirectory does the same under Windows PowerShell's .NET Framework compatibility settings.)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function New-Zip([string] $folder, [string] $destination) {
    $archive = [IO.Compression.ZipFile]::Open($destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $folder -Recurse -File) {
            $entryName = $file.FullName.Substring($folder.Length + 1).Replace([char]92, '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
}
New-Zip $zipStage $zip
New-Zip $symbols (Join-Path $out "$name-symbols.zip")

# 6c. MSIX from the same folder (without the ZIP-only identity package).
if (-not $SkipMsix) {
    $msixArguments = @{ Architecture = $Architecture; AppDirectory = $app; Version = "$Version.0"; Publisher = $Subject; Output = (Join-Path $out "Mavue-$Version-$Architecture.msix") }
    if ($NoSign) { $msixArguments.NoSign = $true }
    if ($Thumbprint) { $msixArguments.Thumbprint = $Thumbprint }
    if ($TimestampUrl) { $msixArguments.TimestampUrl = $TimestampUrl }
    & (Join-Path $PSScriptRoot 'package-msix.ps1') @msixArguments
}

# 6d. Checksums.
$sums = Get-ChildItem $out -File | Where-Object { $_.Extension -in '.zip', '.msix' } | Sort-Object Name |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
Set-Content (Join-Path $out 'SHA256SUMS.txt') $sums -Encoding ascii

# 7. Consistency: the ZIP's Mavue\ and the MSIX's Mavue\ hold exactly the release folder's files (same SHA-256), the
#    ZIP has its install files, both have the license files, and the symbols match the shipped Mavue assemblies.
function Get-EntryHashes([string] $archive, [string] $prefix) {
    $hashes = @{}
    $zipFile = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zipFile.Entries) {
            $entryName = [Uri]::UnescapeDataString($entry.FullName)  # MSIX entry names are percent-encoded
            if ($entryName.Contains([char]92)) { throw "$archive has an entry name with a backslash: $entryName" }
            if (-not $entryName.StartsWith($prefix) -or $entryName.EndsWith('/')) { continue }
            $stream = $entry.Open()
            try { $hashes[$entryName.Substring($prefix.Length)] = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)) } finally { $stream.Dispose() }
        }
    } finally { $zipFile.Dispose() }
    return $hashes
}
$expected = @{}
foreach ($file in Get-ChildItem $app -Recurse -File) {
    $expected[$file.FullName.Substring($app.Length + 1).Replace([char]92, '/')] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash -replace '(..)(?!$)', '$1-'
}
$problems = @()
$zipped = Get-EntryHashes $zip 'Mavue/'
foreach ($key in $expected.Keys) { if ($zipped[$key] -ne $expected[$key]) { $problems += "ZIP: Mavue/$key missing or different" } }
foreach ($key in $zipped.Keys) { if (-not $expected.ContainsKey($key) -and $key -ne 'Uninstall.ps1') { $problems += "ZIP: unexpected Mavue/$key" } }
$zipRoot = [IO.Compression.ZipFile]::OpenRead($zip)
try { $rootNames = $zipRoot.Entries | ForEach-Object FullName } finally { $zipRoot.Dispose() }
foreach ($required in 'Install.cmd', 'Install.ps1', 'Uninstall.ps1', 'README.txt', 'Mavue/Uninstall.ps1', 'Mavue/licenses/LICENSE.txt', 'Mavue/licenses/NOTICE.txt', 'Mavue/licenses/THIRD-PARTY-NOTICES.txt', 'Mavue/licenses/EULA.txt') {
    if ($rootNames -notcontains $required) { $problems += "ZIP: missing $required" }
}
if (-not $SkipMsix) {
    $msix = Join-Path $out "Mavue-$Version-$Architecture.msix"
    $packaged = Get-EntryHashes $msix 'Mavue/'
    foreach ($key in $expected.Keys) {
        if ($key -match '\.(msix|ps1|cmd)$') { continue } # ZIP-only files
        if ($packaged[$key] -ne $expected[$key]) { $problems += "MSIX: Mavue/$key missing or different" }
    }
    foreach ($key in $packaged.Keys) { if (-not $expected.ContainsKey($key)) { $problems += "MSIX: unexpected Mavue/$key" } }
    $rootEntries = Get-EntryHashes $msix ''
    foreach ($required in 'AppxManifest.xml', 'AppxBlockMap.xml') { if (-not $rootEntries.ContainsKey($required)) { $problems += "MSIX: missing $required" } }
    if (-not $NoSign -and -not $rootEntries.ContainsKey('AppxSignature.p7x')) { $problems += 'MSIX: not signed' }
}
$symbolNames = (Get-EntryHashes (Join-Path $out "$name-symbols.zip") '').Keys
foreach ($assembly in Get-ChildItem $app -Filter 'Mavue*.dll' | Where-Object { $_.Name -notlike 'Mavue.Shell.Native*' -and $_.Name -notlike 'Mavue.Shell.Preview*' }) {
    if ($symbolNames -notcontains ($assembly.BaseName + '.pdb')) { $problems += "symbols: missing $($assembly.BaseName).pdb" }
}
if ($problems) { $problems | ForEach-Object { Write-Warning $_ }; throw "Release consistency check failed ($($problems.Count) problem(s))." }
Write-Host "Consistency: $($expected.Count) files identical in the release folder, the ZIP$(if (-not $SkipMsix) { ' and the MSIX' }); install and license files present."
# A release needs a code-signing certificate that users' PCs trust; the development certificate is for testing only.
if ($NoSign) { Write-Warning 'NOT SIGNED: not for distribution (a release needs a trusted code-signing certificate, docs/PACKAGING.md section 6).' }
elseif ($Subject -eq 'CN=Mavue Dev' -and -not $Thumbprint) { Write-Warning 'Signed with the DEVELOPMENT certificate (CN=Mavue Dev): for testing only. A release needs a trusted code-signing certificate (docs/PACKAGING.md section 6).' }
if (-not $TimestampUrl -and -not $NoSign) { Write-Warning 'No timestamp: the signatures end with the certificate (a release needs -TimestampUrl).' }
Get-ChildItem $out -File | ForEach-Object { Write-Host ('{0,-45} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB)) }
