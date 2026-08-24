# BluetoothAutoLock

![Build](https://github.com/yuloop/BluetoothAutoLock/actions/workflows/build.yml/badge.svg)
![License](https://img.shields.io/badge/license-MIT-green)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)

轻量级 Windows 托盘程序：仅当配置的蓝牙钥匙离开且用户同时已空闲达到阈值时，才自动锁屏。

- 系统托盘常驻，带图形化配置窗口 — 日常使用无需命令行
- 面向 .NET Framework 4.x — Windows 10 LTSC 2021 开箱即用
- 通过 Windows 任务计划实现开机自启与崩溃自动重启
- 配置持久化到 `config.ini` 纯文本（支持手写编辑）
- 单文件 `.exe` 约 90 KB，仅依赖 Win32 + WinForms，无第三方 DLL

> **安全提示**：AI 异常分析的 API Key（`GameProblemMonitorAiApiKey`）默认为空。**严禁提交真实 Key 到 Git**，请通过环境变量 `ARK_API_KEY` 注入，或在本地私密 `config.ini` 中临时填写。`Config.Save()` 默认不再落地明文 Key，仅驻留内存。

## 为什么不用 Windows 动态锁？

Windows 自带的动态锁在蓝牙信号变弱后约 30 秒就锁，且不可调。本程序可自定义“离开 + 空闲”双重阈值（例如 300 秒 / 5 分钟），且只要检测到蓝牙钥匙或键盘/鼠标活动，就不会锁屏。

## 目录结构

```
BluetoothAutoLock/
├── src/                 # C# 源码
│   ├── Program.cs
│   ├── TrayApp.cs           # 托盘图标与右键菜单
│   ├── SettingsForm.cs      # 配置窗口
│   ├── BluetoothMonitor.cs  # 轮询与状态机
│   ├── LolOptimizer.cs      # LoL 游戏模式 WSL/显示器/远程软件优化
│   ├── GameEnvironmentOptimizer.cs # 一键游戏环境清理 + 异常监控/AI 分析
│   ├── Config.cs            # INI 加载/保存（保留注释回写）
│   ├── Logger.cs            # 线程安全日志与轮转
│   └── NativeMethods.cs     # Win32 P/Invoke
├── scripts/
│   ├── build.cmd        # 调用系统自带 csc.exe 编译，无需 SDK
│   ├── install.ps1      # 注册开机任务
│   └── uninstall.ps1    # 移除任务
├── config/
│   └── config.ini       # 默认配置模板（Key 留空）
└── build/               # 产物：BluetoothAutoLock.exe + config.ini
```

## 编译

```cmd
cd D:\xiangmudata\win_bluetooth_auto_scan_lock_on_disconnect
.\scripts\build.cmd
```

产物：`build\BluetoothAutoLock.exe` + `build\config.ini`。

编译脚本调用 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，该文件随 Windows 10 自带，**无需安装 .NET SDK / MSBuild / NuGet**。

> GitHub Actions 已配置自动编译：每次 `push` / `pull_request` 到 `main` 分支，`windows-latest` Runner 会执行 `.\scripts\build.cmd` 并上传 `BluetoothAutoLock.exe` 为 Artifact。

## 图形化配置

直接运行即可，程序常驻托盘：

```cmd
.\build\BluetoothAutoLock.exe
```

通知区出现盾牌图标，**右键**打开菜单，**双击**打开设置窗口。

设置窗口包含：

| 字段 | 说明 |
|------|------|
| **目标设备** | 已配对蓝牙设备下拉，带 `[已连接]` / `[已配对]` 标记，点 **刷新** 重新扫描 |
| **蓝牙离开阈值** | 键盘/鼠标空闲 30 秒后，蓝牙钥匙持续离开多少秒才锁屏 1–3600 |
| **最终复检次数** | 锁屏前最终蓝牙复检次数，默认 3 |
| **轮询随机下限** | 最小轮询间隔，实际间隔为该值到 +7 秒随机，默认 8 即 8–15 秒 |
| **锁屏快捷键** | 锁屏前按序触发的可选快捷键，可备注 |
| **日志级别** | `Debug` / `Info` / `Warn` / `Error` |
| **日志路径** | 日志文件位置，父目录自动创建 |
| **LoL 游戏优化** | 可选：检测到 LoL 启动时，游戏前一次性回收 WSL 缓存、可选禁用虚拟显示器、可选关闭 AskLink / ToDesk，游戏静默 10 分钟后静默恢复 |

点 **保存** — 配置写入 exe 旁的 `config.ini` 并立即重启监控。

托盘菜单还提供：
- **状态：…** — 当前状态（已连接 / 蓝牙丢失但使用中 / 已锁 / 已暂停 等）
- **设置…** — 打开配置窗口，含实时状态/日志面板
- **暂停/恢复监控** — 临时禁用蓝牙/空闲锁屏（如演示时）
- **智能优化游戏环境（游戏前运行）** — 仅在异常时清理 LOL 卡顿总结中的 node/omx/python 残留组或 LoL 崩溃残留
- **启用/禁用游戏环境异常监控** — 5 分钟后台分析，仅严重残留、崩溃、疑似死循环托盘提醒
- **打开日志目录**
- **关于 / 退出**

## 文本配置

也可直接编辑 `build\config.ini`：

| 键 | 默认 | 说明 |
|----|------|------|
| `DeviceName` | _(空)_ | 选中设备显示名，仅作展示 |
| `DeviceAddress` | _(空)_ | 必填，Classic 蓝牙 MAC `AA:BB:CC:DD:EE:FF`，仅用此 ID 判近 |
| `DisconnectDelaySeconds` | `150` | 空闲 30 秒后，持续离开多少秒才锁，150=2.5 分钟 1–3600 |
| `DisconnectConfirmSeconds` | `3` | 锁前最终复检次数 1–10 |
| `PollingIntervalSeconds` | `8` | 最小轮询间隔，实际为该值到 +7 秒随机 1–60 |
| `LogPath` | `C:\ProgramData\BluetoothAutoLock\service.log` | 日志路径 |
| `LogLevel` | `Info` | `Debug`/`Info`/`Warn`/`Error` |
| `MaxLogSizeMB` | `5` | 超过则轮转到 `<name>.log.1` |
| `LockShortcutSettleMilliseconds` | `3000` | 触发快捷键后等待多久再锁屏 0–10000 |
| `LockShortcut1` 等 | _(空)_ | 锁前按序触发 `Ctrl+Alt+K\|备注` |
| `LolOptimizerEnabled` | `false` | 启用 LoL 游戏模式自动化 |
| `LolProcessName` | `League of Legends` | LoL 进程名（不含 .exe） |
| `VirtualDisplayDeviceId` | _(空)_ | 游戏时禁用的显示设备实例 ID |
| `LolOptimizerPollSeconds` | `3` | 检测 LoL 启动轮询 1–60 |
| `LolWslOptimizeEnabled` | `true` | 每次 LoL 会话前一次性回收 WSL 缓存 |
| `WslDistro` | `Ubuntu` | `drop_caches` 使用的 WSL 发行版 |
| `LolAutoCloseRemoteEnabled` | `true` | 游戏时关闭远程软件 |
| `RemoteCloseProcessNames` | `AskLink,ToDesk` | 需关闭的进程名，仅重启本程序确实关闭且游戏前已运行的实例 |
| `RemoteRestartQuietMinutes` | `10` | LoL 退出后静默多久再恢复远程软件 |
| `GameProblemMonitorEnabled` | `true` | 启用 5 分钟游戏环境异常监控 |
| `GameProblemMonitorIntervalMinutes` | `5` | 后台分析间隔 |
| `GameProblemMonitorAiEnabled` | `true` | 仅本地规则发现候选后才调 AI |
| `GameProblemMonitorAiEndpoint` | `https://ark.cn-beijing.volces.com/api/v3/responses` | Ark Responses 接口 |
| `GameProblemMonitorAiModel` | `doubao-seed-2-0-lite-260215` | 异常判定模型 |
| `GameProblemMonitorAiApiKey` | _(空)_ | **留空**，请用环境变量 `ARK_API_KEY` 注入，切勿提交 |
| `GameProblemMonitorAiApiKeyEnv` | `ARK_API_KEY` | 读取 Key 的环境变量名 |
| `GameProblemNotifyRepeatMinutes` | `30` | 重复托盘通知抑制窗口 |
| `GameProblemRunawayCpuPercent` | `90` | 疑似死循环单核 CPU 阈值，需连续命中才提醒 |
| `GameOptimizerToolZombieCountThreshold` | `20` | node/omx 僵尸组数量阈值 |
| `GameOptimizerToolZombieMemoryMB` | `2048` | 异常内存阈值 |
| `GameOptimizerLolRenderCountThreshold` | `2` | `LeagueClientUxRender` 超此数视为残留 |
| `GameOptimizerToolCleanupProcessNames` | `node,omx-node-stdio-hidden,python` | 一键清理候选进程名 |
| `GameOptimizerLolCleanupProcessNames` | `LeagueCrashHandler64,LeagueClientUxRender,LeagueClient,League of Legends,RiotClientServices` | LoL 残留清理列表 |

图形界面的 **保存** 会保留文件既有注释，手写与 GUI 编辑可共存。

## 安装（开机自启 + 崩溃重启）

```powershell
# 仅当前用户（无需管理员）
.\scripts\install.ps1

# 全用户（所有用户登录均触发，需管理员 PowerShell）
.\scripts\install.ps1 -Scope AllUsers
```

注册名为 `BluetoothAutoLock` 的计划任务：

- **触发器：** 登录时
- **动作：** 运行 `build\BluetoothAutoLock.exe`（托盘）
- **窗口：** 隐藏（仅托盘）
- **失败重启：** 每 1 分钟重试，最多 999 次
- **电源：** 允许在电池模式下启动与运行

脚本幂等 — 重复执行会停止并替换旧任务。

## 卸载

```powershell
.\scripts\uninstall.ps1
```

## 验证

```powershell
Get-ScheduledTask -TaskName 'BluetoothAutoLock'      # 是否已注册？
Start-ScheduledTask -TaskName 'BluetoothAutoLock'    # 立即启动（无需注销）
Get-Content -Wait 'C:\ProgramData\BluetoothAutoLock\service.log'   # 实时日志
```

正常日志示例：

```
[2026-05-07 19:02:10.221] [INFO ] Starting BluetoothAutoLock 1.1.6 | Config(...)
[2026-05-09 19:02:10.317] [INFO ] Monitor started. Target: address=AA:BB:CC:DD:EE:FF | Rule=idle 30s -> scan Bluetooth; lock only after target absent for configured window and no keyboard/mouse input | IdleBeforeBluetoothCheck=30s | BluetoothAbsenceBeforeLock=150s | FinalRecheckAttempts=3 | Polling=random 8-15s
[2026-05-09 19:02:10.401] [INFO ] Target seen connected: ExamplePhone [AA:BB:CC:DD:EE:FF]
[2026-05-09 19:14:55.802] [INFO ] Keyboard/mouse has been idle for at least 30s and target is not nearby; starting 150s Bluetooth absence window before lock.
[2026-05-09 19:17:25.811] [INFO ] Target absent for at least 150s after keyboard/mouse idle threshold and no input occurred; locking workstation.
```

## 命令行参数（高级/脚本）

托盘是默认模式，下列参数用于诊断：

| 参数 | 行为 |
|------|------|
| _(无)_ | 运行托盘 |
| `--config` | 仅打开设置窗口（无托盘、无监控） |
| `--list` / `-l` | 打印所有已配对设备后退出 |
| `--once` | 单次检测后退出（诊断） |
| `--test-lock-shortcuts` | 仅触发已配置锁屏快捷键后退出，不开设置窗口也不锁屏 |
| `--version`/`-v` | 打印版本后退出 |
| `--help` / `-h` | 显示帮助 |

> exe 为 Windows 子系统程序，`cmd` 中执行 `--list`/`--once` 会立即返回提示符，输出在下方打印。需干净捕获请重定向：`BluetoothAutoLock.exe --list > out.txt`

## 资源占用

- 可执行文件 ~90 KB
- 托盘：2 线程（UI + 监控）；工作集 ~25–30 MB（WinForms 基线）
- 无网络监听
- 无第三方 DLL — 仅 Win32（`Irprops.cpl`、`kernel32.dll`、`user32.dll`）与 .NET BCL

## 可靠性特性

- 轮询内所有异常均捕获并记日志，下一个 tick 继续，蓝牙 API 瞬时抖动不会导致进程退出
- 崩溃自启：任务计划每 1 分钟重试，最多 999 次
- 重锁抑制：蓝牙离开锁屏触发后，解锁 Windows 会话会清除抑制并开启全新“空闲+离开”窗口，不会立刻重锁
- 安全优先：只要检测到蓝牙钥匙，就不锁，即使 PC 空闲
- 活动用户保护：只要近期有键鼠输入，就不锁，即使蓝牙钥匙离开
- 最终复检：调用 `LockWorkStation` 前做最后蓝牙探测，任一次成功即取消锁屏
- 日志轮转：单备份 `<log>.1`，不会无限增长
- LoL 优化仅恢复其改动过的资源：虚拟显示状态与游戏前已运行且被关闭的远程软件；恢复的 AskLink/ToDesk 仅走本程序托管的最小化/托盘静默启动，不影响正常启动
- 暂停/恢复会停止并重启监控线程

## 排查

- **`--list` 无输出** 蓝牙未开启或无已配对设备，先在 设置 → 蓝牙 中配对手机
- **从不锁** 在托盘打开设置，若设备显示 `[已配对]` 而非 `[已连接]`，说明手机未建立活跃蓝牙 Profile，打开一次同步类 App 即可
- **使用中却锁了** 不应发生，查看日志是否出现 `not locking while the computer is in use`
- **编译报 `csc.exe` 找不到** 系统为精简版，需安装 .NET Framework 4.x

## 蓝牙后端

使用 `Irprops.cpl`（标准 Win32 `BluetoothAPIs`）的 `BluetoothFindFirstDevice` / `BluetoothFindNextDevice` 并读取 `BLUETOOTH_DEVICE_INFO.fConnected`，与 Windows 自身一致，支持所有 Windows 10/11 含 LTSC。
