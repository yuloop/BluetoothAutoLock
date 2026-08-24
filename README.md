# BluetoothAutoLock

Lightweight Windows tray application that locks the workstation only after the
configured Bluetooth key is absent and the user has also been idle for the
configured threshold.

- System-tray app with a configuration dialog — no command line needed for daily use.
- Targets .NET Framework 4.x — runs out of the box on Windows 10 LTSC 2021.
- Auto-start on logon and auto-restart on crash via Windows Task Scheduler.
- Settings persist to a plain `config.ini` (also editable by hand).
- Single-file `.exe`, ~90 KB, only Win32 + WinForms — no third-party DLLs.

## Why not Windows Dynamic Lock alone?

Windows' built-in Dynamic Lock waits ~30 s after the Bluetooth signal weakens
and offers no tuning. This program lets you pick the exact absent+idle
threshold (e.g. 300 s / 5 min) and blocks locking while either the Bluetooth
key is detected or keyboard/mouse input shows you are still using the PC.

## Layout

```
BluetoothAutoLock/
├── src/                 # C# sources
│   ├── Program.cs
│   ├── TrayApp.cs           # NotifyIcon + right-click menu
│   ├── SettingsForm.cs      # configuration dialog
│   ├── BluetoothMonitor.cs  # poll loop + state machine
│   ├── LolOptimizer.cs      # LoL game-mode WSL/display/remote-app optimizer
│   ├── GameEnvironmentOptimizer.cs # one-click game-env cleanup + anomaly monitor/AI analysis
│   ├── Config.cs            # INI load/save (round-trip preserves comments)
│   ├── Logger.cs            # thread-safe file logger with rotation
│   └── NativeMethods.cs     # Win32 P/Invoke
├── scripts/
│   ├── build.cmd        # compile with built-in csc.exe (no SDK needed)
│   ├── install.ps1      # register Scheduled Task (logon trigger)
│   └── uninstall.ps1    # remove Scheduled Task
├── config/
│   └── config.ini       # default config template
└── build/               # output: BluetoothAutoLock.exe + config.ini
```

## Build

```cmd
cd D:\xiangmudata\win_bluetooth_auto_scan_lock_on_disconnect
.\scripts\build.cmd
```

Output: `build\BluetoothAutoLock.exe` + `build\config.ini`.

The build script invokes `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
which ships with every Windows 10 install. **No .NET SDK, MSBuild, or NuGet
required.**

## Configure (GUI)

Just run the program — it lives in the system tray.

```cmd
.\build\BluetoothAutoLock.exe
```

A shield icon appears in the notification area. **Right-click** for the menu,
or **double-click** to open Settings:

![Settings dialog](build/settings-dialog.png)

The settings dialog shows:

| Field                  | Notes                                                                     |
|------------------------|---------------------------------------------------------------------------|
| **Target device**      | Drop-down of all paired Bluetooth devices, with `[connected]` / `[paired]` markers. Click **Refresh** to re-scan. |
| **Bluetooth absence threshold** | After keyboard/mouse has been idle for 30s, seconds the Bluetooth key must remain absent before locking. 1 – 3600. |
| **Final recheck attempts** | Number of Bluetooth rechecks immediately before locking. Default 3.       |
| **Polling random lower bound** | Minimum poll interval. Actual interval is randomized from this value through +7 seconds; default 8 means 8–15s. |
| **Lock shortcuts**     | Optional shortcuts triggered in order immediately before locking; each shortcut can have a note. |
| **Log level**          | `Debug` / `Info` / `Warn` / `Error`.                                      |
| **Log path**           | Where to write the log file. Parent directory is auto-created.            |
| **LoL game optimization** | Optional: when the LoL process starts, reclaim WSL cache once before the game, optionally disable a virtual display, and optionally close AskLink / ToDesk until the game has been quiet for 10 minutes, then restore those apps with a restore-only minimized/tray-silent startup. |

Click **Save** — settings are written to `config.ini` next to the exe and the
monitor restarts immediately.

The tray menu also exposes:
- **Status: …** — shows the current state (Connected / Bluetooth missing but in use / Locked / Paused / etc).
- **Settings…** — open the dialog. The dialog includes a realtime status/log panel with current state, status timestamp, and next action.
- **Pause / Resume monitoring** — temporarily disable Bluetooth/idle locking (e.g. while presenting).
- **智能优化游戏环境（游戏前运行）** — checks the LOL lag-summary patterns and cleans only abnormal node/omx/python zombie groups or LoL crash/client residue.
- **Enable / disable game environment anomaly monitor** — 5-minute background analysis with log output and severe-only tray alerts.
- **Open log folder** — opens Explorer at the log file.
- **About / Quit**.

## Configure (file)

If you prefer text, edit `build\config.ini` directly:

| Key                       | Default                                              | Description                                                          |
|---------------------------|------------------------------------------------------|----------------------------------------------------------------------|
| `DeviceName`              | _(empty)_                                            | Display label saved from the selected device; not used for proximity matching. |
| `DeviceAddress`           | _(empty)_                                            | Required unique Classic Bluetooth MAC `AA:BB:CC:DD:EE:FF`; proximity matching uses this ID only. |
| `DisconnectDelaySeconds`  | `150`                                                | After keyboard/mouse has been idle for 30s, required continuous Bluetooth-absence seconds before locking. 150 = 2.5 minutes. 1–3600. |
| `DisconnectConfirmSeconds`| `3`                                                  | Final Bluetooth recheck attempts before locking. Any successful final probe cancels the lock. 1–10. |
| `PollingIntervalSeconds`  | `8`                                                  | Minimum poll interval; actual interval is randomized from this value through +7 seconds. Default 8 means 8–15s. 1–60. |
| `LogPath`                 | `C:\ProgramData\BluetoothAutoLock\service.log`       | Log file path.                                                       |
| `LogLevel`                | `Info`                                               | `Debug`, `Info`, `Warn`, `Error`.                                    |
| `MaxLogSizeMB`            | `5`                                                  | Rotate to `<name>.log.1` when exceeded.                              |
| `LockShortcutSettleMilliseconds` | `3000` | Wait time after triggering lock shortcuts before locking, giving global hotkey handlers time to run. 0–10000. |
| `LockShortcut1`, `LockShortcut2`, ... | _(empty)_ | Optional shortcuts triggered in order before locking. Format: `Ctrl+Alt+K|note`; notes are shown in Settings and logs. |
| `LolOptimizerEnabled`     | `false`                                              | Enable LoL game-mode automation.                                     |
| `LolProcessName`          | `League of Legends`                                  | LoL process name without `.exe`.                                     |
| `VirtualDisplayDeviceId`  | _(empty)_                                            | Optional display device instance ID to disable during LoL and restore after exit. |
| `LolOptimizerPollSeconds` | `3`                                                  | Poll interval for detecting LoL start. 1–60.                         |
| `LolWslOptimizeEnabled`   | `true`                                               | Reclaim WSL cache once before each LoL session; does not limit WSL CPU. |
| `WslDistro`               | `Ubuntu`                                             | WSL distro used for `drop_caches`.                                   |
| `LolAutoCloseRemoteEnabled` | `true`                                             | If enabled, close configured remote apps at LoL start.               |
| `RemoteCloseProcessNames` | `AskLink,ToDesk`                                     | Process names to close; only successfully closed, pre-running apps are restarted. |
| `RemoteRestartQuietMinutes` | `10`                                               | After LoL exits, wait this many quiet minutes with no LoL process before restarting closed remote apps; the restart path requests minimized/tray-silent startup only for this app-managed restore. |
| `GameProblemMonitorEnabled` | `true` | Enable 5-minute game-environment anomaly monitoring. |
| `GameProblemMonitorIntervalMinutes` | `5` | Background analysis interval. |
| `GameProblemMonitorAiEnabled` | `true` | Use AI only after local rules find anomaly candidates. |
| `GameProblemMonitorAiEndpoint` | `https://ark.cn-beijing.volces.com/api/v3/responses` | Ark Responses API endpoint. |
| `GameProblemMonitorAiModel` | `doubao-seed-2-0-lite-260215` | AI model used for anomaly judgement. |
| `GameProblemMonitorAiApiKey` | _(empty)_ | Optional API key; alternatively set `GameProblemMonitorAiApiKeyEnv`. |
| `GameProblemMonitorAiApiKeyEnv` | `ARK_API_KEY` | Environment variable fallback for the API key. |
| `GameProblemNotifyRepeatMinutes` | `30` | Duplicate tray notification suppression window. |
| `GameProblemRunawayCpuPercent` | `90` | Single-core CPU threshold for suspected runaway loops; notification requires consecutive hits. |
| `GameOptimizerToolZombieCountThreshold` | `20` | node/omx zombie-group count threshold from the LOL lag summary. |
| `GameOptimizerToolZombieMemoryMB` | `2048` | node/omx/python abnormal memory threshold. |
| `GameOptimizerLolRenderCountThreshold` | `2` | `LeagueClientUxRender` count above this is treated as residue. |
| `GameOptimizerToolCleanupProcessNames` | `node,omx-node-stdio-hidden,python` | Process names eligible for one-click cleanup when that group is abnormal. |
| `GameOptimizerLolCleanupProcessNames` | `LeagueCrashHandler64,LeagueClientUxRender,LeagueClient,League of Legends,RiotClientServices` | LoL residue cleanup list. |

The GUI's **Save** button preserves any existing comments in the file —
hand edits and GUI edits coexist cleanly.

## Install (auto-start + auto-restart)

```powershell
# current user only (no admin needed)
.\scripts\install.ps1

# system-wide (any user logon, requires admin PowerShell)
.\scripts\install.ps1 -Scope AllUsers
```

This registers a Scheduled Task named `BluetoothAutoLock` with:

- **Trigger:** at logon
- **Action:** run `build\BluetoothAutoLock.exe` (tray app)
- **Window:** hidden (tray-only)
- **Restart on failure:** every 1 minute, up to 999 retries
- **Battery:** allowed to start and stay running on battery

The script is idempotent — re-running it stops and replaces the existing task.

## Uninstall

```powershell
.\scripts\uninstall.ps1
```

## Verify

```powershell
Get-ScheduledTask -TaskName 'BluetoothAutoLock'      # task registered?
Start-ScheduledTask -TaskName 'BluetoothAutoLock'    # start now without re-logon
Get-Content -Wait 'C:\ProgramData\BluetoothAutoLock\service.log'   # tail logs
```

A normal log looks like:

```
[2026-05-07 19:02:10.221] [INFO ] Starting BluetoothAutoLock 1.1.6 | Config(...)
[2026-05-09 19:02:10.317] [INFO ] Monitor started. Target: address=AA:BB:CC:DD:EE:FF | Rule=idle 30s -> scan Bluetooth; lock only after target absent for configured window and no keyboard/mouse input | IdleBeforeBluetoothCheck=30s | BluetoothAbsenceBeforeLock=150s | FinalRecheckAttempts=3 | Polling=random 8-15s
[2026-05-09 19:02:10.401] [INFO ] Target seen connected: ExamplePhone [AA:BB:CC:DD:EE:FF]
[2026-05-09 19:14:55.802] [INFO ] Keyboard/mouse has been idle for at least 30s and target is not nearby; starting 150s Bluetooth absence window before lock.
[2026-05-09 19:17:25.811] [INFO ] Target absent for at least 150s after keyboard/mouse idle threshold and no input occurred; locking workstation.
```

## CLI flags (advanced / scripting)

The tray app is the default, but a few flags are useful for diagnostics or scripts:

| Flag              | Behavior                                                                  |
|-------------------|---------------------------------------------------------------------------|
| _(none)_          | Run the tray app.                                                         |
| `--config`        | Open just the settings dialog (no tray, no monitor).                      |
| `--list` / `-l`   | Print all paired devices and exit.                                        |
| `--once`          | Single check pass, then exit (diagnostic).                                |
| `--test-lock-shortcuts` | Trigger configured lock shortcuts only, then exit. No settings window and no workstation lock. |
| `--version`/`-v`  | Print version and exit.                                                   |
| `--help` / `-h`   | Show usage.                                                               |

> The exe is built as a Windows-subsystem app, so when running `--list` or
> `--once` from `cmd`, the prompt returns immediately and the output prints
> below. For clean capture, redirect: `BluetoothAutoLock.exe --list > out.txt`.

## Resource footprint

- ~90 KB executable.
- Tray app: 2 threads (UI + monitor); ~25–30 MB working set (WinForms baseline).
- No network sockets.
- No third-party DLLs — only Win32 (`Irprops.cpl`, `kernel32.dll`, `user32.dll`)
  and the .NET Framework BCL (mscorlib, System, System.Drawing, System.Windows.Forms).

## Reliability features

- All exceptions in the polling loop are caught and logged; the loop
  continues on the next tick. The process never terminates from a transient
  Bluetooth API hiccup.
- Crash auto-restart: Task Scheduler retries every 1 minute, up to 999 times.
- Re-lock suppression: once a Bluetooth-absent idle lock fires, unlocking the
  Windows session clears suppression but starts a fresh absent+idle window, so
  it will not immediately relock while you are using the computer.
- Safety-first lock rule: if the configured Bluetooth key is detected, the app
  never locks, even when the PC is idle.
- Active-user guard: if keyboard/mouse input is recent, the app never locks, even
  when the Bluetooth key is absent.
- Final presence check: before calling `LockWorkStation`, the app performs last
  Bluetooth probes; any successful probe cancels the lock.
- Startup behavior follows the same rule: a missing Bluetooth key can only lock
  after the PC has also been idle for the configured threshold.
- Log rotation: single backup at `<log>.1`, no infinite growth.
- LoL optimizer restores only resources it changed: virtual display state and remote apps that were running before the game and were actually closed. Restored AskLink / ToDesk instances are launched through the app-managed minimized/tray-silent path only; normal user or system startup is not modified. WSL optimization is a one-shot cache reclaim and does not change CPU state.
- Pause/resume stops and restarts the monitor thread instead of leaving a
  paused loop half-alive.
- Settings dialog is single-instance inside the tray app; repeated tray clicks
  focus the existing settings window instead of opening duplicates.
- The tray app uses a per-session mutex so manually starting the exe again
  exits instead of creating a second tray process.
- Graceful shutdown via tray **Quit**, system shutdown, or service stop.

## Troubleshooting

- **`--list` prints nothing.** Your Bluetooth radio is off or there are no
  paired devices. Pair the phone in Settings → Devices → Bluetooth first.
- **Lock never fires.** Open Settings via the tray; if the device shows as
  `[paired]` instead of `[connected]`, the phone hasn't established an active
  Bluetooth profile yet. Open KDE Connect / your sync app once; that creates
  a profile binding so `fConnected` flips to true.
- **Lock fires while I am using the PC.** It should not: keyboard/mouse activity
  blocks locking. Check the log for `not locking while the computer is in use`.
- **`csc.exe` not found** during build. You're on a stripped-down Windows
  install; install .NET Framework 4.x.

## Bluetooth backend

Uses `Irprops.cpl` (the standard Win32 `BluetoothAPIs`) with
`BluetoothFindFirstDevice` / `BluetoothFindNextDevice` and reads
`BLUETOOTH_DEVICE_INFO.fConnected`. This is the same API Windows itself uses;
available on every Windows 10/11 SKU including LTSC.
