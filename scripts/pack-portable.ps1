<#
.SYNOPSIS
    Publishes WeArchive as a self-contained win-x64 CLI and produces the portable ZIP.

.DESCRIPTION
    Implements the distribution decision in docs/adr/0007-cli-self-contained-distribution.md:
    a self-contained publish of src/WeArchive.Cli (no .NET runtime required on the target
    machine) plus a `wearchive.cmd` PATH shim, packaged as WeArchive-win-x64.zip.

    There is no installer or auto-updater. The product is a command-line tool for humans,
    scripts and agents, so a versioned portable ZIP that is extracted and invoked directly
    is sufficient (Issue #9 non-goals).

    Publishing a solution with -o is not supported, so only the CLI project is published.

.PARAMETER Version
    Version stamped into the produced assembly. Defaults to the VersionPrefix in
    Directory.Build.props.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER OutputRoot
    Directory that receives the publish output and the ZIP. Defaults to <repo>/artifacts.

.PARAMETER SkipPublish
    Reuse the existing publish directory and only rebuild the ZIP. Use this in CI, where the
    publish step has already run.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Configuration = 'Release',
    [string] $OutputRoot,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts' }

if (-not $Version) {
    [xml] $props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
    $Version = $props.Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
}

$project = Join-Path $repoRoot 'src/WeArchive.Cli/WeArchive.Cli.csproj'
$publishDir = Join-Path $OutputRoot 'publish/win-x64'
$zipPath = Join-Path $OutputRoot 'WeArchive-win-x64.zip'
$exeName = 'WeArchive.exe'

Write-Host "WeArchive portable CLI packaging"
Write-Host "  version      : $Version"
Write-Host "  configuration: $Configuration"
Write-Host "  publish dir  : $publishDir"

if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    & dotnet publish $project `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:Version=$Version `
        -p:PublishReadyToRun=false `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

$exe = Join-Path $publishDir $exeName
if (-not (Test-Path $exe)) { throw "Expected $exe to exist after publish." }

# A self-contained publish is a folder. The PATH entry point documented in docs/CLI.md is
# the lower-case `wearchive`, so the package carries a one-line shim for it; Windows also
# resolves `WeArchive.exe` case-insensitively once the folder is on PATH.
$shim = Join-Path $publishDir 'wearchive.cmd'
@"
@echo off
"%~dp0$exeName" %*
"@ | Set-Content -Path $shim -Encoding ascii

if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "Produced:"
Write-Host ("  {0} ({1:N1} MB)" -f $publishDir, ((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))
Write-Host ("  {0} ({1:N1} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
