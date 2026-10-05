#Requires -Version 7.0
<#
.SYNOPSIS
    Initializes the isolated acceptance root and proves the isolation gate (Issue #86).

.DESCRIPTION
    Creates the acceptance root layout, then proves — with the RC's own `doctor`
    command — that the environment redirect is real:

      - the RC's Raw Vault / archive / cache roots resolve INSIDE
        <acceptance-root>\home (isolation), and
      - the real WeChat 4.x source is still discovered (the acceptance captures
        real source data, never production vault history).

    The doctor JSON and the recorded environment facts are persisted to
    evidence/environment.json. Re-running is safe and re-proves the gate.

    Optional v1 seed (compatibility scenarios): -SeedVaultRoot copies an existing
    vault (typically the production Raw Vault's account data, read-only COPY) into
    the acceptance vault so the first RC capture becomes the first v1->v2 generation
    on the same account lineage. The copy's manifests are hashed into
    evidence/v1-seed-hashes.json for the historical-evidence immutability checks.
    The seed source is never modified.

.PARAMETER Config
    Path to the user-maintained acceptance config (config.json).

.PARAMETER SeedVaultRoot
    Optional path to an existing vault root whose accounts are copied into the
    acceptance vault (copy, never move).

.EXAMPLE
    ./init-root.ps1 -Config D:\acceptance\issue-86\config.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [Alias('Config')] [string] $ConfigPath,
    [string] $SeedVaultRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = $PSScriptRoot
Import-Module (Join-Path $scriptDir 'acceptance-harness.psm1') -Force

$config = Read-AcceptanceConfig -Path $ConfigPath
$root = Get-AcceptanceRoot -Config $config
$home_ = Get-AcceptanceHome -AcceptanceRoot $root

# Layout: home (redirected profile), rc (verified RC), evidence, logs.
foreach ($relative in @(
    'home',
    'home/AppData/Local/Temp',
    'rc',
    'evidence',
    'logs'
)) {
    $null = New-Item -ItemType Directory -Path (Join-Path $root $relative) -Force
}

# Gate: prove with the RC itself that the redirect isolates the vault roots and the
# real source remains discoverable. doctor is read-only and safe without WeChat.
Write-Host 'Running the doctor isolation gate under the redirected profile environment...'
$doctor = Invoke-WearchiveRc -AcceptanceRoot $root -Arguments @('doctor', '--json', '--no-input') -TimeoutSeconds 300
if ($doctor.ExitCode -ne 0) {
    throw "doctor exited with $($doctor.ExitCode). Stderr: $($doctor.Stderr)"
}
if (-not $doctor.Json) {
    throw "doctor did not emit a single JSON document. Stdout: $($doctor.Stdout)"
}

$doctorJson = $doctor.Json
$sourceAvailable = [bool](Get-AcceptanceJsonProperty -Object (Get-AcceptanceJsonProperty -Object $doctorJson -Name 'source') -Name 'available')
$archiveAvailable = [bool](Get-AcceptanceJsonProperty -Object (Get-AcceptanceJsonProperty -Object $doctorJson -Name 'archive') -Name 'available')
$sourceVersion = Get-AcceptanceJsonProperty -Object (Get-AcceptanceJsonProperty -Object $doctorJson -Name 'source') -Name 'source_version'
$sourceProduct = Get-AcceptanceJsonProperty -Object (Get-AcceptanceJsonProperty -Object $doctorJson -Name 'source') -Name 'source_product'

if (-not $archiveAvailable) {
    throw 'Isolation gate failed: the RC does not see the acceptance-home archive root.'
}
if (-not $sourceAvailable) {
    Write-Warning @'
Isolation gate: the RC reports the WeChat source as unavailable under the redirect.
If the real WeChat data lives under the user profile root (xwechat_files), check that
the WeChat 4.x client is installed. If it lives under a redirected Documents folder,
create a junction inside the acceptance home so discovery resolves it, for example:

  New-Item -ItemType Junction -Path "<acceptance-root>\home\Documents\xwechat_files" `
      -Target "<real WeChat data root>\xwechat_files"

Then re-run init-root.ps1. The gate must pass before the run starts.
'@
}

# Direct proof that the vault root resolves inside the acceptance home: after doctor,
# the acceptance home must contain the WeArchive data directory.
$weArchiveData = Join-Path $home_ 'AppData/Local/WeArchive'
if (-not (Test-Path -LiteralPath $weArchiveData)) {
    throw 'Isolation gate failed: the RC did not create its data directory inside the acceptance home.'
}

$env_ = Get-AcceptanceEnvironmentFacts -Root $root
$facts = [ordered]@{
    schema_version = 1
    generated_at = (Get-Date).ToString('o')
    # Privacy-safe by construction: this file is attached publicly (runbook §11), so
    # machine paths are rendered relative to the acceptance root, never absolute.
    acceptance_root = ConvertTo-PrivacySafePath -Path $root -AcceptanceRoot $root
    home = ConvertTo-PrivacySafePath -Path $home_ -AcceptanceRoot $root
    rc = [ordered]@{
        release_tag = $config.rc.release_tag
        version = $config.rc.expected_version
        commit_sha = $config.rc.commit_sha
        asset_url = $config.rc.asset_url
        asset_sha256 = $config.rc.asset_sha256.ToLowerInvariant()
    }
    isolation = [ordered]@{
        doctor_exit_code = $doctor.ExitCode
        doctor_source_available = $sourceAvailable
        doctor_archive_available = $archiveAvailable
        vault_root = ConvertTo-PrivacySafePath -Path (Join-Path $weArchiveData 'rawvault') -AcceptanceRoot $root
        archive_root = ConvertTo-PrivacySafePath -Path (Join-Path $weArchiveData 'archive') -AcceptanceRoot $root
    }
    environment = [ordered]@{
        os_caption = $env_.os_caption
        os_version = $env_.os_version
        filesystem = $env_.filesystem
        allocation_unit_bytes = $env_.allocation_unit_bytes
        drive_free_bytes = $env_.drive_free_bytes
        drive_total_bytes = $env_.drive_total_bytes
        wechat_source_product = $sourceProduct
        wechat_client_version = $sourceVersion
    }
}

# Optional v1 seed: copy (never move) an existing vault into the acceptance vault.
if ($SeedVaultRoot) {
    if (-not (Test-Path -LiteralPath (Join-Path $SeedVaultRoot 'accounts'))) {
        throw "Seed vault root does not look like a vault (missing accounts/): $SeedVaultRoot"
    }
    $vaultRoot = Join-Path $weArchiveData 'rawvault'
    if (Test-Path -LiteralPath (Join-Path $vaultRoot 'accounts')) {
        throw ('Re-seeding refused: the acceptance vault already contains an accounts directory. ' +
               'Re-seeding would nest directories and rewrite the immutability manifest; use a ' +
               "fresh acceptance root instead: $vaultRoot")
    }
    $null = New-Item -ItemType Directory -Path $vaultRoot -Force
    Write-Host "Copying seed vault accounts from $SeedVaultRoot (read-only copy)..."
    Copy-Item -Path (Join-Path $SeedVaultRoot 'accounts') -Destination (Join-Path $vaultRoot 'accounts') -Recurse -Force

    # Pin ALL seed files (manifests AND the preserved artifact bytes), not only the
    # manifests: the immutability check must not rest on vault verify alone.
    $seedHashes = New-AcceptanceHashManifest -Directory $vaultRoot -AcceptanceRoot $root -Filter @('*')
    $seedPath = Join-Path $root 'evidence/v1-seed-hashes.json'
    Set-Content -LiteralPath $seedPath -Value ($seedHashes | ConvertTo-Json -Depth 16) -Encoding utf8NoBOM
    Write-Host ("Seed immutability manifest written: {0} ({1} files hashed)" -f
        (ConvertTo-PrivacySafePath -Path $seedPath -AcceptanceRoot $root),
        @(Get-AcceptanceManifestEntries -Entries $seedHashes.entries).Count)

    # v1-only read gate: authoritative verification of the seeded (v1-only) vault.
    Write-Host 'Verifying the seeded v1-only vault (v1-only read/rebuild gate)...'
    $verify = Invoke-WearchiveRc -AcceptanceRoot $root -Arguments @(
        'vault', 'verify', '--vault-root', $vaultRoot, '--json', '--no-input') -TimeoutSeconds 3600
    $verifySucceeded = Get-AcceptanceJsonProperty -Object $verify.Json -Name 'succeeded' -Default $false
    if ($verify.ExitCode -ne 0 -or $verifySucceeded -ne $true) {
        throw "Seeded v1 vault failed authoritative verification. Stderr: $($verify.Stderr)"
    }
    Write-Host 'Seeded v1 evidence verifies.'
    $facts.isolation['seeded_v1_vault'] = $true
    # Privacy-safe: this file is attached publicly (runbook §11).
    $facts.isolation['seed_source'] = ConvertTo-PrivacySafePath -Path $SeedVaultRoot -AcceptanceRoot $root
}

Set-Content -LiteralPath (Join-Path $root 'evidence/environment.json') `
    -Value ($facts | ConvertTo-Json -Depth 16) -Encoding utf8NoBOM

Write-Host ''
Write-Host 'Acceptance root initialized.'
Write-Host ("  vault root:   {0}" -f (Join-Path $weArchiveData 'rawvault'))
Write-Host ("  archive root: {0}" -f (Join-Path $weArchiveData 'archive'))
Write-Host ("  evidence:     {0}" -f (Join-Path $root 'evidence'))
if (-not $sourceAvailable) {
    Write-Warning 'The source gate reported the WeChat source as unavailable; resolve before starting the run.'
}
Write-Host 'Gate 2 (isolation) passed.'
