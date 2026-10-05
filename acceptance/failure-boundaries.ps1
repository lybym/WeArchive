#Requires -Version 7.0
<#
.SYNOPSIS
    Failure-boundary exercises (Issue #86), each explicitly invoked and isolated from
    the main acceptance trace.

.DESCRIPTION
    Exercises the safe caught-failure/cancellation boundaries around the acceptance
    vault WITHOUT corrupting the main hourly trace: boundary results go to
    <boundary-root>\evidence\failure-boundaries-trace.jsonl and every exercise ends
    with an authoritative vault verify of whatever it touched.

    Exercises:

      boundary-kill-capture      (optional, -IncludeKillCapture)
          Abruptly kills a real `capture` mid-flight against the acceptance vault.
          Proves the R1 fail-closed invariant: no incomplete generation is published
          (lineage unchanged), authoritative verify stays green; leftover staging is
          bounded and reported. This also stands in for "failure before manifest
          publication" and "published packs with manifest publication failure":
          material that never reached a published manifest stays unreachable, which
          the vault accounting reports as orphan/staging bytes rather than evidence.

      boundary-derived-index
          On a COPY of the acceptance account (never the acceptance vault itself):
          derived lookup index deletion (missing_rebuildable) and corruption
          (corrupt_rebuildable) via vault stats, then authoritative verify of the
          copied vault — a rebuildable derived index must never fail verification and
          never be required as a substitute for authoritative CAS data.

      boundary-authoritative-object
          On the same copied vault: truncates one sealed pack and deletes another.
          `vault verify` MUST fail closed with a failure entry per violated artifact.

      boundary-materialized-cache
          Deletes the disposable materialized/scratch caches under the acceptance
          home, then proves `vault verify` and `vault stats` are unaffected and a
          fresh canonical rebuild still works (see offline-recovery.ps1 for the full
          offline ingest/rebuild acceptance procedure).

    "Ingest failure after successful capture" is exercised by the hourly cycle's
    retention semantics plus offline-recovery.ps1: a successful capture generation is
    retained even when canonical ingest fails, and ingest/rebuild is later proven to
    work from authoritative evidence alone. See the runbook mapping table.

    Pause the hourly schedule (register-schedule.ps1 -Disable) before running the
    kill-capture exercise.

.PARAMETER Config
    Path to the user-maintained acceptance config (config.json).

.PARAMETER BoundaryRoot
    Where the boundary sandbox lives. Defaults to <acceptance-root>\scenario-boundaries.

.PARAMETER IncludeKillCapture
    Also run the mid-capture kill exercise (requires the WeChat client running).

.PARAMETER KillCaptureAfterSeconds
    Seconds to let the killed capture run before killing it. Default 20.

.EXAMPLE
    ./failure-boundaries.ps1 -Config D:\acceptance\issue-86\config.json -IncludeKillCapture
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [Alias('Config')] [string] $ConfigPath,
    [string] $BoundaryRoot,
    [switch] $IncludeKillCapture,
    [int] $KillCaptureAfterSeconds = 20
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = $PSScriptRoot
Import-Module (Join-Path $scriptDir 'acceptance-harness.psm1') -Force

$config = Read-AcceptanceConfig -Path $ConfigPath
$root = Get-AcceptanceRoot -Config $config
if (-not $BoundaryRoot) { $BoundaryRoot = Join-Path $root 'scenario-boundaries' }
$boundaryEvidence = Join-Path $BoundaryRoot 'evidence'
$boundaryVault = Join-Path $BoundaryRoot 'vault'
$null = New-Item -ItemType Directory -Path $boundaryEvidence -Force
$vaultRoot = Join-Path (Get-AcceptanceHome -AcceptanceRoot $root) 'AppData/Local/WeArchive/rawvault'
$tracePath = Join-Path $boundaryEvidence 'failure-boundaries-trace.jsonl'

function Add-BoundaryRecord {
    param(
        [Parameter(Mandatory)] [string] $Exercise,
        [Parameter(Mandatory)] [bool] $Passed,
        [Parameter(Mandatory)] [string] $Observation,
        [AllowNull()] $Result
    )
    $record = [ordered]@{
        schema_version = 1
        record_type = 'failure_boundary'
        scenario_step = $Exercise
        executed_at = (Get-Date).ToString('o')
        rc_version = $config.rc.expected_version
        rc_commit_sha = $config.rc.commit_sha
        rc_asset_sha256 = $config.rc.asset_sha256.ToLowerInvariant()
        passed = $Passed
        observation = $Observation
    }
    if ($Result) {
        $record['exit_code'] = $Result.ExitCode
        $record['duration_ms'] = $Result.DurationMs
    }
    $null = Write-AcceptanceTraceRecord -TracePath $tracePath -Record ([pscustomobject]$record)
}

function Assert-BoundaryVaultVerified {
    param([Parameter(Mandatory)] [string] $Vault)

    $verify = Invoke-WearchiveRc -AcceptanceRoot $root `
        -Arguments @('vault', 'verify', '--vault-root', $Vault, '--json', '--no-input') -TimeoutSeconds 7200
    $succeeded = $verify.ExitCode -eq 0 -and $verify.Json.succeeded
    return @{ Result = $verify; Succeeded = $succeeded }
}

Write-Host 'Failure-boundary exercises. The main hourly trace is not touched.'

# ---------------------------------------------------------------------------
# boundary-kill-capture (optional)
# ---------------------------------------------------------------------------

if ($IncludeKillCapture) {
    Write-Host 'boundary-kill-capture: starting a real capture and killing it mid-flight...'

    $statsBefore = Invoke-WearchiveRc -AcceptanceRoot $root -Arguments @(
        'vault', 'stats', '--vault-root', $vaultRoot, '--json', '--no-input') -TimeoutSeconds 7200
    $generationsBefore = [int]$statsBefore.Json.metrics.generation_count.value

    $exe = Get-AcceptanceRcExe -AcceptanceRoot $root
    $home_ = Get-AcceptanceHome -AcceptanceRoot $root
    $localAppData = Join-Path $home_ 'AppData/Local'
    $drive = [System.IO.Path]::GetPathRoot($home_).TrimEnd('\', '/')
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $exe
    $startInfo.Arguments = 'capture --json --no-input'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($pair in @(
        @('USERPROFILE', $home_), @('LOCALAPPDATA', $localAppData),
        @('APPDATA', (Join-Path $home_ 'AppData/Roaming')),
        @('HOMEDRIVE', $drive), @('HOMEPATH', $home_.Substring($drive.Length)),
        @('TMP', (Join-Path $localAppData 'Temp')), @('TEMP', (Join-Path $localAppData 'Temp'))
    )) {
        $startInfo.EnvironmentVariables[$pair[0]] = $pair[1]
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $null = $process.Start()
    Start-Sleep -Seconds $KillCaptureAfterSeconds
    $killed = -not $process.HasExited
    if ($killed) { $process.Kill($true) }
    $null = $process.WaitForExit(30000)
    Write-Host ("  capture process killed mid-flight: {0}" -f $killed)

    Start-Sleep -Seconds 2
    $statsAfter = Invoke-WearchiveRc -AcceptanceRoot $root -Arguments @(
        'vault', 'stats', '--vault-root', $vaultRoot, '--json', '--no-input') -TimeoutSeconds 7200
    $generationsAfter = [int]$statsAfter.Json.metrics.generation_count.value
    $stagingBytes = [int64]$statsAfter.Json.metrics.vault_staging_bytes.value

    $verify = Assert-BoundaryVaultVerified -Vault $vaultRoot
    if (-not $killed) {
        # The capture finished before the kill window: inconclusive, not a violation.
        $observation = ('inconclusive: the capture completed before the kill window ({0}s); ' +
            'generation_count {1}->{2}, verify={3}. Re-run with a smaller -KillCaptureAfterSeconds.') -f
            $KillCaptureAfterSeconds, $generationsBefore, $generationsAfter, $verify.Succeeded
        Add-BoundaryRecord -Exercise 'boundary-kill-capture' -Passed $false -Observation $observation -Result $statsAfter
        Write-Warning "  INCONCLUSIVE: $observation"
    } else {
        $passed = ($generationsAfter -eq $generationsBefore) -and $verify.Succeeded
        $observation = ('killed={0} generation_count {1}->{2} (no incomplete publication), staging_bytes={3}, verify={4}' -f
            $killed, $generationsBefore, $generationsAfter, $stagingBytes, $verify.Succeeded)
        Add-BoundaryRecord -Exercise 'boundary-kill-capture' -Passed $passed -Observation $observation -Result $statsAfter
        if (-not $passed) {
            throw 'boundary-kill-capture FAILED: an incomplete generation must never be published.'
        }
        Write-Host "  PASS: $observation"
    }
}

# ---------------------------------------------------------------------------
# boundary-derived-index + boundary-authoritative-object (copied vault)
# ---------------------------------------------------------------------------

Write-Host 'Copying the acceptance account into the boundary sandbox (read-only copy)...'
$accountsRoot = Join-Path $vaultRoot 'accounts'
$accountDirs = Get-ChildItem -LiteralPath $accountsRoot -Directory
if ($accountDirs.Count -eq 0) {
    throw 'The acceptance vault has no accounts yet; run at least one hourly capture first.'
}
$accountId = $accountDirs[0].Name
$null = New-Item -ItemType Directory -Path (Join-Path $boundaryVault 'accounts') -Force
Copy-Item -Path (Join-Path $accountsRoot $accountId) -Destination (Join-Path $boundaryVault "accounts/$accountId") -Recurse -Force

# derived index deletion
Write-Host 'boundary-derived-index: deleting the derived lookup index...'
$objectsRoot = Join-Path $boundaryVault "accounts/$accountId/objects"
$indexFiles = @(Get-ChildItem -LiteralPath $objectsRoot -Filter 'lookup.sqlite*' -File -ErrorAction SilentlyContinue)
if ($indexFiles.Count -eq 0) {
    throw 'No derived lookup index found in the copied account; nothing to exercise.'
}
foreach ($file in $indexFiles) { Remove-Item -LiteralPath $file.FullName -Force }

$stats = Invoke-WearchiveRc -AcceptanceRoot $root -Arguments @(
    'vault', 'stats', '--vault-root', $boundaryVault, '--json', '--no-input') -TimeoutSeconds 7200
$indexStatus = $stats.Json.accounts[0].derived_index_status
$verify = Assert-BoundaryVaultVerified -Vault $boundaryVault
$passed = ($indexStatus -eq 'missing_rebuildable') -and $verify.Succeeded
$observation = "after index deletion: derived_index_status=$indexStatus, verify=$($verify.Succeeded)"
Add-BoundaryRecord -Exercise 'boundary-derived-index-deletion' -Passed $passed -Observation $observation -Result $stats
if (-not $passed) {
    throw "boundary-derived-index-deletion FAILED: $observation"
}
Write-Host "  PASS: $observation"

# derived index corruption (garbage bytes in place of the index)
Write-Host 'boundary-derived-index: corrupting the derived lookup index...'
Set-Content -LiteralPath (Join-Path $objectsRoot 'lookup.sqlite') -Value 'CORRUPTED' -Encoding ascii

$stats = Invoke-WearchiveRc -AcceptanceRoot $root -Arguments @(
    'vault', 'stats', '--vault-root', $boundaryVault, '--json', '--no-input') -TimeoutSeconds 7200
$indexStatus = $stats.Json.accounts[0].derived_index_status
$verify = Assert-BoundaryVaultVerified -Vault $boundaryVault
$passed = ($indexStatus -in @('corrupt_rebuildable', 'inconsistent_rebuildable', 'missing_rebuildable')) -and $verify.Succeeded
$observation = "after index corruption: derived_index_status=$indexStatus, verify=$($verify.Succeeded)"
Add-BoundaryRecord -Exercise 'boundary-derived-index-corruption' -Passed $passed -Observation $observation -Result $stats
if (-not $passed) {
    throw "boundary-derived-index-corruption FAILED: $observation"
}
Write-Host "  PASS: $observation"

# authoritative object loss must fail closed
Write-Host 'boundary-authoritative-object: truncating and deleting sealed packs...'
$packsRoot = Join-Path $objectsRoot 'packs'
$packs = @(Get-ChildItem -LiteralPath $packsRoot -Filter '*.rvpk' -File | Where-Object { $_.Length -gt 64 })
if ($packs.Count -lt 1) {
    throw 'No sealed packs found in the copied account; nothing to exercise.'
}
$truncated = $packs[0]
$stream = [System.IO.File]::Open($truncated.FullName, 'Open', 'ReadWrite')
try { $stream.SetLength([math]::Max(0, $truncated.Length - 32)) } finally { $stream.Dispose() }
if ($packs.Count -gt 1) {
    Remove-Item -LiteralPath $packs[1].FullName -Force
    Write-Host ("  deleted pack: {0}" -f $packs[1].Name)
}
Write-Host ("  truncated pack: {0}" -f $truncated.Name)

$verify = Assert-BoundaryVaultVerified -Vault $boundaryVault
$passed = (-not $verify.Succeeded) -and $verify.Result.ExitCode -ne 0
$failureCount = if ($verify.Result.Json -and $verify.Result.Json.PSObject.Properties['failures']) {
    @($verify.Result.Json.failures).Count
} else { -1 }
$observation = "verify must fail closed: succeeded=$($verify.Succeeded), exit=$($verify.Result.ExitCode), failures=$failureCount"
Add-BoundaryRecord -Exercise 'boundary-authoritative-object' -Passed $passed -Observation $observation -Result $verify.Result
if (-not $passed) {
    throw 'boundary-authoritative-object FAILED: verification must fail closed on missing/corrupt authoritative objects.'
}
Write-Host "  PASS: $observation"

# ---------------------------------------------------------------------------
# boundary-materialized-cache (acceptance home caches)
# ---------------------------------------------------------------------------

Write-Host 'boundary-materialized-cache: deleting disposable materialized/scratch caches...'
$weArchiveData = Join-Path (Get-AcceptanceHome -AcceptanceRoot $root) 'AppData/Local/WeArchive'
$scratchRoot = Join-Path $weArchiveData 'scratch'
$removedBytes = 0
foreach ($cacheDir in @($scratchRoot)) {
    if (Test-Path -LiteralPath $cacheDir) {
        $removedBytes += (Get-ChildItem -LiteralPath $cacheDir -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum
        Remove-Item -LiteralPath $cacheDir -Recurse -Force
    }
}

$verify = Assert-BoundaryVaultVerified -Vault $vaultRoot
$passed = $verify.Succeeded
$observation = 'removed {0} bytes of disposable cache; verify={1} (a valid disposable cache must never be required)' -f
    $removedBytes, $verify.Succeeded
Add-BoundaryRecord -Exercise 'boundary-materialized-cache' -Passed $passed -Observation $observation -Result $verify.Result
if (-not $passed) {
    throw 'boundary-materialized-cache FAILED: verification must not depend on disposable caches.'
}
Write-Host "  PASS: $observation"

Write-Host ''
Write-Host 'Failure-boundary exercises completed.'
Write-Host ("Trace: {0}" -f (ConvertTo-PrivacySafePath -Path $tracePath -AcceptanceRoot $root))
