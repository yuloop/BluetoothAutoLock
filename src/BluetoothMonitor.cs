using System;
using System.Collections.Generic;
using System.Threading;

namespace BluetoothAutoLock
{
    internal sealed class DeviceSnapshot
    {
        public string Name;
        public ulong Address;
        public string AddressText;
        public bool Connected;
        public bool Authenticated;
        public bool Remembered;
    }

    internal sealed class MonitorStatusSnapshot
    {
        public readonly long Sequence;
        public readonly DateTime UpdatedAt;
        public readonly string Status;
        public readonly string NextAction;

        public MonitorStatusSnapshot(long sequence, DateTime updatedAt, string status, string nextAction)
        {
            Sequence = sequence;
            UpdatedAt = updatedAt;
            Status = status ?? "";
            NextAction = nextAction ?? "";
        }

        public static MonitorStatusSnapshot Create(string status, string nextAction)
        {
            return new MonitorStatusSnapshot(0, DateTime.Now, status, nextAction);
        }

        public string ToLogLine()
        {
            return string.Format("{0:HH:mm:ss} | 状态：{1} | 下一步：{2}", UpdatedAt, Status, NextAction);
        }
    }

    internal sealed class BluetoothMonitor
    {
        private enum FinalCheckResult
        {
            TargetAbsent,
            TargetPresent,
            UserActive
        }

        private enum PresenceConfidence
        {
            None,
            Low,
            High
        }

        private sealed class PresenceEvidence
        {
            public PresenceConfidence Confidence = PresenceConfidence.None;
            public string Label = "";
            public string Source = "none";
            public bool PairedSnapshot;
            public bool? SdpProbe;
            public bool FConnected;

            public void Set(PresenceConfidence confidence, string source)
            {
                if ((int)confidence < (int)Confidence) return;
                Confidence = confidence;
                Source = string.IsNullOrEmpty(source) ? "none" : source;
            }

            public string ToLogText()
            {
                return "confidence=" + ConfidenceText(Confidence) +
                    " | source=" + Source +
                    " | pairedSnapshot=" + (PairedSnapshot ? "yes" : "no") +
                    " | SDP=" + (SdpProbe.HasValue ? SdpProbe.Value.ToString() : "transient/none") +
                    " | fConnected=" + FConnected;
            }
        }

        private const int IdleSecondsBeforeBluetoothCheck = 30;

        private readonly Config _cfg;
        private readonly Logger _log;
        private readonly Func<bool> _shouldStop;
        private readonly Random _pollRandom = new Random();

        private bool _wasConnected;
        private DateTime? _missingSince;
        private int _missingProbeCount;
        private DateTime _appLockFinishedUtc = DateTime.MinValue;
        private bool _appLockDeferred;
        private volatile bool _wakeRequested;
        private readonly LockLifecycleState _lockLifecycle = new LockLifecycleState();
        private readonly object _statusGate = new object();
        private long _statusSequence;
        private DateTime _statusUpdatedAt = DateTime.Now;
        private string _nextActionText = "等待首次扫描";

        public volatile string CurrentStatusText = "初始化中";

        public BluetoothMonitor(Config cfg, Logger log, Func<bool> shouldStop)
        {
            _cfg = cfg;
            _log = log;
            _shouldStop = shouldStop ?? (() => false);
            SetStatus("初始化中", "等待首次扫描");
        }

        public MonitorStatusSnapshot GetStatusSnapshot()
        {
            lock (_statusGate)
            {
                return new MonitorStatusSnapshot(_statusSequence, _statusUpdatedAt, CurrentStatusText, _nextActionText);
            }
        }

        public void OverrideStatusForUi(string status, string nextAction)
        {
            SetStatus(status, nextAction);
        }

        private void SetStatus(string status, string nextAction)
        {
            if (string.IsNullOrEmpty(status)) status = "初始化中";
            if (string.IsNullOrEmpty(nextAction)) nextAction = "继续监控";

            lock (_statusGate)
            {
                CurrentStatusText = status;
                _nextActionText = nextAction;
                _statusUpdatedAt = DateTime.Now;
                _statusSequence++;
            }
        }

        public static List<DeviceSnapshot> EnumerateDevices()
        {
            var list = new List<DeviceSnapshot>();
            var search = new NativeMethods.BLUETOOTH_DEVICE_SEARCH_PARAMS();
            search.dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(search);
            search.fReturnAuthenticated = true;
            search.fReturnRemembered = true;
            search.fReturnUnknown = false;
            search.fReturnConnected = true;
            search.fIssueInquiry = false;
            search.cTimeoutMultiplier = 2;
            search.hRadio = IntPtr.Zero;

            var info = new NativeMethods.BLUETOOTH_DEVICE_INFO();
            info.dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(info);

            IntPtr handle = NativeMethods.BluetoothFindFirstDevice(ref search, ref info);
            if (handle == IntPtr.Zero) return list;

            try
            {
                do
                {
                    list.Add(new DeviceSnapshot
                    {
                        Name = info.szName ?? "",
                        Address = info.Address,
                        AddressText = NativeMethods.FormatBluetoothAddress(info.Address),
                        Connected = info.fConnected,
                        Authenticated = info.fAuthenticated,
                        Remembered = info.fRemembered
                    });

                    info = new NativeMethods.BLUETOOTH_DEVICE_INFO();
                    info.dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(info);
                }
                while (NativeMethods.BluetoothFindNextDevice(handle, ref info));
            }
            finally
            {
                NativeMethods.BluetoothFindDeviceClose(handle);
            }

            return list;
        }

        public DeviceSnapshot FindTarget(List<DeviceSnapshot> devices)
        {
            ulong target;
            bool hasAddr = NativeMethods.TryParseBluetoothAddress(_cfg.DeviceAddress, out target);
            if (!hasAddr) return null;

            foreach (var d in devices)
                if (d.Address == target) return d;

            return null;
        }

        public void RunOnce()
        {
            // 诊断模式：跳过键鼠空闲闸门和锁屏动作，仅打印一次完整的蓝牙探测结果，
            // 方便用户用 --once 排查"为何不锁屏 / 为何锁屏"。
            ulong configuredAddress;
            if (!NativeMethods.TryParseBluetoothAddress(_cfg.DeviceAddress, out configuredAddress))
            {
                _log.Warn("RunOnce: 未配置目标蓝牙地址 (DeviceAddress 为空或非法)。");
                return;
            }

            int idleSeconds = NativeMethods.GetIdleSeconds();
            int idleRequiredSeconds = MinimumIdleSecondsBeforeBluetoothCheck();
            int absenceRequiredSeconds = BluetoothAbsenceSecondsBeforeLock();
            _log.Info("RunOnce: 目标=" + _cfg.DeviceAddress + " | 当前键鼠空闲=" + idleSeconds +
                "s | 触发条件=空闲" + idleRequiredSeconds + "s + 蓝牙缺失" + absenceRequiredSeconds + "s");

            List<DeviceSnapshot> devices;
            try
            {
                devices = EnumerateDevices();
            }
            catch (Exception ex)
            {
                _log.Warn("RunOnce: EnumerateDevices 失败: " + ex.Message);
                return;
            }

            DeviceSnapshot target = FindTarget(devices);
            PresenceEvidence evidence = ProbePassivePresence(target, configuredAddress, 15000, false);
            _log.Info("RunOnce: 被动证据 " + evidence.ToLogText());

            ScanHit activeHit = null;
            if (evidence.Confidence != PresenceConfidence.High)
            {
                _log.Info("RunOnce: 被动证据非高可信，执行一次主动扫描...");
                try { activeHit = BluetoothScanner.FindClassicTargetByAddress(ActiveScanSeconds(), configuredAddress); }
                catch (Exception ex) { _log.Warn("RunOnce: 主动扫描失败: " + ex.Message); }
            }
            _log.Info("RunOnce: 主动扫描 " + (activeHit != null ? DescribeScanHit(activeHit) : "未命中/已跳过"));

            bool present = evidence.Confidence == PresenceConfidence.High || activeHit != null;
            string verdict = present
                ? "目标在附近 — 不会锁屏"
                : (idleSeconds < idleRequiredSeconds
                    ? "目标不在附近，但键鼠仍活跃；空闲满 " + idleRequiredSeconds + "s 后才进入锁屏倒计时"
                    : "目标不在附近且键鼠已空闲，正常模式会进入 " + absenceRequiredSeconds + "s 蓝牙缺失计时");
            _log.Info("RunOnce: 结论 — " + verdict);
        }

        public void ReArmAfterSessionUnlock()
        {
            // 不管是不是本程序锁的屏，都立刻叫醒监控线程：Windows 锁着时没锁成的微信/QQ 要马上补锁。
            _wakeRequested = true;
            if (!_lockLifecycle.RequestRearmAfterSessionUnlock()) return;

            _log.Info("Current Windows session unlocked after Bluetooth lock; scheduling a fresh idle-then-Bluetooth-absence window.");
            SetStatus("已解锁 — 即将重新计时", "监控线程将重新开始：键鼠空闲30秒后检查蓝牙，蓝牙缺失满阈值才会再次锁屏");
        }

        public void RunLoop()
        {
            int pollMinSeconds = PollingMinSeconds();
            int pollMaxSeconds = PollingMaxSeconds();
            _log.Info("Monitor started. Target: " +
                (string.IsNullOrEmpty(_cfg.DeviceAddress) ? "address=<missing>" : ("address=" + _cfg.DeviceAddress)) +
                " | Rule=idle 30s -> scan Bluetooth; lock only after target absent for configured window and no keyboard/mouse input" +
                " | IdleBeforeBluetoothCheck=" + IdleSecondsBeforeBluetoothCheck + "s" +
                " | BluetoothAbsenceBeforeLock=" + _cfg.DisconnectDelaySeconds + "s" +
                " | FinalRecheckAttempts=" + FinalRecheckAttempts() +
                " | Polling=random " + pollMinSeconds + "-" + pollMaxSeconds + "s" +
                " | LeaveActions: lockScreen=" + _cfg.LockScreenEnabled + ", lockWeChatQQ=" + LockAppsEnabled());

            while (!_shouldStop())
            {
                _wakeRequested = false;
                try
                {
                    Tick(DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    _log.Error("Tick failed: " + ex.GetType().Name + ": " + ex.Message);
                }

                int pollMs = NextPollDelayMs();
                int elapsed = 0;
                while (elapsed < pollMs && !_shouldStop() && !_wakeRequested)
                {
                    int slice = Math.Min(250, pollMs - elapsed);
                    Thread.Sleep(slice);
                    elapsed += slice;
                }
            }

            _log.Info("Monitor stopped.");
        }

        private void Tick(DateTime nowUtc)
        {
            _log.Debug("Tick begin: idle=" + NativeMethods.GetIdleSeconds() + "s");
            if (_lockLifecycle.ConsumeRearmRequest())
            {
                ResetMissingState();
                _wasConnected = false;
                _log.Info("Current Windows session unlocked after Bluetooth lock; monitor re-armed with a fresh idle-then-Bluetooth-absence window.");
                SetStatus("已解锁 — 重新计时", "重新开始：键鼠空闲30秒后检查蓝牙，蓝牙缺失满阈值才会再次锁屏");
            }

            if (UserReturnPolicy.InputOccurredAfter(_appLockFinishedUtc, DateTime.UtcNow, NativeMethods.GetIdleSeconds()) &&
                _lockLifecycle.RearmAfterUserReturn())
            {
                ResetMissingState();
                _wasConnected = false;
                _log.Info("Keyboard/mouse input after locking WeChat/QQ; monitor re-armed with a fresh idle-then-Bluetooth-absence window.");
                SetStatus("你回来了 — 重新计时", "键鼠空闲30秒后检查蓝牙，蓝牙缺失满阈值才会再次锁定");
            }

            if (_lockLifecycle.IsLockedUntilSessionUnlock)
            {
                return;
            }

            if (_appLockDeferred)
            {
                RunDeferredAppLock();
                return;
            }

            if (!_cfg.LockScreenEnabled && !LockAppsEnabled())
            {
                ResetMissingState();
                _log.Debug("Lock screen and WeChat/QQ lock are both disabled; skipping Bluetooth probes.");
                SetStatus("离开后动作都已关闭", "在设置里勾选“锁定 Windows 屏幕”或“锁定微信/QQ”后才会检查蓝牙");
                return;
            }

            ulong configuredAddress;
            bool hasConfiguredAddress = NativeMethods.TryParseBluetoothAddress(_cfg.DeviceAddress, out configuredAddress);
            if (!hasConfiguredAddress)
            {
                SetStatus("未配置目标蓝牙ID", "等待用户在设置中选择一个带唯一地址的 Classic 蓝牙设备");
                _log.Debug("No target Classic Bluetooth address configured; monitor is idle.");
                return;
            }

            int idleRequiredSeconds = MinimumIdleSecondsBeforeBluetoothCheck();
            int absenceRequiredSeconds = BluetoothAbsenceSecondsBeforeLock();
            int idleSeconds = NativeMethods.GetIdleSeconds();
            if (idleSeconds < idleRequiredSeconds)
            {
                int remainingIdle = Math.Max(1, idleRequiredSeconds - idleSeconds);
                ResetMissingState();
                _log.Debug("Keyboard/mouse active " + idleSeconds +
                    "s ago; skipping Bluetooth probes to reduce overhead. Need " +
                    remainingIdle + "s more idle time before Bluetooth scan is needed.");
                SetStatus("使用中/刚操作 — 暂不扫描蓝牙",
                    "键鼠空闲满 " + idleRequiredSeconds + " 秒后才扫描蓝牙；还需 " + remainingIdle + " 秒");
                return;
            }

            List<DeviceSnapshot> devices;
            try
            {
                devices = EnumerateDevices();
            }
            catch (Exception ex)
            {
                _log.Warn("EnumerateDevices failed: " + ex.Message);
                return;
            }

            DeviceSnapshot target = FindTarget(devices);
            // 2026-09-15 实测:成功 SDP 响应常见 ~5s,慢响应可达 8.5-10.1s,8s 超时会截断慢响应;
            // 另一关键实测:扫描(AEP+BLE)会打挂随后 15s 内的 SDP 探测(0/4 成功 vs 不扫描 6/6),
            // 因此锁屏判定路径(本 Tick)不再穿插主动扫描。
            _log.Debug("Tick: probing passive presence (timeout=15s, targetSnapshotFound=" + (target != null) + ")...");
            PresenceEvidence evidence = ProbePassivePresence(target, configuredAddress, 15000, true);
            string targetLabel = evidence.Label;

            idleSeconds = NativeMethods.GetIdleSeconds();
            if (idleSeconds < idleRequiredSeconds)
            {
                ResetMissingState();
                _log.Info("Keyboard/mouse input occurred during Bluetooth check (" + idleSeconds +
                    "s idle); cancelling this lock cycle.");
                SetStatus("使用中 — 已取消本轮锁屏", "重新等键鼠空闲 " + idleRequiredSeconds + " 秒后再检查蓝牙");
                return;
            }

            _log.Debug("Probe: target=" + targetLabel +
                " | " + evidence.ToLogText() +
                " => highConfidencePresent=" + (evidence.Confidence == PresenceConfidence.High));

            if (ShouldAcceptPresentEvidence(evidence))
            {
                if (!_wasConnected)
                    _log.Info("Target seen with high-confidence Bluetooth evidence: " + targetLabel + " | " + evidence.Source);
                else if (_missingSince.HasValue)
                    _log.Info("Target detected again with high-confidence evidence; cancelling Bluetooth-absence lock timer. " + evidence.Source);

                ResetMissingState();
                _wasConnected = true;
                SetStatus("已连接：" + targetLabel, "继续监控；蓝牙可见，不会锁屏");
                return;
            }
            if (evidence.Confidence == PresenceConfidence.High)
            {
                return;
            }

            if (evidence.Confidence == PresenceConfidence.Low)
            {
                _log.Info("Weak Bluetooth presence ignored for lock cancellation: " +
                    targetLabel + " | " + evidence.ToLogText());
            }

            _wasConnected = false;

            if (!_missingSince.HasValue)
            {
                _missingSince = DateTime.UtcNow;
                _missingProbeCount = 1;
                _log.Info("Keyboard/mouse has been idle for at least " + idleRequiredSeconds +
                    "s and target is not nearby; starting " + absenceRequiredSeconds +
                    "s Bluetooth absence window before lock.");
                SetStatus("蓝牙未检测到 — 开始计时",
                    "需持续 " + absenceRequiredSeconds + " 秒扫不到蓝牙才会锁屏；使用电脑或扫到蓝牙会取消");
            }
            else
            {
                _missingProbeCount++;
            }

            double missingFor = (DateTime.UtcNow - _missingSince.Value).TotalSeconds;
            idleSeconds = NativeMethods.GetIdleSeconds();

            if (idleSeconds < idleRequiredSeconds)
            {
                int remainingIdle = Math.Max(1, idleRequiredSeconds - idleSeconds);
                _log.Info("Target absent for " + ((int)missingFor) + "s, but keyboard/mouse was active " + idleSeconds +
                    "s ago; cancelling Bluetooth absence window. Need " + remainingIdle + "s more idle time before checking Bluetooth again.");
                ResetMissingState();
                SetStatus("使用中 — 已取消蓝牙缺失计时", "重新等键鼠空闲 " + idleRequiredSeconds + " 秒后再检查蓝牙");
                return;
            }

            if (missingFor < absenceRequiredSeconds)
            {
                int remainingAbsence = Math.Max(1, (int)Math.Ceiling(absenceRequiredSeconds - missingFor));
                _log.Debug("Target absent for " + ((int)missingFor) + "/" + absenceRequiredSeconds +
                    "s while idle; continuing Bluetooth absence window.");
                SetStatus("蓝牙未检测到 — " + remainingAbsence + " 秒后才可能锁屏",
                    "继续扫描；期间使用电脑或扫到蓝牙都会取消本轮锁屏");
                return;
            }

            _log.Info("Target absent for " + ((int)missingFor) + "s after keyboard/mouse idle threshold; running final Bluetooth presence check before lock.");
            SetStatus("条件满足 — 锁屏前复核", "最后扫描目标蓝牙并检查键鼠；扫到或使用电脑都会取消");
            FinalCheckResult finalResult = FinalPresenceCheckBeforeLock(idleRequiredSeconds, true);
            if (finalResult == FinalCheckResult.TargetPresent)
            {
                _log.Info("Final presence check saw the target; cancelling lock.");
                ResetMissingState();
                _wasConnected = true;
                SetStatus("已连接：" + targetLabel, "继续监控；蓝牙可见，不会锁屏");
                return;
            }
            if (finalResult == FinalCheckResult.UserActive)
            {
                _log.Info("Keyboard/mouse input occurred during final Bluetooth check; cancelling lock.");
                ResetMissingState();
                _wasConnected = false;
                SetStatus("使用中 — 锁屏已取消", "重新等键鼠空闲 " + idleRequiredSeconds + " 秒后再检查蓝牙");
                return;
            }

            if (_shouldStop())
            {
                _log.Info("Stop requested before LockWorkStation; aborting this tick.");
                return;
            }

            idleSeconds = NativeMethods.GetIdleSeconds();
            if (idleSeconds < idleRequiredSeconds)
            {
                _log.Info("Keyboard/mouse input occurred immediately before LockWorkStation; cancelling lock.");
                ResetMissingState();
                SetStatus("使用中 — 锁屏已取消", "重新等键鼠空闲 " + idleRequiredSeconds + " 秒后再检查蓝牙");
                return;
            }

            bool lockScreen = _cfg.LockScreenEnabled;
            bool lockApps = LockAppsEnabled();
            _log.Info("Target absent for at least " + absenceRequiredSeconds +
                "s after keyboard/mouse idle threshold and no input occurred; running leave actions (lockScreen=" +
                lockScreen + ", lockWeChatQQ=" + lockApps + ").");
            int appLocks = 0;
            bool userInputDuringShortcuts = false;
            DateTime shortcutsFinishedUtc = DateTime.UtcNow;
            if (lockApps)
            {
                if (NativeMethods.IsInputDesktopAvailable())
                {
                    appLocks = TriggerLockShortcuts(lockScreen, out userInputDuringShortcuts);
                }
                else
                {
                    // Windows 已经锁屏（例如远程软件断开时锁的），模拟按键送不到微信/QQ；
                    // 先记下来，Windows 解锁时手机还不在旁边就马上补锁。
                    _appLockDeferred = true;
                    _log.Info("Windows is already locked, so shortcuts cannot reach WeChat/QQ; deferring the WeChat/QQ lock until the session unlocks and the target is still away.");
                }
                shortcutsFinishedUtc = DateTime.UtcNow;
            }
            if (!lockScreen)
            {
                if (_appLockDeferred)
                {
                    ResetMissingState();
                    SetDeferredAppLockStatus();
                    return;
                }
                if (userInputDuringShortcuts)
                {
                    _log.Info("Keyboard/mouse input occurred while running the lock shortcuts; the user is back, keep monitoring.");
                    ResetMissingState();
                    SetStatus("你回来了 — 继续监控", "键鼠空闲30秒后检查蓝牙");
                    return;
                }
                if (appLocks == 0)
                {
                    _log.Warn("No lock shortcut was sent (target not running or sending failed); keep monitoring and retry after the next absence window.");
                    ResetMissingState();
                    SetStatus("锁定微信/QQ 没有成功", "目标程序没运行或按键失败；下一轮蓝牙缺失满阈值后再试");
                    return;
                }
                _appLockFinishedUtc = shortcutsFinishedUtc;
                _lockLifecycle.MarkAppLockSucceeded();
                _log.Info("WeChat/QQ lock finished without locking the workstation (" + appLocks +
                    " shortcut(s) sent); waiting for keyboard/mouse input before re-arming.");
                SetStatus("已锁定微信/QQ（等你回来）", "你回来碰键盘鼠标后重新计时；这期间不会重复锁定");
                return;
            }

            if (appLocks > 0 && !userInputDuringShortcuts) WaitBeforeWorkstationLock(appLocks);
            if (userInputDuringShortcuts ||
                UserReturnPolicy.InputOccurredAfter(shortcutsFinishedUtc, DateTime.UtcNow, NativeMethods.GetIdleSeconds()))
            {
                _log.Info("Keyboard/mouse input occurred during or after the lock shortcuts; cancelling workstation lock.");
                ResetMissingState();
                SetStatus("使用中 — 锁屏已取消", "重新等键鼠空闲 " + idleRequiredSeconds + " 秒后再检查蓝牙");
                return;
            }

            bool ok = false;
            try { ok = NativeMethods.LockWorkStation(); }
            catch (Exception ex) { _log.Error("LockWorkStation threw: " + ex.Message); }
            if (ok)
            {
                _lockLifecycle.MarkLockSucceeded();
                _log.Info("LockWorkStation succeeded; suppressing all monitor actions until the current Windows session unlocks.");
                SetStatus("已自动锁屏（等待本会话解锁）", "仅当前 Windows 会话解锁后重新计时；蓝牙恢复不会重复锁屏");
            }
            else
            {
                _log.Warn("LockWorkStation returned false (last error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "); will retry while target remains absent and no keyboard/mouse input occurs.");
                SetStatus("锁屏失败 — 将重试", "下次轮询仍满足条件时重试");
            }
        }

        private void RunDeferredAppLock()
        {
            if (!LockAppsEnabled())
            {
                _appLockDeferred = false;
                return;
            }
            if (!NativeMethods.IsInputDesktopAvailable())
            {
                _log.Debug("Windows is still locked; the deferred WeChat/QQ lock waits for the session to unlock.");
                SetDeferredAppLockStatus();
                return;
            }

            _appLockDeferred = false;
            _log.Info("Windows is unlocked again; checking whether the target is still away before the deferred WeChat/QQ lock.");
            // 解锁的人正在操作电脑，所以不看键鼠空闲；也不做兜底扫描（要多等 12 秒，
            // 而且平时扫不到手机），SDP 复核完就尽快锁定。
            FinalCheckResult result = FinalPresenceCheckBeforeLock(0, false);
            ResetMissingState();
            if (result == FinalCheckResult.TargetPresent)
            {
                _wasConnected = true;
                _log.Info("Target is nearby after the unlock; the deferred WeChat/QQ lock is cancelled.");
                SetStatus("手机在旁边 — 不补锁", "继续监控");
                return;
            }
            _wasConnected = false;
            if (_shouldStop()) return;
            if (!NativeMethods.IsInputDesktopAvailable())
            {
                _appLockDeferred = true;
                _log.Info("Windows was locked again during the Bluetooth check; the WeChat/QQ lock stays deferred.");
                SetDeferredAppLockStatus();
                return;
            }

            bool userInputSeen;
            int appLocks = TriggerLockShortcuts(false, out userInputSeen);
            if (appLocks == 0)
            {
                _log.Warn("Deferred WeChat/QQ lock sent no shortcut (target not running or sending failed); keep monitoring.");
                SetStatus("补锁微信/QQ 没有成功", "目标程序没运行或按键失败；下一轮蓝牙缺失满阈值后再试");
                return;
            }
            _appLockFinishedUtc = DateTime.UtcNow;
            _lockLifecycle.MarkAppLockSucceeded();
            _log.Info("Deferred WeChat/QQ lock finished after the unlock (" + appLocks +
                " shortcut(s) sent); waiting for keyboard/mouse input before re-arming.");
            SetStatus("已补锁微信/QQ", "你回来碰键盘鼠标后重新计时");
        }

        private void SetDeferredAppLockStatus()
        {
            SetStatus("Windows 已锁屏 — 解锁后补锁微信/QQ", "Windows 解锁时手机还不在旁边，就马上锁定微信/QQ");
        }

        private bool LockAppsEnabled()
        {
            return _cfg.LockShortcutsEnabled && _cfg.LockShortcutMappings != null && _cfg.LockShortcutMappings.Count > 0;
        }

        private int TriggerLockShortcuts(bool screenLockFollows, out bool userInputSeen)
        {
            SetStatus("锁定微信/QQ", "正在执行 " + _cfg.LockShortcutMappings.Count + " 个锁屏快捷键" + (screenLockFollows ? "，然后锁屏" : ""));
            return LockShortcutRunner.TriggerAll(
                _cfg.LockShortcutMappings,
                _cfg.LockShortcutPreDelayMilliseconds,
                msg => _log.Info(msg),
                msg => _log.Warn(msg),
                out userInputSeen);
        }

        private void WaitBeforeWorkstationLock(int sent)
        {
            int settleMs = Math.Max(0, Math.Min(10000, _cfg.LockShortcutSettleMilliseconds));
            if (settleMs <= 0) return;
            _log.Info("Waiting " + settleMs + "ms before LockWorkStation so global hotkey handlers can run.");
            SetStatus("等待快捷键生效", "已触发 " + sent + " 个快捷键；等待 " + settleMs + "ms 后锁屏");
            Thread.Sleep(settleMs);
        }

        private void ResetMissingState()
        {
            _missingSince = null;
            _missingProbeCount = 0;
        }

        private PresenceEvidence ProbePassivePresence(DeviceSnapshot target, ulong configuredAddress, int timeoutMs, bool allowPreviousPresenceOnTransient)
        {
            PresenceEvidence evidence = new PresenceEvidence();
            evidence.Label = NativeMethods.FormatBluetoothAddress(configuredAddress);

            if (target != null)
            {
                evidence.Label = target.Name + " [" + target.AddressText + "]";
                evidence.PairedSnapshot = true;
                evidence.FConnected = target.Connected;
                evidence.SdpProbe = WinRtBluetooth.IsInRange(target.Address, timeoutMs);

                if (evidence.SdpProbe.HasValue && evidence.SdpProbe.Value)
                {
                    evidence.Set(PresenceConfidence.High, target.Connected ? "SDP=true; fConnected=true" : "SDP=true");
                }
                else if (target.Connected)
                {
                    evidence.Set(PresenceConfidence.Low, "fConnected=true without SDP confirmation");
                }
                else if (!evidence.SdpProbe.HasValue && allowPreviousPresenceOnTransient && _wasConnected)
                {
                    evidence.Set(PresenceConfidence.Low, "SDP transient; previous high-confidence presence");
                }
            }
            else
            {
                evidence.SdpProbe = WinRtBluetooth.IsInRange(configuredAddress, timeoutMs);
                if (evidence.SdpProbe.HasValue && evidence.SdpProbe.Value)
                {
                    evidence.Set(PresenceConfidence.High, "address SDP=true");
                }
                else if (!evidence.SdpProbe.HasValue && allowPreviousPresenceOnTransient && _wasConnected)
                {
                    evidence.Set(PresenceConfidence.Low, "address SDP transient; previous high-confidence presence");
                }
            }

            return evidence;
        }
        private bool ShouldAcceptPresentEvidence(PresenceEvidence evidence)
        {
            // 2026-09-14 误锁根因 B：手机省电静默时偶发的单次高可信命中曾被
            // “连续 2 次确认”门槛忽略（16:56 误锁）。SDP=true / 可信 RSSI 命中
            // 本身就是手机回包，单次即应清零缺失计时：宁可暂缓锁屏，也不误锁。
            return evidence != null && evidence.Confidence == PresenceConfidence.High;
        }

        private static string ConfidenceText(PresenceConfidence confidence)
        {
            switch (confidence)
            {
                case PresenceConfidence.High: return "high";
                case PresenceConfidence.Low: return "low";
                default: return "none";
            }
        }

        private int MinimumIdleSecondsBeforeBluetoothCheck()
        {
            // Keyboard/mouse checks are cheap.  Only after this idle gate do we
            // start the heavier Bluetooth proximity scans.
            return IdleSecondsBeforeBluetoothCheck;
        }

        private int BluetoothAbsenceSecondsBeforeLock()
        {
            // The configured delay is now the required continuous duration for
            // Bluetooth absence after the 30-second keyboard/mouse idle gate.
            return Math.Max(1, _cfg.DisconnectDelaySeconds);
        }

        private int FinalRecheckAttempts()
        {
            return Math.Max(1, Math.Min(10, _cfg.DisconnectConfirmSeconds));
        }

        private int PollingMinSeconds()
        {
            return Math.Max(1, Math.Min(60, _cfg.PollingIntervalSeconds));
        }

        private int PollingMaxSeconds()
        {
            int min = PollingMinSeconds();
            return Math.Max(min, Math.Min(60, min + 7));
        }

        private int NextPollDelayMs()
        {
            int min = PollingMinSeconds();
            int max = PollingMaxSeconds();
            int seconds;
            lock (_pollRandom)
            {
                seconds = _pollRandom.Next(min, max + 1);
            }
            return seconds * 1000;
        }

        private int ActiveScanSeconds()
        {
            // 2026-09-15: 主动扫描已从常规 Tick 与终复核 SDP 重试之间移除
            // (实测扫描会打挂随后 15s 内的 SDP 探测),仅 RunOnce 诊断使用。
            return Math.Max(5, Math.Min(10, Math.Max(1, _cfg.PollingIntervalSeconds)));
        }

        private int FinalScanSeconds()
        {
            // 终复核全部 SDP 失败后的兜底扫描窗口:最后一道防线,给满 12s 上限,
            // 尽量覆盖 AEP Updated 事件的最坏到达延迟。
            return Math.Max(8, Math.Min(12, Math.Max(1, _cfg.PollingIntervalSeconds) + 4));
        }

        private static string DescribeScanHit(ScanHit hit)
        {
            if (hit == null) return "目标蓝牙";
            string name = string.IsNullOrEmpty(hit.Name) ? "(unnamed)" : hit.Name;
            string addr = string.IsNullOrEmpty(hit.Address) ? "?" : hit.Address;
            string kind = string.IsNullOrEmpty(hit.Kind) ? "?" : hit.Kind;
            string rssi = hit.RssiDbm.HasValue ? (", RSSI " + hit.RssiDbm.Value + " dBm") : "";
            string signalSource = ", signal=" + (hit.LiveSignal ? "updated" : "added");
            return name + " [" + addr + ", " + kind + rssi + signalSource + "]";
        }

        private FinalCheckResult FinalPresenceCheckBeforeLock(int idleRequiredSeconds, bool allowFallbackScan)
        {
            int attempts = FinalRecheckAttempts();
            int waitMs = Math.Max(800, Math.Min(2000, Math.Max(1, _cfg.PollingIntervalSeconds) * 500));

            _log.Info("Running final Bluetooth presence check before lock (" +
                attempts + " attempts, any present response cancels lock).");
            SetStatus("锁屏前复核蓝牙", "正在执行最终蓝牙复核；扫到即取消锁屏");

            ulong configuredAddress;
            bool hasConfiguredAddress = NativeMethods.TryParseBluetoothAddress(_cfg.DeviceAddress, out configuredAddress);
            if (!hasConfiguredAddress)
            {
                _log.Warn("Final presence check skipped because no target Classic Bluetooth address is configured.");
                return FinalCheckResult.TargetAbsent;
            }
            int totalPresent = 0;

            for (int i = 1; i <= attempts && !_shouldStop(); i++)
            {
                int idleSeconds = NativeMethods.GetIdleSeconds();
                if (idleSeconds < idleRequiredSeconds)
                {
                    _log.Info("Final presence check " + i + "/" + attempts +
                        ": keyboard/mouse active " + idleSeconds + "s ago; cancelling lock.");
                    return FinalCheckResult.UserActive;
                }

                bool present = false;
                try
                {
                    List<DeviceSnapshot> devices = EnumerateDevices();
                    DeviceSnapshot target = FindTarget(devices);
                    // 终复核是锁屏前最后防线:深睡手机 page 响应可达 5-10s(实测最慢 10.1s),
                    // 超时给足 15s,否则"在场但响应慢"直接被判缺席。
                    PresenceEvidence evidence = ProbePassivePresence(target, configuredAddress, 15000, false);
                    present = evidence.Confidence == PresenceConfidence.High;
                    _log.Info("Final presence check " + i + "/" + attempts + ": " +
                        evidence.ToLogText() + " => highConfidencePresent=" + present);
                }
                catch (Exception ex)
                {
                    _log.Warn("Final presence check " + i + "/" + attempts + " failed: " + ex.Message);
                }

                idleSeconds = NativeMethods.GetIdleSeconds();
                if (idleSeconds < idleRequiredSeconds)
                {
                    _log.Info("Final presence check " + i + "/" + attempts +
                        ": keyboard/mouse became active during Bluetooth probe; cancelling lock.");
                    return FinalCheckResult.UserActive;
                }

                if (present)
                {
                    totalPresent++;
                    _log.Info("Final presence check saw target nearby (probe " + i + "/" + attempts + "); cancelling lock.");
                    return FinalCheckResult.TargetPresent;
                }

                if (i < attempts)
                {
                    if (!SleepInterruptibleUnlessIdle(waitMs, idleRequiredSeconds))
                    {
                        _log.Info("Final presence check wait interrupted by keyboard/mouse input; cancelling lock.");
                        return FinalCheckResult.UserActive;
                    }
                }
            }

            // 2026-09-15 实测:主动扫描会打挂随后 15s 内的 SDP 探测,
            // 所以只在全部 SDP 复核都失败后,做一次兜底扫描。
            if (allowFallbackScan && !_shouldStop())
            {
                int idleBeforeScan = NativeMethods.GetIdleSeconds();
                if (idleBeforeScan < idleRequiredSeconds)
                {
                    _log.Info("Final active scan skipped: keyboard/mouse active " + idleBeforeScan + "s ago; cancelling lock.");
                    return FinalCheckResult.UserActive;
                }
                try
                {
                    ScanHit lastScanHit = BluetoothScanner.FindClassicTargetByAddress(FinalScanSeconds(), configuredAddress);
                    if (lastScanHit != null)
                    {
                        _log.Info("Final active scan saw target " + DescribeScanHit(lastScanHit) + "; cancelling lock.");
                        return FinalCheckResult.TargetPresent;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Final active scan failed: " + ex.Message);
                }
            }

            _log.Info("Final presence check did not see target nearby (" +
                totalPresent + "/" + attempts + " probes succeeded); proceeding to lock.");
            return FinalCheckResult.TargetAbsent;
        }

        private bool SleepInterruptibleUnlessIdle(int milliseconds, int idleRequiredSeconds)
        {
            int elapsed = 0;
            while (elapsed < milliseconds && !_shouldStop())
            {
                if (NativeMethods.GetIdleSeconds() < idleRequiredSeconds)
                    return false;
                int slice = Math.Min(250, milliseconds - elapsed);
                Thread.Sleep(slice);
                elapsed += slice;
            }
            return true;
        }
    }
}
