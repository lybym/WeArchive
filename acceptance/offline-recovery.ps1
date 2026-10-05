#Requires -Version 7.0
<#
.SYNOPSIS
    Offline recovery acceptance procedure (Issue #86): ingest/rebuild with the live
    source and key acquisition unavailable, without derived index or caches.

.DESCRIPTION
    Runs the Issue #86 offline recovery sequence against the isolated acceptance
    vault and records every step into
    <recovery-root>\evidence\offline-recovery-trace.jsonl:

      1. pre-state: authoritative vault verify, and current canonical counts via a
         `rebuild` baseline;
      2. source/key state: records the source availability (doctor) and attempts one
         capture — a failed capture with key-acquisition-unavailable is the expected
         offline proof; a successful capture is only acceptable when it reused all
         evidence (zero new payload, no key acquisition attempted);
      3. deletes the derived v2 object index (lookup.sqlite*) for the account;
      4. removes the disposable materialized/scratch caches;
      5. `vault verify` — MUST pass: a valid disposable cache must never be required
         to compensate for missing authoritative CAS data;
      6. deletes the canonical archive and runs `rebuild` into a fresh archive from
         the latest published generation;
      7. compares the fresh canonical counts against the pre-state baseline;
      8. re-verifies the historical-evidence immutability manifest (v1 seed) when
         init-root.ps1 seeded one, and hashes all generation manifests before/after
         the phase — prior published evidence must be unchanged.

    Optional: -ExportConversation <id-or-alias> runs one canonical `export` while the
    source stays offline, recording the Issue #66 regression evidence (export must be
    canonical-only and fully offline).

.PARAMETER Config
    Path to the user-maintained acceptance config (config.json).

.PARAMETER RecoveryRoot
    Where the recovery evidence lives. Defaults to <acceptance-root>\scenario-recovery.

.PARAMETER ExportConversation
    Optional conversation selector for the offline export evidence (Issue #66).

.EXAMPLE
    ./offline-recovery.ps1 -Config D:\acceptance\issue-86\config.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [Alias('Config')] [string] $ConfigPath,
    [string] $RecoveryRoot,
    [string] $ExportConversation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = $PSScriptRoot
Import-Module (Join-Path $scriptDir 'acceptance-harness.psm1') -Force

$config = Read-AcceptanceConfig -Path $ConfigPath
$root = Get-AcceptanceRoot -Config $config
if (-not $RecoveryRoot) { $RecoveryRoot = Join-Path $root 'scenario-recovery' }
$recoveryEvidence = Join-Path $RecoveryRoot 'evidence'
$null = New-Item -ItemType Directory -Path $recoveryEvidence -Force
$vaultRoot = Get-AcceptanceVaultRoot -AcceptanceRoot $root
$archiveRoot = Get-AcceptanceArchiveRoot -AcceptanceRoot $root
$tracePath = Join-Path $recoveryEvidence 'offline-recovery-trace.jsonl'

function Add-RecoveryRecord {
    param(
        [Parameter(Mandatory)] [string] $Step,
        [Parameter(Mandatory)] [bool] $Passed,
        [Parameter(Mandatory)] [string] $Observation,
        [AllowNull()] $Result
    )
    $record = [ordered]@{
        schema_version = 1
        record_type = 'offline_recovery'
        scenario_step = $Step
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

function Invoke-RecoveryStep {
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Arguments, [int] $TimeoutSeconds = 7200)
    return (Invoke-WearchiveRc -AcceptanceRoot $root -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds)
}

# --- Manifest immutability baseline ------------------------------------------

$accountsRoot = Join-Path $vaultRoot 'accounts'
$accountDirs = @(Get-ChildItem -LiteralPath $accountsRoot -Directory)
if ($accountDirs.Count -eq 0) {
    throw 'The acceptance vault has no accounts yet; run the hourly captures first.'
}
if ($accountDirs.Count -gt 1) {
    throw 'The acceptance vault holds more than one account; run offline recovery per isolated root.'
}
$accountId = $accountDirs[0].Name

$manifestBaseline = New-AcceptanceHashManifest -Directory $vaultRoot -AcceptanceRoot $root -Filter @('manifest.json')

# --- Step 1: pre-state --------------------------------------------------------

Write-Host 'offline-recovery step 1: pre-state verify + canonical baseline...'
$verify = Invoke-RecoveryStep -Arguments @('vault', 'verify', '--vault-root', $vaultRoot, '--json', '--no-input')
$verifySucceeded = Get-AcceptanceJsonProperty -Object $verify.Json -Name 'succeeded' -Default $false
if ($verify.ExitCode -ne 0 -or $verifySucceeded -ne $true) {
    Add-RecoveryRecord -Step 'pre-state-verify' -Passed $false `
        -Observation ("vault verify failed before the offline phase (exit {0}). Stderr: {1}" -f
            $verify.ExitCode, $verify.Stderr) -Result $verify
    throw 'Pre-state vault verify failed; the offline phase requires a healthy vault.'
}
Add-RecoveryRecord -Step 'pre-state-verify' -Passed $true -Observation 'vault verify succeeded' -Result $verify

$rebuildBaseline = Invoke-RecoveryStep -Arguments @('rebuild', '--json', '--no-input')
$rebuildSucceeded = Get-AcceptanceJsonProperty -Object $rebuildBaseline.Json -Name 'succeeded' -Default $false
if ($rebuildBaseline.ExitCode -ne 0 -or $rebuildSucceeded -ne $true) {
    Add-RecoveryRecord -Step 'pre-state-rebuild' -Passed $false `
        -Observation ("canonical rebuild baseline failed (exit {0}). Stderr: {1}" -f
            $rebuildBaseline.ExitCode, $rebuildBaseline.Stderr) -Result $rebuildBaseline
    throw 'Pre-state canonical rebuild failed.'
}
$baselineCounts = [ordered]@{
    account_count = [int]$rebuildBaseline.Json.account_count
    participant_count = [int]$rebuildBaseline.Json.participant_count
    conversation_count = [int]$rebuildBaseline.Json.conversation_count
    message_count = [int]$rebuildBaseline.Json.message_count
}
Add-RecoveryRecord -Step 'pre-state-rebuild' -Passed $true -Observation ($baselineCounts | ConvertTo-Json -Compress) -Result $rebuildBaseline
Write-Host ("  baseline counts: $($baselineCounts | ConvertTo-Json -Compress)")

# --- Step 2: source/key state (the offline premise, recorded and fail-closed) --

Write-Host 'offline-recovery step 2: recording the live source/key state...'
$doctor = Invoke-RecoveryStep -Arguments @('doctor', '--json', '--no-input') -TimeoutSeconds 300
if ($doctor.ExitCode -ne 0) {
    Add-RecoveryRecord -Step 'source-key-state' -Passed $false `
        -Observation ("doctor failed (exit {0}); the offline premise cannot be recorded" -f $doctor.ExitCode) -Result $doctor
    throw "doctor failed during the offline phase (exit $($doctor.ExitCode))."
}
$source = Get-AcceptanceJsonProperty -Object $doctor.Json -Name 'source'
$sourceAvailable = [bool](Get-AcceptanceJsonProperty -Object $source -Name 'available')
$sourceVersion = Get-AcceptanceJsonProperty -Object $source -Name 'source_version'
Write-Host ("  doctor source.available = {0} (source_version = {1})" -f $sourceAvailable, $sourceVersion)

# The RC's own attempt is the key-acquisition proof: when materialization is required
# (new/changed evidence), the key can only come from a running, signed-in client.
$capture = Invoke-RecoveryStep -Arguments @('capture', '--json', '--no-input')
$offlineState = 'doctor source.available={0}, source_version={1}' -f $sourceAvailable, $sourceVersion
if ($capture.ExitCode -eq 0) {
    $counters = Get-AcceptanceJsonProperty -Object $capture.Json -Name 'storage_counters'
    $newBytes = [int64](Get-AcceptanceJsonProperty -Object $counters -Name 'new_data_bytes' -Default 0)
    $newNodes = [int64](Get-AcceptanceJsonProperty -Object $counters -Name 'new_map_nodes' -Default 0)
    if ($newBytes -ne 0 -or $newNodes -ne 0) {
        # New evidence WAS materialized, so keys WERE acquirable: the offline premise
        # does not hold. Fail closed and abort — the phase must be re-run with the
        # source/key path actually unavailable.
        $observation = ('FAILED premise: capture succeeded and materialized new evidence ' +
            '(new_data_bytes={0}, new_map_nodes={1}); the source key acquisition succeeded, so ' +
            'the live source/key path was NOT unavailable ({2}). Re-run this phase with the ' +
            'WeChat client closed.') -f $newBytes, $newNodes, $offlineState
        Add-RecoveryRecord -Step 'source-key-state' -Passed $false -Observation $observation -Result $capture
        throw $observation
    }
    $offlineProof = ('OFFLINE (zero-new-payload path): capture succeeded but reused all evidence ' +
        '(new_data_bytes=0, new_map_nodes=0), so no key acquisition was required; {0}.') -f $offlineState
    Add-RecoveryRecord -Step 'source-key-state' -Passed $true -Observation $offlineProof -Result $capture
    Write-Host "  $offlineProof"
} else {
    $errorCode = Get-AcceptanceJsonProperty -Object (Get-AcceptanceJsonProperty -Object $capture.Json -Name 'error') -Name 'code'
    $offlineProof = ('OFFLINE (capture-refusal path): capture failed with exit {0}, error code {1}: ' +
        'the source/key acquisition could not succeed; {2}. Stderr: {3}') -f
        $capture.ExitCode, $errorCode, $offlineState, ($capture.Stderr.Trim() -replace '\s+', ' ')
    Add-RecoveryRecord -Step 'source-key-state' -Passed $true -Observation $offlineProof -Result $capture
    Write-Host "  $offlineProof"
}

# --- Step 3-4: derived index + disposable caches ------------------------------

Write-Host 'offline-recovery step 3: deleting the derived v2 object index...'
$objectsRoot = Join-Path $vaultRoot "accounts/$accountId/objects"
$indexFiles = @(Get-ChildItem -LiteralPath $objectsRoot -Filter 'lookup.sqlite*' -File -ErrorAction SilentlyContinue)
foreach ($file in $indexFiles) { Remove-Item -LiteralPath $file.FullName -Force }
Add-RecoveryRecord -Step 'delete-derived-index' -Passed $true `
    -Observation ("removed {0} derived index file(s) for {1}" -f $indexFiles.Count, $accountId)
Write-Host ("  removed {0} file(s)." -f $indexFiles.Count)

Write-Host 'offline-recovery step 4: removing disposable materialized/scratch caches...'
$scratchRoot = Join-Path (Get-AcceptanceDataRoot -AcceptanceRoot $root) 'scratch'
if (Test-Path -LiteralPath $scratchRoot) {
    Remove-Item -LiteralPath $scratchRoot -Recurse -Force
}
Add-RecoveryRecord -Step 'delete-materialized-cache' -Passed $true `
    -Observation 'removed scratch/materialized cache directory'
Write-Host '  caches removed.'

# --- Step 5: verify without derived state -------------------------------------

Write-Host 'offline-recovery step 5: authoritative vault verify without index/cache...'
$stats = Invoke-RecoveryStep -Arguments @('vault', 'stats', '--vault-root', $vaultRoot, '--json', '--no-input')
$statAccounts = Get-AcceptanceJsonProperty -Object $stats.Json -Name 'accounts'
if ($stats.ExitCode -ne 0 -or $null -eq $statAccounts -or @($statAccounts).Count -eq 0) {
    Add-RecoveryRecord -Step 'verify-without-derived-state' -Passed $false `
        -Observation ("vault stats failed or produced no accounts document (exit {0})" -f $stats.ExitCode) -Result $stats
    throw "vault stats failed during the offline phase (exit $($stats.ExitCode))."
}
$indexStatus = Get-AcceptanceJsonProperty -Object @($statAccounts)[0] -Name 'derived_index_status'
$verify = Invoke-RecoveryStep -Arguments @('vault', 'verify', '--vault-root', $vaultRoot, '--json', '--no-input')
$verifySucceeded = Get-AcceptanceJsonProperty -Object $verify.Json -Name 'succeeded' -Default $false
$passed = $verify.ExitCode -eq 0 -and $verifySucceeded -eq $true -and $indexStatus -eq 'missing_rebuildable'
Add-RecoveryRecord -Step 'verify-without-derived-state' -Passed $passed `
    -Observation ("derived_index_status={0}, verify={1} (exit {2}): authoritative CAS evidence alone sustains verification" -f
        $indexStatus, $verifySucceeded, $verify.ExitCode) -Result $verify
if (-not $passed) {
    throw "Verify without derived state failed (index status: $indexStatus, verify succeeded: $verifySucceeded)."
}
Write-Host '  PASS.'

# --- Step 6-7: fresh canonical rebuild ----------------------------------------

Write-Host 'offline-recovery step 6: deleting the canonical archive and rebuilding fresh...'
if (Test-Path -LiteralPath $archiveRoot) {
    Remove-Item -LiteralPath $archiveRoot -Recurse -Force
}
$rebuild = Invoke-RecoveryStep -Arguments @('rebuild', '--json', '--no-input')
if ($rebuild.ExitCode -ne 0 -or -not $rebuild.Json.succeeded) {
    Add-RecoveryRecord -Step 'fresh-rebuild' -Passed $false -Observation 'fresh canonical rebuild failed offline' -Result $rebuild
    throw 'Offline fresh canonical rebuild failed.'
}
$freshCounts = [ordered]@{
    account_count = [int]$rebuild.Json.account_count
    participant_count = [int]$rebuild.Json.participant_count
    conversation_count = [int]$rebuild.Json.conversation_count
    message_count = [int]$rebuild.Json.message_count
}
$countsMatch = ($freshCounts.account_count -eq $baselineCounts.account_count) -and
    ($freshCounts.participant_count -eq $baselineCounts.participant_count) -and
    ($freshCounts.conversation_count -eq $baselineCounts.conversation_count) -and
    ($freshCounts.message_count -eq $baselineCounts.message_count)
Add-RecoveryRecord -Step 'fresh-rebuild' -Passed $countsMatch `
    -Observation ("fresh archive offline: {0} (baseline {1}, match={2})" -f
        ($freshCounts | ConvertTo-Json -Compress), ($baselineCounts | ConvertTo-Json -Compress), $countsMatch) -Result $rebuild
if (-not $countsMatch) {
    throw "Fresh rebuild counts diverge from the baseline: $($freshCounts | ConvertTo-Json -Compress)"
}
Write-Host '  PASS: fresh canonical archive matches the baseline counts.'

# --- Step 8: immutability ------------------------------------------------------

Write-Host 'offline-recovery step 8: historical evidence immutability...'
$manifestAfter = New-AcceptanceHashManifest -Directory $vaultRoot -AcceptanceRoot $root -Filter @('manifest.json')
$immutable = $true
$violations = @()
$baselineEntries = Get-AcceptanceManifestEntries -Entries $manifestBaseline.entries
$afterEntries = Get-AcceptanceManifestEntries -Entries $manifestAfter.entries
foreach ($entry in $baselineEntries) {
    $after = $afterEntries | Where-Object { $_.Name -eq $entry.Name } | Select-Object -First 1
    if (-not $after -or $after.Value -ne $entry.Value) {
        $immutable = $false
        $violations += $entry.Name
    }
}
Add-RecoveryRecord -Step 'manifest-immutability' -Passed $immutable `
    -Observation ("{0} generation manifests hashed before/after; violations: {1}" -f
        $baselineEntries.Count,
        ($(if ($violations.Count) { $violations -join ', ' } else { 'none' })))
if (-not $immutable) {
    throw ("Generation manifests changed during the offline phase: {0}" -f ($violations -join ', '))
}

$seedPath = Join-Path $root 'evidence/v1-seed-hashes.json'
if (Test-Path -LiteralPath $seedPath) {
    $seedManifest = Get-Content -LiteralPath $seedPath -Raw | ConvertFrom-Json
    $seedOk = Test-AcceptanceHashManifest -Manifest $seedManifest -AcceptanceRoot $root
    Add-RecoveryRecord -Step 'v1-seed-immutability' -Passed $seedOk `
        -Observation 'pre-existing copied v1 evidence unchanged'
    if (-not $seedOk) {
        throw 'Pre-existing v1 evidence changed during the acceptance run.'
    }
}

# --- Optional: offline canonical export (Issue #66 regression) -----------------

if ($ExportConversation) {
    Write-Host ("offline-recovery: canonical export of '{0}' while fully offline (Issue #66)..." -f $ExportConversation)
    $export = Invoke-RecoveryStep -Arguments @(
        'export', '--conversation', $ExportConversation, '--json', '--no-input') -TimeoutSeconds 7200
    $exportOk = $export.ExitCode -eq 0
    Add-RecoveryRecord -Step 'offline-export-issue66' -Passed $exportOk `
        -Observation ('canonical-only export ran offline (source unavailable); exit={0}' -f $export.ExitCode) -Result $export
    if (-not $exportOk) {
        throw "Offline canonical export failed (Issue #66 regression): $($export.Stderr)"
    }
}

Write-Host ''
Write-Host 'Offline recovery acceptance procedure completed.'
Write-Host ("Trace: {0}" -f (ConvertTo-PrivacySafePath -Path $tracePath -AcceptanceRoot $root))
