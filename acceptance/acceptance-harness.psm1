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
    $startInfo.Arguments = ($Arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
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
        [switch] $StatsSampled
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
        duration_ms = $null
    }

    if ($null -ne $CaptureResult) {
        $capture.exit_code = $CaptureResult.ExitCode
        $capture.duration_ms = $CaptureResult.DurationMs
        $capture.single_json_document = $CaptureResult.SingleJsonDocument
        $json = $CaptureResult.Json
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
        $verify.exit_code = $VerifyResult.ExitCode
        $verify.duration_ms = $VerifyResult.DurationMs
        $json = $VerifyResult.Json
        if ($null -ne $json) {
            if ($json.PSObject.Properties['succeeded']) {
                $verify.succeeded = [bool]$json.succeeded
            }
            if ($json.PSObject.Properties['failures']) {
                $verify.failure_count = @($json.failures).Count
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
        $metrics['exit_code'] = $StatsResult.ExitCode
        $metrics['duration_ms'] = $StatsResult.DurationMs
        $statsBlock = [pscustomobject]$metrics
    } else {
        $statsBlock = $null
    }

    $envFacts = Get-AcceptanceEnvironmentFacts
    $environment = [ordered]@{
        acceptance_root = ConvertTo-PrivacySafePath -Path $AcceptanceRoot -AcceptanceRoot $AcceptanceRoot
        vault_root = ConvertTo-PrivacySafePath -Path (Join-Path (Get-AcceptanceHome -AcceptanceRoot $AcceptanceRoot) 'AppData/Local/WeArchive/rawvault') -AcceptanceRoot $AcceptanceRoot
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
        Best-effort per field: an unavailable observation is recorded as $null.
    #>
    $facts = @{
        os_caption = $null
        os_version = $null
        filesystem = $null
        allocation_unit_bytes = $null
        drive_free_bytes = $null
        drive_total_bytes = $null
        wechat_client_version = $null
    }

    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        $facts.os_caption = $os.Caption
        $facts.os_version = $os.Version
    } catch [Exception] {
        # Unavailable observation stays $null; never fabricate.
    }

    try {
        $volume = Get-Volume -DriveLetter ([System.IO.Path]::GetPathRoot((Get-Location).Path).TrimEnd('\', ':')) -ErrorAction Stop
        $facts.filesystem = $volume.FileSystem
        $facts.drive_free_bytes = [int64]$volume.SizeRemaining
        $facts.drive_total_bytes = [int64]$volume.Size
    } catch [Exception] {
        # Unavailable observation stays $null; never fabricate.
    }

    try {
        $drive = [System.IO.Path]::GetPathRoot((Get-Location).Path).TrimEnd('\', '/')
        $ntfsInfo = & fsutil fsinfo ntfsInfo $drive 2>$null
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
    #>
    param(
        [Parameter(Mandatory)] [string] $Directory,
        [Parameter(Mandatory)] [string[]] $Filter = @('manifest.json', '*.zip')
    )

    $manifest = [ordered]@{
        generated_at = (Get-Date).ToString('o')
        root = $Directory
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
    #>
    param(
        [Parameter(Mandatory)] $Manifest
    )

    $violations = @()
    foreach ($entry in Get-AcceptanceManifestEntries -Entries $Manifest.entries) {
        $path = Join-Path $Manifest.root $entry.Name
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
    'Get-AcceptanceRcExe',
    'Invoke-WearchiveRc',
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
