<#
.SYNOPSIS
    Produces the Velopack installer and update assets for WeArchive.

.DESCRIPTION
    Implements the "Installer" artifact from docs/adr/0004-distribution-velopack.md.

    Runs the self-contained win-x64 publish, then packs it with the Velopack CLI. The vpk
    version is pinned to the Velopack package version referenced by the application, because
    Velopack's own guidance is that the packaging tool and the client library must match.

    Produces, under <repo>/artifacts/velopack:
      Setup.exe                 installer
      WeArchive-<version>-full.nupkg, *-delta.nupkg, RELEASES-*, releases.win.json
      WeArchive-win-x64-Portable.zip

.PARAMETER Version
    Release version, for example 0.1.0. Must be a valid semantic version for vpk.

.PARAMETER Channel
    Velopack release channel. Defaults to win.

.PARAMETER SkipPublish
    Reuse an existing publish directory instead of running dotnet publish again.

.PARAMETER CertificateThumbprint
    Optional Authenticode certificate thumbprint. Signing is not required for the MVP; passing
    this is how it is added later without changing the artifact layout.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Channel = 'win',
    [switch] $SkipPublish,
    [string] $CertificateThumbprint
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
$publishDir = Join-Path $artifacts 'publish/win-x64'
$releaseDir = Join-Path $artifacts 'velopack'

if (-not $Version) {
    [xml] $props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
    $Version = $props.Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
}

# vpk rejects build metadata; keep only the dotted numeric release.
if ($Version -notmatch '^\d+\.\d+\.\d+') {
    throw "Version '$Version' is not a valid release version for vpk (expected e.g. 1.2.3)."
}

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'pack-portable.ps1') -Version $Version
}

if (-not (Test-Path (Join-Path $publishDir 'WeArchive.exe'))) {
    throw "Publish directory '$publishDir' does not contain WeArchive.exe. Run without -SkipPublish first."
}

# The CLI must match the client library version referenced by the app.
$clientVersion = (Select-String -Path (Join-Path $repoRoot 'Directory.Packages.props') `
        -Pattern 'Include="Velopack"\s+Version="([^"]+)"').Matches.Groups[1].Value
if (-not $clientVersion) { throw 'Could not determine the Velopack package version.' }

Write-Host "Ensuring Velopack CLI v$clientVersion"
& dotnet tool install --global vpk --version $clientVersion 2>$null
& dotnet tool update --global vpk --version $clientVersion | Out-Null

if (Test-Path $releaseDir) { Remove-Item -Recurse -Force $releaseDir }
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

$vpkArgs = @(
    'pack',
    '--packId', 'WeArchive',
    '--packVersion', $Version,
    '--packDir', $publishDir,
    '--mainExe', 'WeArchive.exe',
    '--packTitle', 'WeArchive',
    '--channel', $Channel,
    '--outputDir', $releaseDir
)

if ($CertificateThumbprint) {
    $vpkArgs += @('--signParams', "/sha1 $CertificateThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256")
}

Write-Host "vpk $($vpkArgs -join ' ')"
& vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host 'Velopack release assets:'
Get-ChildItem $releaseDir -File | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
