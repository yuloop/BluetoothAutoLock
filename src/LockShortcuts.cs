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
            IntPtr module = NativeMethods.GetModuleHandle(null);
            _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardCallback, module, 0);
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseCallback, module, 0);
            _ready.Set();
            try
            {
                while (NativeMethods.GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
                {
                }
            }
            finally
            {
                if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
                if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
            }
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
            _ready.Wait(5000);
            if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
        }
    }

    internal static class LockShortcutRunner
    {
        public static int TriggerAll(IEnumerable<LockShortcutMapping> mappings, int preDelayMilliseconds, Action<string> info, Action<string> warn)
        {
            bool userInputSeen;
            return TriggerAll(mappings, preDelayMilliseconds, info, warn, out userInputSeen);
        }

        public static int TriggerAll(IEnumerable<LockShortcutMapping> mappings, int preDelayMilliseconds, Action<string> info, Action<string> warn, out bool userInputSeen)
        {
            using (var detector = new RealInputDetector())
            {
                if (!detector.Watching && warn != null) warn("Real keyboard/mouse input detector could not start; only input after the shortcuts can cancel the workstation lock.");
                int sent = TriggerAllCore(mappings, preDelayMilliseconds, info, warn);
                userInputSeen = detector.UserInputSeen;
                return sent;
            }
        }

        private static int TriggerAllCore(IEnumerable<LockShortcutMapping> mappings, int preDelayMilliseconds, Action<string> info, Action<string> warn)
        {
            if (mappings == null) return 0;

            int preDelay = Math.Max(0, Math.Min(10000, preDelayMilliseconds));
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

                    ShortcutSendResult result = SendShortcut(mapping.Shortcut);
                    sent++;
                    if (info != null)
                    {
                        info("Lock shortcut triggered: " + mapping.Shortcut + note + preText + targetText +
                            " | foreground=" + DescribeForegroundProcess() +
                            " | method=" + result.Method +
                            " | InputEvents=" + result.SentInputs + "/" + result.RequestedInputs +
                            " | lastError=" + result.LastWin32Error);
                    }
                    Thread.Sleep(300);
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

        private static TargetWindowCandidate FindMainWindow(HashSet<uint> pids)
        {
            var candidates = new List<TargetWindowCandidate>();
            NativeMethods.EnumWindows(delegate (IntPtr window, IntPtr lParam)
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(window, out pid);
                if (pid != 0 && pids.Contains(pid)) candidates.Add(DescribeWindow(window));
                return true;
            }, IntPtr.Zero);

            return TargetWindowPolicy.ChooseMainWindow(candidates);
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
