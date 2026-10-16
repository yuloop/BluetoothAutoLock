using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BluetoothAutoLock
{
    internal sealed class LockShortcutMapping
    {
        public string Shortcut { get; set; }
        public string PreShortcut { get; set; }
        public string Note { get; set; }
        public string TargetProcess { get; set; }

        public LockShortcutMapping()
            : this("", "")
        {
        }

        public LockShortcutMapping(string shortcut, string note)
            : this(shortcut, note, "")
        {
        }

        public LockShortcutMapping(string shortcut, string note, string preShortcut)
            : this(shortcut, note, preShortcut, "")
        {
        }

        public LockShortcutMapping(string shortcut, string note, string preShortcut, string targetProcess)
        {
            Shortcut = shortcut ?? "";
            PreShortcut = preShortcut ?? "";
            Note = CleanNote(note);
            TargetProcess = CleanTargetProcess(targetProcess);
        }

        public string ToConfigValue()
        {
            string keys = string.IsNullOrEmpty(PreShortcut) ? (Shortcut ?? "") : (PreShortcut + ">" + (Shortcut ?? ""));
            string note = CleanNote(Note);
            string target = CleanTargetProcess(TargetProcess);
            if (target.Length > 0) return keys + "|" + note + "|" + TargetPrefix + target;
            if (string.IsNullOrEmpty(note)) return keys;
            return keys + "|" + note;
        }

        public string ToDisplayText()
        {
            if (string.IsNullOrEmpty(PreShortcut)) return Shortcut ?? "";
            return PreShortcut + " → " + (Shortcut ?? "");
        }

        public static bool TryParseConfigValue(string value, out LockShortcutMapping mapping)
        {
            mapping = null;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string shortcutText = value;
            string note = "";
            string target = "";
            int sep = value.IndexOf('|');
            if (sep >= 0)
            {
                shortcutText = value.Substring(0, sep);
                note = value.Substring(sep + 1);
                int targetSep = note.LastIndexOf("|" + TargetPrefix, StringComparison.OrdinalIgnoreCase);
                if (targetSep >= 0)
                {
                    target = note.Substring(targetSep + 1 + TargetPrefix.Length);
                    note = note.Substring(0, targetSep);
                }
                else if (note.TrimStart().StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    target = note.TrimStart().Substring(TargetPrefix.Length);
                    note = "";
                }
            }

            string preShortcut = "";
            int arrow = shortcutText.IndexOf('>');
            if (arrow >= 0)
            {
                string preText = shortcutText.Substring(0, arrow);
                shortcutText = shortcutText.Substring(arrow + 1);
                if (!string.IsNullOrWhiteSpace(preText) && !TryNormalizeShortcutText(preText, out preShortcut)) return false;
            }

            string shortcut;
            if (!TryNormalizeShortcutText(shortcutText, out shortcut)) return false;
            mapping = new LockShortcutMapping(shortcut, note, preShortcut, target);
            return true;
        }

        private const string TargetPrefix = "target=";

        public static string CleanTargetProcess(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string clean = name.Replace("|", "").Replace(">", "").Trim();
            if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(0, clean.Length - 4).Trim();
            return clean;
        }

        public static bool TryNormalizeShortcutText(string text, out string shortcut)
        {
            ShortcutParts parts;
            if (!TryParseShortcutText(text, out parts))
            {
                shortcut = "";
                return false;
            }

            shortcut = parts.ToCanonicalText();
            return true;
        }

        public static bool TryFromKeyEvent(Keys keyCode, Keys modifiers, bool winDown, out string shortcut)
        {
            shortcut = "";
            if (IsModifierKey(keyCode)) return false;
            if (keyCode == Keys.None) return false;

            var parts = new ShortcutParts
            {
                Ctrl = (modifiers & Keys.Control) == Keys.Control,
                Alt = (modifiers & Keys.Alt) == Keys.Alt,
                Shift = (modifiers & Keys.Shift) == Keys.Shift,
                Win = winDown,
                Key = keyCode & Keys.KeyCode
            };

            if (IsModifierKey(parts.Key)) return false;
            shortcut = parts.ToCanonicalText();
            return !string.IsNullOrEmpty(shortcut);
        }

        public static string CleanNote(string note)
        {
            if (string.IsNullOrEmpty(note)) return "";
            return note.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        internal static bool TryGetShortcutParts(string text, out bool ctrl, out bool alt, out bool shift, out bool win, out Keys key)
        {
            ShortcutParts parts;
            bool ok = TryParseShortcutText(text, out parts);
            ctrl = parts.Ctrl;
            alt = parts.Alt;
            shift = parts.Shift;
            win = parts.Win;
            key = parts.Key;
            return ok;
        }

        private static bool TryParseShortcutText(string text, out ShortcutParts parts)
        {
            parts = new ShortcutParts();
            if (string.IsNullOrWhiteSpace(text)) return false;

            string[] tokens = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return false;

            foreach (string raw in tokens)
            {
                string token = (raw ?? "").Trim();
                if (token.Length == 0) continue;

                if (IsToken(token, "ctrl") || IsToken(token, "control"))
                {
                    parts.Ctrl = true;
                    continue;
                }
                if (IsToken(token, "alt") || IsToken(token, "menu"))
                {
                    parts.Alt = true;
                    continue;
                }
                if (IsToken(token, "shift"))
                {
                    parts.Shift = true;
                    continue;
                }
                if (IsToken(token, "win") || IsToken(token, "windows") || IsToken(token, "lwin") || IsToken(token, "rwin"))
                {
                    parts.Win = true;
                    continue;
                }

                Keys key;
                if (!TryParseMainKey(token, out key)) return false;
                if (parts.Key != Keys.None) return false;
                parts.Key = key;
            }

            return parts.Key != Keys.None && !IsModifierKey(parts.Key);
        }

        private static bool TryParseMainKey(string token, out Keys key)
        {
            key = Keys.None;
            if (string.IsNullOrWhiteSpace(token)) return false;

            string t = token.Trim();
            if (t.Length == 1)
            {
                char c = char.ToUpperInvariant(t[0]);
                if (c >= 'A' && c <= 'Z')
                {
                    key = (Keys)((int)Keys.A + (c - 'A'));
                    return true;
                }
                if (c >= '0' && c <= '9')
                {
                    key = (Keys)((int)Keys.D0 + (c - '0'));
                    return true;
                }
            }

            string normalized = t.Replace(" ", "").Replace("_", "");
            if (IsToken(normalized, "esc")) normalized = "Escape";
            else if (IsToken(normalized, "del")) normalized = "Delete";
            else if (IsToken(normalized, "pgup")) normalized = "PageUp";
            else if (IsToken(normalized, "pgdn")) normalized = "PageDown";
            else if (IsToken(normalized, "ins")) normalized = "Insert";
            else if (IsToken(normalized, "spacebar")) normalized = "Space";
            else if (IsToken(normalized, "return")) normalized = "Enter";
            else if (IsToken(normalized, "backspace")) normalized = "Back";
            else if (IsToken(normalized, "back")) normalized = "Back";

            Keys parsed;
            if (!Enum.TryParse(normalized, true, out parsed)) return false;
            parsed = parsed & Keys.KeyCode;
            if (parsed == Keys.None || IsModifierKey(parsed)) return false;
            key = parsed;
            return true;
        }

        private static bool IsToken(string actual, string expected)
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModifierKey(Keys key)
        {
            Keys k = key & Keys.KeyCode;
            return k == Keys.ControlKey ||
                   k == Keys.ShiftKey ||
                   k == Keys.Menu ||
                   k == Keys.LControlKey ||
                   k == Keys.RControlKey ||
                   k == Keys.LShiftKey ||
                   k == Keys.RShiftKey ||
                   k == Keys.LMenu ||
                   k == Keys.RMenu ||
                   k == Keys.LWin ||
                   k == Keys.RWin;
        }

        private struct ShortcutParts
        {
            public bool Ctrl;
            public bool Alt;
            public bool Shift;
            public bool Win;
            public Keys Key;

            public string ToCanonicalText()
            {
                var tokens = new List<string>();
                if (Ctrl) tokens.Add("Ctrl");
                if (Alt) tokens.Add("Alt");
                if (Shift) tokens.Add("Shift");
                if (Win) tokens.Add("Win");
                tokens.Add(KeyToText(Key));
                return string.Join("+", tokens.ToArray());
            }
        }

        private static string KeyToText(Keys key)
        {
            Keys k = key & Keys.KeyCode;
            if (k >= Keys.A && k <= Keys.Z) return ((char)('A' + (k - Keys.A))).ToString();
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num" + ((int)(k - Keys.NumPad0)).ToString();

            switch (k)
            {
                case Keys.Escape: return "Esc";
                case Keys.Back: return "Backspace";
                case Keys.Space: return "Space";
                case Keys.Return: return "Enter";
                case Keys.Next: return "PageDown";
                case Keys.Prior: return "PageUp";
                default: return k.ToString();
            }
        }
    }

    internal enum ShortcutCaptureAction
    {
        PassThrough,
        Swallow,
        Captured,
        Cleared,
        Cancelled
    }

    internal sealed class ShortcutCaptureState
    {
        private bool _ctrl;
        private bool _alt;
        private bool _shift;
        private bool _win;

        public string Captured { get; private set; }

        public void Reset()
        {
            _ctrl = false;
            _alt = false;
            _shift = false;
            _win = false;
            Captured = "";
        }

        public ShortcutCaptureAction Process(Keys key, bool keyDown)
        {
            Keys k = key & Keys.KeyCode;
            if (k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey) { _ctrl = keyDown; return ShortcutCaptureAction.Swallow; }
            if (k == Keys.Menu || k == Keys.LMenu || k == Keys.RMenu) { _alt = keyDown; return ShortcutCaptureAction.Swallow; }
            if (k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey) { _shift = keyDown; return ShortcutCaptureAction.Swallow; }
            if (k == Keys.LWin || k == Keys.RWin) { _win = keyDown; return ShortcutCaptureAction.Swallow; }

            bool noModifiers = !_ctrl && !_alt && !_shift && !_win;
            if (noModifiers && k == Keys.Tab) return ShortcutCaptureAction.PassThrough;
            if (!keyDown) return ShortcutCaptureAction.Swallow;
            if (k == Keys.Escape)
            {
                Reset();
                return ShortcutCaptureAction.Cancelled;
            }
            if (noModifiers && (k == Keys.Back || k == Keys.Delete))
            {
                Captured = "";
                return ShortcutCaptureAction.Cleared;
            }

            Keys modifiers = Keys.None;
            if (_ctrl) modifiers |= Keys.Control;
            if (_alt) modifiers |= Keys.Alt;
            if (_shift) modifiers |= Keys.Shift;

            string shortcut;
            if (!LockShortcutMapping.TryFromKeyEvent(k, modifiers, _win, out shortcut)) return ShortcutCaptureAction.Swallow;
            Captured = shortcut;
            return ShortcutCaptureAction.Captured;
        }
    }

    internal sealed class ShortcutCaptureHook : IDisposable
    {
        private readonly NativeMethods.LowLevelKeyboardProc _callback;
        private readonly ShortcutCaptureState _state = new ShortcutCaptureState();
        private readonly Action<string> _captured;
        private readonly Action _cleared;
        private readonly Action _cancelled;
        private IntPtr _hook = IntPtr.Zero;

        public ShortcutCaptureHook(Action<string> captured, Action cleared, Action cancelled)
        {
            _captured = captured;
            _cleared = cleared;
            _cancelled = cancelled;
            _callback = HookCallback;
        }

        public bool Start()
        {
            _state.Reset();
            if (_hook != IntPtr.Zero) return true;
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, NativeMethods.GetModuleHandle(null), 0);
            return _hook != IntPtr.Zero;
        }

        public void Stop()
        {
            if (_hook == IntPtr.Zero) return;
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _state.Reset();
        }

        public void Dispose()
        {
            Stop();
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var data = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));
                    if ((data.flags & NativeMethods.LLKHF_INJECTED) == 0)
                    {
                        int message = wParam.ToInt32();
                        bool keyDown = message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN;
                        ShortcutCaptureAction action = _state.Process((Keys)data.vkCode, keyDown);
                        if (action == ShortcutCaptureAction.Captured && _captured != null) _captured(_state.Captured);
                        else if (action == ShortcutCaptureAction.Cleared && _cleared != null) _cleared();
                        else if (action == ShortcutCaptureAction.Cancelled && _cancelled != null) _cancelled();
                        if (action != ShortcutCaptureAction.PassThrough) return new IntPtr(1);
                    }
                }
                catch
                {
                }
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }

    internal sealed class RealInputDetector : IDisposable
    {
        private readonly NativeMethods.LowLevelKeyboardProc _keyboardCallback;
        private readonly NativeMethods.LowLevelKeyboardProc _mouseCallback;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private readonly Thread _thread;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private IntPtr _mouseHook = IntPtr.Zero;
        private volatile uint _threadId;
        private volatile bool _userInputSeen;
        private volatile bool _stopRequested;
        private int _disposed;

        public RealInputDetector()
        {
            _keyboardCallback = KeyboardCallback;
            _mouseCallback = MouseCallback;
            _thread = new Thread(Run) { IsBackground = true, Name = "RealInputDetector" };
            _thread.Start();
            _ready.Wait(2000);
        }

        public bool UserInputSeen
        {
            get { return _userInputSeen; }
        }

        public bool Watching
        {
            get { return _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero; }
        }

        private void Run()
        {
            NativeMethods.MSG message;
            NativeMethods.PeekMessage(out message, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);
            _threadId = NativeMethods.GetCurrentThreadId();
            if (!_stopRequested)
            {
                IntPtr module = NativeMethods.GetModuleHandle(null);
                _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardCallback, module, 0);
                _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseCallback, module, 0);
            }
            UIntPtr wakeTimer = NativeMethods.SetTimer(IntPtr.Zero, UIntPtr.Zero, 200, IntPtr.Zero);
            _ready.Set();
            try
            {
                while (!_stopRequested && NativeMethods.GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
                {
                }
            }
            finally
            {
                if (wakeTimer != UIntPtr.Zero) NativeMethods.KillTimer(IntPtr.Zero, wakeTimer);
                ReleaseHooks();
            }
        }

        private void ReleaseHooks()
        {
            IntPtr keyboard = Interlocked.Exchange(ref _keyboardHook, IntPtr.Zero);
            if (keyboard != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(keyboard);
            IntPtr mouse = Interlocked.Exchange(ref _mouseHook, IntPtr.Zero);
            if (mouse != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(mouse);
        }

        private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var data = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));
                    if ((data.flags & NativeMethods.LLKHF_INJECTED) == 0) _userInputSeen = true;
                }
                catch
                {
                }
            }
            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var data = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                    if ((data.flags & NativeMethods.LLMHF_INJECTED) == 0) _userInputSeen = true;
                }
                catch
                {
                }
            }
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stopRequested = true;
            _ready.Wait(5000);
            uint threadId = _threadId;
            if (threadId != 0)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero)) break;
                    Thread.Sleep(100);
                }
            }
            if (!_thread.Join(2000)) ReleaseHooks();
        }
    }

    // 锁屏快捷键的节拍参数。故意不依赖 Config：策略测试只编译 LockShortcuts.cs，不编译 Config.cs。
    internal sealed class LockShortcutOptions
    {
        // 按完前置快捷键后，至少等多久再按锁定快捷键。
        public int PreDelayMilliseconds;
        // 上面那段最少等待之后，最多再等多久让目标程序自己把窗口叫到最前面（0=不等）。
        public int PreWaitTimeoutMilliseconds;
        // 锁完之后是否再按一次前置快捷键，把窗口隐藏起来。
        public bool HideAfterwards;
        // 按完锁定快捷键之后，至少等多久才按隐藏键：锁定动作还没走完就按切换键，
        // 有些程序（168 实测 QQ）会锁不上。
        public int HideDelayMilliseconds;

        public static LockShortcutOptions Standard(int preDelayMilliseconds)
        {
            LockShortcutOptions options = new LockShortcutOptions();
            options.PreDelayMilliseconds = preDelayMilliseconds;
            options.PreWaitTimeoutMilliseconds = 2500;
            options.HideAfterwards = false;
            options.HideDelayMilliseconds = 800;
            return options;
        }
    }

    internal static class LockShortcutRunner
    {
        public static int TriggerAll(IEnumerable<LockShortcutMapping> mappings, LockShortcutOptions options, Action<string> info, Action<string> warn)
        {
            bool userInputSeen;
            return TriggerAll(mappings, options, info, warn, out userInputSeen);
        }

        public static int TriggerAll(IEnumerable<LockShortcutMapping> mappings, LockShortcutOptions options, Action<string> info, Action<string> warn, out bool userInputSeen)
        {
            var detector = new RealInputDetector();
            int sent;
            try
            {
                if (!detector.Watching && warn != null) warn("Real keyboard/mouse input detector could not start; only input after the shortcuts can cancel the workstation lock.");
                sent = TriggerAllCore(mappings, options, info, warn);
                Thread.Sleep(100);
            }
            finally
            {
                detector.Dispose();
            }
            userInputSeen = detector.UserInputSeen;
            return sent;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum) return minimum;
            return value > maximum ? maximum : value;
        }

        private static int TriggerAllCore(IEnumerable<LockShortcutMapping> mappings, LockShortcutOptions options, Action<string> info, Action<string> warn)
        {
            if (mappings == null) return 0;
            if (options == null) options = LockShortcutOptions.Standard(1000);

            int preDelay = Clamp(options.PreDelayMilliseconds, 0, 10000);
            int preWait = Clamp(options.PreWaitTimeoutMilliseconds, 0, 10000);
            int hideDelay = Clamp(options.HideDelayMilliseconds, 0, 10000);
            bool hideAfterwards = options.HideAfterwards;
            int sent = 0;
            foreach (LockShortcutMapping mapping in mappings)
            {
                if (mapping == null || string.IsNullOrWhiteSpace(mapping.Shortcut)) continue;

                string note = string.IsNullOrWhiteSpace(mapping.Note) ? "" : (" | 备注：" + mapping.Note);
                try
                {
                    string target = mapping.TargetProcess ?? "";
                    HashSet<uint> pids = target.Length == 0 ? null : LockShortcutTarget.FindProcessIds(target);
                    bool alreadyFront = pids != null && LockShortcutTarget.IsFrontMost(pids);
                    string preText = "";
                    if (!string.IsNullOrWhiteSpace(mapping.PreShortcut))
                    {
                        if (alreadyFront)
                        {
                            preText = " | pre=" + mapping.PreShortcut + " skipped(target already front-most)";
                        }
                        else
                        {
                            SendShortcut(mapping.PreShortcut);
                            preText = " | pre=" + mapping.PreShortcut + " then wait " + preDelay + "ms";
                            if (preDelay > 0) Thread.Sleep(preDelay);
                            if (pids != null) pids = LockShortcutTarget.FindProcessIds(target);

                            // 前置键是那个程序自己的全局热键，窗口出来有快有慢：168 实测等 500ms 时 QQ 的
                            // 窗口还没显示，程序只能靠 SW_SHOW 强显，紧接着的锁定热键 QQ 就不认了。
                            // 所以这一段改成“等它自己把窗口显示出来”，最多再等 preWait 毫秒；等不到才走
                            // 下面的强显兜底。窗口出来了照样要走 BringToFront 去拿焦点——显示出来不等于
                            // 拿到焦点（冒烟实测：假目标程序自己 Show 了，前台仍是别的程序）。
                            if (pids != null && preWait > 0)
                            {
                                int waited = LockShortcutTarget.WaitForVisibleWindow(pids, preWait);
                                preText += waited >= 0
                                    ? " | ready=visible@" + (preDelay + waited) + "ms"
                                    : " | ready=timeout(" + preWait + "ms), falling back to force-show";
                            }
                        }
                    }

                    if (pids != null && pids.Count == 0)
                    {
                        if (warn != null) warn("Lock shortcut skipped: " + mapping.ToDisplayText() + note + preText + " | target=" + target + " is not running");
                        continue;
                    }

                    string targetText = "";
                    if (pids != null)
                        targetText = " | target=" + target + (alreadyFront ? " | front=already" : LockShortcutTarget.BringToFront(pids));

                    string windowsBefore = LockShortcutTarget.DescribeVisibleWindows(pids);
                    ShortcutSendResult result = SendShortcut(mapping.Shortcut);
                    sent++;
                    if (info != null)
                    {
                        info("Lock shortcut triggered: " + mapping.Shortcut + note + preText + targetText +
                            " | foreground=" + DescribeForegroundProcess() +
                            " | windows=" + windowsBefore +
                            " | method=" + result.Method +
                            " | InputEvents=" + result.SentInputs + "/" + result.RequestedInputs +
                            " | lastError=" + result.LastWin32Error);
                    }
                    // 锁定键之后至少留 300ms 再走下一行；要隐藏窗口时按配置等更久，让程序把锁定动作
                    // 走完——QQ 的锁定比微信慢，按完就收窗口的话它根本没锁上（168 实测）。
                    int pauseMs = hideAfterwards && hideDelay > 300 ? hideDelay : 300;
                    Thread.Sleep(pauseMs);

                    if (hideAfterwards && !string.IsNullOrWhiteSpace(mapping.PreShortcut))
                    {
                        // 锁完再把窗口收起来：这一行的前置快捷键本身就是该程序“显示/隐藏主窗口”
                        // 的全局热键（微信 Ctrl+Alt+W、QQ Ctrl+Alt+Shift+Q）。刚才按它是把窗口叫出来
                        // 锁上，现在再按一次就把它隐藏掉。只按按键、不切前台、不碰锁定状态；
                        // 目标程序没运行（上面 continue）或快捷键没按成（抛异常）时都不会走到这里。
                        //
                        // 这是切换键，按下去是“藏”还是“叫出来”取决于窗口此刻的状态，所以只在确有
                        // 把握时才按：必须配了目标程序，而且此刻该进程的窗口确实在最前面（隐藏或
                        // 最小化的窗口不可能是前台窗口）。宁可不按——少按一次只是窗口留在桌面上，
                        // 按错一次却会把窗口重新叫出来，看起来像没锁。
                        // 隐藏失败不能算整个锁定失败，所以单独 catch。
                        if (pids == null)
                        {
                            if (info != null)
                                info("Lock shortcut hide skipped: no target process, cannot tell whether the window is in front | pre=" + mapping.PreShortcut + note);
                        }
                        else if (!LockShortcutTarget.IsFrontMost(pids))
                        {
                            if (warn != null)
                                warn("Lock shortcut hide skipped: " + target + " is not front-most " + pauseMs + "ms after " + mapping.Shortcut + "; not pressing " + mapping.PreShortcut + note);
                        }
                        else
                        {
                            try
                            {
                                // 隐藏之前再看一眼目标进程的可见窗口：只是为了留证据——锁定成功与否
                                // 没法直接问程序，但窗口有没有多出/变成锁屏窗口，看得出来。
                                string windowsAfter = LockShortcutTarget.DescribeVisibleWindows(pids);
                                ShortcutSendResult hideResult = SendShortcut(mapping.PreShortcut);
                                if (info != null)
                                {
                                    info("Lock shortcut window hidden: " + mapping.PreShortcut + note + targetText +
                                        " | waited=" + pauseMs + "ms" +
                                        " | windows=" + windowsBefore + " -> " + windowsAfter +
                                        " | method=" + hideResult.Method +
                                        " | InputEvents=" + hideResult.SentInputs + "/" + hideResult.RequestedInputs);
                                }
                            }
                            catch (Exception hideEx)
                            {
                                if (warn != null) warn("Lock shortcut hide failed: " + mapping.PreShortcut + note + targetText + " | " + hideEx.Message);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (warn != null) warn("Lock shortcut failed: " + mapping.ToDisplayText() + note + " | " + ex.Message);
                }
            }

            return sent;
        }

        private static string DescribeForegroundProcess()
        {
            try
            {
                IntPtr foreground = NativeMethods.GetForegroundWindow();
                if (foreground == IntPtr.Zero) return "none";
                uint pid;
                NativeMethods.GetWindowThreadProcessId(foreground, out pid);
                using (Process process = Process.GetProcessById((int)pid))
                    return process.ProcessName;
            }
            catch
            {
                return "unknown";
            }
        }

        internal static void SendKey(ushort virtualKey, bool keyUp)
        {
            NativeMethods.INPUT[] arr = { NativeMethods.CreateKeyboardInput(virtualKey, keyUp) };
            NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
        }

        private static ShortcutSendResult SendShortcut(string shortcut)
        {
            bool ctrl;
            bool alt;
            bool shift;
            bool win;
            Keys key;
            if (!LockShortcutMapping.TryGetShortcutParts(shortcut, out ctrl, out alt, out shift, out win, out key))
                throw new InvalidOperationException("快捷键格式无效");

            var down = new List<ushort>();
            if (ctrl) down.Add((ushort)Keys.ControlKey);
            if (alt) down.Add((ushort)Keys.Menu);
            if (shift) down.Add((ushort)Keys.ShiftKey);
            if (win) down.Add((ushort)Keys.LWin);
            down.Add((ushort)(key & Keys.KeyCode));

            var inputs = new List<NativeMethods.INPUT>();
            foreach (ushort vk in down)
                inputs.Add(NativeMethods.CreateKeyboardInput(vk, false));
            for (int i = down.Count - 1; i >= 0; i--)
                inputs.Add(NativeMethods.CreateKeyboardInput(down[i], true));

            NativeMethods.INPUT[] arr = inputs.ToArray();
            uint written = NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
            int lastError = written == arr.Length ? 0 : Marshal.GetLastWin32Error();
            if (written != arr.Length)
                throw new InvalidOperationException("SendInput 只发送了 " + written + "/" + arr.Length + " 个输入，lastError=" + lastError);

            return new ShortcutSendResult(arr.Length, written, lastError, "SendInputScanCode");
        }

        private sealed class ShortcutSendResult
        {
            public readonly int RequestedInputs;
            public readonly uint SentInputs;
            public readonly int LastWin32Error;
            public readonly string Method;

            public ShortcutSendResult(int requestedInputs, uint sentInputs, int lastWin32Error, string method)
            {
                RequestedInputs = requestedInputs;
                SentInputs = sentInputs;
                LastWin32Error = lastWin32Error;
                Method = method;
            }
        }
    }

    internal static class LockShortcutTarget
    {
        private const int ShowWaitMilliseconds = 2000;

        public static HashSet<uint> FindProcessIds(string processName)
        {
            var pids = new HashSet<uint>();
            if (string.IsNullOrWhiteSpace(processName)) return pids;
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                    pids.Add((uint)process.Id);
            }
            return pids;
        }

        public static bool IsFrontMost(HashSet<uint> pids)
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            if (!NativeMethods.IsWindowVisible(foreground) || NativeMethods.IsIconic(foreground)) return false;
            uint pid;
            NativeMethods.GetWindowThreadProcessId(foreground, out pid);
            return pids.Contains(pid);
        }

        public static string BringToFront(HashSet<uint> pids)
        {
            TargetWindowCandidate window = FindMainWindow(pids);
            if (window == null) return " | front=failed(no window), sent globally";

            string action = "";
            if (!window.Visible)
            {
                NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_SHOW);
                action = "shown,";
                TargetWindowCandidate shown = WaitForVisibleMainWindow(pids, ShowWaitMilliseconds);
                if (shown != null) window = shown;
                Thread.Sleep(400);
            }
            else if (window.Iconic)
            {
                NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_RESTORE);
                action = "restored,";
                Thread.Sleep(300);
            }

            string method;
            bool front = ForceForeground(window.Handle, pids, out method);
            if (front) Thread.Sleep(300);
            return " | front=" + action + (front ? "activated(" + method + ")" : "failed(" + method + "), sent globally") +
                " | window='" + window.Title + "'/" + window.ClassName;
        }

        private static bool ForceForeground(IntPtr hwnd, HashSet<uint> pids, out string method)
        {
            method = "already";
            if (IsForegroundTarget(pids)) return true;

            method = "direct";
            NativeMethods.SetForegroundWindow(hwnd);
            if (WaitForForeground(pids, 300)) return true;

            method = "attach";
            NativeMethods.MSG ignoredMessage;
            NativeMethods.PeekMessage(out ignoredMessage, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            uint ignoredPid;
            uint foregroundThread = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out ignoredPid);
            uint currentThread = NativeMethods.GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != currentThread &&
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
            if (WaitForForeground(pids, 300)) return true;

            method = "alt";
            LockShortcutRunner.SendKey(NativeMethods.VK_MENU, keyUp: false);
            try
            {
                NativeMethods.SetForegroundWindow(hwnd);
            }
            finally
            {
                LockShortcutRunner.SendKey(NativeMethods.VK_MENU, keyUp: true);
            }
            return WaitForForeground(pids, 500);
        }

        private static bool WaitForForeground(HashSet<uint> pids, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            do
            {
                if (IsForegroundTarget(pids)) return true;
                Thread.Sleep(50);
            }
            while (watch.ElapsedMilliseconds < timeoutMs);
            return false;
        }

        private static bool IsForegroundTarget(HashSet<uint> pids)
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            uint pid;
            NativeMethods.GetWindowThreadProcessId(foreground, out pid);
            return pids.Contains(pid);
        }

        private static TargetWindowCandidate WaitForVisibleMainWindow(HashSet<uint> pids, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            do
            {
                TargetWindowCandidate window = FindMainWindow(pids);
                if (window != null && window.Visible) return window;
                Thread.Sleep(100);
            }
            while (watch.ElapsedMilliseconds < timeoutMs);
            return null;
        }

        // 等目标进程把主窗口“显示出来”（前置键按下之后）。返回等到的毫秒数，超时返回 -1。
        // 注意只看“显示没显示”，不看“是不是前台窗口”：程序自己 Show 出来的窗口常常拿不到焦点
        // （Windows 前台规则），那不等于前置键没生效；要焦点还得靠下面的 BringToFront。
        public static int WaitForVisibleWindow(HashSet<uint> pids, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            do
            {
                TargetWindowCandidate window = FindMainWindow(pids);
                if (window != null && window.Visible) return (int)watch.ElapsedMilliseconds;
                Thread.Sleep(100);
            }
            while (watch.ElapsedMilliseconds < timeoutMs);
            return -1;
        }

        // 目标进程此刻“看得见”的顶层窗口清单：数量 + 最多 3 个“标题/类名”。只进日志当证据用。
        public static string DescribeVisibleWindows(HashSet<uint> pids)
        {
            if (pids == null || pids.Count == 0) return "none";

            var visible = new List<TargetWindowCandidate>();
            foreach (TargetWindowCandidate window in CollectWindows(pids))
            {
                if (window == null || !window.Visible || window.Iconic) continue;
                if (string.IsNullOrEmpty(window.Title)) continue;
                visible.Add(window);
            }

            var text = new StringBuilder();
            text.Append(visible.Count).Append(':');
            for (int i = 0; i < visible.Count && i < 3; i++)
            {
                if (i > 0) text.Append(',');
                text.Append('\'').Append(visible[i].Title).Append("'/").Append(visible[i].ClassName);
            }
            return text.ToString();
        }

        private static TargetWindowCandidate FindMainWindow(HashSet<uint> pids)
        {
            return TargetWindowPolicy.ChooseMainWindow(CollectWindows(pids));
        }

        private static List<TargetWindowCandidate> CollectWindows(HashSet<uint> pids)
        {
            var candidates = new List<TargetWindowCandidate>();
            if (pids == null || pids.Count == 0) return candidates;
            NativeMethods.EnumWindows(delegate (IntPtr window, IntPtr lParam)
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(window, out pid);
                if (pid != 0 && pids.Contains(pid)) candidates.Add(DescribeWindow(window));
                return true;
            }, IntPtr.Zero);

            return candidates;
        }

        private static TargetWindowCandidate DescribeWindow(IntPtr window)
        {
            var title = new StringBuilder(256);
            var className = new StringBuilder(256);
            NativeMethods.GetWindowText(window, title, title.Capacity);
            NativeMethods.GetClassName(window, className, className.Capacity);
            NativeMethods.RECT rect;
            bool hasRect = NativeMethods.GetWindowRect(window, out rect);
            int style = NativeMethods.GetWindowLong(window, NativeMethods.GWL_STYLE);
            int exStyle = NativeMethods.GetWindowLong(window, NativeMethods.GWL_EXSTYLE);

            return new TargetWindowCandidate
            {
                Handle = window,
                Visible = NativeMethods.IsWindowVisible(window),
                Iconic = NativeMethods.IsIconic(window),
                HasOwner = NativeMethods.GetWindow(window, NativeMethods.GW_OWNER) != IntPtr.Zero,
                ToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0,
                Minimizable = (style & NativeMethods.WS_MINIMIZEBOX) != 0,
                Title = title.ToString(),
                ClassName = className.ToString(),
                Width = hasRect ? rect.Right - rect.Left : 0,
                Height = hasRect ? rect.Bottom - rect.Top : 0
            };
        }
    }
}
