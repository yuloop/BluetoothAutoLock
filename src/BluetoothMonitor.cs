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
        private const int RequiredHighConfidencePresentConfirmations = 2;

        private readonly Config _cfg;
        private readonly Logger _log;
        private readonly Func<bool> _shouldStop;
        private readonly Random _pollRandom = new Random();

        private bool _wasConnected;
        private DateTime? _missingSince;
        private int _missingProbeCount;
        private int _presentConfirmCount;
        private bool _lockSuppressed;
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
            if (handle == IntPtr.Zero)
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (err != NativeMethods.ERROR_NO_MORE_ITEMS && err != 0)
                {
                    System.Diagnostics.Debug.WriteLine("EnumerateDevices: BluetoothFindFirstDevice failed (err=" + err + "); treating as no devices.");
                }
                return list;
            }

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
            PresenceEvidence evidence = ProbePassivePresence(target, configuredAddress, 4000, false);
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
            if (!_lockSuppressed) return;

            _log.Info("Windows session unlocked after Bluetooth lock; clearing suppression and requiring a fresh idle-then-Bluetooth-absence window before another lock.");
            ResetMissingState();
            _wasConnected = false;
            _lockSuppressed = false;
            SetStatus("已解锁 — 重新计时", "重新开始：键鼠空闲30秒后检查蓝牙，蓝牙缺失满阈值才会再次锁屏");
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
                " | Polling=random " + pollMinSeconds + "-" + pollMaxSeconds + "s");

            while (!_shouldStop())
            {
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
                while (elapsed < pollMs && !_shouldStop())
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
                _lockSuppressed = false;
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
            PresenceEvidence evidence = ProbePassivePresence(target, configuredAddress, 3000, true);
            string targetLabel = evidence.Label;

            ScanHit activeScanHit = null;
            if (evidence.Confidence != PresenceConfidence.High)
            {
                SetStatus("正在主动扫描 Classic 蓝牙", "常规连接状态未命中；只用配置的唯一蓝牙ID确认手机是否在旁边");
                activeScanHit = BluetoothScanner.FindClassicTargetByAddress(ActiveScanSeconds(), configuredAddress);
                if (activeScanHit != null)
                {
                    targetLabel = DescribeScanHit(activeScanHit);
                    evidence.Label = targetLabel;
                    evidence.Set(PresenceConfidence.High, "activeScan=" + targetLabel);
                    string scanMsg = "Target found by active Bluetooth scan: " + targetLabel;
                    if (!_wasConnected || _missingSince.HasValue || _lockSuppressed) _log.Info(scanMsg);
                    else _log.Debug(scanMsg);
                }
            }

            idleSeconds = NativeMethods.GetIdleSeconds();
            if (idleSeconds < idleRequiredSeconds)
            {
                ResetMissingState();
                _lockSuppressed = false;
                _log.Info("Keyboard/mouse input occurred during Bluetooth check (" + idleSeconds +
                    "s idle); cancelling this lock cycle.");
                SetStatus("使用中 — 已取消本轮锁屏", "重新等键鼠空闲 " + idleRequiredSeconds + " 秒后再检查蓝牙");
                return;
            }

            _log.Debug("Probe: target=" + targetLabel +
                " | " + evidence.ToLogText() +
                " | activeScan=" + (activeScanHit != null ? "high-confidence-hit" : "miss/skipped") +
                " => highConfidencePresent=" + (evidence.Confidence == PresenceConfidence.High));

            if (ShouldAcceptPresentEvidence(evidence))
            {
                if (!_wasConnected)
                    _log.Info("Target seen with high-confidence Bluetooth evidence: " + targetLabel + " | " + evidence.Source);
                else if (_lockSuppressed)
                    _log.Info("Target detected after Bluetooth lock; re-arming. " + evidence.Source);
                else if (_missingSince.HasValue)
                    _log.Info("Target detected again with sustained high-confidence evidence; cancelling Bluetooth-absence lock timer. " + evidence.Source);

                ResetMissingState();
                _wasConnected = true;
                _lockSuppressed = false;
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

            if (_lockSuppressed)
            {
                _log.Debug("Lock already triggered for this absent cycle; waiting for unlock or Bluetooth detection.");
                SetStatus("已锁屏（等待蓝牙恢复）", "等待解锁或蓝牙恢复后重新计时");
                return;
            }

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
                _lockSuppressed = false;
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
            FinalCheckResult finalResult = FinalPresenceCheckBeforeLock(idleRequiredSeconds);
            if (finalResult == FinalCheckResult.TargetPresent)
            {
                _log.Info("Final presence check saw the target; cancelling lock.");
                ResetMissingState();
                _wasConnected = true;
                _lockSuppressed = false;
                SetStatus("已连接：" + targetLabel, "继续监控；蓝牙可见，不会锁屏");
                return;
            }
            if (finalResult == FinalCheckResult.UserActive)
            {
                _log.Info("Keyboard/mouse input occurred during final Bluetooth check; cancelling lock.");
                ResetMissingState();
                _wasConnected = false;
                _lockSuppressed = false;
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

            _log.Info("Target absent for at least " + absenceRequiredSeconds +
                "s after keyboard/mouse idle threshold and no input occurred; triggering configured shortcuts and locking workstation.");
            TriggerLockShortcutsBeforeLock();
            bool ok = false;
            try { ok = NativeMethods.LockWorkStation(); }
            catch (Exception ex) { _log.Error("LockWorkStation threw: " + ex.Message); }
            if (ok)
            {
                _lockSuppressed = true;
                SetStatus("已锁屏（等待解锁/蓝牙恢复）", "等待解锁或蓝牙恢复");
            }
            else
            {
                _log.Warn("LockWorkStation returned false (last error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "); will retry while target remains absent and no keyboard/mouse input occurs.");
                SetStatus("锁屏失败 — 将重试", "下次轮询仍满足条件时重试");
            }
        }

        private void TriggerLockShortcutsBeforeLock()
        {
            if (_cfg.LockShortcutMappings == null || _cfg.LockShortcutMappings.Count == 0) return;

            SetStatus("触发锁屏快捷键", "正在执行 " + _cfg.LockShortcutMappings.Count + " 个快捷键，然后锁屏");
            int sent = LockShortcutRunner.TriggerAll(
                _cfg.LockShortcutMappings,
                msg => _log.Info(msg),
                msg => _log.Warn(msg));
            if (sent > 0)
            {
                int settleMs = Math.Max(0, Math.Min(10000, _cfg.LockShortcutSettleMilliseconds));
                if (settleMs > 0)
                {
                    _log.Info("Waiting " + settleMs + "ms before LockWorkStation so global hotkey handlers can run.");
                    SetStatus("等待快捷键生效", "已触发 " + sent + " 个快捷键；等待 " + settleMs + "ms 后锁屏");
                    Thread.Sleep(settleMs);
                }
            }
        }

        private void ResetMissingState()
        {
            _missingSince = null;
            _missingProbeCount = 0;
            _presentConfirmCount = 0;
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
            if (evidence == null || evidence.Confidence != PresenceConfidence.High)
            {
                _presentConfirmCount = 0;
                return false;
            }

            if (!_missingSince.HasValue && !_lockSuppressed)
            {
                _presentConfirmCount = 0;
                return true;
            }

            _presentConfirmCount++;
            if (_presentConfirmCount < RequiredHighConfidencePresentConfirmations)
            {
                string elapsedText = _missingSince.HasValue
                    ? (((int)(DateTime.UtcNow - _missingSince.Value).TotalSeconds).ToString() + "s")
                    : "n/a";
                _log.Info("High-confidence Bluetooth presence seen during an absence/lock-suppressed cycle (" +
                    _presentConfirmCount + "/" + RequiredHighConfidencePresentConfirmations +
                    "); keeping the absence timer until it is confirmed. MissingFor=" +
                    elapsedText + " | " + evidence.ToLogText());
                SetStatus("蓝牙疑似恢复 — 确认中",
                    "已看到高可信蓝牙信号 " + _presentConfirmCount + "/" +
                    RequiredHighConfidencePresentConfirmations + " 次；确认前不清零缺失计时");
                return false;
            }

            return true;
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
            // The phone may be visible to Windows' AEP/BLE scanner even when
            // classic fConnected/RFCOMM SDP says false. Keep this bounded so
            // one monitor tick cannot hang indefinitely.
            return Math.Max(3, Math.Min(6, Math.Max(1, _cfg.PollingIntervalSeconds)));
        }

        private static string DescribeScanHit(ScanHit hit)
        {
            if (hit == null) return "目标蓝牙";
            string name = string.IsNullOrEmpty(hit.Name) ? "(unnamed)" : hit.Name;
            string addr = string.IsNullOrEmpty(hit.Address) ? "?" : hit.Address;
            string kind = string.IsNullOrEmpty(hit.Kind) ? "?" : hit.Kind;
            string rssi = hit.RssiDbm.HasValue ? (", RSSI " + hit.RssiDbm.Value + " dBm") : "";
            string live = hit.LiveSignal ? ", live" : "";
            return name + " [" + addr + ", " + kind + rssi + live + "]";
        }

        private FinalCheckResult FinalPresenceCheckBeforeLock(int idleRequiredSeconds)
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
                    PresenceEvidence evidence = ProbePassivePresence(target, configuredAddress, 4000, false);
                    present = evidence.Confidence == PresenceConfidence.High;
                    _log.Info("Final presence check " + i + "/" + attempts + ": " +
                        evidence.ToLogText() + " => highConfidencePresent=" + present);

                    if (!present)
                    {
                        ScanHit scanHit = BluetoothScanner.FindClassicTargetByAddress(ActiveScanSeconds(), configuredAddress);
                        if (scanHit != null)
                        {
                            present = true;
                            _log.Info("Final presence check " + i + "/" + attempts + ": active scan saw target " + DescribeScanHit(scanHit));
                        }
                    }
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
