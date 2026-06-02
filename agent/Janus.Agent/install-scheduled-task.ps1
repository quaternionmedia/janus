# Janus.Agent install script.
#
# Registers a Windows Task Scheduler task that launches Janus.Agent
# automatically when you log in. The task runs in your interactive user
# session (NOT Session 0), which is what the agent needs -- clipboard
# APIs, global hotkeys, and the WTSRegisterSessionNotification listener
# all require a real desktop session.
#
# Usage (from a normal PowerShell prompt, not as Administrator):
#   .\install-scheduled-task.ps1 -Side P -Port COM9
#   .\install-scheduled-task.ps1 -Side W -Port COM7
#
# To uninstall:
#   .\install-scheduled-task.ps1 -Side P -Uninstall
#
# To preview without making changes:
#   .\install-scheduled-task.ps1 -Side P -Port COM9 -WhatIf
#
# This script lives at <repo>/agent/Janus.Agent/install-scheduled-task.ps1
# (or wherever the agent project lives in your repo). It resolves the
# exe by walking from its own location, so the same script works on any
# machine regardless of repo root.

[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Install', Position = 0)]
    [Parameter(Mandatory = $true, ParameterSetName = 'Uninstall', Position = 0)]
    [ValidateSet('P', 'W')]
    [string]$Side,

    [Parameter(Mandatory = $true, ParameterSetName = 'Install', Position = 1)]
    [string]$Port,

    [Parameter(Mandatory = $true, ParameterSetName = 'Uninstall')]
    [switch]$Uninstall
)

# -----------------------------------------------------------------------------
# Task name
# -----------------------------------------------------------------------------

# Task name pattern lets the two sides coexist on the same machine if you ever
# need that. Cosmetic; nothing depends on the name format itself.
$TaskName = "Janus.Agent ($(if ($Side -eq 'P') { 'Personal' } else { 'Work' }))"

# -----------------------------------------------------------------------------
# Uninstall path
# -----------------------------------------------------------------------------

if ($Uninstall) {
    $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $existing) {
        Write-Host "Task '$TaskName' is not registered. Nothing to do."
        exit 0
    }

    if ($PSCmdlet.ShouldProcess($TaskName, "Unregister scheduled task")) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Unregistered task: $TaskName"
    }
    exit 0
}

# -----------------------------------------------------------------------------
# Resolve the exe path
#
# Walk up from this script's location to find the project root, then probe in
# the order the user specified:
#   1. <project-root>/Janus.Agent.exe       (manually copied release build)
#   2. <project>/bin/Release/net9.0-windows/Janus.Agent.Pico.exe
#   3. <project>/bin/Debug/net9.0-windows/Janus.Agent.Pico.exe
#
# Script is expected to live at:
#   <something>/agent/Janus.Agent/install-scheduled-task.ps1
# so the project root is $PSScriptRoot/.. (its parent), and the project dir is
# $PSScriptRoot itself.
# -----------------------------------------------------------------------------

$ProjectDir = $PSScriptRoot
$ProjectRoot = Split-Path -Parent $PSScriptRoot

# All three probes now use the same exe name. The project's
# <AssemblyName> is Janus.Agent, so dotnet build emits Janus.Agent.exe
# in its bin folders; a manually-copied release exe gets the same
# name when dropped next to the project dir.
$Candidates = @(
    (Join-Path $ProjectRoot 'Janus.Agent.exe'),
    (Join-Path $ProjectDir 'bin\Release\net9.0-windows\Janus.Agent.exe'),
    (Join-Path $ProjectDir 'bin\Debug\net9.0-windows\Janus.Agent.exe')
)

$ExePath = $null
foreach ($candidate in $Candidates) {
    if (Test-Path $candidate -PathType Leaf) {
        $ExePath = (Resolve-Path $candidate).Path
        break
    }
}

if (-not $ExePath) {
    Write-Error @"
Could not find Janus.Agent exe. Looked in:
$($Candidates -join "`n")

Build the project (dotnet build -c Release) or drop a release exe at
$($Candidates[0])
and re-run.
"@
    exit 1
}

$WorkingDir = Split-Path -Parent $ExePath

Write-Host "Found exe:       $ExePath"
Write-Host "Working dir:     $WorkingDir"
Write-Host "Side / port:     $Side $Port"
Write-Host "Task name:       $TaskName"
Write-Host ""

# -----------------------------------------------------------------------------
# Build the task definition
# -----------------------------------------------------------------------------

# The agent reads its first arg as the device id (P or W) and second as the
# COM port. See Program.cs / Main().
$ActionArgs = "$Side $Port"

$Action = New-ScheduledTaskAction `
    -Execute $ExePath `
    -Argument $ActionArgs `
    -WorkingDirectory $WorkingDir

# "At log on of the current user." Using the explicit user string (rather than
# the AtLogOn-without-user form) confines the trigger to your account, so a
# different user logging into the same box doesn't accidentally launch your
# agent.
$CurrentUser = "$env:USERDOMAIN\$env:USERNAME"
$Trigger = New-ScheduledTaskTrigger -AtLogOn -User $CurrentUser

# Settings -- restart on failure, no timeout, no battery throttling.
$Settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew

# Run only when the user is logged on, in their interactive session, at their
# own privilege level (NOT elevated -- the agent doesn't need admin and we
# don't want UAC prompts on login).
$Principal = New-ScheduledTaskPrincipal `
    -UserId $CurrentUser `
    -LogonType Interactive `
    -RunLevel Limited

# -----------------------------------------------------------------------------
# Register (or replace if it already exists)
# -----------------------------------------------------------------------------

if ($PSCmdlet.ShouldProcess($TaskName, "Register scheduled task")) {
    $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Task '$TaskName' already exists; replacing."
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }

    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $Action `
        -Trigger $Trigger `
        -Settings $Settings `
        -Principal $Principal | Out-Null

    Write-Host ""
    Write-Host "Registered '$TaskName'. It will launch on next login."
    Write-Host ""
    Write-Host "To launch it now without logging out:"
    Write-Host "    Start-ScheduledTask -TaskName '$TaskName'"
    Write-Host ""
    Write-Host "To inspect / modify in GUI:"
    Write-Host "    taskschd.msc -> Task Scheduler Library -> '$TaskName'"
    Write-Host ""
    Write-Host "To uninstall:"
    Write-Host "    .\install-scheduled-task.ps1 -Side $Side -Uninstall"
}