# Sample BluetoothAutoLock perf for ~10 seconds
$p = Get-Process -Name BluetoothAutoLock -ErrorAction Stop
$cpuStart = $p.TotalProcessorTime
$wallStart = Get-Date
Start-Sleep -Seconds 10
$p.Refresh()
$cpuDelta = ($p.TotalProcessorTime - $cpuStart).TotalMilliseconds
$wallDelta = ((Get-Date) - $wallStart).TotalMilliseconds
$cores = (Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
$pct = [math]::Round(100.0 * $cpuDelta / $wallDelta / $cores, 3)

[pscustomobject]@{
    PID                = $p.Id
    'WorkingSet (MB)'  = [math]::Round($p.WorkingSet64 / 1MB, 1)
    'PrivateMem (MB)'  = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    'VirtualMem (MB)'  = [math]::Round($p.VirtualMemorySize64 / 1MB, 1)
    Threads            = $p.Threads.Count
    Handles            = $p.HandleCount
    'CPU% (10s avg)'   = $pct
    'CPU time (s)'     = [math]::Round($p.TotalProcessorTime.TotalSeconds, 1)
    'Uptime'           = ((Get-Date) - $p.StartTime).ToString("hh\:mm\:ss")
} | Format-List
