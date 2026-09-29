using System;
using System.Collections.Generic;
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
                AppLockWaitsForUserReturn();
                UserReturnIgnoresInjectedInput();
                LeaveActionsRespectToggles();
                ParsesPreShortcutMapping();
                ChoosesMainWindowFromRealWeChatAndQqLayout();
                ShortcutRunnerChecksFrontBeforePreShortcut();
                CaptureStateRecordsHotkeysOwnedByOtherPrograms();
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

        private static void AppLockWaitsForUserReturn()
        {
            var state = new LockLifecycleState();
            state.MarkAppLockSucceeded();
            Assert(state.IsLockedUntilSessionUnlock, "只锁微信/QQ 后也必须暂停监控，避免反复锁定");
            Assert(!state.RequestRearmAfterSessionUnlock(), "只锁微信/QQ 时，Windows 解锁事件不能重新开始监控");
            Assert(!state.ConsumeRearmRequest(), "只锁微信/QQ 时不存在待消费的解锁请求");
            Assert(state.RearmAfterUserReturn(), "用户回来操作键鼠后应重新开始监控");
            Assert(!state.IsLockedUntilSessionUnlock, "重新开始监控后应解除暂停");
            Assert(!state.RearmAfterUserReturn(), "没有锁定时不能重复重新开始");

            state.MarkLockSucceeded();
            Assert(!state.RearmAfterUserReturn(), "锁屏后只能由 Windows 解锁事件重新开始，键鼠输入不算");
            Pass();
        }

        private static void UserReturnIgnoresInjectedInput()
        {
            DateTime finished = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
            DateTime now = finished.AddSeconds(20);
            Assert(!UserReturnPolicy.InputOccurredAfter(finished, now, 21), "锁定前的键鼠输入不算用户回来");
            Assert(!UserReturnPolicy.InputOccurredAfter(finished, now, 19), "程序自己按下的锁定键不能算用户回来");
            Assert(UserReturnPolicy.InputOccurredAfter(finished, now, 5), "锁定完成后出现的键鼠输入应算用户回来");
            Assert(!UserReturnPolicy.InputOccurredAfter(finished, now, int.MaxValue), "读不到空闲时间时不能算用户回来");
            Assert(!UserReturnPolicy.InputOccurredAfter(finished, now, -1), "非法空闲时间不能算用户回来");
            Pass();
        }

        private static void LeaveActionsRespectToggles()
        {
            string source = ReadSource("BluetoothMonitor.cs");
            string tick = ExtractMethodBody(source, "private void Tick(DateTime nowUtc)");
            int rearm = tick.IndexOf("_lockLifecycle.RearmAfterUserReturn()", StringComparison.Ordinal);
            int guard = tick.IndexOf("_lockLifecycle.IsLockedUntilSessionUnlock", StringComparison.Ordinal);
            Assert(rearm >= 0 && guard >= 0 && rearm < guard, "用户回来后的重新开始必须在锁定暂停检查之前");
            Assert(tick.Contains("!_cfg.LockScreenEnabled && !LockAppsEnabled()"), "两个离开动作都关闭时不应探测蓝牙");
            int appLock = tick.IndexOf("_lockLifecycle.MarkAppLockSucceeded()", StringComparison.Ordinal);
            int lockWorkstation = tick.IndexOf("NativeMethods.LockWorkStation()", StringComparison.Ordinal);
            Assert(appLock >= 0 && lockWorkstation >= 0 && appLock < lockWorkstation, "关闭锁屏时只锁微信/QQ，必须在调用锁屏之前返回");
            int nothingSent = tick.IndexOf("appLocks == 0", StringComparison.Ordinal);
            Assert(nothingSent >= 0 && nothingSent < appLock, "一个快捷键都没按出去时不能进入“等你回来”，要继续监控");
            int recheck = tick.IndexOf("UserReturnPolicy.InputOccurredAfter(shortcutsFinishedUtc", StringComparison.Ordinal);
            Assert(recheck >= 0 && recheck < lockWorkstation, "按完快捷键后、锁屏前要再确认用户没有回来");
            Pass();
        }

        private static void ParsesPreShortcutMapping()
        {
            LockShortcutMapping mapping;
            Assert(LockShortcutMapping.TryParseConfigValue(" Ctrl + Alt + W > ctrl+l | 锁定微信 ", out mapping), "应能解析带前置快捷键的映射");
            Assert(mapping.PreShortcut == "Ctrl+Alt+W" && mapping.Shortcut == "Ctrl+L" && mapping.Note == "锁定微信",
                "前置快捷键、快捷键、备注应分别解析并规范化");
            Assert(mapping.ToConfigValue() == "Ctrl+Alt+W>Ctrl+L|锁定微信", "保存格式应为 前置快捷键>快捷键|备注");
            Assert(mapping.ToDisplayText() == "Ctrl+Alt+W → Ctrl+L", "列表里应显示 前置快捷键 → 快捷键");

            Assert(LockShortcutMapping.TryParseConfigValue("Alt+Shift+P|锁定QQ", out mapping), "没有前置快捷键的旧格式应继续可用");
            Assert(mapping.PreShortcut == "" && mapping.Shortcut == "Alt+Shift+P", "旧格式的前置快捷键应为空");
            Assert(mapping.ToConfigValue() == "Alt+Shift+P|锁定QQ", "没有前置快捷键时保存格式不变");

            Assert(!LockShortcutMapping.TryParseConfigValue("Ctrl+Alt+W>|锁定微信", out mapping), "缺少快捷键时应拒绝");
            Assert(!LockShortcutMapping.TryParseConfigValue("Ctrl+Alt>Ctrl+L|锁定微信", out mapping), "前置快捷键只有修饰键时应拒绝");

            Assert(LockShortcutMapping.TryParseConfigValue("Ctrl+Alt+W>Ctrl+L|锁定微信|target=Weixin.exe", out mapping), "应能解析带目标程序的映射");
            Assert(mapping.TargetProcess == "Weixin" && mapping.Note == "锁定微信", "目标程序应去掉 .exe，备注不受影响");
            Assert(mapping.ToConfigValue() == "Ctrl+Alt+W>Ctrl+L|锁定微信|target=Weixin", "保存格式应为 前置快捷键>快捷键|备注|target=程序名");
            Assert(LockShortcutMapping.TryParseConfigValue("Alt+Shift+P||target=QQ", out mapping), "备注为空时也应能解析目标程序");
            Assert(mapping.TargetProcess == "QQ" && mapping.Note == "", "备注为空、目标程序为 QQ");
            Assert(mapping.ToConfigValue() == "Alt+Shift+P||target=QQ", "备注为空时保存格式应保持可解析");
            Pass();
        }

        private static void ChoosesMainWindowFromRealWeChatAndQqLayout()
        {
            var weChat = new List<TargetWindowCandidate>
            {
                Window(0x5A1BFE, false, false, false, 0x06000000, 0x000800A8, "Weixin", "Qt51514QWindowToolSaveBits", 246, 70),
                Window(0x90940, false, false, false, unchecked((int)0x86C70000), 0x00000100, "微信", "Qt51514QWindowIcon", 1135, 974),
                Window(0x851384, false, false, false, unchecked((int)0x86CF0000), 0x00000100, "Weixin", "Qt51514QWindowIcon", 176, 199),
                Window(0x509C4, false, false, false, 0x04C00000, 0x00000100, "WxTrayIconMessageWindow", "Qt51514WxTrayIconMessageWindowClass", 1920, 1023),
                Window(0x5017B6, false, false, true, unchecked((int)0x8C000000), 0, "Default IME", "IME", 0, 0)
            };
            TargetWindowCandidate weChatMain = TargetWindowPolicy.ChooseMainWindow(weChat);
            Assert(weChatMain != null && weChatMain.Handle == new IntPtr(0x90940), "微信缩在托盘时应选中隐藏的“微信”主窗口，而不是托盘消息窗口或小弹窗");

            var qq = new List<TargetWindowCandidate>
            {
                Window(0x30198, true, false, false, 0x14C70000, 0x00200100, "QQ", "Chrome_WidgetWin_1", 1264, 996),
                Window(0x1961B6A, false, false, false, 0x04C70000, 0x00200100, "QQ", "Chrome_WidgetWin_1", 800, 600),
                Window(0x11C1916, false, false, false, 0x04020000, 0x00200000, "QQ", "Chrome_WidgetWin_1", 32, 39),
                Window(0x81CE2, false, false, false, 0x04020000, 0x00200000, "QQ", "Chrome_WidgetWin_1", 350, 510),
                Window(0x511DD8, false, false, false, unchecked((int)0x84000000), 0, "GDI+ Window (QQ.exe)", "GDI+ Hook Window Class", 1, 1)
            };
            TargetWindowCandidate qqMain = TargetWindowPolicy.ChooseMainWindow(qq);
            Assert(qqMain != null && qqMain.Handle == new IntPtr(0x30198), "QQ 主窗口开着时应优先选中可见的主窗口");

            qq[0] = Window(0x30198, false, false, false, 0x04C70000, 0x00200100, "QQ", "Chrome_WidgetWin_1", 1264, 996);
            qqMain = TargetWindowPolicy.ChooseMainWindow(qq);
            Assert(qqMain != null && qqMain.Handle == new IntPtr(0x30198), "QQ 缩到托盘时应选中最大的隐藏主窗口");

            qq[0] = Window(0x30198, true, true, false, 0x34C70000, 0x00200100, "QQ", "Chrome_WidgetWin_1", 160, 28);
            qqMain = TargetWindowPolicy.ChooseMainWindow(qq);
            Assert(qqMain != null && qqMain.Handle == new IntPtr(0x30198), "QQ 最小化时应选中最小化的主窗口并还原");
            Pass();
        }

        private static void ShortcutRunnerChecksFrontBeforePreShortcut()
        {
            string source = ReadSource("LockShortcuts.cs");
            string body = ExtractMethodBody(source, "public static int TriggerAll(IEnumerable<LockShortcutMapping> mappings, int preDelayMilliseconds, Action<string> info, Action<string> warn)");
            int front = body.IndexOf("LockShortcutTarget.IsFrontMost(pids)", StringComparison.Ordinal);
            int pre = body.IndexOf("SendShortcut(mapping.PreShortcut)", StringComparison.Ordinal);
            int bring = body.IndexOf("LockShortcutTarget.BringToFront(pids)", StringComparison.Ordinal);
            int main = body.IndexOf("SendShortcut(mapping.Shortcut)", StringComparison.Ordinal);
            Assert(front >= 0 && pre > front && bring > pre && main > bring,
                "应先判断目标是否已在最前面，再按前置快捷键，再把目标切到最前面，最后按快捷键");
            int refresh = body.IndexOf("pids = LockShortcutTarget.FindProcessIds(target)", pre, StringComparison.Ordinal);
            Assert(refresh > pre && refresh < bring, "按完前置快捷键后要重新查目标进程，前置键可能拉起新进程");
            Pass();
        }

        private static void CaptureStateRecordsHotkeysOwnedByOtherPrograms()
        {
            var state = new ShortcutCaptureState();
            state.Reset();
            Assert(state.Process(System.Windows.Forms.Keys.LControlKey, true) == ShortcutCaptureAction.Swallow, "录制时按下 Ctrl 应先拦下");
            Assert(state.Process(System.Windows.Forms.Keys.LMenu, true) == ShortcutCaptureAction.Swallow, "录制时按下 Alt 应先拦下");
            Assert(state.Process(System.Windows.Forms.Keys.W, true) == ShortcutCaptureAction.Captured && state.Captured == "Ctrl+Alt+W",
                "微信占用的 Ctrl+Alt+W 应能录下来");
            Assert(state.Process(System.Windows.Forms.Keys.W, false) == ShortcutCaptureAction.Swallow, "松开按键也应拦下，不漏给微信");
            state.Process(System.Windows.Forms.Keys.LMenu, false);
            state.Process(System.Windows.Forms.Keys.LControlKey, false);

            Assert(state.Process(System.Windows.Forms.Keys.RMenu, true) == ShortcutCaptureAction.Swallow, "右 Alt 也应算 Alt");
            state.Process(System.Windows.Forms.Keys.RShiftKey, true);
            Assert(state.Process(System.Windows.Forms.Keys.P, true) == ShortcutCaptureAction.Captured && state.Captured == "Alt+Shift+P",
                "QQ 占用的 Alt+Shift+P 应能录下来");
            state.Process(System.Windows.Forms.Keys.P, false);
            state.Process(System.Windows.Forms.Keys.RShiftKey, false);
            state.Process(System.Windows.Forms.Keys.RMenu, false);

            state.Process(System.Windows.Forms.Keys.LWin, true);
            Assert(state.Process(System.Windows.Forms.Keys.D, true) == ShortcutCaptureAction.Captured && state.Captured == "Win+D", "Win 组合键应能录下来");
            state.Process(System.Windows.Forms.Keys.D, false);
            state.Process(System.Windows.Forms.Keys.LWin, false);

            Assert(state.Process(System.Windows.Forms.Keys.Back, true) == ShortcutCaptureAction.Cleared, "单独按 Backspace 应清空");
            Assert(state.Process(System.Windows.Forms.Keys.Escape, true) == ShortcutCaptureAction.Cancelled, "单独按 Esc 应退出录制，键盘不会被一直拦着");
            Assert(state.Process(System.Windows.Forms.Keys.Tab, true) == ShortcutCaptureAction.PassThrough, "单独按 Tab 应放行，方便切到下一个输入框");
            Assert(state.Process(System.Windows.Forms.Keys.Tab, false) == ShortcutCaptureAction.PassThrough, "Tab 松开也应放行");
            Pass();
        }

        private static TargetWindowCandidate Window(long handle, bool visible, bool iconic, bool hasOwner, int style, int exStyle, string title, string className, int width, int height)
        {
            return new TargetWindowCandidate
            {
                Handle = new IntPtr(handle),
                Visible = visible,
                Iconic = iconic,
                HasOwner = hasOwner,
                ToolWindow = (exStyle & 0x00000080) != 0,
                Minimizable = (style & 0x00020000) != 0,
                Title = title,
                ClassName = className,
                Width = width,
                Height = height
            };
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
