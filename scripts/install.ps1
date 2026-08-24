<#
.SYNOPSIS
  Installs BluetoothAutoLock as a Scheduled Task that runs at user logon and
  automatically restarts on crash.

.NOTES
  Default mode (-Scope CurrentUser, no admin needed) registers the task under
  the current user's account. Use -Scope AllUsers for a system-wide install
  (requires elevation; runs at any user logon).

  Add -Elevated to register the task with Highest run level. This is required
  if you enable the LoL optimizer's virtual-display auto-toggle (pnputil needs
  admin). Registering an Elevated task itself requires Administrator
  PowerShell.
#>

[CmdletBinding()]
param(
    [ValidateSet("CurrentUser", "AllUsers")]
    [string] $Scope = "CurrentUser",

    [string] $TaskName = "BluetoothAutoLock",

    [string] $ExePath,

    [switch] $Elevated
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) {
    $ExePath = Join-Path $repoRoot "build\BluetoothAutoLock.exe"
}
if (-not (Test-Path $ExePath)) {
    throw "Executable not found at $ExePath. Run scripts\build.ps1 first."
}
$ExePath = (Resolve-Path $ExePath).Path
$workDir = Split-Path -Parent $ExePath

$current = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = $current.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($Scope -eq "AllUsers" -and -not $isAdmin) {
    throw "AllUsers install requires running PowerShell as Administrator."
}
if ($Elevated -and -not $isAdmin) {
    throw "-Elevated requires running PowerShell as Administrator (so the task can be registered with Highest RunLevel)."
}

# Idempotent: stop and remove any existing task with this name so the exe isn't held open
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "[install] removing existing task '$TaskName'" -ForegroundColor Yellow
    try { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue } catch { }
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

# --- Action ---
$action = New-ScheduledTaskAction `
    -Execute $ExePath `
    -WorkingDirectory $workDir

# --- Trigger ---
if ($Scope -eq "AllUsers") {
    $trigger = New-ScheduledTaskTrigger -AtLogOn
} else {
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
}

# --- Principal ---
$runLevel = if ($Elevated) { "Highest" } else { "Limited" }
if ($Scope -eq "AllUsers") {
    $principal = New-ScheduledTaskPrincipal -GroupId "BUILTIN\Users" -RunLevel $runLevel
} else {
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel $runLevel
}

# --- Settings: hidden, restart on failure, no time limit ---
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -Hidden `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew

$settings.DisallowStartIfOnBatteries = $false
$settings.StopIfGoingOnBatteries = $false

$task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description "Locks the workstation when the configured Bluetooth device disconnects."

Register-ScheduledTask -TaskName $TaskName -InputObject $task | Out-Null

Write-Host "[install] registered scheduled task '$TaskName'" -ForegroundColor Green
Write-Host "          executable: $ExePath"
Write-Host "          scope:      $Scope"
Write-Host "          runLevel:   $runLevel"
Write-Host ""
Write-Host "Verify with:  Get-ScheduledTask -TaskName '$TaskName'"
Write-Host "Start now:    Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "View logs:    Get-Content -Wait 'C:\ProgramData\BluetoothAutoLock\service.log'"
