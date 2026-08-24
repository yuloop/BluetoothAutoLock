<#
.SYNOPSIS
  Removes the BluetoothAutoLock scheduled task.
#>

[CmdletBinding()]
param(
    [string] $TaskName = "BluetoothAutoLock"
)

$ErrorActionPreference = "Stop"

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "[uninstall] task '$TaskName' not present; nothing to do." -ForegroundColor Yellow
    return
}

# Stop running instances first so the exe file isn't locked
try {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
} catch { }

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
Write-Host "[uninstall] removed scheduled task '$TaskName'." -ForegroundColor Green
