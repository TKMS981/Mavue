<#
.SYNOPSIS
  Authenticode-signs Mavue's own binaries (and the unsigned pdfium.dll it ships) in a build folder, or an MSIX package.
.DESCRIPTION
  Signing targets (docs/PACKAGING.md section 3): Mavue.exe, Mavue.QuickView.Host.exe, every Mavue.*.dll (managed assemblies,
  Mavue.Shell.Native.dll, Mavue.Shell.Preview.dll incl. x86\), and pdfium.dll (bblanchon/pdfium-binaries are
  unsigned; BSD-3 allows redistribution, signing marks it as shipped by Mavue). Files already signed (Windows App SDK,
  .NET: Microsoft) are left alone. SHA-256 file digest, RFC 3161 timestamp (-TimestampUrl from your CA or Azure
  Trusted Signing; without it signatures expire with the certificate - development only).
  SignTool comes from the Microsoft.Windows.SDK.BuildTools NuGet package.
.EXAMPLE
  tools/sign-release.ps1 -Path artifacts\msix\layout -Thumbprint <sha1> -TimestampUrl http://timestamp.example -DryRun
#>
param(
    [Parameter(Mandatory)] [string] $Path,
    [string] $Thumbprint,
    [string] $Subject = 'CN=Mavue Dev',
    [string] $TimestampUrl,
    [switch] $DryRun
)
$ErrorActionPreference = 'Stop'
$signTool = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') -Recurse -Filter signtool.exe |
    Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signTool) { throw 'signtool.exe not found (restore the solution first).' }

$certificate = if ($Thumbprint) { Get-Item "Cert:\CurrentUser\My\$Thumbprint" } else {
    Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1 }
if (-not $certificate -and -not $DryRun) { throw "No code-signing certificate ($Subject / $Thumbprint) in Cert:\CurrentUser\My." }

$arguments = @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint, '/s', 'My')
if ($TimestampUrl) { $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256') } else { Write-Warning 'No -TimestampUrl: signatures will not outlive the certificate (development only).' }

$item = Get-Item $Path
if ($item.PSIsContainer) {
    $targets = Get-ChildItem $item.FullName -Recurse -Include *.exe, *.dll | Where-Object {
        ($_.Name -like 'Mavue*' -or $_.Name -eq 'pdfium.dll') -and (Get-AuthenticodeSignature $_.FullName).Status -ne 'Valid' }
} else {
    $targets = @($item) # an .msix: sign it after its contents (tools/package-msix.ps1 -SignFiles)
}

foreach ($target in $targets) {
    if ($DryRun) { Write-Host "would sign $($target.FullName)"; continue }
    & $signTool.FullName @arguments $target.FullName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SignTool failed for $($target.FullName) (exit $LASTEXITCODE)" }
    Write-Host "signed $($target.FullName)"
}
Write-Host "$(@($targets).Count) file(s)$(if ($DryRun) { ' to sign' } else { ' signed' })."
