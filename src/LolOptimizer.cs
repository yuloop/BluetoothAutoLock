using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace BluetoothAutoLock
{
    /// <summary>
    /// LoL 启动 / 退出时自动处理 WSL、AskLink 虚拟显示器和远程软件。
    /// 低占用策略：低频轮询检测启动，发现游戏进程后用 WaitForExit 等退出。
    /// </summary>
    internal sealed class LolOptimizer
    {
        private readonly Config _cfg;
        private readonly Logger _log;
        private readonly Func<bool> _shouldStop;

        private string _configuredLolProcessName;
        private string _virtualDisplayDeviceId;
        private bool _vdInitiallyEnabled;
        private bool _vdDisabledByMe;
        private bool _isElevated;
        private bool _elevationWarned;
        private WslGameOptimizer _wslOptimizer;
        private RemoteAppGameManager _remoteApps;

        public volatile string CurrentStatusText = "未启用";

        private sealed class ManagedState
        {
            public bool DisabledByThisApp;
            public string DeviceId;
        }

        public LolOptimizer(Config cfg, Logger log, Func<bool> shouldStop)
        {
            _cfg = cfg;
            _log = log;
            _shouldStop = shouldStop ?? (() => false);
        }

        public void RunLoop()
        {
            int pollMs = Math.Max(1, _cfg.LolOptimizerPollSeconds) * 1000;
            _isElevated = IsRunningAsAdministrator();
            _virtualDisplayDeviceId = (_cfg.VirtualDisplayDeviceId ?? "").Trim();
            _configuredLolProcessName = GetConfiguredLolProcessName();
            _wslOptimizer = new WslGameOptimizer(_cfg, _log);
            _remoteApps = new RemoteAppGameManager(_cfg, _log, IsLolRunning, _shouldStop);

            _log.Info("LolOptimizer started. Process='" + _configuredLolProcessName +
                      "', VirtualDisplayId=" + (string.IsNullOrEmpty(_virtualDisplayDeviceId) ? "<unset>" : _virtualDisplayDeviceId) +
                      ", Elevated=" + _isElevated + ", WslOpt=" + _cfg.LolWslOptimizeEnabled +
                      ", RemoteClose=" + _cfg.LolAutoCloseRemoteEnabled + ", Mode=LowOverheadPoll");

            InitializeVirtualDisplayState();
            CurrentStatusText = "等待 LoL 启动";

            Process watchedProcess = null;
            try
            {
                while (!_shouldStop())
                {
                    if (watchedProcess == null)
                    {
                        watchedProcess = FindLolProcess();
                        if (watchedProcess == null)
                        {
                            if (_remoteApps != null) _remoteApps.TickIdle();
                            UpdateStatusText(false);
                            SleepInterruptibly(pollMs);
                            continue;
                        }

                        _log.Info("LolOptimizer: detected LoL start (poll).");
                        ApplyGameModeOptimizations();
                        UpdateStatusText(true);
                    }

                    if (WaitForLolExit(watchedProcess))
                    {
                        DisposeProcess(ref watchedProcess);
                        _log.Info("LolOptimizer: detected LoL exit (wait).");
                        RestoreAfterLolExit();
                        UpdateStatusText(false);
                    }
                }
            }
            finally
            {
                DisposeProcess(ref watchedProcess);
                RestoreIfNeededOnStop();
                CurrentStatusText = "未启用";
                _log.Info("LolOptimizer stopped.");
            }
        }

        private void ApplyGameModeOptimizations()
        {
            try
            {
                if (_wslOptimizer != null) _wslOptimizer.EnterGameMode();
            }
            catch (Exception ex)
            {
                _log.Warn("LolOptimizer: WSL game-mode optimization failed: " + ex.Message);
            }

            try
            {
                if (_remoteApps != null) _remoteApps.CloseForGame();
            }
            catch (Exception ex)
            {
                _log.Warn("LolOptimizer: remote app close failed: " + ex.Message);
            }

            DisableDeviceIfManaged();
        }

        private void RestoreAfterLolExit()
        {
            try
            {
                if (_wslOptimizer != null) _wslOptimizer.LeaveGameMode();
            }
            catch (Exception ex)
            {
                _log.Warn("LolOptimizer: WSL game-mode state reset failed: " + ex.Message);
            }

            EnableDeviceIfManaged();

            try
            {
                if (_remoteApps != null) _remoteApps.MarkGameExited();
            }
            catch (Exception ex)
            {
                _log.Warn("LolOptimizer: remote app delayed restore scheduling failed: " + ex.Message);
            }
        }

        private void InitializeVirtualDisplayState()
        {
            if (string.IsNullOrEmpty(_virtualDisplayDeviceId))
            {
                _vdInitiallyEnabled = false;
                _log.Info("LolOptimizer: VirtualDisplayDeviceId not configured; nothing to manage.");
                return;
            }

            if (!_isElevated)
            {
                _vdInitiallyEnabled = false;
                _log.Warn("LolOptimizer: not running as administrator — cannot manage virtual display. " +
                          "Re-install the scheduled task with -Elevated to enable display management.");
                return;
            }

            ManagedState state = LoadManagedState();
            bool stateMatches = state.DisabledByThisApp && SameDeviceId(state.DeviceId, _virtualDisplayDeviceId);
            bool? enabled = QueryDeviceEnabled(_virtualDisplayDeviceId);

            if (stateMatches)
            {
                _vdInitiallyEnabled = true;
                if (enabled.HasValue && !enabled.Value)
                {
                    _vdDisabledByMe = true;
                    if (IsLolRunning())
                    {
                        _log.Info("LolOptimizer: recovered managed disabled state; LoL is still running.");
                    }
                    else
                    {
                        _log.Info("LolOptimizer: recovered managed disabled state; LoL is not running, restoring virtual display.");
                        EnableDeviceIfManaged();
                    }
                    return;
                }

                _log.Info("LolOptimizer: managed state file is stale; virtual display is not disabled by this app now.");
                SaveManagedState(false, _virtualDisplayDeviceId);
            }

            if (enabled.HasValue)
            {
                _vdInitiallyEnabled = enabled.Value;
                _log.Info("LolOptimizer: virtual display initial state: " + (enabled.Value ? "Enabled" : "Disabled"));
                if (!enabled.Value)
                {
                    if (IsLolRunning())
                    {
                        _vdInitiallyEnabled = true;
                        _vdDisabledByMe = true;
                        SaveManagedState(true, _virtualDisplayDeviceId);
                        _log.Info("LolOptimizer: virtual display is already disabled while LoL is running; treating it as managed carry-over.");
                    }
                    else
                    {
                        _log.Info("LolOptimizer: virtual display already disabled by user; will not auto-manage.");
                    }
                }
            }
            else
            {
                _vdInitiallyEnabled = true;
                _log.Warn("LolOptimizer: cannot read virtual display state; assume Enabled.");
            }
        }

        private void UpdateStatusText(bool lolRunning)
        {
            if (lolRunning)
            {
                List<string> parts = new List<string>();
                if (_wslOptimizer != null && _wslOptimizer.IsGameModeActive) parts.Add("WSL 缓存已回收");
                if (_vdDisabledByMe) parts.Add("虚拟显示器已禁用");
                if (_remoteApps != null && _remoteApps.HasClosedApps) parts.Add("远程软件已关闭");

                if (parts.Count > 0) CurrentStatusText = "LoL 运行中 — " + string.Join(" / ", parts.ToArray());
                else if (string.IsNullOrEmpty(_virtualDisplayDeviceId)) CurrentStatusText = "LoL 运行中（未配置虚拟显示器）";
                else if (!_isElevated) CurrentStatusText = "LoL 运行中（需管理员权限）";
                else if (!_vdInitiallyEnabled) CurrentStatusText = "LoL 运行中（不接管虚拟显示器）";
                else CurrentStatusText = "LoL 运行中";
            }
            else
            {
                string remoteSuffix = _remoteApps != null ? _remoteApps.IdleStatusSuffix() : "";
                CurrentStatusText = "等待 LoL 启动" + remoteSuffix;
            }
        }

        private void SleepInterruptibly(int milliseconds)
        {
            int elapsed = 0;
            while (elapsed < milliseconds && !_shouldStop())
            {
                int slice = Math.Min(250, milliseconds - elapsed);
                Thread.Sleep(slice);
                elapsed += slice;
            }
        }

        private bool WaitForLolExit(Process process)
        {
            if (process == null) return true;
            while (!_shouldStop())
            {
                bool selectedExited;
                try
                {
                    selectedExited = process.HasExited || process.WaitForExit(1000);
                }
                catch
                {
                    selectedExited = true;
                }

                if (!selectedExited) continue;

                // LoL may replace the watched process during patching / reconnects.
                // Treat the game as ended only when no configured LoL process remains.
                if (!IsLolRunning()) return true;
                Thread.Sleep(1000);
            }
            return false;
        }

        private bool IsLolRunning()
        {
            Process p = FindLolProcess();
            if (p == null) return false;
            try { return !p.HasExited; }
            catch { return false; }
            finally { try { p.Dispose(); } catch { } }
        }

        private Process FindLolProcess()
        {
            Process[] procs = SafeGetProcessesByName(_configuredLolProcessName);
            if (procs == null || procs.Length == 0) return null;

            Process selected = null;
            foreach (Process p in procs)
            {
                if (p == null) continue;
                try
                {
                    if (p.HasExited)
                    {
                        p.Dispose();
                        continue;
                    }
                    if (selected == null) selected = p;
                    else p.Dispose();
                }
                catch
                {
                    try { p.Dispose(); } catch { }
                }
            }
            return selected;
        }

        private static Process[] SafeGetProcessesByName(string name)
        {
            try { return Process.GetProcessesByName(NormalizeProcessName(name)); }
            catch { return null; }
        }

        private static void DisposeProcess(ref Process process)
        {
            if (process == null) return;
            try { process.Dispose(); } catch { }
            process = null;
        }

        private void DisableDeviceIfManaged()
        {
            if (!_isElevated)
            {
                if (!_elevationWarned)
                {
                    _log.Warn("LolOptimizer: skip disable virtual display (need administrator).");
                    _elevationWarned = true;
                }
                return;
            }
            if (!_vdInitiallyEnabled) return;
            if (_vdDisabledByMe) return;
            if (string.IsNullOrEmpty(_virtualDisplayDeviceId)) return;

            if (DisableDevice(_virtualDisplayDeviceId))
            {
                _vdDisabledByMe = true;
                SaveManagedState(true, _virtualDisplayDeviceId);
                _log.Info("LolOptimizer: virtual display disabled.");
            }
        }

        private void EnableDeviceIfManaged()
        {
            if (!_vdDisabledByMe) return;
            if (string.IsNullOrEmpty(_virtualDisplayDeviceId)) return;

            if (EnableDevice(_virtualDisplayDeviceId))
            {
                _vdDisabledByMe = false;
                SaveManagedState(false, _virtualDisplayDeviceId);
                _log.Info("LolOptimizer: virtual display restored.");
            }
        }

        private void RestoreIfNeededOnStop()
        {
            try
            {
                if (_vdDisabledByMe && !string.IsNullOrEmpty(_virtualDisplayDeviceId))
                {
                    _log.Info("LolOptimizer: stopping; restoring virtual display.");
                    EnableDeviceIfManaged();
                }
                if (_wslOptimizer != null) _wslOptimizer.LeaveGameMode();
                if (_remoteApps != null) _remoteApps.RestoreNow("optimizer stopping");
            }
            catch (Exception ex)
            {
                _log.Warn("LolOptimizer: restore on stop failed: " + ex.Message);
            }
        }

        private bool DisableDevice(string deviceId)
        {
            uint devInst;
            if (TryLocateDevice(deviceId, out devInst))
            {
                int cr = NativeMethods.CM_Disable_DevNode(devInst, 0);
                if (cr == NativeMethods.CR_SUCCESS) return true;
                _log.Warn("LolOptimizer: CM_Disable_DevNode failed: CR=" + cr + "; trying pnputil fallback.");
            }
            return RunPnputil("/disable-device " + QuoteArgument(deviceId));
        }

        private bool EnableDevice(string deviceId)
        {
            uint devInst;
            if (TryLocateDevice(deviceId, out devInst))
            {
                int cr = NativeMethods.CM_Enable_DevNode(devInst, 0);
                if (cr == NativeMethods.CR_SUCCESS) return true;
                _log.Warn("LolOptimizer: CM_Enable_DevNode failed: CR=" + cr + "; trying pnputil fallback.");
            }
            return RunPnputil("/enable-device " + QuoteArgument(deviceId));
        }

        private bool? QueryDeviceEnabled(string deviceId)
        {
            uint devInst;
            if (!TryLocateDevice(deviceId, out devInst)) return null;

            uint status;
            uint problem;
            int cr = NativeMethods.CM_Get_DevNode_Status(out status, out problem, devInst, 0);
            if (cr == NativeMethods.CR_SUCCESS)
            {
                bool disabled = (status & NativeMethods.DN_HAS_PROBLEM) != 0 && problem == NativeMethods.CM_PROB_DISABLED;
                return !disabled;
            }

            _log.Debug("LolOptimizer: CM_Get_DevNode_Status failed: CR=" + cr);
            return null;
        }

        private bool TryLocateDevice(string deviceId, out uint devInst)
        {
            devInst = 0;
            try
            {
                int cr = NativeMethods.CM_Locate_DevNode(out devInst, deviceId, NativeMethods.CM_LOCATE_DEVNODE_NORMAL);
                if (cr == NativeMethods.CR_SUCCESS) return true;
                _log.Debug("LolOptimizer: CM_Locate_DevNode failed for '" + deviceId + "': CR=" + cr);
                return false;
            }
            catch (Exception ex)
            {
                _log.Debug("LolOptimizer: CM_Locate_DevNode threw: " + ex.Message);
                return false;
            }
        }

        private bool RunPnputil(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("pnputil.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    string stderr = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(8000))
                    {
                        try { p.Kill(); } catch { }
                        _log.Warn("LolOptimizer: pnputil " + args + " timeout");
                        return false;
                    }
                    if (p.ExitCode == 0) return true;
                    _log.Warn("LolOptimizer: pnputil " + args + " => exit " + p.ExitCode +
                              (string.IsNullOrEmpty(stderr) ? "" : " | " + stderr.Trim()));
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("LolOptimizer: pnputil " + args + " threw: " + ex.Message);
                return false;
            }
        }

        private ManagedState LoadManagedState()
        {
            var state = new ManagedState();
            string path = GetManagedStatePath();
            try
            {
                if (!File.Exists(path)) return state;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (string.Equals(key, "DisabledByThisApp", StringComparison.OrdinalIgnoreCase))
                        state.DisabledByThisApp = ParseBool(value);
                    else if (string.Equals(key, "DeviceId", StringComparison.OrdinalIgnoreCase))
                        state.DeviceId = value;
                }
            }
            catch (Exception ex)
            {
                _log.Debug("LolOptimizer: load managed state failed: " + ex.Message);
            }
            return state;
        }

        private void SaveManagedState(bool disabledByThisApp, string deviceId)
        {
            string path = GetManagedStatePath();
            try
            {
                if (!disabledByThisApp)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string[] lines = new string[]
                {
                    "DisabledByThisApp=true",
                    "DeviceId=" + (deviceId ?? ""),
                    "ChangedUtc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };
                File.WriteAllLines(path, lines, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _log.Warn("LolOptimizer: save managed state failed: " + ex.Message);
            }
        }

        private static string GetManagedStatePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "BluetoothAutoLock", "lol-state.ini");
        }

        private string GetConfiguredLolProcessName()
        {
            string name = NormalizeProcessName(_cfg.LolProcessName);
            if (name.Length == 0) name = "League of Legends";
            return name;
        }

        private static string NormalizeProcessName(string text)
        {
            string t = (text ?? "").Trim();
            if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(0, t.Length - 4);
            return t;
        }

        private static bool SameDeviceId(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool ParseBool(string value)
        {
            string t = (value ?? "").Trim().ToLowerInvariant();
            return t == "true" || t == "1" || t == "yes" || t == "on" || t == "enabled" || t == "enable";
        }

        private sealed class WslGameOptimizer
        {
            private const int MaxConsecutiveFailures = 3;

            private readonly Config _cfg;
            private readonly Logger _log;
            private bool _cacheReclaimAttemptedForCurrentGame;
            private bool _cacheReclaimedForCurrentGame;
            private int _consecutiveFailures;
            private bool _autoDisabled;
            private bool _distroAvailabilityChecked;
            private bool _distroAvailable;

            public WslGameOptimizer(Config cfg, Logger log)
            {
                _cfg = cfg;
                _log = log;
            }

            public bool IsGameModeActive
            {
                get { return _cacheReclaimedForCurrentGame; }
            }

            public void EnterGameMode()
            {
                if (!_cfg.LolWslOptimizeEnabled) return;
                if (_autoDisabled) return;
                if (_cacheReclaimAttemptedForCurrentGame)
                {
                    _log.Debug("LolOptimizer: WSL cache reclaim already attempted for this game; skip duplicate optimization.");
                    return;
                }

                _cacheReclaimAttemptedForCurrentGame = true;

                if (!EnsureDistroAvailable())
                {
                    _cacheReclaimedForCurrentGame = false;
                    return;
                }

                bool ok = ReclaimMemoryCache();
                _cacheReclaimedForCurrentGame = ok;

                if (ok)
                {
                    _consecutiveFailures = 0;
                }
                else
                {
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        _autoDisabled = true;
                        _log.Warn("LolOptimizer: WSL cache reclaim 连续 " + _consecutiveFailures +
                                  " 次失败，本进程内自动停用 WSL 优化；如已修复 WSL，可重启程序重试。");
                    }
                }
            }

            public void LeaveGameMode()
            {
                _cacheReclaimAttemptedForCurrentGame = false;
                _cacheReclaimedForCurrentGame = false;
            }

            private bool EnsureDistroAvailable()
            {
                if (_distroAvailabilityChecked) return _distroAvailable;
                _distroAvailabilityChecked = true;

                string distro = ResolveDistroName();
                ProcessRunResult result = RunHidden("wsl.exe", "-l -q", 6000);
                if (!result.Success)
                {
                    _log.Warn("LolOptimizer: 跳过 WSL 优化 — 无法执行 'wsl.exe -l -q' (" + result.Describe() +
                              ")；本进程不再尝试 WSL，重启可重新检测。");
                    _autoDisabled = true;
                    _distroAvailable = false;
                    return false;
                }

                string stdout = result.StdOut ?? "";
                // wsl.exe -l -q 在 Windows 上输出为 UTF-16，被当 UTF-8 读会出现 \0 字节；
                // 这里同时按 \0/\r/\n 分割，宽松匹配名称。
                string[] tokens = stdout.Split(new char[] { '\0', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string raw in tokens)
                {
                    string token = (raw ?? "").Trim();
                    if (token.Length == 0) continue;
                    if (string.Equals(token, distro, StringComparison.OrdinalIgnoreCase))
                    {
                        _distroAvailable = true;
                        return true;
                    }
                }

                _log.Warn("LolOptimizer: 跳过 WSL 优化 — 未在 'wsl.exe -l -q' 中找到配置的发行版 '" + distro +
                          "'；本进程不再尝试 WSL，可在设置里改 WslDistro 后重启程序。");
                _autoDisabled = true;
                _distroAvailable = false;
                return false;
            }

            private string ResolveDistroName()
            {
                return string.IsNullOrWhiteSpace(_cfg.WslDistro) ? "Ubuntu" : _cfg.WslDistro.Trim();
            }

            private bool ReclaimMemoryCache()
            {
                string distro = ResolveDistroName();
                string args = "-d " + QuoteArgument(distro) + " -u root -- sh -lc " +
                              QuoteArgument("sync; echo 3 > /proc/sys/vm/drop_caches");

                ProcessRunResult result = RunHidden("wsl.exe", args, 20000);
                if (result.Success)
                {
                    _log.Info("LolOptimizer: WSL cache reclaimed for distro '" + distro + "'.");
                    return true;
                }
                else
                {
                    _log.Warn("LolOptimizer: WSL cache reclaim failed for distro '" + distro + "': " + result.Describe() +
                              " (consecutive=" + (_consecutiveFailures + 1) + "/" + MaxConsecutiveFailures + ")");
                    return false;
                }
            }
        }

        private sealed class RemoteAppGameManager
        {
            private readonly Config _cfg;
            private readonly Logger _log;
            private readonly Func<bool> _isGameRunning;
            private readonly Func<bool> _shouldStop;
            private readonly List<RemoteAppRecord> _pendingRestart = new List<RemoteAppRecord>();
            private DateTime _restartDueUtc = DateTime.MinValue;

            private sealed class RemoteAppRecord
            {
                public string ProcessName;
                public string ExecutablePath;
            }

            public RemoteAppGameManager(Config cfg, Logger log, Func<bool> isGameRunning, Func<bool> shouldStop)
            {
                _cfg = cfg;
                _log = log;
                _isGameRunning = isGameRunning ?? (() => false);
                _shouldStop = shouldStop ?? (() => false);
            }

            public bool HasClosedApps
            {
                get { return _pendingRestart.Count > 0; }
            }

            public void CloseForGame()
            {
                if (!_cfg.LolAutoCloseRemoteEnabled) return;

                if (_pendingRestart.Count > 0)
                {
                    _restartDueUtc = DateTime.MinValue;
                    _log.Info("LolOptimizer: remote apps are already closed by this app; restart postponed because LoL is running again.");
                    return;
                }

                string[] names = SplitProcessNames(_cfg.RemoteCloseProcessNames);
                foreach (string name in names)
                    CloseOneProcessGroup(name);

                if (_pendingRestart.Count == 0)
                    _log.Info("LolOptimizer: no running configured remote apps were closed before game mode.");
            }

            public void MarkGameExited()
            {
                if (_pendingRestart.Count == 0) return;
                int minutes = _cfg.RemoteRestartQuietMinutes;
                if (minutes < 1) minutes = 10;
                _restartDueUtc = DateTime.UtcNow.AddMinutes(minutes);
                _log.Info("LolOptimizer: LoL exited; remote apps will be restored after " + minutes +
                          " quiet minute(s) with no LoL process.");
            }

            public void TickIdle()
            {
                if (_pendingRestart.Count == 0 || _restartDueUtc == DateTime.MinValue) return;
                if (_shouldStop()) return;

                if (_isGameRunning())
                {
                    _restartDueUtc = DateTime.MinValue;
                    _log.Info("LolOptimizer: LoL restarted during remote-app quiet period; restore postponed.");
                    return;
                }

                if (DateTime.UtcNow >= _restartDueUtc)
                    RestoreNow("remote-app quiet period elapsed");
            }

            public string IdleStatusSuffix()
            {
                if (_pendingRestart.Count == 0 || _restartDueUtc == DateTime.MinValue) return "";
                TimeSpan left = _restartDueUtc - DateTime.UtcNow;
                if (left.TotalSeconds < 1) left = TimeSpan.Zero;
                int minutes = Math.Max(0, (int)Math.Ceiling(left.TotalMinutes));
                return "（远程软件 " + minutes + " 分钟后恢复）";
            }

            public void RestoreNow(string reason)
            {
                if (_pendingRestart.Count == 0) return;

                List<RemoteAppRecord> records = new List<RemoteAppRecord>(_pendingRestart);
                _pendingRestart.Clear();
                _restartDueUtc = DateTime.MinValue;

                foreach (RemoteAppRecord record in records)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(record.ExecutablePath) || !File.Exists(record.ExecutablePath))
                        {
                            _log.Warn("LolOptimizer: skip restarting " + record.ProcessName + "; executable path is unavailable.");
                            continue;
                        }

                        if (IsProcessGroupRunning(record.ProcessName))
                        {
                            _log.Info("LolOptimizer: skip restarting remote app " + record.ProcessName +
                                      " because it is already running; normal/manual startup state is left untouched.");
                            continue;
                        }

                        Process started = null;
                        try
                        {
                            started = StartRemoteAppMinimizedForRestore(record);
                            QuietRestoreStartupWindows(record, started);
                        }
                        finally
                        {
                            if (started != null)
                            {
                                try { started.Dispose(); } catch { }
                            }
                        }

                        _log.Info("LolOptimizer: restarted remote app " + record.ProcessName +
                                  " with restore-only quiet tray startup (" + reason + ").");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("LolOptimizer: failed to restart remote app " + record.ProcessName + ": " + ex.Message);
                    }
                }
            }

            private Process StartRemoteAppMinimizedForRestore(RemoteAppRecord record)
            {
                ProcessStartInfo psi = new ProcessStartInfo(record.ExecutablePath);
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Minimized;
                string dir = Path.GetDirectoryName(record.ExecutablePath);
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
                return Process.Start(psi);
            }

            private void QuietRestoreStartupWindows(RemoteAppRecord record, Process started)
            {
                string normalizedName = NormalizeProcessName(record.ProcessName);
                if (normalizedName.Length == 0) return;

                bool hideToTray = IsKnownTrayRemoteApp(normalizedName);
                int startedPid = 0;
                try
                {
                    if (started != null) startedPid = started.Id;
                }
                catch { }

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(hideToTray ? 12000 : 3500);
                int affectedWindows = 0;

                while (DateTime.UtcNow < deadline && !_shouldStop())
                {
                    HashSet<int> targetPids = GetRestoreTargetPids(normalizedName, startedPid);
                    if (targetPids.Count > 0)
                    {
                        int affectedNow = ApplyQuietWindowState(targetPids, hideToTray);
                        affectedWindows += affectedNow;
                        if (affectedNow > 0 && !hideToTray) break;
                    }

                    Thread.Sleep(200);
                }

                if (affectedWindows > 0)
                {
                    _log.Debug("LolOptimizer: quieted " + affectedWindows.ToString(CultureInfo.InvariantCulture) +
                               " startup window(s) for restored " + normalizedName +
                               (hideToTray ? " by hiding to tray." : " by starting minimized."));
                }
            }

            private static bool IsKnownTrayRemoteApp(string normalizedName)
            {
                string name = (normalizedName ?? "").Trim();
                return string.Equals(name, "AskLink", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "AskLinkLauncher", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "ToDesk", StringComparison.OrdinalIgnoreCase);
            }

            private static HashSet<int> GetRestoreTargetPids(string normalizedName, int startedPid)
            {
                HashSet<int> ids = new HashSet<int>();
                if (startedPid > 0 && IsPidAlive(startedPid)) ids.Add(startedPid);

                Process[] procs = SafeGetProcessesByName(normalizedName);
                if (procs == null || procs.Length == 0) return ids;

                foreach (Process p in procs)
                {
                    if (p == null) continue;
                    try
                    {
                        if (!p.HasExited) ids.Add(p.Id);
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
                return ids;
            }

            private static bool IsProcessGroupRunning(string processName)
            {
                string normalizedName = NormalizeProcessName(processName);
                if (normalizedName.Length == 0) return false;

                Process[] procs = SafeGetProcessesByName(normalizedName);
                if (procs == null || procs.Length == 0) return false;

                bool running = false;
                foreach (Process p in procs)
                {
                    if (p == null) continue;
                    try
                    {
                        if (!p.HasExited) running = true;
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }

                    if (running) break;
                }
                return running;
            }

            private static int ApplyQuietWindowState(HashSet<int> targetPids, bool hideToTray)
            {
                if (targetPids == null || targetPids.Count == 0) return 0;

                int affected = 0;
                NativeMethods.EnumWindowsProc callback = delegate(IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        if (hWnd == IntPtr.Zero) return true;
                        if (!NativeMethods.IsWindowVisible(hWnd)) return true;

                        uint pid;
                        NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                        if (pid == 0 || !targetPids.Contains((int)pid)) return true;

                        NativeMethods.ShowWindow(hWnd,
                            hideToTray ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOWMINNOACTIVE);
                        affected++;
                    }
                    catch { }
                    return true;
                };

                try { NativeMethods.EnumWindows(callback, IntPtr.Zero); } catch { }
                return affected;
            }

            private void CloseOneProcessGroup(string processName)
            {
                string normalizedName = NormalizeProcessName(processName);
                if (normalizedName.Length == 0) return;

                Process[] procs = SafeGetProcessesByName(normalizedName);
                if (procs == null || procs.Length == 0) return;

                List<int> pids = new List<int>();
                string restartPath = null;
                foreach (Process p in procs)
                {
                    if (p == null) continue;
                    try
                    {
                        if (p.HasExited) continue;
                        pids.Add(p.Id);
                        if (string.IsNullOrEmpty(restartPath))
                            restartPath = TryGetExecutablePath(p);
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                if (pids.Count == 0) return;

                foreach (int pid in pids)
                    ForceKillProcessTree(pid);

                bool exited = WaitForPidsToExit(pids, 5000);
                if (!exited)
                {
                    _log.Warn("LolOptimizer: " + normalizedName + " did not fully exit; it will not be auto-restarted.");
                    return;
                }

                if (string.IsNullOrEmpty(restartPath) || !File.Exists(restartPath))
                {
                    _log.Warn("LolOptimizer: " + normalizedName + " was closed, but executable path was not readable; skip auto-restart.");
                    return;
                }

                RemoteAppRecord record = new RemoteAppRecord();
                record.ProcessName = normalizedName;
                record.ExecutablePath = restartPath;
                _pendingRestart.Add(record);
                _log.Info("LolOptimizer: closed remote app " + normalizedName + " (" + pids.Count +
                          " process(es)); restart path captured.");
            }

            private static string[] SplitProcessNames(string text)
            {
                string source = string.IsNullOrWhiteSpace(text) ? "AskLink,ToDesk" : text;
                string[] raw = source.Split(new char[] { ',', ';', '|', '\r', '\n', '\t', ' ' },
                    StringSplitOptions.RemoveEmptyEntries);
                List<string> names = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string item in raw)
                {
                    string name = NormalizeProcessName(item);
                    if (name.Length == 0) continue;
                    if (seen.Add(name)) names.Add(name);
                }
                return names.ToArray();
            }

            private static string TryGetExecutablePath(Process p)
            {
                try
                {
                    if (p == null || p.MainModule == null) return null;
                    return p.MainModule.FileName;
                }
                catch
                {
                    return null;
                }
            }

            private static void ForceKillProcessTree(int pid)
            {
                ProcessRunResult result = RunHidden("taskkill.exe", "/PID " + pid.ToString(CultureInfo.InvariantCulture) + " /T /F", 8000);
                if (result.Success) return;

                try
                {
                    Process p = Process.GetProcessById(pid);
                    try
                    {
                        if (!p.HasExited) p.Kill();
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
                catch { }
            }

            private static bool WaitForPidsToExit(List<int> pids, int timeoutMs)
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    bool anyAlive = false;
                    foreach (int pid in pids)
                    {
                        if (IsPidAlive(pid))
                        {
                            anyAlive = true;
                            break;
                        }
                    }
                    if (!anyAlive) return true;
                    Thread.Sleep(150);
                }

                foreach (int pid in pids)
                    if (IsPidAlive(pid)) return false;
                return true;
            }

            private static bool IsPidAlive(int pid)
            {
                try
                {
                    Process p = Process.GetProcessById(pid);
                    try { return !p.HasExited; }
                    finally { try { p.Dispose(); } catch { } }
                }
                catch
                {
                    return false;
                }
            }
        }

        private sealed class ProcessRunResult
        {
            public bool Success;
            public int ExitCode;
            public bool TimedOut;
            public string Error;
            public string StdOut;

            public string Describe()
            {
                if (TimedOut) return "timeout";
                if (!string.IsNullOrEmpty(Error)) return Error;
                return "exit " + ExitCode.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static ProcessRunResult RunHidden(string fileName, string arguments, int timeoutMs)
        {
            ProcessRunResult result = new ProcessRunResult();
            result.ExitCode = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(fileName, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;

                using (Process p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        result.Error = "process did not start";
                        return result;
                    }

                    if (!p.WaitForExit(timeoutMs))
                    {
                        result.TimedOut = true;
                        try { p.Kill(); } catch { }
                        return result;
                    }

                    result.ExitCode = p.ExitCode;
                    string stderr = "";
                    string stdout = "";
                    try { stderr = p.StandardError.ReadToEnd(); } catch { }
                    try { stdout = p.StandardOutput.ReadToEnd(); } catch { }
                    result.StdOut = stdout;
                    result.Success = p.ExitCode == 0;
                    if (!result.Success && !string.IsNullOrWhiteSpace(stderr))
                        result.Error = stderr.Trim();
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        private static string QuoteArgument(string value)
        {
            string s = value ?? "";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        public static bool IsRunningAsAdministrator()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
