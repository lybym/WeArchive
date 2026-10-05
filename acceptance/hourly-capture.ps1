#Requires -Version 7.0
<#
.SYNOPSIS
    Runs one scheduled hourly capture/verify cycle and appends the evidence record
    (Issue #86 main acceptance trace).

.DESCRIPTION
    This is the script the scheduled task invokes every hour. For the CURRENT planned
    hourly point it:

      1. fills in missed_no_execution records for any planned points that passed with
         no execution at all (machine off / task not triggered) — never silently
         skipped;
      2. no-ops when the current point already has a record (idempotent re-runs);
      3. runs `capture --account <selector>` against the isolated acceptance vault;
      4. runs `vault verify --vault-root <acceptance vault>` on the published result
         (every successfully published generation must pass authoritative verification);
      5. classifies the point (success / success_partial / failed_capture /
         failed_verify) and appends exactly one evidence record to the hourly trace,
         carrying every per-capture field Issue #86 requires, including the no-change
         hour expectation check and the exact RC binding;
      6. optionally samples read-only `vault stats` accounting (-SampleStats).

    The trace is <acceptance-root>\evidence\hourly-trace.jsonl. Re-running with
    -Retry replaces a failed record for the CURRENT point after a new attempt.

    -WhatIf / -Summarize are read-only modes: -WhatIf resolves the current point and
    prints what would happen without touching the vault or trace; -Summarize renders
    the aggregate evidence summary for the final issue post.

.PARAMETER Config
    Path to the user-maintained acceptance config (config.json).

.PARAMETER Retry
    Re-attempt the current point even if a failed record already exists for it.

.PARAMETER SampleStats
    Additionally sample `vault stats` (read-only accounting) into this point's record.

.PARAMETER SkipVerify
    Skip the authoritative vault verify step. Default is OFF (the acceptance criteria
    require verification of every successfully published generation); use only with
    an explicit operator reason recorded in the runbook.

.PARAMETER Summarize
    Print the aggregate evidence summary (points accounted, classification breakdown,
    no-change hours, totals) instead of running a cycle.

.EXAMPLE
    ./hourly-capture.ps1 -Config D:\acceptance\issue-86\config.json
    ./hourly-capture.ps1 -Config D:\acceptance\issue-86\config.json -Summarize
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [Alias('Config')] [string] $ConfigPath,
    [switch] $Retry,
    [switch] $SampleStats,
    [switch] $SkipVerify,
    [switch] $Summarize
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = $PSScriptRoot
Import-Module (Join-Path $scriptDir 'acceptance-harness.psm1') -Force

$config = Read-AcceptanceConfig -Path $ConfigPath
$root = Get-AcceptanceRoot -Config $config
$tracePath = Join-Path $root 'evidence/hourly-trace.jsonl'
$plannedHours = [int]$config.run.planned_hours

if ($Summarize) {
    $records = @(Read-AcceptanceTrace -TracePath $tracePath)
    $current = 0
    try { $current = Get-AcceptanceCurrentPointIndex -Config $config } catch { $current = $plannedHours }
    $byOutcome = @{}
    foreach ($record in $records) {
        $outcome = $record.planned_point.outcome
        if (-not $byOutcome.ContainsKey($outcome)) { $byOutcome[$outcome] = 0 }
        $byOutcome[$outcome]++
    }

    $captureTotals = [ordered]@{
        logical_generation_bytes = 0
        new_data_bytes = 0
        new_map_nodes = 0
        new_pack_bytes = 0
    }
    $noChangePoints = 0
    $noChangeViolations = @()
    $partialPoints = @()
    foreach ($record in $records) {
        if ($record.capture.storage_counters) {
            $captureTotals.new_data_bytes += [int64]$record.capture.storage_counters.new_data_bytes
            $captureTotals.new_map_nodes += [int64]$record.capture.storage_counters.new_map_nodes
            $captureTotals.new_pack_bytes += [int64]$record.capture.storage_counters.new_pack_bytes
            $captureTotals.logical_generation_bytes += [int64]$record.capture.storage_counters.logical_bytes
        }
        if ($record.capture.no_change_expectation_met -eq $true) {
            $noChangePoints++
        }
        if ($record.capture.no_change_expectation_met -eq $false) {
            $noChangeViolations += $record.planned_point.index
        }
        if ($record.planned_point.outcome -eq 'success_partial') {
            $partialPoints += $record.planned_point.index
        }
    }

    $summary = [ordered]@{
        schema_version = 1
        generated_at = (Get-Date).ToString('o')
        rc = [ordered]@{
            release_tag = $config.rc.release_tag
            version = $config.rc.expected_version
            commit_sha = $config.rc.commit_sha
            asset_url = $config.rc.asset_url
            asset_sha256 = $config.rc.asset_sha256.ToLowerInvariant()
        }
        run = [ordered]@{
            started_at_local = $config.run.started_at_local
            planned_hours = $plannedHours
            elapsed_planned_points = [math]::Min($current, $plannedHours)
            points_recorded = $records.Count
            points_unaccounted = ([math]::Min($current, $plannedHours) - $records.Count)
        }
        classification = $byOutcome
        no_change = [ordered]@{
            no_change_points = $noChangePoints
            expectation_violations = $noChangeViolations
        }
        partial_points = $partialPoints
        storage_totals = $captureTotals
    }

    $summary | ConvertTo-Json -Depth 16
    return
}

# ---- Scheduled cycle --------------------------------------------------------

$now = Get-Date
$current = Get-AcceptanceCurrentPointIndex -Config $config -At $now
if ($current -ge $plannedHours) {
    Write-Host "All $plannedHours planned points have passed; nothing to do. Use -Summarize for the aggregate."
    return
}

# The WhatIf gate comes first: a dry run must not touch the trace or the vault.
if (-not $PSCmdlet.ShouldProcess("acceptance vault point $current", 'capture + vault verify')) {
    $whatIfMessage = ('WhatIf: would execute point {0} (planned {1}): fill in missed past points, ' +
        'capture --account <configured selector>, then vault verify; outcome appended to {2}.') -f
        $current, (Get-AcceptancePlannedPointTime -Config $config -Index $current).ToString('o'),
        (ConvertTo-PrivacySafePath -Path $tracePath -AcceptanceRoot $root)
    Write-Host $whatIfMessage
    return
}

$records = @(Read-AcceptanceTrace -TracePath $tracePath)
$recordedIndexes = @($records | ForEach-Object { [int]$_.planned_point.index })

# 1. Classify never-executed past points (machine off / task not triggered).
$missed = Get-AcceptanceMissedPointIndexes -Config $config -RecordedIndexes $recordedIndexes -At $now
foreach ($index in $missed) {
    $record = New-AcceptanceMissedRecord -Config $config -AcceptanceRoot $root -PointIndex $index `
        -RecordedAt $now -Explanation ('No harness execution observed for this planned hourly point ' +
        '(host powered off, suspended or the scheduled task did not trigger). No capture was attempted.')
    $null = Write-AcceptanceTraceRecord -TracePath $tracePath -Record $record
    Write-Warning ("Planned point {0} ({1}) recorded as missed_no_execution." -f $index, $record.planned_point.planned_at)
}

# 2. Idempotency: the current point must not be re-executed silently.
$records = @(Read-AcceptanceTrace -TracePath $tracePath)
$existingRecord = $records | Where-Object { [int]$_.planned_point.index -eq $current } | Select-Object -First 1
if ($existingRecord) {
    $outcome = $existingRecord.planned_point.outcome
    if ($Retry -and $outcome -in @('failed_capture', 'failed_verify')) {
        Write-Host "Point $current has a failed record; -Retry re-attempts it."
    } else {
        Write-Host "Point $current already recorded ($outcome); nothing to do (idempotent re-run)."
        return
    }
}

# 3. The scheduled capture/verify cycle.
Write-Host "Point ${current}: capturing..."
$vaultRoot = Join-Path (Get-AcceptanceHome -AcceptanceRoot $root) 'AppData/Local/WeArchive/rawvault'
$capture = Invoke-WearchiveRc -AcceptanceRoot $root -Arguments @(
    'capture', '--account', $config.capture.account_selector, '--json', '--no-input')

$captureSummary = if ($capture.Json) {
    'generation={0} mode={1} completeness={2} expected={3} captured={4} reused={5}' -f
        $capture.Json.generation_id, $capture.Json.mode, $capture.Json.completeness,
        $capture.Json.coverage_summary.expected,
        $capture.Json.coverage_summary.captured,
        $capture.Json.coverage_summary.reused
} else {
    "exit=$($capture.ExitCode) (no JSON document)"
}
Write-Host "  capture: $captureSummary"

$verify = $null
$verifyExit = 1
$verifyJson = $null
if ($capture.ExitCode -eq 0) {
    if ($SkipVerify) {
        Write-Warning 'vault verify skipped by -SkipVerify; the point cannot count as a fully verified success.'
    } else {
        Write-Host '  verifying (authoritative vault verify)...'
        $verify = Invoke-WearchiveRc -AcceptanceRoot $root -Arguments @(
            'vault', 'verify', '--vault-root', $vaultRoot, '--json', '--no-input') -TimeoutSeconds 7200
        $verifyExit = $verify.ExitCode
        $verifyJson = $verify.Json
        Write-Host ("  verify: exit={0} succeeded={1}" -f $verify.ExitCode, $verify.Json.succeeded)
    }
}

$stats = $null
if ($SampleStats) {
    Write-Host '  sampling vault stats (read-only accounting)...'
    $stats = Invoke-WearchiveRc -AcceptanceRoot $root -Arguments @(
        'vault', 'stats', '--vault-root', $vaultRoot, '--json', '--no-input') -TimeoutSeconds 7200
}

$outcome = Get-AcceptancePointOutcome `
    -CaptureExitCode $capture.ExitCode -CaptureJson $capture.Json `
    -VerifyExitCode $verifyExit -VerifyJson $verifyJson

$explanation = $null
if ($SkipVerify -and $capture.ExitCode -eq 0 -and $outcome -eq 'failed_verify') {
    # No verify ran; the published generation is counted as published-but-unverified.
    $outcome = 'success_partial'
}

$explanation = $null
if ($outcome -eq 'failed_capture') {
    $code = $null
    if ($capture.Json -and $capture.Json.PSObject.Properties['error']) {
        $code = $capture.Json.error.code
    }
    $explanation = "Capture failed (exit $($capture.ExitCode), error code: $code). Stderr: " +
        ($capture.Stderr.Trim() -replace '\s+', ' ')
    if ($explanation.Length -gt 800) { $explanation = $explanation.Substring(0, 800) + ' [...]' }
    Write-Warning "Point $current classified $outcome. $explanation"
} elseif ($outcome -eq 'failed_verify') {
    $explanation = 'Capture published a complete generation but the authoritative vault verify did not pass. ' +
        'This is an acceptance-blocking failure that must be investigated before the run continues.'
    Write-Warning "Point $current classified $outcome."
} elseif ($outcome -eq 'success_partial') {
    if ($SkipVerify) {
        $explanation = 'Authoritative vault verify was skipped by operator (-SkipVerify); the published ' +
            'generation is recorded as published-but-unverified and cannot count as a fully verified success.'
    } else {
        $diagnostics = @()
        if ($capture.Json -and $capture.Json.PSObject.Properties['diagnostics']) {
            $diagnostics = @($capture.Json.diagnostics | ForEach-Object { $_.code })
        }
        $explanation = 'Published generation is partial; coverage gaps: ' +
            (($diagnostics | Select-Object -Unique) -join ', ') + '.'
    }
    Write-Warning "Point $current classified $outcome. $explanation"
}

$record = New-AcceptanceCaptureRecord -Config $config -AcceptanceRoot $root -PointIndex $current `
    -Outcome $outcome -ExecutedAt $now -CaptureResult $capture -VerifyResult $verify `
    -StatsResult $stats -StatsSampled:$SampleStats -Explanation $explanation
$action = Write-AcceptanceTraceRecord -TracePath $tracePath -Record $record
Write-Host ("Point {0} recorded ({1}, {2}) to {3}." -f $current, $outcome, $action,
    (ConvertTo-PrivacySafePath -Path $tracePath -AcceptanceRoot $root))

if ($outcome -in @('failed_verify')) {
    throw "Point $current failed authoritative verification; investigate before the next scheduled point."
}
