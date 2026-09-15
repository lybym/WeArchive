<#
.SYNOPSIS
    Publishes WeArchive as a self-contained win-x64 application and produces the portable ZIP.

.DESCRIPTION
    Implements the "Portable" artifact from docs/adr/0004-distribution-velopack.md:
    a self-contained publish (no .NET runtime required on the target machine) packaged as
    WeArchive-win-x64.zip, ready to extract and run.

    Publishing a solution with -o is not supported, so only the application project is
    published.

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

$project = Join-Path $repoRoot 'src/WeArchive.App/WeArchive.App.csproj'
$publishDir = Join-Path $OutputRoot 'publish/win-x64'
$zipPath = Join-Path $OutputRoot 'WeArchive-win-x64.zip'

Write-Host "WeArchive portable packaging"
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

$exe = Join-Path $publishDir 'WeArchive.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe to exist after publish." }

if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "Produced:"
Write-Host ("  {0} ({1:N1} MB)" -f $publishDir, ((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))
Write-Host ("  {0} ({1:N1} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
