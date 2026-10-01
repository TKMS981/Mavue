<#
.SYNOPSIS
  Launches Mavue with --smoke-test and verifies it renders a first frame and exits with code 0.
  Requires an interactive desktop session (not usable in a service/headless CI agent).
.PARAMETER Configuration
  Build configuration to test (Debug or Release).
.PARAMETER TimeoutSeconds
  Maximum time to wait for the app to exit.
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\Mavue.App\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\Mavue.exe"
if (-not (Test-Path $exe)) { throw "Not built: $exe (run 'dotnet build Mavue.slnx -c $Configuration' first)" }

# The .NET 10 runtime may be installed per-user only (see docs/BUILD.md); point the apphost at it.
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (-not $env:DOTNET_ROOT -and (Test-Path (Join-Path $userDotnet 'shared\Microsoft.NETCore.App'))) {
    $env:DOTNET_ROOT = $userDotnet
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -PassThru
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    $process.Kill()
    throw "Mavue did not exit within $TimeoutSeconds s"
}
$sw.Stop()

if ($process.ExitCode -ne 0) { throw "Smoke test failed with exit code $($process.ExitCode)" }
Write-Output ("Smoke test passed: first frame rendered and exited in {0} ms ({1})" -f $sw.ElapsedMilliseconds, $Configuration)
