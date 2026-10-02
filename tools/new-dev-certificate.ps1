<#
.SYNOPSIS
  Creates (or reuses) a self-signed code-signing certificate for signing the identity package during development.
.DESCRIPTION
  Follows Microsoft Learn "Create a certificate for package signing": the certificate is created in
  Cert:\CurrentUser\My with a non-exportable private key (no .pfx file is written), and its public part is added to
  Cert:\CurrentUser\TrustedPeople so Add-AppxPackage accepts the package (no administrator rights needed).
  Development only: end users need a package signed with a certificate their PC trusts (docs/WINDOWS-INTEGRATION.md section 15).
  Remove with: Get-ChildItem Cert:\CurrentUser\My, Cert:\CurrentUser\TrustedPeople | Where-Object Subject -eq 'CN=Mavue Dev' | Remove-Item
#>
param([string] $Subject = 'CN=Mavue Dev')
$ErrorActionPreference = 'Stop'

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date).AddDays(7) -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $certificate) {
    $certificate = New-SelfSignedCertificate -Type Custom -Subject $Subject -KeyUsage DigitalSignature `
        -FriendlyName 'Mavue development package signing' -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(1) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Write-Host "Created $($certificate.Thumbprint) ($Subject)"
}

if (-not (Get-ChildItem Cert:\CurrentUser\TrustedPeople | Where-Object Thumbprint -eq $certificate.Thumbprint)) {
    $publicPart = Join-Path ([IO.Path]::GetTempPath()) "mavue-dev-$($certificate.Thumbprint).cer"
    try {
        Export-Certificate -Cert $certificate -FilePath $publicPart | Out-Null
        Import-Certificate -FilePath $publicPart -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
    }
    finally {
        Remove-Item $publicPart -ErrorAction SilentlyContinue
    }
    Write-Host "Trusted $($certificate.Thumbprint) in CurrentUser\TrustedPeople"
}

$certificate.Thumbprint
