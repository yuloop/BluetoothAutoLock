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
        public string Note { get; set; }

        public LockShortcutMapping()
            : this("", "")
        {
        }

        public LockShortcutMapping(string shortcut, string note)
        {
            Shortcut = shortcut ?? "";
            Note = CleanNote(note);
        }

        public string ToConfigValue()
        {
            string note = CleanNote(Note);
            if (string.IsNullOrEmpty(note)) return Shortcut ?? "";
            return (Shortcut ?? "") + "|" + note;
        }

        public static bool TryParseConfigValue(string value, out LockShortcutMapping mapping)
        {
            mapping = null;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string shortcutText = value;
            string note = "";
            int sep = value.IndexOf('|');
            if (sep >= 0)
            {
                shortcutText = value.Substring(0, sep);
                note = value.Substring(sep + 1);
            }

            string shortcut;
            if (!TryNormalizeShortcutText(shortcutText, out shortcut)) return false;
            mapping = new LockShortcutMapping(shortcut, note);
            return true;
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

    internal static class LockShortcutRunner
    {
        public static int TriggerAll(IEnumerable<LockShortcutMapping> mappings, string weChatShowWindowShortcut, Action<string> info, Action<string> warn)
        {
            if (mappings == null) return 0;

            int sent = 0;
            foreach (LockShortcutMapping mapping in mappings)
            {
                if (mapping == null || string.IsNullOrWhiteSpace(mapping.Shortcut)) continue;

                string note = string.IsNullOrWhiteSpace(mapping.Note) ? "" : (" | 备注：" + mapping.Note);
                try
                {
                    ShortcutTargetActivation activation = LockShortcutTargetActivator.TryActivate(mapping, weChatShowWindowShortcut);
                    if (!activation.ReadyToSend)
                    {
                        if (warn != null) warn("Lock shortcut skipped: " + mapping.Shortcut + note + activation.ToLogSuffix());
                        continue;
                    }

                    ShortcutSendResult result = SendShortcut(mapping.Shortcut);
                    sent++;
                    if (info != null)
                    {
                        info("Lock shortcut triggered: " + mapping.Shortcut + note +
                            activation.ToLogSuffix() +
                            " | method=" + result.Method +
                            " | InputEvents=" + result.SentInputs + "/" + result.RequestedInputs +
                            " | lastError=" + result.LastWin32Error);
                    }
                    Thread.Sleep(300);
                }
                catch (Exception ex)
                {
                    if (warn != null) warn("Lock shortcut failed: " + mapping.Shortcut + note + " | " + ex.Message);
                }
            }

            return sent;
        }

        internal static ShortcutSendResult SendShortcut(string shortcut)
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

        internal static void SendKey(ushort virtualKey, bool keyUp)
        {
            NativeMethods.INPUT[] arr = { NativeMethods.CreateKeyboardInput(virtualKey, keyUp) };
            NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
        }

        internal sealed class ShortcutSendResult
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

    internal static class LockShortcutTargetResolver
    {
        internal static bool TryGetTargetProcessNames(LockShortcutMapping mapping, out string[] processNames)
        {
            processNames = null;
            if (mapping == null) return false;

            string text = ((mapping.Note ?? "") + " " + (mapping.Shortcut ?? "")).Trim();
            if (text.Length == 0) return false;

            if (Contains(text, "微信") || Contains(text, "weixin") || Contains(text, "wechat"))
            {
                processNames = new[] { "Weixin", "WeChat" };
                return true;
            }

            if (Contains(text, "qq") || Contains(text, "tim"))
            {
                processNames = new[] { "QQ", "TIM" };
                return true;
            }

            return false;
        }

        internal static bool IsWeChatTarget(string[] processNames)
        {
            return processNames != null && Array.IndexOf(processNames, "Weixin") >= 0;
        }

        private static bool Contains(string value, string token)
        {
            return value != null && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal static class LockShortcutTargetActivator
    {
        private const int ShowWaitMilliseconds = 2500;

        public static ShortcutTargetActivation TryActivate(LockShortcutMapping mapping, string weChatShowWindowShortcut)
        {
            string[] processNames;
            if (!LockShortcutTargetResolver.TryGetTargetProcessNames(mapping, out processNames))
                return ShortcutTargetActivation.NotNeeded();

            string target = string.Join(",", processNames);
            HashSet<uint> pids = FindProcessIds(processNames);
            if (pids.Count == 0)
                return ShortcutTargetActivation.Failed(target, "未运行");

            TargetWindowCandidate window = FindMainWindow(pids);
            if (window == null)
                return ShortcutTargetActivation.Failed(target, "未找到主窗口");

            string shown = "visible";
            if (!window.Visible)
            {
                string showShortcut = LockShortcutTargetResolver.IsWeChatTarget(processNames)
                    ? (weChatShowWindowShortcut ?? "").Trim()
                    : "";
                window = ShowHiddenWindow(window, pids, showShortcut, out shown);
                if (window == null)
                    return ShortcutTargetActivation.Failed(target, "托盘里的窗口没能叫出来(" + shown + ")");
            }
            else if (window.Iconic)
            {
                NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_RESTORE);
                shown = "restored";
                Thread.Sleep(300);
            }

            string foregroundMethod;
            if (!ForceForeground(window.Handle, pids, out foregroundMethod))
                return ShortcutTargetActivation.Failed(target, "没能切到最前面(show=" + shown + ", " + foregroundMethod + ")");

            Thread.Sleep(300);
            return ShortcutTargetActivation.Activated(target, window, shown, foregroundMethod);
        }

        private static TargetWindowCandidate ShowHiddenWindow(TargetWindowCandidate hidden, HashSet<uint> pids, string showShortcut, out string shown)
        {
            TargetWindowCandidate window;
            if (!string.IsNullOrEmpty(showShortcut))
            {
                LockShortcutRunner.SendShortcut(showShortcut);
                shown = "hotkey " + showShortcut;
                window = WaitForVisibleMainWindow(pids, ShowWaitMilliseconds);
                if (window != null)
                {
                    Thread.Sleep(500);
                    return window;
                }
            }

            NativeMethods.ShowWindow(hidden.Handle, NativeMethods.SW_SHOW);
            shown = string.IsNullOrEmpty(showShortcut) ? "ShowWindow" : ("hotkey " + showShortcut + " -> ShowWindow");
            window = WaitForVisibleMainWindow(pids, ShowWaitMilliseconds);
            if (window != null) Thread.Sleep(500);
            return window;
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
            LockShortcutRunner.SendKey(NativeMethods.VK_MENU, false);
            NativeMethods.SetForegroundWindow(hwnd);
            LockShortcutRunner.SendKey(NativeMethods.VK_MENU, true);
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

        private static HashSet<uint> FindProcessIds(string[] processNames)
        {
            var pids = new HashSet<uint>();
            foreach (string name in processNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    using (process)
                        pids.Add((uint)process.Id);
                }
            }
            return pids;
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

    internal sealed class ShortcutTargetActivation
    {
        private readonly bool _attempted;
        private readonly bool _activated;
        private readonly string _target;
        private readonly TargetWindowCandidate _window;
        private readonly string _shown;
        private readonly string _foregroundMethod;
        private readonly string _reason;

        private ShortcutTargetActivation(bool attempted, bool activated, string target, TargetWindowCandidate window, string shown, string foregroundMethod, string reason)
        {
            _attempted = attempted;
            _activated = activated;
            _target = target ?? "";
            _window = window;
            _shown = shown ?? "";
            _foregroundMethod = foregroundMethod ?? "";
            _reason = reason ?? "";
        }

        public bool ReadyToSend
        {
            get { return !_attempted || _activated; }
        }

        public static ShortcutTargetActivation NotNeeded()
        {
            return new ShortcutTargetActivation(false, false, "", null, "", "", "");
        }

        public static ShortcutTargetActivation Activated(string target, TargetWindowCandidate window, string shown, string foregroundMethod)
        {
            return new ShortcutTargetActivation(true, true, target, window, shown, foregroundMethod, "");
        }

        public static ShortcutTargetActivation Failed(string target, string reason)
        {
            return new ShortcutTargetActivation(true, false, target, null, "", "", reason);
        }

        public string ToLogSuffix()
        {
            if (!_attempted) return "";
            if (_activated && _window != null)
                return " | target=" + _target +
                    " | targetHwnd=0x" + _window.Handle.ToInt64().ToString("X") +
                    " | window='" + _window.Title + "'/" + _window.ClassName +
                    " | show=" + _shown +
                    " | foreground=true(" + _foregroundMethod + ")";
            return " | target=" + _target + " | foreground=false | targetReason=" + _reason;
        }
    }
}
