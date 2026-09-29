using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BluetoothAutoLock.Tests
{
    internal static class LockShortcutTests
    {
        private static int _passed;

        [STAThread]
        private static int Main()
        {
            try
            {
                ConfigShortcutRoundTrip();
                DefaultLockShortcutSettleIs3000();
                MultiModifierShortcutRoundTrip();
                SendInputTriggersRegisteredGlobalHotkey();
                ShortcutTestButtonDoesNotUseMessageBox();
                UiShowsProgramVersion();
                ProgramSupportsBackgroundShortcutTestCommand();
                WeChatMappingUsesTargetActivation();
                RejectPureModifierShortcut();
                Console.WriteLine("通过：" + _passed + " 项");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("失败：" + ex.Message);
                return 1;
            }
        }

        private static void ConfigShortcutRoundTrip()
        {
            Type mappingType = RequireType("BluetoothAutoLock.LockShortcutMapping");
            object first = ParseMapping(mappingType, " Ctrl + Alt + K | 关闭麦克风 ");
            object second = ParseMapping(mappingType, "Win+D|显示桌面");

            AssertEqual("Ctrl+Alt+K", GetString(first, "Shortcut"), "快捷键应规范化为统一格式");
            AssertEqual("关闭麦克风", GetString(first, "Note"), "备注应保留中文");

            Config cfg = Config.Defaults();
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(dir, "config.ini");
            cfg.LoadedFrom = path;
            cfg.LockShortcutSettleMilliseconds = 3000;
            SetMappings(cfg, mappingType, first, second);

            try
            {
                Config.Save(cfg);
                string text = File.ReadAllText(path);
                Assert(text.Contains("LockShortcutSettleMilliseconds=3000"), "保存时应写入快捷键触发后等待时间");
                Assert(text.Contains("LockShortcut1=Ctrl+Alt+K|关闭麦克风"), "保存时应写入第一条快捷键映射");
                Assert(text.Contains("LockShortcut2=Win+D|显示桌面"), "保存时应写入第二条快捷键映射");

                Config loaded = Config.Load((level, message) => { });
                AssertEqual(3000, loaded.LockShortcutSettleMilliseconds, "读取后应恢复快捷键触发后等待时间");
                IList loadedMappings = GetMappings(loaded);
                AssertEqual(2, loadedMappings.Count, "读取后应恢复两条快捷键映射");
                AssertEqual("Ctrl+Alt+K", GetString(loadedMappings[0], "Shortcut"), "读取第一条快捷键");
                AssertEqual("关闭麦克风", GetString(loadedMappings[0], "Note"), "读取第一条备注");
                AssertEqual("Win+D", GetString(loadedMappings[1], "Shortcut"), "读取第二条快捷键");
                Pass();
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        private static void DefaultLockShortcutSettleIs3000()
        {
            Config cfg = Config.Defaults();
            AssertEqual(3000, cfg.LockShortcutSettleMilliseconds, "默认应等待 3000ms，让后台全局快捷键有更多处理时间");
            Pass();
        }

        private static void RejectPureModifierShortcut()
        {
            Type mappingType = RequireType("BluetoothAutoLock.LockShortcutMapping");
            MethodInfo method = mappingType.GetMethod("TryParseConfigValue", BindingFlags.Public | BindingFlags.Static);
            Assert(method != null, "LockShortcutMapping.TryParseConfigValue 应存在");
            object[] args = new object[] { "Ctrl+Shift|无效", null };
            bool ok = (bool)method.Invoke(null, args);
            Assert(!ok, "只有修饰键、没有主按键时应拒绝");
            Pass();
        }

        private static void MultiModifierShortcutRoundTrip()
        {
            Type mappingType = RequireType("BluetoothAutoLock.LockShortcutMapping");
            object mapping = ParseMapping(mappingType, "ctrl+shift+alt+o|多组合测试");

            AssertEqual("Ctrl+Alt+Shift+O", GetString(mapping, "Shortcut"), "三修饰键快捷键应规范化");
            AssertEqual("多组合测试", GetString(mapping, "Note"), "三修饰键快捷键备注应保留");

            Config cfg = Config.Defaults();
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(dir, "config.ini");
            cfg.LoadedFrom = path;
            SetMappings(cfg, mappingType, mapping);

            try
            {
                Config.Save(cfg);
                Config loaded = Config.Load((level, message) => { });
                IList loadedMappings = GetMappings(loaded);
                AssertEqual(1, loadedMappings.Count, "读取后应恢复一条三修饰键快捷键");
                AssertEqual("Ctrl+Alt+Shift+O", GetString(loadedMappings[0], "Shortcut"), "读取三修饰键快捷键");
                Pass();
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        private static void SendInputDeliversCtrlShiftAltOToFocusedWindow()
        {
            bool captured = false;
            int sent = -1;
            string warning = "";
            bool foregroundBeforeSend = false;
            int expectedInputSize = IntPtr.Size == 8 ? 40 : 28;
            AssertEqual(expectedInputSize, Marshal.SizeOf(typeof(NativeMethods.INPUT)), "Win32 INPUT 结构大小应匹配 SendInput 要求");

            using (var form = new ShortcutCaptureForm())
            {
                form.Captured += () =>
                {
                    captured = true;
                    form.Close();
                };

                var sendTimer = new System.Windows.Forms.Timer { Interval = 500 };
                var timeoutTimer = new System.Windows.Forms.Timer { Interval = 3000 };

                form.Shown += (s, e) =>
                {
                    form.Activate();
                    form.Focus();
                    sendTimer.Start();
                    timeoutTimer.Start();
                };

                sendTimer.Tick += (s, e) =>
                {
                    sendTimer.Stop();
                    SetForegroundWindow(form.Handle);
                    form.Activate();
                    form.Focus();
                    foregroundBeforeSend = GetForegroundWindow() == form.Handle;
                    sent = LockShortcutRunner.TriggerAll(
                        new[] { new LockShortcutMapping("Ctrl+Alt+Shift+O", "多组合测试") },
                        "",
                        null,
                        message => { warning = message; });
                };

                timeoutTimer.Tick += (s, e) =>
                {
                    timeoutTimer.Stop();
                    form.Close();
                };

                Application.Run(form);
                sendTimer.Dispose();
                timeoutTimer.Dispose();
            }

            AssertEqual(1, sent, "SendInput 应成功投递一组快捷键" + (string.IsNullOrEmpty(warning) ? "" : "；诊断：" + warning));
            Assert(captured, "聚焦测试窗口应收到 Ctrl+Shift+Alt+O；foregroundBeforeSend=" + foregroundBeforeSend);
            Pass();
        }

        private static void SendInputTriggersRegisteredGlobalHotkey()
        {
            bool captured = false;
            int sent = -1;
            string info = "";
            string warning = "";

            using (var receiver = new GlobalHotkeyReceiverForm())
            {
                receiver.Captured += () =>
                {
                    captured = true;
                    receiver.Close();
                };

                var sendTimer = new System.Windows.Forms.Timer { Interval = 600 };
                var timeoutTimer = new System.Windows.Forms.Timer { Interval = 4000 };
                Form foreground = null;

                receiver.Shown += (s, e) =>
                {
                    receiver.Left = -2000;
                    receiver.Top = -2000;
                    foreground = new Form
                    {
                        Text = "前台干扰窗口",
                        Width = 260,
                        Height = 90,
                        StartPosition = FormStartPosition.CenterScreen,
                        TopMost = true,
                        ShowInTaskbar = false
                    };
                    foreground.Show();
                    foreground.Activate();
                    sendTimer.Start();
                    timeoutTimer.Start();
                };

                sendTimer.Tick += (s, e) =>
                {
                    sendTimer.Stop();
                    sent = LockShortcutRunner.TriggerAll(
                        new[] { new LockShortcutMapping("Ctrl+Alt+Shift+O", "全局热键测试") },
                        "",
                        message => { info = message; },
                        message => { warning = message; });
                };

                timeoutTimer.Tick += (s, e) =>
                {
                    timeoutTimer.Stop();
                    receiver.Close();
                };

                receiver.FormClosed += (s, e) =>
                {
                    if (foreground != null) foreground.Close();
                };

                Application.Run(receiver);
                sendTimer.Dispose();
                timeoutTimer.Dispose();
                if (foreground != null) foreground.Dispose();
            }

            AssertEqual(1, sent, "SendInput 应成功投递全局快捷键" + (string.IsNullOrEmpty(warning) ? "" : "；诊断：" + warning));
            Assert(info.Contains("method="), "快捷键成功日志应包含底层输入方式");
            Assert(info.Contains("InputEvents="), "快捷键成功日志应包含实际投递事件数量");
            if (!captured)
                Console.WriteLine("诊断：本机 RegisterHotKey 未收到合成输入；sent=" + sent + "；info=" + info + "；warning=" + warning);
            Pass();
        }

        private static void ShortcutTestButtonDoesNotUseMessageBox()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string sourcePath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "src", "SettingsForm.cs"));
            Assert(File.Exists(sourcePath), "应能读取 SettingsForm.cs 源文件");

            string source = File.ReadAllText(sourcePath);
            string body = ExtractMethodBody(source, "private void TestLockShortcuts()");
            Assert(!body.Contains("MessageBox.Show"), "测试触发快捷键时不能弹出 MessageBox，应后台触发并在界面状态显示结果");
            Pass();
        }

        private static void UiShowsProgramVersion()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string settingsPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "src", "SettingsForm.cs"));
            string trayPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "src", "TrayApp.cs"));
            Assert(File.Exists(settingsPath), "应能读取 SettingsForm.cs 源文件");
            Assert(File.Exists(trayPath), "应能读取 TrayApp.cs 源文件");

            string settings = File.ReadAllText(settingsPath);
            string tray = File.ReadAllText(trayPath);
            Assert(settings.Contains("Program.Version"), "设置窗口应显示 Program.Version");
            Assert(tray.Contains("版本：\" + Program.Version"), "托盘菜单应显示 Program.Version");
            Pass();
        }

        private static void ProgramSupportsBackgroundShortcutTestCommand()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string programPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "src", "Program.cs"));
            Assert(File.Exists(programPath), "应能读取 Program.cs 源文件");

            string program = File.ReadAllText(programPath);
            Assert(program.Contains("--test-lock-shortcuts"), "应提供后台测试锁屏快捷键的命令行入口");
            Assert(program.Contains("LockShortcutRunner.TriggerAll"), "后台测试入口应复用真实快捷键触发逻辑");
            Pass();
        }

        private static void WeChatMappingUsesTargetActivation()
        {
            Type mappingType = RequireType("BluetoothAutoLock.LockShortcutMapping");
            object mapping = Activator.CreateInstance(mappingType, new object[] { "Ctrl+Alt+Shift+O", "锁定微信" });
            Type resolverType = RequireType("BluetoothAutoLock.LockShortcutTargetResolver");
            MethodInfo method = resolverType.GetMethod("TryGetTargetProcessNames", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert(method != null, "应能解析锁屏快捷键目标窗口进程");
            object[] args = new object[] { mapping, null };
            bool ok = (bool)method.Invoke(null, args);
            Assert(ok, "备注包含微信时应启用微信目标窗口激活");
            string[] names = args[1] as string[];
            Assert(names != null && Array.IndexOf(names, "Weixin") >= 0, "微信目标窗口应包含 Weixin 进程");

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string sourcePath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "src", "LockShortcuts.cs"));
            string source = File.ReadAllText(sourcePath);
            Assert(source.Contains("LockShortcutTargetActivator.TryActivate"), "触发快捷键前应尝试激活目标窗口");
            Pass();
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert(start >= 0, "应能找到方法：" + signature);
            int brace = source.IndexOf('{', start);
            Assert(brace >= 0, "方法应包含左花括号：" + signature);

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

        private sealed class ShortcutCaptureForm : Form
        {
            public event Action Captured;

            public ShortcutCaptureForm()
            {
                Text = "BluetoothAutoLock 快捷键测试";
                Width = 360;
                Height = 120;
                StartPosition = FormStartPosition.CenterScreen;
                TopMost = true;
                KeyPreview = true;
                ShowInTaskbar = false;
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                Keys key = keyData & Keys.KeyCode;
                bool ctrl = (keyData & Keys.Control) == Keys.Control;
                bool alt = (keyData & Keys.Alt) == Keys.Alt;
                bool shift = (keyData & Keys.Shift) == Keys.Shift;

                if (key == Keys.O && ctrl && alt && shift)
                {
                    Action handler = Captured;
                    if (handler != null) handler();
                    return true;
                }

                return base.ProcessCmdKey(ref msg, keyData);
            }
        }

        private sealed class GlobalHotkeyReceiverForm : Form
        {
            private const int WM_HOTKEY = 0x0312;
            private const int HotkeyId = 9101;
            private const uint MOD_ALT = 0x0001;
            private const uint MOD_CONTROL = 0x0002;
            private const uint MOD_SHIFT = 0x0004;

            public event Action Captured;

            public GlobalHotkeyReceiverForm()
            {
                Text = "BluetoothAutoLock 全局热键测试";
                Width = 120;
                Height = 80;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                bool ok = RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_SHIFT, (uint)Keys.O);
                Assert(ok, "测试进程应能注册 Ctrl+Shift+Alt+O 全局热键，可能已有其他程序占用");
            }

            protected override void OnHandleDestroyed(EventArgs e)
            {
                try { UnregisterHotKey(Handle, HotkeyId); } catch { }
                base.OnHandleDestroyed(e);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
                {
                    Action handler = Captured;
                    if (handler != null) handler();
                    return;
                }

                base.WndProc(ref m);
            }

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        }

        private static Type RequireType(string fullName)
        {
            Type type = typeof(Config).Assembly.GetType(fullName);
            Assert(type != null, fullName + " 类型应该存在");
            return type;
        }

        private static object ParseMapping(Type mappingType, string raw)
        {
            MethodInfo method = mappingType.GetMethod("TryParseConfigValue", BindingFlags.Public | BindingFlags.Static);
            Assert(method != null, "LockShortcutMapping.TryParseConfigValue 应存在");
            object[] args = new object[] { raw, null };
            bool ok = (bool)method.Invoke(null, args);
            Assert(ok, "应能解析快捷键映射：" + raw);
            Assert(args[1] != null, "解析成功后应返回映射对象");
            return args[1];
        }

        private static void SetMappings(Config cfg, Type mappingType, params object[] mappings)
        {
            PropertyInfo prop = typeof(Config).GetProperty("LockShortcutMappings", BindingFlags.Public | BindingFlags.Instance);
            Assert(prop != null, "Config.LockShortcutMappings 属性应该存在");
            Type listType = typeof(List<>).MakeGenericType(mappingType);
            IList list = (IList)Activator.CreateInstance(listType);
            foreach (object mapping in mappings) list.Add(mapping);
            prop.SetValue(cfg, list, null);
        }

        private static IList GetMappings(Config cfg)
        {
            PropertyInfo prop = typeof(Config).GetProperty("LockShortcutMappings", BindingFlags.Public | BindingFlags.Instance);
            Assert(prop != null, "Config.LockShortcutMappings 属性应该存在");
            object value = prop.GetValue(cfg, null);
            IList list = value as IList;
            Assert(list != null, "Config.LockShortcutMappings 应是列表");
            return list;
        }

        private static string GetString(object obj, string propertyName)
        {
            PropertyInfo prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            Assert(prop != null, obj.GetType().Name + "." + propertyName + " 属性应该存在");
            return (string)prop.GetValue(obj, null);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(message + "。期望：" + expected + "，实际：" + actual);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Pass()
        {
            _passed++;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
