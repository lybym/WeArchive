<#
.SYNOPSIS
    Smoke-tests the shipped portable ZIP rather than the publish folder it was built from.

.DESCRIPTION
    `smoke-test-cli.ps1` verifies the published artifact directory; this script verifies the
    object that actually ships. It asserts the archive contains `WeArchive.exe` and the
    `wearchive.cmd` PATH shim *at the ZIP root* (a nested or missing layout would break the
    documented "extract and put the folder on PATH" instructions), extracts the archive into a
    clean directory, and then runs the full artifact contract from there.

    The PATH shim is exercised as well as the exe, because `docs/CLI.md` documents the
    lower-case `wearchive` as the entry point and the Issue requires the artifact to be
    suitable for direct invocation by scripts and agents.

.PARAMETER ZipPath
    Portable ZIP to verify. Defaults to <repo>/artifacts/WeArchive-win-x64.zip.

.PARAMETER ExpectedVersion
    Version the extracted artifact must report, for example 0.1.0 or 0.0.0-ci. Optional.

.PARAMETER KeepExtracted
    Leave the extraction directory in place for inspection instead of deleting it.
#>
[CmdletBinding()]
param(
    [string] $ZipPath,
    [string] $ExpectedVersion,
    [switch] $KeepExtracted
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ZipPath) { $ZipPath = Join-Path $repoRoot 'artifacts/WeArchive-win-x64.zip' }
if (-not (Test-Path $ZipPath)) { throw "Portable ZIP not found: $ZipPath" }
$ZipPath = (Resolve-Path $ZipPath).Path

Write-Host "Verifying portable ZIP $ZipPath"

# ---- layout: the PATH entry points must be at the archive root ----

$archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $entryCount = $entries.Count
}
finally {
    $archive.Dispose()
}

Write-Host "  entries: $entryCount"

foreach ($required in @('WeArchive.exe', 'wearchive.cmd')) {
    if ($entries -notcontains $required) {
        throw "Portable ZIP must contain '$required' at its root so extraction is directly runnable. Found: $($entries -join ', ')"
    }
}

# Anything under a top-level folder would mean the archive wraps the app in an extra
# directory, which breaks "extract and add the folder to PATH".
$nested = @($entries | Where-Object { $_ -match '^[^/]+/(WeArchive\.exe|wearchive\.cmd)$' })
if ($nested.Count -gt 0) {
    throw "Portable ZIP nests the entry points instead of placing them at the root: $($nested -join ', ')"
}

# ---- extract into a clean directory and run the real contract ----

$extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("wearchive-package-" + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $extractDir | Out-Null

try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $extractDir)
    Write-Host "  extracted to $extractDir"

    $smokeTest = Join-Path $PSScriptRoot 'smoke-test-cli.ps1'
    $parameters = @{ ArtifactDirectory = $extractDir }
    if ($ExpectedVersion) { $parameters.ExpectedVersion = $ExpectedVersion }

    Write-Host ''
    Write-Host '--- extracted WeArchive.exe ---'
    & $smokeTest @parameters

    Write-Host ''
    Write-Host '--- extracted wearchive.cmd PATH shim ---'
    & $smokeTest @parameters -ViaShim
}
finally {
    if ($KeepExtracted) {
        Write-Host "  kept extraction directory: $extractDir"
    }
    elseif (Test-Path $extractDir) {
        Remove-Item -Recurse -Force $extractDir -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host 'Portable package smoke test passed.'
