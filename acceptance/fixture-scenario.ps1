#Requires -Version 7.0
<#
.SYNOPSIS
    Controlled truncate/rewrite fixture exercise (Issue #86 hourly-operation scenario).

.DESCRIPTION
    Reproduces, in an isolated scenario sandbox, the storage behavior of a source
    artifact being truncated/rewritten in place between two capture points — the
    controlled fixture Issue #86 requires when the live source does not naturally
    produce one. The exercise never touches the real WeChat source, the production
    vault, or the main hourly trace: everything happens inside

        <scenario-root>\home\Documents\xwechat_files\wxid_acceptance_fixture

    which the RC discovers through its redirected Documents folder (the known-folder
    seam init-root.ps1 proves), and the outcome trace is written to
    <scenario-root>\evidence\fixture-trace.jsonl.

    The fixture account is WeChat-4.x-shaped:

      - session/session.db (SessionTable) and contact/contact.db (contact) are
        PLAINTEXT synthetic databases shaped like tests/Support/SyntheticWeChatSource;
        the adapter preserves plaintext source files in place and the key verifier
        skips plaintext pages;
      - one REAL encrypted message shard copied from the live source (message_0.db)
        acts as the key anchor so transient key acquisition — which the RC performs
        whenever changed evidence exists — verifies against genuinely encrypted pages;
      - message/message_1.db is the PLAINTEXT synthetic shard the fixture mutates:
        (a) appended rows ("normal append"), (b) the file replaced wholesale with a
        shorter fresh database ("truncate/rewrite" — the source-side event class
        whose physical growth must be attributable, like VACUUM/rewrite).

    Expected observations (recorded into the fixture trace):
      - the baseline capture publishes a complete v2 generation;
      - the append capture reuses unchanged partitions and publishes new blocks only
        for the changed shard;
      - the rewrite capture produces new data blocks for the rewritten artifact and
        bounded orphan/unreachable bytes for the replaced blocks — no unexplained
        duplication — with authoritative vault verify passing;
      - published manifests are immutable across all later captures.

    The WeChat client must be running and signed in: the key anchor requires the
    transient key only the running client holds. The scenario never acquires keys
    itself and refuses to run without the client.

.PARAMETER Config
    Path to the user-maintained acceptance config (config.json).

.PARAMETER LiveAccountDirectory
    The real WeChat 4.x account directory (the xwechat_files\<wxid> folder) whose
    databases provide the encrypted key anchor. One single read-only COPY is taken;
    the directory is never written to.

.PARAMETER ScenarioRoot
    Where the isolated scenario sandbox lives. Defaults to
    <acceptance-root>\scenario-fixture.

.EXAMPLE
    ./fixture-scenario.ps1 -Config D:\acceptance\issue-86\config.json `
        -LiveAccountDirectory "C:\Users\<you>\xwechat_files\<wxid_xxx>"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [Alias('Config')] [string] $ConfigPath,
    [Parameter(Mandatory)] [string] $LiveAccountDirectory,
    [string] $ScenarioRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = $PSScriptRoot
Import-Module (Join-Path $scriptDir 'acceptance-harness.psm1') -Force

$config = Read-AcceptanceConfig -Path $ConfigPath
$root = Get-AcceptanceRoot -Config $config
if (-not $ScenarioRoot) { $ScenarioRoot = Join-Path $root 'scenario-fixture' }

# --- Sandbox layout ----------------------------------------------------------

$scenarioHome = Join-Path $ScenarioRoot 'home'
$fixtureSourceRoot = Join-Path $scenarioHome 'Documents/xwechat_files'
$fixtureRoot = Join-Path $fixtureSourceRoot 'wxid_acceptance_fixture'
$fixtureDb = Join-Path $fixtureRoot 'db_storage'
$scenarioEvidence = Join-Path $ScenarioRoot 'evidence'
foreach ($relative in @(
    (Join-Path $fixtureDb 'session'),
    (Join-Path $fixtureDb 'contact'),
    (Join-Path $fixtureDb 'message'),
    # The profile-id resolver reads xwechat_files\all_users\login\<login>; without this
    # marker the account directory name would be truncated at its last underscore.
    (Join-Path $fixtureSourceRoot 'all_users/login/wxid_acceptance_fixture'),
    (Join-Path $scenarioHome 'AppData/Local/Temp'),
    $scenarioEvidence
)) {
    $null = New-Item -ItemType Directory -Path $relative -Force
}

# --- Gates -------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $LiveAccountDirectory)) {
    throw "Live account directory not found: $LiveAccountDirectory"
}
$encryptedShard = Get-ChildItem -LiteralPath (Join-Path $LiveAccountDirectory 'db_storage/message') `
    -Filter '*.db' -File -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $encryptedShard) {
    throw 'No encrypted message shard found in the live account directory (key anchor source).'
}

Write-Host 'Fixture sandbox ready. Source/client gate runs with the first capture step.'

# --- Synthetic WeChat-4.x-shaped plaintext databases -------------------------

function ConvertTo-Md5Hex {
    param([Parameter(Mandatory)] [string] $Value)
    return ([System.BitConverter]::ToString(
        [System.Security.Cryptography.MD5]::HashData([System.Text.Encoding]::UTF8.GetBytes($Value))
    )).Replace('-', '').ToLowerInvariant()
}

function New-FixtureDatabase {
    # Builds a plaintext SQLite database by emitting the SQL the RC's own parser
    # expects (docs/MESSAGE_SCHEMA.md shape; mirrors tests/Support/SyntheticWeChatSource).
    # The SQLite CLI is a documented prerequisite of the fixture scenario.
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Sql
    )

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force
    }
    $sqlite3 = Get-Command sqlite3 -ErrorAction SilentlyContinue
    if (-not $sqlite3) {
        throw @'
sqlite3.exe is required to build the synthetic fixture databases and was not found on PATH.
Install the SQLite command-line tools (https://www.sqlite.org/download.html) and re-run.
'@
    }
    $tempSql = "$Path.sql"
    Set-Content -LiteralPath $tempSql -Value $Sql -Encoding utf8NoBOM
    $null = & $sqlite3.Source $tempSql 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sqlite3 failed building $Path"
    }
    Remove-Item -LiteralPath $tempSql -Force
}

function Get-FixtureShardSql {
    param([Parameter(Mandatory)][string[]] $ConversationIds, [Parameter(Mandatory)][int] $MessagesPerConversation, [Parameter(Mandatory)][int] $StartId)

    $builder = [System.Text.StringBuilder]::new()
    $null = $builder.AppendLine('CREATE TABLE Name2Id (rowid INTEGER PRIMARY KEY, user_name TEXT);')
    for ($i = 0; $i -lt $ConversationIds.Count; $i++) {
        $null = $builder.AppendLine("INSERT INTO Name2Id VALUES ($($i + 1), '$($ConversationIds[$i])');")
    }
    foreach ($conversation in $ConversationIds) {
        $table = 'Msg_' + (ConvertTo-Md5Hex -Value $conversation)
        $null = $builder.AppendLine("CREATE TABLE `"$table`" (")
        $null = $builder.AppendLine('  local_id INTEGER PRIMARY KEY,')
        $null = $builder.AppendLine('  server_id INTEGER,')
        $null = $builder.AppendLine('  local_type INTEGER,')
        $null = $builder.AppendLine('  real_sender_id INTEGER,')
        $null = $builder.AppendLine('  create_time INTEGER,')
        $null = $builder.AppendLine('  message_content BLOB,')
        $null = $builder.AppendLine('  WCDB_CT_message_content INTEGER,')
        $null = $builder.AppendLine('  compress_content BLOB);')
        for ($id = $StartId; $id -lt $StartId + $MessagesPerConversation; $id++) {
            $null = $builder.AppendLine(
                "INSERT INTO `"$table`" VALUES ($id, $(7000 + $id), 1, 1, " +
                "$(1736907600 + $id), 'fixture message $id', 0, NULL);")
        }
    }
    return $builder.ToString()
}

$fixtureConversations = @('wxid_fixture_alice', '10086@chatroom')

function New-FixtureShard {
    param([Parameter(Mandatory)][int] $MessagesPerConversation, [Parameter(Mandatory)][int] $StartId)
    New-FixtureDatabase -Path (Join-Path $fixtureDb 'message/message_1.db') -Sql `
        (Get-FixtureShardSql -ConversationIds $fixtureConversations `
            -MessagesPerConversation $MessagesPerConversation -StartId $StartId)
}

Write-Host 'Building synthetic plaintext fixture databases...'
New-FixtureDatabase -Path (Join-Path $fixtureDb 'session/session.db') -Sql (@"
CREATE TABLE SessionTable (
    username TEXT PRIMARY KEY,
    sort_timestamp INTEGER,
    last_timestamp INTEGER,
    last_msg_type INTEGER,
    last_msg_sub_type INTEGER,
    summary TEXT);
$(($fixtureConversations | ForEach-Object { "INSERT INTO SessionTable VALUES ('$_', 1737244800, 1737244800, 1, 0, NULL);" }) -join "`n")
"@)
New-FixtureDatabase -Path (Join-Path $fixtureDb 'contact/contact.db') -Sql (@"
CREATE TABLE contact (
    username TEXT PRIMARY KEY,
    remark TEXT,
    nick_name TEXT,
    alias TEXT,
    local_type INTEGER);
CREATE TABLE stranger (username TEXT PRIMARY KEY, nick_name TEXT);
$(($fixtureConversations | ForEach-Object { "INSERT INTO contact VALUES ('$_', 'Remark $_', 'Nick', NULL, 1);" }) -join "`n")
"@)
New-FixtureShard -MessagesPerConversation 5 -StartId 1

# Key anchor: one read-only COPY of a real encrypted shard.
Copy-Item -LiteralPath $encryptedShard.FullName -Destination (Join-Path $fixtureDb 'message/message_0.db') -Force
Write-Host ("Key anchor copied (read-only copy): {0}" -f $encryptedShard.Name)

$fixtureVault = Join-Path $scenarioHome 'AppData/Local/WeArchive/rawvault'
$tracePath = Join-Path $scenarioEvidence 'fixture-trace.jsonl'

function Add-FixtureTraceRecord {
    param(
        [Parameter(Mandatory)] [string] $Step,
        [Parameter(Mandatory)] $Result,
        [AllowNull()] [string] $Observation
    )
    $record = [ordered]@{
        schema_version = 1
        record_type = 'fixture_scenario'
        scenario_step = $Step
        executed_at = (Get-Date).ToString('o')
        rc_version = $config.rc.expected_version
        rc_commit_sha = $config.rc.commit_sha
        rc_asset_sha256 = $config.rc.asset_sha256.ToLowerInvariant()
        exit_code = $Result.ExitCode
        duration_ms = $Result.DurationMs
        observation = $Observation
    }
    $null = Write-AcceptanceTraceRecord -TracePath $tracePath -Record ([pscustomobject]$record)
}

# --- Step 1: baseline capture -------------------------------------------------

Write-Host 'Fixture step 1/4: baseline capture (also proves the source discovery seam)...'
$baseline = Invoke-WearchiveRc -AcceptanceRoot $root -HomeDirectory $scenarioHome `
    -Arguments @('capture', '--account', 'wxid_acceptance_fixture', '--json', '--no-input')
if ($baseline.ExitCode -ne 0) {
    Add-FixtureTraceRecord -Step 'baseline' -Result $baseline -Observation "failed: $($baseline.Stderr)"
    throw "Fixture baseline capture failed (exit $($baseline.ExitCode)). Stderr: $($baseline.Stderr)"
}
$baselineObservation = 'generation={0} mode={1} counters={2}' -f $baseline.Json.generation_id, $baseline.Json.mode,
    ($baseline.Json.storage_counters | ConvertTo-Json -Compress)
Add-FixtureTraceRecord -Step 'baseline' -Result $baseline -Observation $baselineObservation
Write-Host "  $baselineObservation"

# --- Step 2: append capture ---------------------------------------------------

Write-Host 'Fixture step 2/4: append capture (rows appended to the synthetic shard)...'
New-FixtureShard -MessagesPerConversation 10 -StartId 1
$append = Invoke-WearchiveRc -AcceptanceRoot $root -HomeDirectory $scenarioHome `
    -Arguments @('capture', '--account', 'wxid_acceptance_fixture', '--json', '--no-input')
if ($append.ExitCode -ne 0) {
    Add-FixtureTraceRecord -Step 'append' -Result $append -Observation "failed: $($append.Stderr)"
    throw "Fixture append capture failed (exit $($append.ExitCode)). Stderr: $($append.Stderr)"
}
$appendObservation = 'mode={0} new_data_bytes={1} new_map_nodes={2} reused={3} captured={4}' -f
    $append.Json.mode, $append.Json.storage_counters.new_data_bytes,
    $append.Json.storage_counters.new_map_nodes,
    $append.Json.coverage_summary.reused, $append.Json.coverage_summary.captured
Add-FixtureTraceRecord -Step 'append' -Result $append -Observation $appendObservation
Write-Host "  $appendObservation"

# --- Step 3: truncate/rewrite capture ----------------------------------------

Write-Host 'Fixture step 3/4: truncate/rewrite capture (shard replaced in place, shorter)...'
New-FixtureShard -MessagesPerConversation 3 -StartId 500
$rewrite = Invoke-WearchiveRc -AcceptanceRoot $root -HomeDirectory $scenarioHome `
    -Arguments @('capture', '--account', 'wxid_acceptance_fixture', '--json', '--no-input')
if ($rewrite.ExitCode -ne 0) {
    Add-FixtureTraceRecord -Step 'rewrite' -Result $rewrite -Observation "failed: $($rewrite.Stderr)"
    throw "Fixture rewrite capture failed (exit $($rewrite.ExitCode)). Stderr: $($rewrite.Stderr)"
}
$rewriteObservation = 'mode={0} new_data_bytes={1} new_map_nodes={2} reused={3}' -f
    $rewrite.Json.mode, $rewrite.Json.storage_counters.new_data_bytes,
    $rewrite.Json.storage_counters.new_map_nodes,
    $rewrite.Json.coverage_summary.reused
Add-FixtureTraceRecord -Step 'rewrite' -Result $rewrite -Observation $rewriteObservation
Write-Host "  $rewriteObservation"

# --- Step 4: verify + growth attribution --------------------------------------

Write-Host 'Fixture step 4/4: authoritative verify + growth attribution...'
$verify = Invoke-WearchiveRc -AcceptanceRoot $root -HomeDirectory $scenarioHome `
    -Arguments @('vault', 'verify', '--vault-root', $fixtureVault, '--json', '--no-input') -TimeoutSeconds 7200
$verifyOk = $verify.ExitCode -eq 0 -and $verify.Json.succeeded
Add-FixtureTraceRecord -Step 'verify' -Result $verify -Observation "succeeded=$verifyOk"
if (-not $verifyOk) {
    throw 'Fixture vault failed authoritative verification.'
}

$stats = Invoke-WearchiveRc -AcceptanceRoot $root -HomeDirectory $scenarioHome `
    -Arguments @('vault', 'stats', '--vault-root', $fixtureVault, '--json', '--no-input') -TimeoutSeconds 7200
$orphanBytes = $stats.Json.metrics.'v2_orphan_payload_uncompressed_bytes'.value
$duplicateBytes = $stats.Json.metrics.'duplicate_physical_record_bytes'.value
$reachableBytes = $stats.Json.metrics.'v2_reachable_payload_uncompressed_bytes'.value
$statsObservation = 'reachable={0}B orphan={1}B duplicate={2}B (rewrite growth must be attributable; orphans bounded)' -f
    $reachableBytes, $orphanBytes, $duplicateBytes
Add-FixtureTraceRecord -Step 'stats' -Result $stats -Observation $statsObservation
Write-Host "  $statsObservation"

Write-Host ''
Write-Host 'Fixture scenario completed.'
Write-Host ("Trace: {0}" -f (ConvertTo-PrivacySafePath -Path $tracePath -AcceptanceRoot $root))
Write-Host 'Review fixture-trace.jsonl: the rewrite capture must show attributable growth with'
Write-Host 'bounded orphan bytes and no unexplained duplication (Issue #86 storage evidence).'
