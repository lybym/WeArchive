#Requires -Version 7.0
<#
.SYNOPSIS
    Downloads, hash-verifies and smoke-checks the exact official RC (Issue #86 gate 1).

.DESCRIPTION
    The acceptance run is only valid when bound to the exact official RC artifact
    produced by Issue #85. This script:

      1. resolves the RC ZIP (downloaded into the acceptance root, or an existing
         local ZIP via -LocalZip);
      2. verifies its SHA-256 against the config's rc.asset_sha256 (hard gate — a
         mismatch aborts everything);
      3. extracts it into <acceptance-root>\rc\win-x64;
      4. runs `WeArchive.exe --version --json` and asserts the reported version equals
         rc.expected_version, so no dev build can ever stand in for the RC.

    Nothing outside the acceptance root is written. Run this before init-root.ps1.

.PARAMETER Config
    Path to the user-maintained acceptance config (config.json).

.PARAMETER LocalZip
    Use an already-downloaded RC ZIP instead of downloading. Its hash is still verified.

.PARAMETER Proxy
    Optional HTTP proxy URL for the download (for example http://127.0.0.1:7897 on
    hosts where github.com release downloads are SNI-filtered).

.EXAMPLE
    ./verify-rc.ps1 -Config D:\acceptance\issue-86\config.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [Alias('Config')] [string] $ConfigPath,
    [string] $LocalZip,
    [string] $Proxy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = $PSScriptRoot
Import-Module (Join-Path $scriptDir 'acceptance-harness.psm1') -Force

$config = Read-AcceptanceConfig -Path $ConfigPath
$root = Get-AcceptanceRoot -Config $config
$rcDir = Join-Path $root 'rc'
$null = New-Item -ItemType Directory -Path $rcDir -Force
$zipPath = Join-Path $rcDir 'WeArchive-win-x64.zip'

if ($LocalZip) {
    if (-not (Test-Path -LiteralPath $LocalZip)) {
        throw "Local RC ZIP not found: $LocalZip"
    }
    Copy-Item -LiteralPath $LocalZip -Destination $zipPath -Force
    Write-Host "Using local RC ZIP: $LocalZip"
} else {
    if (-not (Test-Path -LiteralPath $zipPath)) {
        Write-Host "Downloading $($config.rc.asset_url)"
        $handler = [System.Net.Http.HttpClientHandler]::new()
        if ($Proxy) {
            $handler.Proxy = [System.Net.WebProxy]::new($Proxy)
            $handler.UseProxy = $true
        }
        $client = [System.Net.Http.HttpClient]::new($handler)
        $client.Timeout = [TimeSpan]::FromMinutes(15)
        try {
            $bytes = $client.GetByteArrayAsync($config.rc.asset_url).GetAwaiter().GetResult()
            [System.IO.File]::WriteAllBytes($zipPath, $bytes)
        } finally {
            $client.Dispose()
            $handler.Dispose()
        }
        Write-Host ("Downloaded {0:N0} bytes" -f (Get-Item -LiteralPath $zipPath).Length)
    } else {
        Write-Host 'RC ZIP already present; verifying its hash.'
    }
}

# Gate 1: the exact artifact binding.
$actualHash = Get-FileSha256Hex -Path $zipPath
$expectedHash = $config.rc.asset_sha256.ToLowerInvariant()
Write-Host "  SHA-256: $actualHash"
if ($actualHash -ne $expectedHash) {
    Remove-Item -LiteralPath $zipPath -Force
    throw "RC ZIP hash mismatch.`n  expected: $expectedHash`n  actual:   $actualHash`nThe acceptance run must bind to the exact official RC asset. Aborting."
}
Write-Host '  hash verified against the official release asset.'

# Gate 2: the binary inside must self-identify as the expected RC version.
$extractDir = Join-Path $rcDir 'win-x64'
if (-not (Test-Path -LiteralPath (Join-Path $extractDir 'WeArchive.exe'))) {
    Write-Host "Extracting into $extractDir"
    $tempExtract = Join-Path $rcDir ('extract-' + [guid]::NewGuid().ToString('n'))
    $null = New-Item -ItemType Directory -Path $tempExtract -Force
    try {
        Expand-Archive -LiteralPath $zipPath -DestinationPath $tempExtract -Force
        $exe = Get-ChildItem -Path $tempExtract -Recurse -Filter 'WeArchive.exe' | Select-Object -First 1
        if (-not $exe) {
            throw 'The RC ZIP does not contain WeArchive.exe.'
        }
        $null = New-Item -ItemType Directory -Path $extractDir -Force
        Copy-Item -Path (Join-Path $exe.Directory.FullName '*') -Destination $extractDir -Recurse -Force
    } finally {
        Remove-Item -LiteralPath $tempExtract -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$versionOutput = & (Join-Path $extractDir 'WeArchive.exe') --version --json
$versionExit = $LASTEXITCODE
$versionText = ($versionOutput -join "`n").Trim()
if ($versionExit -ne 0) {
    throw "RC --version --json exited with $versionExit. Output: $versionText"
}
$versionDoc = $versionText | ConvertFrom-Json
if (-not $versionDoc.version) {
    throw "RC --version --json did not report a version field. Output: $versionText"
}
if ($versionDoc.version -ne $config.rc.expected_version) {
    throw ("RC reports version '{0}' but the acceptance config binds '{1}'. " +
           'A dev build or a different release must never run the acceptance.') -f
        $versionDoc.version, $config.rc.expected_version
}

Write-Host ("Official RC verified: version {0}, commit {1}" -f $versionDoc.version, $config.rc.commit_sha)
Write-Host ("Binary: {0}" -f (ConvertTo-PrivacySafePath -Path $extractDir -AcceptanceRoot $root))
Write-Host 'Gate 1 passed.'
