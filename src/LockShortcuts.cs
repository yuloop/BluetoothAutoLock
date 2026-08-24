using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        public static int TriggerAll(IEnumerable<LockShortcutMapping> mappings, Action<string> info, Action<string> warn)
        {
            if (mappings == null) return 0;

            int sent = 0;
            foreach (LockShortcutMapping mapping in mappings)
            {
                if (mapping == null || string.IsNullOrWhiteSpace(mapping.Shortcut)) continue;

                try
                {
                    ShortcutTargetActivation activation = LockShortcutTargetActivator.TryActivate(mapping);
                    ShortcutSendResult result = SendShortcut(mapping.Shortcut);
                    sent++;
                    if (info != null)
                    {
                        string note = string.IsNullOrWhiteSpace(mapping.Note) ? "" : (" | 备注：" + mapping.Note);
                        info("Lock shortcut triggered: " + mapping.Shortcut + note +
                            activation.ToLogSuffix() +
                            " | method=" + result.Method +
                            " | InputEvents=" + result.SentInputs + "/" + result.RequestedInputs +
                            " | lastError=" + result.LastWin32Error);
                    }
                    Thread.Sleep(120);
                }
                catch (Exception ex)
                {
                    if (warn != null) warn("Lock shortcut failed: " + mapping.Shortcut + " | " + ex.Message);
                }
            }

            return sent;
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

        private static bool Contains(string value, string token)
        {
            return value != null && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal static class LockShortcutTargetActivator
    {
        public static ShortcutTargetActivation TryActivate(LockShortcutMapping mapping)
        {
            string[] processNames;
            if (!LockShortcutTargetResolver.TryGetTargetProcessNames(mapping, out processNames))
                return ShortcutTargetActivation.NotNeeded();

            IntPtr hwnd;
            string processName;
            if (!TryFindWindow(processNames, out hwnd, out processName))
                return ShortcutTargetActivation.Failed(string.Join(",", processNames), "未找到可见主窗口");

            try
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                Thread.Sleep(150);
                bool foreground = NativeMethods.SetForegroundWindow(hwnd);
                Thread.Sleep(600);
                return ShortcutTargetActivation.Activated(processName, hwnd, foreground);
            }
            catch (Exception ex)
            {
                return ShortcutTargetActivation.Failed(processName, ex.Message);
            }
        }

        private static bool TryFindWindow(string[] processNames, out IntPtr hwnd, out string processName)
        {
            hwnd = IntPtr.Zero;
            processName = "";
            if (processNames == null) return false;

            for (int i = 0; i < processNames.Length; i++)
            {
                string name = processNames[i];
                if (string.IsNullOrWhiteSpace(name)) continue;

                foreach (Process process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        IntPtr handle = process.MainWindowHandle;
                        if (handle == IntPtr.Zero) continue;
                        if (!NativeMethods.IsWindowVisible(handle)) continue;
                        hwnd = handle;
                        processName = process.ProcessName;
                        return true;
                    }
                }
            }

            IntPtr found = IntPtr.Zero;
            string foundProcess = "";
            NativeMethods.EnumWindows(delegate (IntPtr window, IntPtr lParam)
            {
                if (!NativeMethods.IsWindowVisible(window)) return true;

                uint pid;
                NativeMethods.GetWindowThreadProcessId(window, out pid);
                if (pid == 0) return true;

                Process process = null;
                try { process = Process.GetProcessById((int)pid); }
                catch { return true; }

                using (process)
                {
                    for (int i = 0; i < processNames.Length; i++)
                    {
                        if (string.Equals(process.ProcessName, processNames[i], StringComparison.OrdinalIgnoreCase))
                        {
                            found = window;
                            foundProcess = process.ProcessName;
                            return false;
                        }
                    }
                }

                return true;
            }, IntPtr.Zero);

            if (found == IntPtr.Zero) return false;
            hwnd = found;
            processName = foundProcess;
            return true;
        }
    }

    internal sealed class ShortcutTargetActivation
    {
        private readonly bool _attempted;
        private readonly bool _activated;
        private readonly string _target;
        private readonly IntPtr _hwnd;
        private readonly bool _foregroundResult;
        private readonly string _reason;

        private ShortcutTargetActivation(bool attempted, bool activated, string target, IntPtr hwnd, bool foregroundResult, string reason)
        {
            _attempted = attempted;
            _activated = activated;
            _target = target ?? "";
            _hwnd = hwnd;
            _foregroundResult = foregroundResult;
            _reason = reason ?? "";
        }

        public static ShortcutTargetActivation NotNeeded()
        {
            return new ShortcutTargetActivation(false, false, "", IntPtr.Zero, false, "");
        }

        public static ShortcutTargetActivation Activated(string target, IntPtr hwnd, bool foregroundResult)
        {
            return new ShortcutTargetActivation(true, true, target, hwnd, foregroundResult, "");
        }

        public static ShortcutTargetActivation Failed(string target, string reason)
        {
            return new ShortcutTargetActivation(true, false, target, IntPtr.Zero, false, reason);
        }

        public string ToLogSuffix()
        {
            if (!_attempted) return "";
            if (_activated)
                return " | target=" + _target + " | targetHwnd=0x" + _hwnd.ToInt64().ToString("X") + " | foreground=" + _foregroundResult;
            return " | target=" + _target + " | foreground=false | targetReason=" + _reason;
        }
    }
}
