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
│   ├── PresenceReporter.cs  # 在岗上报（可选）：定期 POST 在场/离开状态
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
REM 在本仓库根目录执行（路径按你自己的克隆位置替换）
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
| **手机离开后** | 两个独立开关：**锁定 Windows 屏幕**、**执行锁屏快捷键**（如锁定微信/QQ），可以只开一个，也可以都开 |
| **锁屏快捷键** | 手机离开后按顺序执行。每行可设前置快捷键（如 `Ctrl+Alt+W` 叫出微信）、快捷键（如 `Ctrl+L`）、目标程序（按住 ⊕ 拖到程序窗口上）和备注；选中一行可修改。点进快捷键框（变浅黄）录制时，会先拦下其他程序的同名全局快捷键 |
| **锁完隐藏窗口** | 勾上后，每行锁完会再按一次该行的前置快捷键（= 该程序显示/隐藏主窗口的全局热键，如微信 `Ctrl+Alt+W`、QQ `Ctrl+Alt+Shift+Q`），把刚锁上的窗口收起来。前置键是切换键，按下去是藏还是叫出来取决于窗口状态，所以只在**有把握**时才按：该行必须填了目标程序，而且按完锁定键后这个程序确实还在最前面；拿不准就跳过并记一行日志（宁可窗口留在桌面上，也不把它重新叫出来） |
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
| `LockScreenEnabled` | `true` | 手机离开后锁定 Windows 屏幕，和 `LockShortcutsEnabled` 互相独立 |
| `LockShortcutsEnabled` | `true` | 手机离开后执行锁屏快捷键；不锁屏时只执行一次，等键鼠有真实输入后才重新监控 |
| `LockShortcutPreDelayMilliseconds` | `1000` | 按完前置快捷键后，等多久再按快捷键 0–10000 |
| `LockShortcutSettleMilliseconds` | `3000` | 同时开启锁屏时，按完快捷键等多久再锁屏 0–10000 |
| `LockShortcutHideAfterwards` | `false` | 锁完每一行后，再按一次该行的前置快捷键（= 该程序显示/隐藏窗口的全局热键），把刚锁上的窗口隐藏起来。只在“填了目标程序且它按完锁定键后仍在最前面”时才按；按完会确认窗口真的藏住了，**热键没效果就自己 `SW_HIDE` 兜底**（168 实测 QQ 的热键既叫不出也收不起来）。日志：`Lock shortcut window hidden: … | hide=pre-hotkey@300ms` 或 `hide=SW_HIDE(ok)…(pre-hotkey had no effect)`；拿不准则 `Lock shortcut hide skipped:` |
| `LockShortcutPreWaitTimeoutMilliseconds` | `2500` | 按完前置键后，在“前置键后等待”之外最多再等这么久，**等程序自己把窗口显示出来**（只看显示没显示，不管有没有拿到焦点）；超时才用 SW_SHOW 强显（强显后不少程序不认自己的锁定热键）。日志写 `ready=visible@Xms` 或 `ready=timeout(...)` |
| `LockShortcutHideDelayMilliseconds` | `800` | 按完锁定键后至少等多久才按隐藏键（不隐藏时仍按 300ms 走下一行）。QQ 的锁定比微信慢，值太小会“窗口收了但没锁上” |
| `LockShortcut1` 等 | _(空)_ | 格式 `前置>快捷键\|备注\|target=程序名`（前置和目标可省略），例如 `Ctrl+Alt+W>Ctrl+L\|锁定微信\|target=Weixin`。目标已在最前面就只按快捷键；否则先按前置快捷键，再把目标窗口还原、切到最前面后按快捷键，切不过去也照样全局按；目标没运行就跳过 |
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
| `PresenceReportEnabled` | `false` | 启用“在岗上报”：定期把在场/离开状态 POST 到远端接口 |
| `PresenceReportUrl` | `https://sl.z123j.top:26132/btlock/report` | 上报地址，必须 http/https 绝对地址 |
| `PresenceReportToken` | _(空)_ | 鉴权令牌，作为 `X-Btlock-Token` 请求头发送；**留空则完全不发送** |
| `PresenceReportIntervalSeconds` | `20` | 心跳间隔 5–3600 秒 |
| `PresenceReportTimeoutSeconds` | `5` | 单次请求超时 1–60 秒，超时只记日志 |

图形界面的 **保存** 会保留文件既有注释，手写与 GUI 编辑可共存。

## 在岗上报（Presence reporting，可选）

默认关闭。开启后，程序把本机的“在场 / 离开”状态定期 POST 到远端 HTTP 接口，供外部服务（例如 Linux 侧的通知服务）判断用户是否坐在电脑前。它**只上报状态**，不参与蓝牙匹配、锁屏、快捷键与游戏优化判定。

```ini
PresenceReportEnabled=false
PresenceReportUrl=https://sl.z123j.top:26132/btlock/report
PresenceReportToken=
PresenceReportIntervalSeconds=20
PresenceReportTimeoutSeconds=5
```

- **接口**：`POST <PresenceReportUrl>`，`Content-Type: application/json; charset=utf-8`
- **鉴权**：请求头 `X-Btlock-Token: <PresenceReportToken>`。**Token 留空时一个字节都不发**，只在日志里记一行 `Presence reporting is idle: the token is empty ...`
- **频率**：每 `PresenceReportIntervalSeconds` 秒一次 `heartbeat`；状态或 `inRange` 一变就立刻补一份 `change`；启动首份是 `start`，退出前尽力发一份 `stop`
- **超时**：单次请求 `PresenceReportTimeoutSeconds` 秒。超时或失败只写日志（首次 `[WARN ]`，连续失败降为 `[DEBUG]`），不阻塞监控线程，也不会让程序退出
- **线程**：上报跑在独立后台线程（`IsBackground = true`）；停止时最多等 2 秒把 `stop` 发出去，绝不拖住退出或监控重启
- **状态取值**：`present`（手机在附近）/ `away`（已判定离开，或离开动作延后中）/ `unknown`（尚未得出判定，例如空闲未满 30 秒）

请求体字段：

| 字段 | 说明 |
|------|------|
| `ts` | UTC 时间，形如 `2026-10-14T00:30:00Z` |
| `host` | `Environment.MachineName` |
| `version` | 程序版本，与 `--version` 一致 |
| `idleSeconds` | 键鼠空闲秒数；读不到时为 `null`（不是 0） |
| `absentSeconds` | 手机连续离开的秒数；在场时为 `0` |
| `inRange` | 手机是否在附近（沿用现有 SDP 判定） |
| `state` | `present` / `away` / `unknown` |
| `paused` | 离开处理是否已暂停/延后（例如 Windows 已锁屏，快捷键延后执行） |
| `event` | `start` / `heartbeat` / `change` / `stop` |

在场心跳示例：

```json
{"ts":"2026-10-14T00:30:00Z","host":"YU-PC","version":"1.1.42","idleSeconds":42,"absentSeconds":0,"inRange":true,"state":"present","paused":false,"event":"heartbeat"}
```

手机离开示例：

```json
{"ts":"2026-10-14T00:32:00Z","host":"YU-PC","version":"1.1.42","idleSeconds":95,"absentSeconds":35,"inRange":false,"state":"away","paused":false,"event":"change"}
```

> `PresenceReportToken` 属于凭据：填了 Token 的 `config.ini` 不要提交到 Git。日志与 `Config.Describe()` 只打印 `token=set` / `token=empty`，从不打印 Token 本身。

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
- 在岗上报开启时额外 1 个后台线程（默认关闭；上报失败只写日志，不影响锁屏逻辑）
- 无第三方 DLL — 仅 Win32（`Irprops.cpl`、`kernel32.dll`、`user32.dll`）与 .NET BCL

## 可靠性特性

- 轮询内所有异常均捕获并记日志，下一个 tick 继续，蓝牙 API 瞬时抖动不会导致进程退出
- 崩溃自启：任务计划每 1 分钟重试，最多 999 次
- 重锁抑制：蓝牙离开锁屏触发后，解锁 Windows 会话会清除抑制并开启全新“空闲+离开”窗口，不会立刻重锁
- 安全优先：只要检测到蓝牙钥匙，就不锁，即使 PC 空闲
- 在场判定：SDP 查询必须返回至少一项服务记录才算手机在旁边；Windows 对联系不上的地址也会返回 Success，只是服务列表为空
- 活动用户保护：只要近期有键鼠输入，就不锁，即使蓝牙钥匙离开
- 最终复检：调用 `LockWorkStation` 前做最后蓝牙探测，任一次成功即取消锁屏
- 锁屏时补锁：Windows 已经锁屏时（例如远程软件断开时锁的），任何程序都按不出快捷键；程序先记下来，Windows 一解锁就复核蓝牙，手机仍不在旁边就马上锁定微信/QQ（解锁的人正在操作，这一步不看键鼠空闲）
- 日志轮转：单备份 `<log>.1`，不会无限增长
- LoL 优化仅恢复其改动过的资源：虚拟显示状态与游戏前已运行且被关闭的远程软件；恢复的 AskLink/ToDesk 仅走本程序托管的最小化/托盘静默启动，不影响正常启动
- 暂停/恢复会停止并重启监控线程

## 排查

- **`--list` 无输出** 蓝牙未开启或无已配对设备，先在 设置 → 蓝牙 中配对手机
- **从不锁** 看日志：出现 `pairedSnapshot=no` 说明手机不在 Windows 配对列表里，需要重新配对；1.1.34 之前的版本会把联系不上的手机误判为在旁边，请升级
- **使用中却锁了** 不应发生，查看日志是否出现 `not locking while the computer is in use`
- **编译报 `csc.exe` 找不到** 系统为精简版，需安装 .NET Framework 4.x

## 蓝牙后端

使用 `Irprops.cpl`（标准 Win32 `BluetoothAPIs`）的 `BluetoothFindFirstDevice` / `BluetoothFindNextDevice` 并读取 `BLUETOOTH_DEVICE_INFO.fConnected`，与 Windows 自身一致，支持所有 Windows 10/11 含 LTSC。
