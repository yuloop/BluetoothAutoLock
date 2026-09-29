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
        public string PreShortcut { get; set; }
        public string Note { get; set; }

        public LockShortcutMapping()
            : this("", "")
        {
        }

        public LockShortcutMapping(string shortcut, string note)
            : this(shortcut, note, "")
        {
        }

        public LockShortcutMapping(string shortcut, string note, string preShortcut)
        {
            Shortcut = shortcut ?? "";
            PreShortcut = preShortcut ?? "";
            Note = CleanNote(note);
        }

        public string ToConfigValue()
        {
            string keys = string.IsNullOrEmpty(PreShortcut) ? (Shortcut ?? "") : (PreShortcut + ">" + (Shortcut ?? ""));
            string note = CleanNote(Note);
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
            int sep = value.IndexOf('|');
            if (sep >= 0)
            {
                shortcutText = value.Substring(0, sep);
                note = value.Substring(sep + 1);
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
            mapping = new LockShortcutMapping(shortcut, note, preShortcut);
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
        public static int TriggerAll(IEnumerable<LockShortcutMapping> mappings, int preDelayMilliseconds, Action<string> info, Action<string> warn)
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
                    string preText = "";
                    if (!string.IsNullOrWhiteSpace(mapping.PreShortcut))
                    {
                        SendShortcut(mapping.PreShortcut);
                        preText = " | pre=" + mapping.PreShortcut + " then wait " + preDelay + "ms";
                        if (preDelay > 0) Thread.Sleep(preDelay);
                    }

                    ShortcutSendResult result = SendShortcut(mapping.Shortcut);
                    sent++;
                    if (info != null)
                    {
                        info("Lock shortcut triggered: " + mapping.Shortcut + note + preText +
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
}
