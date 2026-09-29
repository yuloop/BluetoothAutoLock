using System;
using System.IO;

namespace BluetoothAutoLock.Tests
{
    internal static class BluetoothMonitorPolicyTests
    {
        private static int _passed;

        private static int Main()
        {
            try
            {
                AcceptsActualClassicAddedRssi();
                RejectsCachedAndSentinelRssi();
                RequiresRealLockBeforeRearm();
                SuppressesUntilMonitorConsumesCurrentSessionUnlock();
                AcceptsSingleHighConfidenceHitDuringAbsence();
                LockDecisionAvoidsScanBeforeSdpRetries();
                ScannerDelegatesToRssiPolicy();
                MonitorUsesLifecycleGuardBeforeProbing();
                SessionHandlerRequiresActualUnlock();
                SdpPresenceRequiresServiceRecords();
                Console.WriteLine("通过：" + _passed + " 项");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("失败：" + ex.Message);
                return 1;
            }
        }

        private static void AcceptsActualClassicAddedRssi()
        {
            // 实测：手机的 Classic 信号 RSSI -3 dBm / LIVE=no。
            Assert(ClassicBluetoothEvidence.HasCredibleRssi(-3), "Classic 的 -3 dBm 真实 Added 信号必须视为在场");
            Assert(ClassicBluetoothEvidence.HasCredibleRssi(-99), "物理范围内的弱 Classic RSSI 必须视为在场");
            Assert(ClassicBluetoothEvidence.HasCredibleRssi(-1), "物理范围上界的 Classic RSSI 必须视为在场");
            Pass();
        }

        private static void RejectsCachedAndSentinelRssi()
        {
            Assert(!ClassicBluetoothEvidence.HasCredibleRssi(null), "缺少 RSSI 不能视为在场");
            Assert(!ClassicBluetoothEvidence.HasCredibleRssi(0), "缓存 RSSI 0 不能视为在场");
            Assert(!ClassicBluetoothEvidence.HasCredibleRssi(-100), "物理噪声下限不能视为在场");
            Assert(!ClassicBluetoothEvidence.HasCredibleRssi(-127), "-127 哨兵值不能视为在场");
            Assert(!ClassicBluetoothEvidence.HasCredibleRssi(-128), "-128 哨兵值不能视为在场");
            Pass();
        }

        private static void RequiresRealLockBeforeRearm()
        {
            var state = new LockLifecycleState();
            Assert(!state.IsLockedUntilSessionUnlock, "初始状态应允许监控");
            Assert(!state.RequestRearmAfterSessionUnlock(), "未成功锁屏时，SessionUnlock 不能改变监控状态");
            Assert(!state.ConsumeRearmRequest(), "不存在的 rearm 请求不能被消费");
            Assert(!state.IsLockedUntilSessionUnlock, "无效解锁事件后仍应保持正常监控");
            Pass();
        }

        private static void SuppressesUntilMonitorConsumesCurrentSessionUnlock()
        {
            var state = new LockLifecycleState();
            state.MarkLockSucceeded();
            Assert(state.IsLockedUntilSessionUnlock, "成功锁屏后必须抑制所有后续锁屏动作");
            Assert(!state.ConsumeRearmRequest(), "未发生解锁时不能解除锁屏抑制");
            Assert(state.IsLockedUntilSessionUnlock, "蓝牙恢复或键鼠活动不应解除锁屏抑制");
            Assert(state.RequestRearmAfterSessionUnlock(), "有效的当前会话解锁应提交 rearm 请求");
            Assert(state.IsLockedUntilSessionUnlock, "rearm 请求等待监控线程消费前仍必须保持抑制");
            Assert(state.ConsumeRearmRequest(), "监控线程应能消费 rearm 请求");
            Assert(!state.IsLockedUntilSessionUnlock, "监控线程消费 rearm 请求后才允许下一轮监控");
            Pass();
        }

        private static void AcceptsSingleHighConfidenceHitDuringAbsence()
        {
            // 2026-09-14 误锁根因 B：16:55:22 单次 SDP=true 曾被“连续2次确认”门槛挂起，
            // 90 秒后误锁。单次高可信命中必须立即清零缺失计时。
            string source = ReadSource("BluetoothMonitor.cs");
            string body = ExtractMethodBody(source, "private bool ShouldAcceptPresentEvidence(PresenceEvidence evidence)");
            Assert(body.Contains("evidence.Confidence == PresenceConfidence.High"), "单次高可信证据必须被接受");
            Assert(!body.Contains("_presentConfirmCount"), "不得保留多次确认计数门槛");
            Assert(!source.Contains("RequiredHighConfidencePresentConfirmations"), "不得保留连续多次确认常量");
            Pass();
        }

        private static void LockDecisionAvoidsScanBeforeSdpRetries()
        {
            // 2026-09-15 实测:主动扫描会打挂随后 15s 内的 SDP 探测(0/4 成功 vs 不扫描 6/6)。
            // 锁屏判定路径不得在 SDP 重试之间穿插扫描;终复核只在全部 SDP 失败后兜底扫一次。
            string source = ReadSource("BluetoothMonitor.cs");
            string tick = ExtractMethodBody(source, "private void Tick(DateTime nowUtc)");
            Assert(!tick.Contains("FindClassicTargetByAddress"), "Tick 不得在 SDP 探测路径中插入主动扫描");

            string final = ExtractMethodBody(source, "private FinalCheckResult FinalPresenceCheckBeforeLock(int idleRequiredSeconds)");
            Assert(final.Contains("Final active scan saw target"), "终复核必须保留一次兜底主动扫描");
            string[] parts = final.Split(new string[] { "FindClassicTargetByAddress" }, StringSplitOptions.None);
            Assert(parts.Length - 1 == 1, "终复核中的兜底扫描只允许出现一次");
            Pass();
        }

        private static void ScannerDelegatesToRssiPolicy()
        {
            string source = ReadSource("WinRtBluetooth.cs");
            string body = ExtractMethodBody(source, "private static bool HasCredibleClassicRadioEvidence(ScanHit hit)");
            Assert(body.Contains("ClassicBluetoothEvidence.HasCredibleRssi(hit.RssiDbm)"), "Classic 扫描命中必须委托统一 RSSI 策略");
            Assert(!body.Contains("!hit.LiveSignal"), "Classic 的有效 RSSI 命中不得因 LIVE=no 被丢弃");
            Pass();
        }

        private static void MonitorUsesLifecycleGuardBeforeProbing()
        {
            string source = ReadSource("BluetoothMonitor.cs");
            string body = ExtractMethodBody(source, "private void Tick(DateTime nowUtc)");
            int consume = body.IndexOf("_lockLifecycle.ConsumeRearmRequest()", StringComparison.Ordinal);
            int guard = body.IndexOf("_lockLifecycle.IsLockedUntilSessionUnlock", StringComparison.Ordinal);
            int firstProbe = body.IndexOf("NativeMethods.TryParseBluetoothAddress", StringComparison.Ordinal);
            Assert(consume >= 0 && guard >= 0 && firstProbe >= 0 && consume < guard && guard < firstProbe,
                "Tick 必须先消费解锁请求并在蓝牙探测前检查锁屏抑制");
            Assert(!source.Contains("_lockSuppressed"), "旧的可被普通分支清除的锁屏抑制标志不得保留");
            Assert(source.Contains("_lockLifecycle.MarkLockSucceeded()"), "只有锁屏成功后才能进入锁屏抑制状态");
            Pass();
        }

        private static void SessionHandlerRequiresActualUnlock()
        {
            string source = ReadSource("TrayApp.cs");
            string body = ExtractMethodBody(source, "private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)");
            Assert(body.Contains("e.Reason != SessionSwitchReason.SessionUnlock"), "只应接受 SessionUnlock 事件");
            Assert(!body.Contains("SessionSwitchReason.SessionLogon"), "SessionLogon 不能解除自动锁屏抑制");
            Pass();
        }

        private static void SdpPresenceRequiresServiceRecords()
        {
            string scanner = ReadSource("WinRtBluetooth.cs");
            string probe = ExtractMethodBody(scanner, "public static bool? IsInRange(ulong address, int timeoutMs)");
            Assert(probe.Contains("res.Error == BluetoothError.Success && res.Services.Count > 0"),
                "SDP 必须返回至少一项服务记录才算在场：手机不在时 Windows 也回 Success，只是服务列表为空");

            string monitor = ReadSource("BluetoothMonitor.cs");
            string evidence = ExtractMethodBody(monitor, "private PresenceEvidence ProbePassivePresence(DeviceSnapshot target, ulong configuredAddress, int timeoutMs, bool allowPreviousPresenceOnTransient)");
            Assert(!evidence.Contains("evidence.SdpProbe.Value && target.Connected"),
                "已配对手机在旁边时 fConnected 通常为 false，SDP 有服务记录即应视为在场，不得再要求 fConnected");
            Pass();
        }

        private static string ReadSource(string fileName)
        {
            string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "src", fileName));
            Assert(File.Exists(path), "应能读取源文件：" + fileName);
            return File.ReadAllText(path);
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert(start >= 0, "应能找到方法：" + signature);
            int brace = source.IndexOf('{', start);
            Assert(brace >= 0, "方法缺少左花括号：" + signature);

            int depth = 0;
            for (int i = brace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source.Substring(brace, i - brace + 1);
                }
            }

            throw new Exception("方法体未闭合：" + signature);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Pass()
        {
            _passed++;
        }
    }
}
