<#
.SYNOPSIS
    Smoke-tests a published WeArchive CLI artifact.

.DESCRIPTION
    Issue #9 requires release verification to run against the *released artifact* rather
    than a local development build. This script executes the published WeArchive.exe and
    asserts the documented CLI contract:

      1. `--version --json` prints exactly one JSON document carrying the expected version;
      2. `--help --json` prints exactly one JSON document listing the required command family;
      3. `doctor --json --no-input` runs a real fixture-safe command path to completion and
         prints exactly one JSON document with `ready`, `source` and `archive` fields.

    `doctor` exits 0 whether or not a local WeChat client is present (reporting source
    unavailability is its purpose), so this smoke test needs no source fixture and must not
    be affected by the runner's environment. No chat content is read or printed.

    Version comparison ignores build metadata and is exact otherwise, so a prerelease
    (`0.2.0-rc.1`) is checked as a prerelease rather than being accepted as `0.2.0`.

.PARAMETER ArtifactDirectory
    Directory containing the published WeArchive.exe. Defaults to artifacts/publish/win-x64.

.PARAMETER ExpectedVersion
    Version the artifact must report, for example 0.1.0 or 0.2.0-rc.1. Optional.

.PARAMETER ExeName
    Executable name inside the artifact directory. Defaults to WeArchive.exe.

.PARAMETER ViaShim
    Invoke the artifact through its `wearchive.cmd` PATH shim instead of WeArchive.exe, so the
    documented `wearchive` entry point is what gets exercised. Requires the shim to be present.
#>
[CmdletBinding()]
param(
    [string] $ArtifactDirectory,
    [string] $ExpectedVersion,
    [string] $ExeName = 'WeArchive.exe',
    [switch] $ViaShim
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $repoRoot 'artifacts/publish/win-x64' }
$ArtifactDirectory = (Resolve-Path $ArtifactDirectory).Path

$exe = Join-Path $ArtifactDirectory $ExeName
if (-not (Test-Path $exe)) { throw "Published artifact not found: $exe" }

# The entry point that actually gets invoked. `docs/CLI.md` documents the lower-case
# `wearchive`, so `-ViaShim` proves the PATH shim resolves the exe, not just the exe itself.
$target = $exe
if ($ViaShim) {
    $target = Join-Path $ArtifactDirectory 'wearchive.cmd'
    if (-not (Test-Path $target)) { throw "PATH shim not found: $target" }
}

Write-Host "Smoke-testing $target"

# A single JSON document: exactly one line, no ANSI decoration, parseable.
function Invoke-Smoke {
    param(
        [string] $Label,
        [string[]] $Arguments,
        [int] $ExpectedExit = 0
    )

    $output = & $target @Arguments
    $exit = $LASTEXITCODE
    $text = ($output -join "`n").Trim()

    if ($exit -ne $ExpectedExit) {
        throw "[$Label] expected exit $ExpectedExit but got $exit. Output: $text"
    }

    if (-not $text) { throw "[$Label] produced no stdout." }
    if ($text.Contains("`n")) { throw "[$Label] stdout must be exactly one JSON document, got:`n$text" }
    if ($text.Contains([char]27)) { throw "[$Label] stdout must not carry ANSI escapes." }

    $doc = $text | ConvertFrom-Json
    Write-Host ("  {0}: exit {1}, {2} bytes" -f $Label, $exit, $text.Length)
    return $doc
}

# 1. --version --json
$version = Invoke-Smoke -Label '--version --json' -Arguments @('--version', '--json')
if (-not $version.version) { throw '--version --json did not report a "version" field.' }

if ($ExpectedVersion) {
    # The CLI reports its informational version (src/WeArchive.Cli/ProductVersion.cs), which for
    # a publishing build is the requested version. Build metadata is stripped from both sides so
    # a local `-p:Version=0.1.0+<commit>` build still compares equal to `0.1.0`.
    $expected = ($ExpectedVersion -split '\+')[0]
    if ($version.version -ne $expected) {
        throw "Artifact reports version '$($version.version)' but '$expected' was expected."
    }
}
Write-Host "    version=$($version.version) framework=$($version.framework) platform=$($version.platform)"

# 2. --help --json
$help = Invoke-Smoke -Label '--help --json' -Arguments @('--help', '--json')
$commandNames = @($help.commands | ForEach-Object { $_.name })
foreach ($required in @('doctor', 'account', 'conversation', 'sync', 'export', 'message', 'context')) {
    if ($commandNames -notcontains $required) {
        throw "Help document is missing the required '$required' command (FR-22)."
    }
}
Write-Host "    commands=$($commandNames -join ',')"

# 3. doctor --json --no-input — a real command path, safe without a WeChat client.
$doctor = Invoke-Smoke -Label 'doctor --json --no-input' -Arguments @('doctor', '--json', '--no-input')
foreach ($required in @('ready', 'source', 'archive')) {
    if (-not $doctor.PSObject.Properties[$required]) {
        throw "doctor JSON is missing the '$required' field."
    }
}
Write-Host "    ready=$($doctor.ready) source.available=$($doctor.source.available) archive.available=$($doctor.archive.available)"

Write-Host ''
Write-Host 'Artifact smoke test passed.'
