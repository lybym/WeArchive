#Requires -Version 7.0
<#
.SYNOPSIS
    Shared library for the WeArchive official-RC acceptance harness (Issue #86).

.DESCRIPTION
    Every acceptance script dot-sources this module. It owns the three cross-cutting
    contracts the acceptance run depends on:

      1. RC invocation isolation. The official RC resolves its Raw Vault, canonical
         archive, key cache and scratch roots from the per-user local application data
         folder (src/WeArchive.Cli/Program.cs). The harness redirects the RC process's
         profile environment (USERPROFILE and derived variables) into the acceptance
         root, so every RC write lands inside the isolated acceptance home while source
         discovery (real WeChat data) keeps resolving through the real user profile.
         The redirect is re-proven by the `doctor` gate in init-root.ps1, not assumed.

      2. Machine-readable evidence. Every planned hourly capture point produces exactly
         one evidence record (JSON line) in the hourly trace, carrying every per-capture
         field Issue #86 requires. Paths are rendered privacy-safe (relative to the
         acceptance root) before they enter a record.

      3. Point classification. Success, partial success, capture failure, verify
         failure and missed (never-executed) planned points are classified by
         Get-AcceptancePointOutcome / Get-AcceptanceMissedPointIndexes, so no planned
         point is ever left unexplained.

    This module contains no product logic and never writes outside the acceptance root
    it is given.

.NOTES
    Privacy: nothing in this module prints or records source keys, fingerprints or
    personal paths. The acceptance config itself is user-maintained outside the
    repository and is never committed.
#>

Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

$script:AcceptanceConfigSchemaVersion = 1
$script:AcceptanceTraceSchemaVersion = 1

function Read-AcceptanceConfig {
    <#
    .SYNOPSIS
        Loads and validates the user-maintained acceptance config (config.json).
    #>
    param(
        [Parameter(Mandatory)] [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Acceptance config not found: $Path"
    }

    $config = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json

    if (-not $config.PSObject.Properties['schema_version'] -or
        $config.schema_version -ne $script:AcceptanceConfigSchemaVersion) {
        throw "Acceptance config must carry schema_version = $($script:AcceptanceConfigSchemaVersion)."
    }

    foreach ($section in @('run', 'rc', 'capture')) {
        if (-not $config.PSObject.Properties[$section]) {
            throw "Acceptance config is missing the '$section' section."
        }
    }

    foreach ($field in @('planned_hours', 'started_at_local')) {
        if (-not $config.run.PSObject.Properties[$field]) {
            throw "Acceptance config is missing run.$field."
        }
    }

    # Issue #86 floor: at least 7 continuous days with >= 168 planned hourly points.
    if ([int]$config.run.planned_hours -lt 168) {
        throw ("run.planned_hours is {0}; Issue #86 requires at least 168 planned hourly " +
               'points over >= 7 continuous days.') -f [int]$config.run.planned_hours
    }

    foreach ($field in @('asset_url', 'asset_sha256', 'expected_version', 'commit_sha', 'release_tag')) {
        if (-not $config.rc.PSObject.Properties[$field] -or [string]::IsNullOrWhiteSpace([string]$config.rc.$field)) {
            throw "Acceptance config is missing rc.$field (the exact official-RC binding)."
        }
    }

    if ($config.rc.asset_sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'rc.asset_sha256 must be a 64-character hex SHA-256 digest.'
    }

    foreach ($field in @('account_selector')) {
        if (-not $config.capture.PSObject.Properties[$field] -or
            [string]::IsNullOrWhiteSpace([string]$config.capture.$field)) {
            throw "Acceptance config is missing capture.$field."
        }
    }

    if (-not $config.PSObject.Properties['acceptance_root'] -or
        [string]::IsNullOrWhiteSpace([string]$config.acceptance_root)) {
        throw 'Acceptance config is missing acceptance_root (the isolated root; never hardcode personal paths in a committed file).'
    }

    return $config
}

function Get-AcceptanceRoot {
    <#
    .SYNOPSIS
        Returns the verified absolute acceptance root for a config, creating nothing.
    #>
    param(
        [Parameter(Mandatory)] $Config
    )

    $root = [System.IO.Path]::GetFullPath($Config.acceptance_root)
    if (-not (Test-Path -LiteralPath $root)) {
        throw "Acceptance root does not exist. Run init-root.ps1 first: $root"
    }
    return $root
}

function Get-AcceptanceHome {
    <#
    .SYNOPSIS
        Returns the redirected-profile home directory inside the acceptance root.
    #>
    param(
        [Parameter(Mandatory)] [string] $AcceptanceRoot
    )
    return (Join-Path $AcceptanceRoot 'home')
}

function Get-AcceptanceDataRoot {
    <#
    .SYNOPSIS
        Returns the redirected WeArchive data root (%LOCALAPPDATA%\WeArchive inside
        the acceptance home). The single place the product-internal layout is named:
        Raw Vault, archive, key cache and scratch roots all live under it, and the
        isolation gate re-proves the layout with `doctor` before the run starts.
    #>
    param(
        [Parameter(Mandatory)] [string] $AcceptanceRoot
    )
    return (Join-Path (Get-AcceptanceHome -AcceptanceRoot $AcceptanceRoot) 'AppData/Local/WeArchive')
}

function Get-AcceptanceVaultRoot {
    <#
    .SYNOPSIS
        Returns the acceptance Raw Vault root inside the redirected data root.
    #>
    param(
        [Parameter(Mandatory)] [string] $AcceptanceRoot
    )
    return (Join-Path (Get-AcceptanceDataRoot -AcceptanceRoot $AcceptanceRoot) 'rawvault')
}

function Get-AcceptanceArchiveRoot {
    <#
    .SYNOPSIS
        Returns the acceptance canonical archive root inside the redirected data root.
    #>
    param(
        [Parameter(Mandatory)] [string] $AcceptanceRoot
    )
    return (Join-Path (Get-AcceptanceDataRoot -AcceptanceRoot $AcceptanceRoot) 'archive')
}

function Get-AcceptanceRcExe {
    <#
    .SYNOPSIS
        Returns the path of the RC binary inside the acceptance root, verifying presence.
    #>
    param(
        [Parameter(Mandatory)] [string] $AcceptanceRoot
    )

    $exe = Join-Path $AcceptanceRoot 'rc/win-x64/WeArchive.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Official RC binary not found. Run verify-rc.ps1 first: $exe"
    }
    return $exe
}

# ---------------------------------------------------------------------------
# RC invocation (isolated environment redirect)
# ---------------------------------------------------------------------------

function Invoke-WearchiveRc {
    <#
    .SYNOPSIS
        Runs the official RC with an acceptance-home environment redirect and returns
        a structured result. This is the ONLY way acceptance scripts execute the RC.

    .DESCRIPTION
        The redirect sets the child process's USERPROFILE (and the variables derived
        from it) into the home directory: <acceptance-root>\home by default, or the
        -HomeDirectory directory for isolated scenario sandboxes. On Windows the
        known-folder API expands the per-user shell-folder registry values
        (REG_EXPAND_SZ, %USERPROFILE%-based) against the process environment, so the
        RC's LocalApplicationData — and with it the Raw Vault, canonical archive, key
        cache and scratch roots — resolves inside that home. The real user profile
        itself (FOLDERID_Profile) does not follow the override, so WeChat source
        discovery still sees the real client data.

        init-root.ps1 proves this behavior with the `doctor` gate before the run
        starts; this wrapper additionally refuses to run if the home is missing.
    #>
    param(
        [Parameter(Mandatory)] [string] $AcceptanceRoot,
        [string] $HomeDirectory,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Arguments,
        [int] $TimeoutSeconds = 3600
    )

    $exe = Get-AcceptanceRcExe -AcceptanceRoot $AcceptanceRoot
    if (-not $HomeDirectory) {
        $HomeDirectory = Get-AcceptanceHome -AcceptanceRoot $AcceptanceRoot
    }
    $home_ = $HomeDirectory
    if (-not (Test-Path -LiteralPath $home_)) {
        throw "Acceptance home is missing; run init-root.ps1 first: $home_"
    }

    $localAppData = Join-Path $home_ 'AppData/Local'
    $temp = Join-Path $localAppData 'Temp'
    $drive = [System.IO.Path]::GetPathRoot($home_).TrimEnd('\', '/')
    $homePath = $home_.Substring($drive.Length)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $exe
    # Windows argv quoting: escape embedded quotes AND trailing backslashes (a trailing
    # backslash before the closing quote would escape the quote and corrupt parsing).
    $startInfo.Arguments = ($Arguments | ForEach-Object {
        '"' + (($_ -replace '(\\+)$', '$1$1') -replace '"', '\"') + '"'
    }) -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = $AcceptanceRoot

    foreach ($pair in @(
        @('USERPROFILE', $home_),
        @('LOCALAPPDATA', $localAppData),
        @('APPDATA', (Join-Path $home_ 'AppData/Roaming')),
        @('HOMEDRIVE', $drive),
        @('HOMEPATH', $homePath),
        @('TMP', $temp),
        @('TEMP', $temp)
    )) {
        $startInfo.EnvironmentVariables[$pair[0]] = $pair[1]
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $null = $process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $exited = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        try { $process.Kill($true) } catch [System.Management.Automation.PSInvalidCastException] { $process.Kill() }
        throw "RC invocation timed out after $TimeoutSeconds seconds: wearchive $($Arguments -join ' ')"
    }
    $sw.Stop()

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()

    $json = $null
    $singleDocument = $false
    $trimmed = $stdout.Trim()
    if ($trimmed) {
        try {
            $json = $trimmed | ConvertFrom-Json -ErrorAction Stop
            $singleDocument = $true
        } catch {
            $json = $null
            $singleDocument = $false
        }
    }

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Stdout = $stdout
        Stderr = $stderr
        DurationMs = $sw.ElapsedMilliseconds
        Json = $json
        SingleJsonDocument = $singleDocument
    }
}

# ---------------------------------------------------------------------------
# Point classification
# ---------------------------------------------------------------------------

# Stable outcome vocabulary (Issue #86: every planned point must be classified).
$script:AcceptanceOutcomes = @(
    'success',             # capture exit 0, complete, vault verify passed
    'success_partial',     # capture published but completeness = partial (explained by diagnostics)
    'failed_capture',      # capture exited non-zero (error code recorded)
    'failed_verify'        # capture published but authoritative vault verify failed
) + @('missed_no_execution') # planned point passed with no harness execution (machine off, task missed)

function Get-AcceptanceJsonProperty {
    <#
    .SYNOPSIS
        StrictMode-safe access to one property of a parsed RC JSON document.
    .DESCRIPTION
        The CLI contract guarantees one JSON document per invocation, but FAILURE
        shapes differ from success shapes (for example the standard
        `{"error":{code,message}}` envelope has no `succeeded` property, and an
        unparseable stream yields $null). Every JSON access outside the well-formed
        success shape MUST go through this helper; unguarded strict-mode property
        access on such documents crashes the recorder after the operation ran but
        before the evidence record is written, which would fabricate a
        `missed_no_execution` classification on the next cycle.
    #>
    param(
        [AllowNull()] $Object,
        [Parameter(Mandatory)] [string] $Name,
        [AllowNull()] $Default = $null
    )

    if ($null -eq $Object) {
        return $Default
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $Default
    }
    return $property.Value
}

function Get-AcceptancePointOutcome {
    <#
    .SYNOPSIS
        Classifies one executed capture point from the RC results.
    #>
    param(
        [int] $CaptureExitCode,
        [AllowNull()] $CaptureJson,
        [int] $VerifyExitCode,
        [AllowNull()] $VerifyJson
    )

    if ($CaptureExitCode -ne 0) {
        return 'failed_capture'
    }
    if ($null -eq $CaptureJson -or -not $CaptureJson.PSObject.Properties['completeness']) {
        return 'failed_capture'
    }
    if ($CaptureJson.completeness -ne 'complete') {
        # A published partial generation is a successful capture with an explained gap.
        return 'success_partial'
    }
    if ($VerifyExitCode -ne 0 -or
        $null -eq $VerifyJson -or
        -not $VerifyJson.PSObject.Properties['succeeded'] -or
        $VerifyJson.succeeded -ne $true) {
        return 'failed_verify'
    }
    return 'success'
}

function Get-AcceptanceCaptureSummary {
    <#
    .SYNOPSIS
        Renders the one-line presentation summary of a capture result.
    .DESCRIPTION
        The RC emits `{"error":{code,message}}` on stdout for EVERY failed capture,
        so a truthy Json document does NOT imply the success shape. This renderer is
        the single strict-mode-safe place that distinguishes the two shapes; capture
        presentation must never read success-shape fields off an unverified document.
    #>
    param(
        [Parameter(Mandatory)] [AllowNull()] $Result
    )

    $exitCode = Get-AcceptanceJsonProperty -Object $Result -Name 'ExitCode'
    $json = Get-AcceptanceJsonProperty -Object $Result -Name 'Json'

    if ($exitCode -eq 0) {
        if ($null -eq $json) {
            return "exit=0 (no JSON document; CLI contract violation)"
        }
        $generationId = Get-AcceptanceJsonProperty -Object $json -Name 'generation_id'
        $mode = Get-AcceptanceJsonProperty -Object $json -Name 'mode'
        $completeness = Get-AcceptanceJsonProperty -Object $json -Name 'completeness'
        if ($null -eq $generationId -or $null -eq $completeness) {
            return "exit=0 (JSON document lacks the capture success shape; CLI contract violation)"
        }
        $coverage = Get-AcceptanceJsonProperty -Object $json -Name 'coverage_summary'
        return 'generation={0} mode={1} completeness={2} expected={3} captured={4} reused={5}' -f
            $generationId, $mode, $completeness,
            (Get-AcceptanceJsonProperty -Object $coverage -Name 'expected'),
            (Get-AcceptanceJsonProperty -Object $coverage -Name 'captured'),
            (Get-AcceptanceJsonProperty -Object $coverage -Name 'reused')
    }

    # Failure shape: the standard error envelope (or nothing at all).
    $errorCode = Get-AcceptanceJsonProperty -Object (Get-AcceptanceJsonProperty -Object $json -Name 'error') -Name 'code'
    $errorMessage = Get-AcceptanceJsonProperty -Object (Get-AcceptanceJsonProperty -Object $json -Name 'error') -Name 'message'
    $summary = "failed: exit=$exitCode code=$errorCode"
    if ($errorMessage) {
        $summary += ' message=' + (($errorMessage -replace '\s+', ' '))
        if ($summary.Length -gt 400) { $summary = $summary.Substring(0, 400) + ' [...]' }
    }
    return $summary
}

function Test-AcceptanceOfflineCaptureProof {
    <#
    .SYNOPSIS
        Classifies one RC capture attempt as proof (or not) of the offline premise:
        the live WeChat source/key path cannot succeed.
    .DESCRIPTION
        Validated against the RC's capture-failure taxonomy at the pinned RC commit
        (docs/CLI.md + CaptureCommand.cs): failed captures emit the standard error
        envelope with code `failure` (the key-acquisition-failed fatal lands here,
        with the "WeChat is not running..." message), `source_unavailable`,
        `no_accounts` or `account_not_found`; exit 2 usage; exit 130 cancellation.

        Proof classes:
          - capture-refusal (source/key path): exit != 0 with code `source_unavailable`
            (the source data path is gone) or code `failure` whose message indicates
            the client/key path ("not running" / "key"), i.e. key acquisition cannot
            succeed with the client closed.
          - zero-new-payload: exit 0 with zero new data bytes and zero new map nodes
            (all evidence reused; no key acquisition was required).
        Everything else — including `account_not_found`/`no_accounts` (configuration
        problems, not offline evidence), cancellation, or an unrelated `failure` — is
        INCONCLUSIVE and must abort the phase instead of being recorded as proof.
    #>
    param(
        [Parameter(Mandatory)] [AllowNull()] $Result
    )

    $exitCode = Get-AcceptanceJsonProperty -Object $Result -Name 'ExitCode'
    $json = Get-AcceptanceJsonProperty -Object $Result -Name 'Json'

    if ($exitCode -eq 0) {
        $counters = Get-AcceptanceJsonProperty -Object $json -Name 'storage_counters'
        $newBytes = [int64](Get-AcceptanceJsonProperty -Object $counters -Name 'new_data_bytes' -Default 0)
        $newNodes = [int64](Get-AcceptanceJsonProperty -Object $counters -Name 'new_map_nodes' -Default 0)
        if ($newBytes -eq 0 -and $newNodes -eq 0) {
            return [pscustomobject]@{
                Proven = $true
                Class = 'zero-new-payload'
                Reason = 'capture succeeded but reused all evidence (new_data_bytes=0, new_map_nodes=0); no key acquisition was required.'
            }
        }
        return [pscustomobject]@{
            Proven = $false
            Class = 'materialized'
            Reason = ("capture succeeded and materialized new evidence (new_data_bytes={0}, new_map_nodes={1}); " +
                      'the source key acquisition succeeded, so the live source/key path was NOT unavailable.') -f
                $newBytes, $newNodes
        }
    }

    $errorCode = Get-AcceptanceJsonProperty -Object (Get-AcceptanceJsonProperty -Object $json -Name 'error') -Name 'code'
    $errorMessage = [string](Get-AcceptanceJsonProperty -Object (Get-AcceptanceJsonProperty -Object $json -Name 'error') -Name 'message')

    if ($errorCode -eq 'source_unavailable') {
        return [pscustomobject]@{
            Proven = $true
            Class = 'capture-refusal-source-path'
            Reason = 'capture refused with source_unavailable: the WeChat source data path is unavailable.'
        }
    }

    if ($errorCode -eq 'failure' -and
        $errorMessage -match '(not running|database key|key acquisition)') {
        return [pscustomobject]@{
            Proven = $true
            Class = 'capture-refusal-key-path'
            Reason = 'capture refused on the client/key path: key acquisition cannot succeed while the client is unavailable.'
        }
    }

    return [pscustomobject]@{
        Proven = $false
        Class = 'inconclusive'
        Reason = ("capture failed for a reason that does not evidence the offline premise " +
                  "(exit {0}, code {1}, message {2}); re-run the phase under a real offline state.") -f
            $exitCode, $errorCode, $errorMessage
    }
}

function Get-AcceptanceCurrentPointIndex {
    <#
    .SYNOPSIS
        Returns the planned hourly point index for a moment in time.
    #>
    param(
        [Parameter(Mandatory)] $Config,
        [datetime] $At = (Get-Date)
    )

    $start = [datetime]::Parse($Config.run.started_at_local, [cultureinfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind)
    if ($At -lt $start) {
        throw "The acceptance run has not started yet (run.started_at_local = $($Config.run.started_at_local))."
    }
    return [int][math]::Floor(($At.ToUniversalTime() - $start.ToUniversalTime()).TotalHours)
}

function Get-AcceptancePlannedPointTime {
    <#
    .SYNOPSIS
        Returns the planned UTC/local wall-clock time of a point index.
    #>
    param(
        [Parameter(Mandatory)] $Config,
        [Parameter(Mandatory)] [int] $Index
    )

    $start = [datetime]::Parse($Config.run.started_at_local, [cultureinfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind)
    return $start.AddHours($Index)
}

function Get-AcceptanceMissedPointIndexes {
    <#
    .SYNOPSIS
        Returns planned point indexes below the current index that have no record yet.
    .DESCRIPTION
        These are the never-executed points (machine powered off, scheduled task not
        triggered, harness downtime). They are recorded as missed_no_execution with an
        explanation; they are never silently skipped (Issue #86).
    #>
    param(
        [Parameter(Mandatory)] $Config,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [int[]] $RecordedIndexes,
        [datetime] $At = (Get-Date)
    )

    $current = Get-AcceptanceCurrentPointIndex -Config $Config -At $At
    $planned = [int]$Config.run.planned_hours
    # Strictly PAST points only: the current point may still execute this hour, and a
    # finished run accounts for exactly planned_hours points.
    $limit = [math]::Min($current, $planned)
    if ($limit -le 0) {
        return @()
    }

    $recorded = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($index in $RecordedIndexes) { $null = $recorded.Add($index) }

    $missed = @()
    for ($index = 0; $index -lt $limit; $index++) {
        if (-not $recorded.Contains($index)) {
            $missed += $index
        }
    }
    return $missed
}

# ---------------------------------------------------------------------------
# No-change hour expectation
# ---------------------------------------------------------------------------

function Test-AcceptanceNoChangeExpectation {
    <#
    .SYNOPSIS
        Evaluates the Issue #86 no-change expectation for a capture JSON document.
    .DESCRIPTION
        When every partition was reused, the capture must publish zero new data payload
        and zero new map nodes; only bounded generation/provenance/checkpoint metadata
        may grow. Returns $true when the expectation is met, $false when violated, and
        $null when the capture is not a no-change capture (nothing to evaluate).
    #>
    param(
        [AllowNull()] $CaptureJson
    )

    if ($null -eq $CaptureJson -or -not $CaptureJson.PSObject.Properties['coverage_summary']) {
        return $null
    }

    $summary = $CaptureJson.coverage_summary
    $total = [int]$summary.expected
    if ($total -le 0) {
        return $null
    }

    $reused = [int]$summary.reused
    $captured = [int]$summary.captured
    if (($reused + $captured) -ne $total -or $captured -ne 0) {
        return $null
    }

    $counters = $CaptureJson.storage_counters
    $newData = [int]$counters.new_data_bytes
    $newMapNodes = [int]$counters.new_map_nodes
    return ($newData -eq 0 -and $newMapNodes -eq 0)
}

# ---------------------------------------------------------------------------
# Evidence records
# ---------------------------------------------------------------------------

function ConvertTo-PrivacySafePath {
    <#
    .SYNOPSIS
        Renders a path relative to the acceptance root (or marks it external).
        Personal absolute paths never enter an evidence record.
    #>
    param(
        [AllowNull()] [string] $Path,
        [Parameter(Mandatory)] [string] $AcceptanceRoot
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $full = [System.IO.Path]::GetFullPath($Path)
    $rootFull = [System.IO.Path]::GetFullPath($AcceptanceRoot).TrimEnd('\', '/')
    if ($full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative = $full.Substring($rootFull.Length).TrimStart('\', '/')
        return '<acceptance-root>/' + ($relative -replace '\\', '/')
    }
    return '<external>'
}

function New-AcceptanceCaptureRecord {
    <#
    .SYNOPSIS
        Builds one hourly-trace evidence record for an executed capture point.
    #>
    param(
        [Parameter(Mandatory)] $Config,
        [Parameter(Mandatory)] [string] $AcceptanceRoot,
        [Parameter(Mandatory)] [int] $PointIndex,
        [Parameter(Mandatory)] [string] $Outcome,
        [Parameter(Mandatory)] [datetime] $ExecutedAt,
        [AllowNull()] $CaptureResult,
        [AllowNull()] $VerifyResult,
        [AllowNull()] $StatsResult,
        [AllowNull()] [string] $Explanation,
        [switch] $StatsSampled,
        # The record this one supersedes via -Retry: prior attempts are preserved
        # inside the new record so the posted classification never understates the
        # observed failure history.
        [AllowNull()] $PriorAttempt
    )

    if ($script:AcceptanceOutcomes -notcontains $Outcome) {
        throw "Unknown point outcome '$Outcome'."
    }

    $rc = $Config.rc
    $plannedPoint = [ordered]@{
        index = $PointIndex
        planned_at = (Get-AcceptancePlannedPointTime -Config $Config -Index $PointIndex).ToString('o')
        executed_at = $ExecutedAt.ToString('o')
        outcome = $Outcome
        explanation = $Explanation
        attempt = 1
        prior_attempts = @()
    }
    if ($null -ne $PriorAttempt) {
        # Preserve the superseded attempt (and any history it already carried).
        $prior = [ordered]@{
            executed_at = Get-AcceptanceJsonProperty -Object $PriorAttempt.planned_point -Name 'executed_at'
            outcome = Get-AcceptanceJsonProperty -Object $PriorAttempt.planned_point -Name 'outcome'
            explanation = Get-AcceptanceJsonProperty -Object $PriorAttempt.planned_point -Name 'explanation'
        }
        $carried = @(Get-AcceptanceJsonProperty -Object $PriorAttempt.planned_point -Name 'prior_attempts' -Default @())
        $plannedPoint['prior_attempts'] = @($carried) + @([pscustomobject]$prior)
        $plannedPoint['attempt'] = 2 + @($carried).Count
    }
    $rcBlock = [ordered]@{
        release_tag = $rc.release_tag
        version = $rc.expected_version
        commit_sha = $rc.commit_sha
        asset_url = $rc.asset_url
        asset_sha256 = $rc.asset_sha256.ToLowerInvariant()
    }
    $capture = [ordered]@{
        exit_code = $null
        duration_ms = $null
        generation_id = $null
        account_id = $null
        mode = $null
        completeness = $null
        coverage_summary = $null
        storage_counters = $null
        previous_generation_id = $null
        diagnostics_count = $null
        no_change_expectation_met = $null
        single_json_document = $null
    }
    $verify = [ordered]@{
        exit_code = $null
        succeeded = $null
        failure_count = $null
        failures = @()
        stderr = $null
        duration_ms = $null
    }

    if ($null -ne $CaptureResult) {
        $capture.exit_code = Get-AcceptanceJsonProperty -Object $CaptureResult -Name 'ExitCode'
        $capture.duration_ms = Get-AcceptanceJsonProperty -Object $CaptureResult -Name 'DurationMs'
        $capture.single_json_document = Get-AcceptanceJsonProperty -Object $CaptureResult -Name 'SingleJsonDocument'
        $json = Get-AcceptanceJsonProperty -Object $CaptureResult -Name 'Json'
        if ($null -ne $json) {
            foreach ($name in @('generation_id', 'account_id', 'mode', 'completeness', 'coverage_summary',
                                'storage_counters', 'previous_generation_id')) {
                if ($json.PSObject.Properties[$name]) {
                    $capture[$name] = $json.$name
                }
            }
            if ($json.PSObject.Properties['diagnostics']) {
                $capture.diagnostics_count = @($json.diagnostics).Count
            }
        }
        $capture.no_change_expectation_met = Test-AcceptanceNoChangeExpectation -CaptureJson $json
    }

    if ($null -ne $VerifyResult) {
        $verify.exit_code = Get-AcceptanceJsonProperty -Object $VerifyResult -Name 'ExitCode'
        $verify.duration_ms = Get-AcceptanceJsonProperty -Object $VerifyResult -Name 'DurationMs'
        $verifyText = (Get-AcceptanceJsonProperty -Object $VerifyResult -Name 'Stderr') -as [string]
        if (-not [string]::IsNullOrWhiteSpace($verifyText)) {
            $verify.stderr = $verifyText.Trim()
            if ($verify.stderr.Length -gt 800) { $verify.stderr = $verify.stderr.Substring(0, 800) + ' [...]' }
        }
        $json = Get-AcceptanceJsonProperty -Object $VerifyResult -Name 'Json'
        if ($null -ne $json) {
            if ($json.PSObject.Properties['succeeded']) {
                $verify.succeeded = [bool]$json.succeeded
            }
            if ($json.PSObject.Properties['failures']) {
                $failureEntries = @($json.failures)
                $verify.failure_count = $failureEntries.Count
                # Keep the full failure entries (account/generation/artifact/message) so a
                # resolved intermittent verify failure remains auditable afterwards.
                $verify.failures = @($failureEntries | ForEach-Object {
                    [pscustomobject]@{
                        account_id = Get-AcceptanceJsonProperty -Object $_ -Name 'account_id'
                        generation_id = Get-AcceptanceJsonProperty -Object $_ -Name 'generation_id'
                        artifact = Get-AcceptanceJsonProperty -Object $_ -Name 'artifact'
                        message = Get-AcceptanceJsonProperty -Object $_ -Name 'message'
                    }
                })
            }
        }
    }

    if ($StatsSampled -and $null -ne $StatsResult) {
        # Sampled read-only accounting (vault stats): source-neutral storage evidence.
        $metrics = [ordered]@{}
        $json = $StatsResult.Json
        if ($null -ne $json -and $json.PSObject.Properties['metrics'] -and $null -ne $json.metrics) {
            foreach ($property in $json.metrics.PSObject.Properties) {
                $metrics[$property.Name] = [pscustomobject]@{
                    value = $property.Value.value
                    unit = $property.Value.unit
                    basis = $property.Value.basis
                }
            }
        }
        $metrics['exit_code'] = Get-AcceptanceJsonProperty -Object $StatsResult -Name 'ExitCode'
        $metrics['duration_ms'] = Get-AcceptanceJsonProperty -Object $StatsResult -Name 'DurationMs'
        $statsBlock = [pscustomobject]$metrics
    } else {
        $statsBlock = $null
    }

    $envFacts = Get-AcceptanceEnvironmentFacts -Root $AcceptanceRoot
    $environment = [ordered]@{
        acceptance_root = ConvertTo-PrivacySafePath -Path $AcceptanceRoot -AcceptanceRoot $AcceptanceRoot
        vault_root = ConvertTo-PrivacySafePath -Path (Get-AcceptanceVaultRoot -AcceptanceRoot $AcceptanceRoot) -AcceptanceRoot $AcceptanceRoot
        os = $envFacts.os_caption
        filesystem = $envFacts.filesystem
        allocation_unit_bytes = $envFacts.allocation_unit_bytes
    }

    $record = [ordered]@{
        schema_version = $script:AcceptanceTraceSchemaVersion
        record_type = 'capture_point'
        planned_point = [pscustomobject]$plannedPoint
        rc = [pscustomobject]$rcBlock
        capture = [pscustomobject]$capture
        vault_verify = [pscustomobject]$verify
    }
    if ($null -ne $statsBlock) {
        $record['vault_stats'] = $statsBlock
    }
    $record['environment'] = [pscustomobject]$environment

    return [pscustomobject]$record
}

function New-AcceptanceMissedRecord {
    <#
    .SYNOPSIS
        Builds the evidence record for a planned point that never executed.
    #>
    param(
        [Parameter(Mandatory)] $Config,
        [Parameter(Mandatory)] [string] $AcceptanceRoot,
        [Parameter(Mandatory)] [int] $PointIndex,
        [Parameter(Mandatory)] [datetime] $RecordedAt,
        [Parameter(Mandatory)] [string] $Explanation
    )

    return (New-AcceptanceCaptureRecord `
        -Config $Config `
        -AcceptanceRoot $AcceptanceRoot `
        -PointIndex $PointIndex `
        -Outcome 'missed_no_execution' `
        -ExecutedAt $RecordedAt `
        -Explanation $Explanation)
}

function Assert-AcceptanceRecordValid {
    <#
    .SYNOPSIS
        Validates one evidence record against the trace schema contract.
        Throws when the record is not auditable (Issue #86 acceptance requires
        evidence detailed enough to audit commands, versions and outcomes).
    #>
    param(
        [Parameter(Mandatory)] $Record
    )

    foreach ($field in @('schema_version', 'record_type')) {
        if (-not $Record.PSObject.Properties[$field]) {
            throw "Evidence record is missing required field '$field'."
        }
    }

    if ($Record.schema_version -ne $script:AcceptanceTraceSchemaVersion) {
        throw "Evidence record carries schema_version $($Record.schema_version), expected $($script:AcceptanceTraceSchemaVersion)."
    }

    if ($Record.record_type -ne 'capture_point') {
        # Scenario/auxiliary records carry their own shape; they must still bind the RC.
        foreach ($field in @('rc_version', 'rc_commit_sha', 'rc_asset_sha256', 'executed_at')) {
            if (-not $Record.PSObject.Properties[$field]) {
                throw "Evidence record of type '$($Record.record_type)' is missing required field '$field'."
            }
        }
        return $true
    }

    foreach ($field in @('planned_point', 'rc', 'capture', 'vault_verify')) {
        if (-not $Record.PSObject.Properties[$field]) {
            throw "Evidence record is missing required field '$field'."
        }
    }

    foreach ($field in @('index', 'outcome')) {
        if (-not $Record.planned_point.PSObject.Properties[$field]) {
            throw "Evidence record planned_point is missing '$field'."
        }
    }

    if ($script:AcceptanceOutcomes -notcontains $Record.planned_point.outcome) {
        throw "Evidence record carries unknown outcome '$($Record.planned_point.outcome)'."
    }

    foreach ($field in @('version', 'commit_sha', 'asset_sha256')) {
        if (-not $Record.rc.PSObject.Properties[$field] -or [string]::IsNullOrWhiteSpace([string]$Record.rc.$field)) {
            throw "Evidence record rc is missing '$field' (the exact-RC binding must be auditable)."
        }
    }

    $outcome = $Record.planned_point.outcome
    $executed = $outcome -ne 'missed_no_execution'
    if ($executed -and $null -eq $Record.capture.exit_code) {
        throw "Evidence record for an executed point must carry capture.exit_code."
    }
    if ($outcome -eq 'success_partial' -and
        ($null -eq $Record.planned_point.explanation -or [string]::IsNullOrWhiteSpace($Record.planned_point.explanation))) {
        throw 'A success_partial record requires an explanation (the partial gap must be attributed).'
    }
    if ($outcome -eq 'failed_capture' -and
        ($null -eq $Record.planned_point.explanation -or [string]::IsNullOrWhiteSpace($Record.planned_point.explanation))) {
        throw 'A failed_capture record requires an explanation (the failure must be classified, not unexplained).'
    }
    if ($outcome -eq 'missed_no_execution' -and
        ($null -eq $Record.planned_point.explanation -or [string]::IsNullOrWhiteSpace($Record.planned_point.explanation))) {
        throw 'A missed_no_execution record requires an explanation.'
    }

    return $true
}

# ---------------------------------------------------------------------------
# Trace file (JSONL) with idempotent append/replace
# ---------------------------------------------------------------------------

function Read-AcceptanceTrace {
    <#
    .SYNOPSIS
        Reads all evidence records from a JSONL trace file. Missing file → empty list.
    #>
    param(
        [Parameter(Mandatory)] [string] $TracePath
    )

    if (-not (Test-Path -LiteralPath $TracePath)) {
        return @()
    }

    $records = @()
    foreach ($line in Get-Content -LiteralPath $TracePath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        $records += ($line | ConvertFrom-Json)
    }
    return $records
}

function Get-AcceptanceRecordKey {
    <#
    .SYNOPSIS
        Returns the identity key used for idempotent trace writes:
        capture points are identified by their planned index; scenario records by
        their scenario step.
    #>
    param(
        [Parameter(Mandatory)] $Record
    )

    if ($Record.record_type -eq 'capture_point') {
        return 'capture_point#' + [int]$Record.planned_point.index
    }
    if ($Record.PSObject.Properties['scenario_step']) {
        return [string]$Record.record_type + '#' + [string]$Record.scenario_step
    }
    throw "Evidence record of type '$($Record.record_type)' carries no identity (planned_point.index or scenario_step)."
}

function Write-AcceptanceTraceRecord {
    <#
    .SYNOPSIS
        Appends one evidence record to the JSONL trace. If a record with the same
        identity already exists, it is REPLACED (idempotent re-run and explicit
        retry semantics), never duplicated.
    .DESCRIPTION
        The whole read-replace-write is serialized by a named mutex so a manual run
        cannot lose-update a concurrently executing scheduled cycle.
    .OUTPUTS
        'appended' or 'replaced'.
    #>
    param(
        [Parameter(Mandatory)] [string] $TracePath,
        [Parameter(Mandatory)] $Record
    )

    $null = Assert-AcceptanceRecordValid -Record $Record
    $key = Get-AcceptanceRecordKey -Record $Record

    $directory = Split-Path -Parent $TracePath
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        $null = New-Item -ItemType Directory -Path $directory -Force
    }

    # Cross-process guard: one writer at a time per trace file.
    $mutexName = 'Global\WeArchiveAcceptanceTrace_' +
        ([System.BitConverter]::ToString(
            [System.Security.Cryptography.SHA256]::HashData(
                [System.Text.Encoding]::UTF8.GetBytes($TracePath.ToLowerInvariant())
            )).Replace('-', '').Substring(0, 24))
    $mutex = [System.Threading.Mutex]::new($false, $mutexName)
    try {
        $null = $mutex.WaitOne(30000)
        try {
            $records = @(Read-AcceptanceTrace -TracePath $TracePath)
            $existing = $false
            $kept = @()
            foreach ($candidate in $records) {
                if ((Get-AcceptanceRecordKey -Record $candidate) -eq $key) {
                    $existing = $true
                    continue
                }
                $kept += $candidate
            }
            $kept += $Record

            $lines = $kept | ForEach-Object { $_ | ConvertTo-Json -Depth 16 -Compress }
            $tempPath = "$TracePath.tmp"
            Set-Content -LiteralPath $tempPath -Value $lines -Encoding utf8NoBOM
            Move-Item -LiteralPath $tempPath -Destination $TracePath -Force
        } finally {
            $null = $mutex.ReleaseMutex()
        }
    } finally {
        $mutex.Dispose()
    }

    if ($existing) {
        return 'replaced'
    }
    return 'appended'
}

# ---------------------------------------------------------------------------
# Environment facts (machine/OS/storage recording, privacy-safe by default)
# ---------------------------------------------------------------------------

function Get-AcceptanceEnvironmentFacts {
    <#
    .SYNOPSIS
        Collects the machine/OS/storage facts Issue #86 requires to be recorded.
        Volume/filesystem facts describe the drive that hosts -Root (the acceptance
        root — the storage under test), never the harness working directory.
        Best-effort per field: an unavailable observation is recorded as $null.
    #>
    param(
        [Parameter(Mandatory)] [string] $Root
    )

    $facts = @{
        os_caption = $null
        os_version = $null
        filesystem = $null
        allocation_unit_bytes = $null
        drive_free_bytes = $null
        drive_total_bytes = $null
    }

    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    $driveRoot = [System.IO.Path]::GetPathRoot($fullRoot).TrimEnd('\', '/')
    $driveLetter = $driveRoot.TrimEnd(':')

    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        $facts.os_caption = $os.Caption
        $facts.os_version = $os.Version
    } catch [Exception] {
        # Unavailable observation stays $null; never fabricate.
    }

    try {
        $volume = Get-Volume -DriveLetter $driveLetter -ErrorAction Stop
        $facts.filesystem = $volume.FileSystem
        $facts.drive_free_bytes = [int64]$volume.SizeRemaining
        $facts.drive_total_bytes = [int64]$volume.Size
    } catch [Exception] {
        # Unavailable observation stays $null; never fabricate.
    }

    try {
        $ntfsInfo = & fsutil fsinfo ntfsInfo $driveRoot 2>$null
        if ($LASTEXITCODE -eq 0 -and $ntfsInfo) {
            $line = $ntfsInfo | Where-Object { $_ -match 'Bytes Per Cluster\s*:\s*(\d+)' } | Select-Object -First 1
            if ($line) {
                $facts.allocation_unit_bytes = [int]$Matches[1]
            }
        }
    } catch [Exception] {
        # fsutil may require elevation; an unavailable observation is acceptable.
    }

    return $facts
}

# ---------------------------------------------------------------------------
# Hashing / immutability
# ---------------------------------------------------------------------------

function Get-FileSha256Hex {
    <#
    .SYNOPSIS
        Returns the lowercase SHA-256 hex digest of a file.
    #>
    param(
        [Parameter(Mandatory)] [string] $Path
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        } finally {
            $stream.Dispose()
        }
    } finally {
        $sha.Dispose()
    }
}

function New-AcceptanceHashManifest {
    <#
    .SYNOPSIS
        Builds a relative-path -> SHA-256 manifest for the pre-existing (seed/reference)
        evidence inside a directory, for the historical-evidence immutability checks.
    .DESCRIPTION
        The manifest's root is stored PRIVACY-SAFE (relative to the acceptance root),
        because manifest files live in the evidence folder the runbook says to attach
        publicly. Test-AcceptanceHashManifest resolves the prefix back to the real
        acceptance root when re-verifying.
    #>
    param(
        [Parameter(Mandatory)] [string] $Directory,
        [Parameter(Mandatory)] [string] $AcceptanceRoot,
        [Parameter(Mandatory)] [string[]] $Filter = @('*')
    )

    $manifest = [ordered]@{
        generated_at = (Get-Date).ToString('o')
        root = ConvertTo-PrivacySafePath -Path $Directory -AcceptanceRoot $AcceptanceRoot
        entries = [ordered]@{}
    }

    foreach ($pattern in $Filter) {
        foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter $pattern -File -Recurse -ErrorAction SilentlyContinue) {
            $relative = [System.IO.Path]::GetRelativePath($Directory, $file.FullName) -replace '\\', '/'
            if (-not $manifest.entries.Contains($relative)) {
                $manifest.entries[$relative] = Get-FileSha256Hex -Path $file.FullName
            }
        }
    }

    return [pscustomobject]$manifest
}

function Get-AcceptanceManifestEntries {
    <#
    .SYNOPSIS
        Normalizes the entries of a hash manifest (fresh OrderedDictionary or parsed
        JSON object) into a uniform list of {Name, Value} pairs.
    #>
    param(
        [Parameter(Mandatory)] $Entries
    )

    if ($Entries -is [System.Collections.IDictionary]) {
        return @($Entries.GetEnumerator() | ForEach-Object {
            [pscustomobject]@{ Name = [string]$_.Key; Value = [string]$_.Value }
        })
    }
    return @($Entries.PSObject.Properties)
}

function Test-AcceptanceHashManifest {
    <#
    .SYNOPSIS
        Re-verifies a hash manifest. Returns $true when every entry is unchanged.
        Emits the list of violated paths on failure (never silently passes).
    .DESCRIPTION
        The manifest root is stored privacy-safe; -AcceptanceRoot resolves it back to
        the real directory (the caller knows its own acceptance root from its config).
    #>
    param(
        [Parameter(Mandatory)] $Manifest,
        [Parameter(Mandatory)] [string] $AcceptanceRoot
    )

    $manifestRoot = [string](Get-AcceptanceJsonProperty -Object $Manifest -Name 'root' -Default ([string]$Manifest.root))
    if ($manifestRoot -eq '<acceptance-root>') {
        $resolvedRoot = $AcceptanceRoot
    } elseif ($manifestRoot.StartsWith('<acceptance-root>/', [System.StringComparison]::OrdinalIgnoreCase)) {
        $resolvedRoot = Join-Path $AcceptanceRoot $manifestRoot.Substring('<acceptance-root>/'.Length)
    } else {
        # Legacy/absolute manifests (never produced by this module version).
        $resolvedRoot = $manifestRoot
    }

    $violations = @()
    foreach ($entry in Get-AcceptanceManifestEntries -Entries $Manifest.entries) {
        $path = Join-Path $resolvedRoot $entry.Name
        if (-not (Test-Path -LiteralPath $path)) {
            $violations += "$($entry.Name): missing"
            continue
        }
        $actual = Get-FileSha256Hex -Path $path
        if ($actual -ne $entry.Value) {
            $violations += "$($entry.Name): hash changed"
        }
    }

    if ($violations.Count -gt 0) {
        Write-Error ("Historical evidence immutability violated:`n" + ($violations -join "`n"))
        return $false
    }
    return $true
}

# ---------------------------------------------------------------------------
# Export module members
# ---------------------------------------------------------------------------

Export-ModuleMember -Function @(
    'Read-AcceptanceConfig',
    'Get-AcceptanceRoot',
    'Get-AcceptanceHome',
    'Get-AcceptanceDataRoot',
    'Get-AcceptanceVaultRoot',
    'Get-AcceptanceArchiveRoot',
    'Get-AcceptanceRcExe',
    'Invoke-WearchiveRc',
    'Get-AcceptanceJsonProperty',
    'Get-AcceptanceCaptureSummary',
    'Test-AcceptanceOfflineCaptureProof',
    'Get-AcceptancePointOutcome',
    'Get-AcceptanceCurrentPointIndex',
    'Get-AcceptancePlannedPointTime',
    'Get-AcceptanceMissedPointIndexes',
    'Test-AcceptanceNoChangeExpectation',
    'ConvertTo-PrivacySafePath',
    'New-AcceptanceCaptureRecord',
    'New-AcceptanceMissedRecord',
    'Assert-AcceptanceRecordValid',
    'Get-AcceptanceRecordKey',
    'Read-AcceptanceTrace',
    'Write-AcceptanceTraceRecord',
    'Get-AcceptanceEnvironmentFacts',
    'Get-FileSha256Hex',
    'New-AcceptanceHashManifest',
    'Get-AcceptanceManifestEntries',
    'Test-AcceptanceHashManifest'
)
