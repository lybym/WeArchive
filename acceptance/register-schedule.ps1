#Requires -Version 7.0
<#
.SYNOPSIS
    Scheduled-task registration helper for the hourly acceptance cadence (Issue #86).

.DESCRIPTION
    Registers, disables, re-enables or removes a Windows scheduled task that runs
    hourly-capture.ps1 every hour for the 7-day >=168-point acceptance run.

    THIS SCRIPT ONLY MANAGES THE TASK. It never performs a capture itself and is
    never invoked by the harness. The parent run operator decides when to register
    and start the cadence; registering this task must happen AFTER verify-rc.ps1 and
    init-root.ps1 have passed their gates.

    Default task parameters (override with the corresponding switches):
      - Name:        WeArchiveIssue86AcceptanceHourly
      - Trigger:     daily, repeating every 1 hour for 8 days (covers the 7-day run
                     plus margin; missed points are classified by the harness)
      - Action:      pwsh -NoProfile -ExecutionPolicy Bypass -File <repo>\acceptance\hourly-capture.ps1
                     -Config <config path>
      - RunLevel:    limited (no elevation required; the RC runs unprivileged)
      - StartWhenAvailable: yes (a missed trigger runs when the host wakes, and the
                     harness then classifies the gap as missed_no_execution)

    Requirements:
      - pwsh (PowerShell 7) must be on PATH for the SYSTEM account, which is why the
        action resolves pwsh's full path at registration time.

.PARAMETER Config
    Path to the user-maintained acceptance config (config.json) handed to the task.

.PARAMETER TaskName
    Scheduled task name. Default WeArchiveIssue86AcceptanceHourly.

.PARAMETER WhatIf
    Print the exact task definition without registering (default safety net).

.PARAMETER Disable / Enable / Remove
    Manage an already-registered task instead of registering a new one.

.PARAMETER UnregisterAfterRun
    Not implemented on purpose: the operator removes the task explicitly with
    -Remove when the acceptance window closes, so nothing disappears silently.

.EXAMPLE
    # Dry run (prints the definition, registers nothing):
    ./register-schedule.ps1 -Config D:\acceptance\issue-86\config.json -WhatIf

    # Register for real:
    ./register-schedule.ps1 -Config D:\acceptance\issue-86\config.json
#>
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Register')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Register')] [string] $Config,
    [Parameter(ParameterSetName = 'Manage', Mandatory)] [switch] $Disable,
    [Parameter(ParameterSetName = 'Manage', Mandatory)] [switch] $Enable,
    [Parameter(ParameterSetName = 'Manage', Mandatory)] [switch] $Remove,
    [string] $TaskName = 'WeArchiveIssue86AcceptanceHourly'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = $PSScriptRoot

if ($Disable) {
    Disable-ScheduledTask -TaskName $TaskName -ErrorAction Stop | Out-Null
    Write-Host "Task '$TaskName' disabled (hourly cadence paused for boundary exercises)."
    return
}
if ($Enable) {
    Enable-ScheduledTask -TaskName $TaskName -ErrorAction Stop | Out-Null
    Write-Host "Task '$TaskName' re-enabled."
    return
}
if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
    Write-Host "Task '$TaskName' removed."
    return
}

# Register path.
$configFullPath = [System.IO.Path]::GetFullPath($Config)
if (-not (Test-Path -LiteralPath $configFullPath)) {
    throw "Acceptance config not found: $configFullPath"
}
$scriptFullPath = Join-Path $scriptDir 'hourly-capture.ps1'
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source

$action = New-ScheduledTaskAction -Execute $pwsh `
    -Argument ('-NoProfile -ExecutionPolicy Bypass -File "{0}" -Config "{1}"' -f $scriptFullPath, $configFullPath)
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) `
    -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration (New-TimeSpan -Days 8)
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -DontStopIfGoingOnBatteries `
    -AllowStartIfOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
    -MultipleInstances IgnoreNew

$definition = [ordered]@{
    task_name = $TaskName
    action = "$pwsh -NoProfile -ExecutionPolicy Bypass -File $scriptFullPath -Config $configFullPath"
    trigger = 'once, repeating every 1 hour for 8 days, start-when-available'
    run_level = 'limited (current user)'
    instances = 'ignore new (never overlaps the previous cycle)'
}

if ($PSCmdlet.ShouldProcess($TaskName, 'Register the hourly acceptance scheduled task')) {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Settings $settings -Description 'WeArchive Issue #86 official-RC hourly acceptance capture point.' `
        -Force -ErrorAction Stop | Out-Null
    Write-Host "Task '$TaskName' registered. The first trigger fires about 5 minutes from now;"
    Write-Host 'pause it with -Disable before running failure-boundary.ps1 -IncludeKillCapture,'
    Write-Host 'and remove it with -Remove after the acceptance window closes.'
} else {
    Write-Host 'WhatIf: the task definition below would be registered:'
    $definition | ConvertTo-Json -Depth 4
}
