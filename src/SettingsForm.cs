using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace BluetoothAutoLock
{
    internal sealed class SettingsForm : Form
    {
        private readonly Config _cfg;
        private readonly Func<MonitorStatusSnapshot> _statusProvider;
        private readonly Logger _log;

        // === 原有控件 ===
        private ComboBox _deviceCombo;
        private Button _refreshBtn;
        private NumericUpDown _delayNum;
        private NumericUpDown _confirmNum;
        private NumericUpDown _pollNum;
        private ComboBox _logLevelCombo;
        private TextBox _logPathText;
        private Label _hintLabel;
        private int _deviceLoadGeneration;
        private const string BluetoothHintText = "规则：刷新会主动扫描附近 Classic 蓝牙；保存后只按所选设备地址判断，不按设备名匹配。";

        // === 锁屏前快捷键 控件 ===
        private ListView _lockShortcutList;
        private TextBox _lockShortcutCaptureText;
        private TextBox _lockShortcutNoteText;
        private NumericUpDown _lockShortcutSettleNum;
        private Button _lockShortcutAddBtn;
        private Button _lockShortcutRemoveBtn;
        private Button _lockShortcutClearBtn;
        private Button _lockShortcutTestBtn;
        private Label _lockShortcutStatusLabel;
        private TextBox _lockShortcutPreCaptureText;
        private NumericUpDown _lockShortcutPreDelayNum;
        private TextBox _lockShortcutTargetText;
        private Label _lockShortcutFinder;
        private Button _lockShortcutUpdateBtn;
        private bool _findingTarget;
        private ShortcutCaptureHook _captureHook;
        private TextBox _captureHookTarget;
        private CheckBox _lockScreenChk;
        private CheckBox _lockAppsChk;
        private readonly List<LockShortcutMapping> _lockShortcutMappings = new List<LockShortcutMapping>();
        private string _capturedLockShortcut = "";
        private string _capturedPreShortcut = "";
        private const string PreCapturePlaceholder = "可不填；点击后按键";
        private bool _lockShortcutWinDown;

        // === 实时状态 / 日志 控件 ===
        private GroupBox _liveGroup;
        private Label _liveStatusValueLabel;
        private Label _liveTimeValueLabel;
        private Label _liveNextValueLabel;
        private TextBox _liveLogText;
        private System.Windows.Forms.Timer _liveTimer;
        private readonly List<LiveLogEntry> _liveLogEntries = new List<LiveLogEntry>();
        private long _lastLiveSequence = -1;
        private const int MaxLiveLogEntries = 500;
        private const string LiveLogCountdownPlaceholder = "<倒计时>";
        private static readonly Regex RemainingSecondsLiveLogRegex =
            new Regex(@"(还需\s*)\d+(\s*秒)", RegexOptions.Compiled);
        private static readonly Regex LockCountdownStatusLiveLogRegex =
            new Regex(@"\d+\s*秒后才可能锁屏", RegexOptions.Compiled);

        private sealed class LiveLogEntry
        {
            public string Key;
            public string Line;
        }

        // === LoL → 虚拟显示器 控件 ===
        private GroupBox _lolGroup;
        private CheckBox _lolEnableChk;
        private CheckBox _lolRemoteCloseChk;
        private TextBox _lolProcessText;
        private TextBox _lolVdIdText;
        private NumericUpDown _lolPollNum;
        private Label _lolHintLabel;

        // === 游戏环境智能优化 / 异常监控 控件 ===
        private GroupBox _gameGroup;
        private CheckBox _gameMonitorEnableChk;
        private CheckBox _gameAiEnableChk;
        private NumericUpDown _gameIntervalNum;
        private TextBox _gameAiEndpointText;
        private TextBox _gameAiModelText;
        private TextBox _gameAiKeyText;
        private Label _gameHintLabel;

        private Button _okBtn;
        private Button _cancelBtn;

        private sealed class DeviceItem
        {
            public string DisplayName;
            public string Address;
            public bool Connected;
            public bool Paired;
            public bool Nearby;
            public short? RssiDbm;
            public override string ToString()
            {
                string state = Connected ? "[已连接] " : (Nearby ? "[附近Classic] " : (Paired ? "[已配对] " : "[Classic] "));
                string name = string.IsNullOrWhiteSpace(DisplayName) ? "(未命名)" : DisplayName;
                string rssi = RssiDbm.HasValue ? ("  RSSI " + RssiDbm.Value + " dBm") : "";
                return state + name + "  " + Address + rssi;
            }
        }

        private sealed class DeviceLoadResult
        {
            public string SelectedAddress;
            public List<DeviceItem> Items = new List<DeviceItem>();
            public Exception EnumerateError;
            public Exception ScanError;
        }

        public SettingsForm(Config cfg) : this(cfg, null, null)
        {
        }

        public SettingsForm(Config cfg, Func<MonitorStatusSnapshot> statusProvider, Logger log)
        {
            _cfg = cfg;
            _statusProvider = statusProvider;
            _log = log;
            BuildUi();
            BindFromCfg();
            StartLiveStatusTimer();
        }

        private void BuildUi()
        {
            Text = "蓝牙自动锁屏 " + Program.Version + " — 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(600, 640);
            Font = new Font("Microsoft YaHei UI", 9F);

            var tabs = new TabControl
            {
                Left = 12,
                Top = 12,
                Width = ClientSize.Width - 24,
                Height = ClientSize.Height - 72
            };
            var bluetoothTab = new TabPage("蓝牙锁屏");
            var shortcutTab = new TabPage("锁屏快捷键");
            var gameTab = new TabPage("游戏优化");
            tabs.TabPages.Add(bluetoothTab);
            tabs.TabPages.Add(shortcutTab);
            tabs.TabPages.Add(gameTab);
            Controls.Add(tabs);

            int labelLeft = 16;
            int labelWidth = 130;
            int ctrlLeft = 150;
            int ctrlWidth = tabs.Width - ctrlLeft - 32;
            int rowH = 34;
            int y = 18;

            bluetoothTab.Controls.Add(new Label { Text = "目标设备：", Left = labelLeft, Top = y + 4, Width = labelWidth });
            _deviceCombo = new ComboBox { Left = ctrlLeft, Top = y, Width = ctrlWidth - 90, DropDownStyle = ComboBoxStyle.DropDownList };
            bluetoothTab.Controls.Add(_deviceCombo);
            _refreshBtn = new Button { Text = "刷新", Left = ctrlLeft + ctrlWidth - 80, Top = y - 1, Width = 80, Height = 26 };
            _refreshBtn.Click += (s, e) => LoadDevices();
            bluetoothTab.Controls.Add(_refreshBtn);
            y += rowH;

            bluetoothTab.Controls.Add(new Label { Text = "手机离开后：", Left = labelLeft, Top = y + 4, Width = labelWidth });
            _lockScreenChk = new CheckBox { Text = "锁定 Windows 屏幕", Left = ctrlLeft, Top = y + 2, AutoSize = true };
            bluetoothTab.Controls.Add(_lockScreenChk);
            _lockAppsChk = new CheckBox { Text = "执行锁屏快捷键（如锁定微信/QQ）", Left = ctrlLeft + 160, Top = y + 2, AutoSize = true };
            bluetoothTab.Controls.Add(_lockAppsChk);
            y += rowH;

            bluetoothTab.Controls.Add(new Label { Text = "蓝牙缺失阈值：", Left = labelLeft, Top = y + 4, Width = labelWidth });
            _delayNum = new NumericUpDown { Left = ctrlLeft, Top = y, Width = 100, Minimum = 1, Maximum = 3600 };
            bluetoothTab.Controls.Add(_delayNum);
            bluetoothTab.Controls.Add(new Label { Text = "（默认150；键鼠先空闲30秒）", Left = ctrlLeft + 110, Top = y + 4, Width = 220, ForeColor = Color.Gray });
            y += rowH;

            bluetoothTab.Controls.Add(new Label { Text = "锁前复核次数：", Left = labelLeft, Top = y + 4, Width = labelWidth });
            _confirmNum = new NumericUpDown { Left = ctrlLeft, Top = y, Width = 100, Minimum = 1, Maximum = 10 };
            bluetoothTab.Controls.Add(_confirmNum);
            bluetoothTab.Controls.Add(new Label { Text = "（默认3次；任一次扫到即取消）", Left = ctrlLeft + 110, Top = y + 4, Width = 250, ForeColor = Color.Gray });
            y += rowH;

            bluetoothTab.Controls.Add(new Label { Text = "轮询随机下限：", Left = labelLeft, Top = y + 4, Width = labelWidth });
            _pollNum = new NumericUpDown { Left = ctrlLeft, Top = y, Width = 100, Minimum = 1, Maximum = 60 };
            bluetoothTab.Controls.Add(_pollNum);
            bluetoothTab.Controls.Add(new Label { Text = "（默认8；实际随机为下限到下限+7秒）", Left = ctrlLeft + 110, Top = y + 4, Width = 280, ForeColor = Color.Gray });
            y += rowH;

            bluetoothTab.Controls.Add(new Label { Text = "日志级别：", Left = labelLeft, Top = y + 4, Width = labelWidth });
            _logLevelCombo = new ComboBox { Left = ctrlLeft, Top = y, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            _logLevelCombo.Items.AddRange(new object[] { "Debug", "Info", "Warn", "Error" });
            bluetoothTab.Controls.Add(_logLevelCombo);
            y += rowH;

            bluetoothTab.Controls.Add(new Label { Text = "日志路径：", Left = labelLeft, Top = y + 4, Width = labelWidth });
            _logPathText = new TextBox { Left = ctrlLeft, Top = y, Width = ctrlWidth };
            bluetoothTab.Controls.Add(_logPathText);
            y += rowH + 4;

            _hintLabel = new Label
            {
                Left = labelLeft,
                Top = y,
                Width = tabs.Width - 36,
                Height = 32,
                ForeColor = Color.DimGray,
                Text = BluetoothHintText
            };
            bluetoothTab.Controls.Add(_hintLabel);
            y += _hintLabel.Height + 8;

            _liveGroup = new GroupBox
            {
                Text = "实时状态 / 日志",
                Left = 12,
                Top = y,
                Width = tabs.Width - 36,
                Height = 210,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            BuildLiveGroup();
            bluetoothTab.Controls.Add(_liveGroup);

            BuildLockShortcutTab(shortcutTab, tabs.Width);

            // === LoL 游戏优化 分组 ===
            y = 18;
            _lolGroup = new GroupBox
            {
                Text = "LoL 游戏优化（可选）",
                Left = 12,
                Top = y,
                Width = tabs.Width - 36,
                Height = 230,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            BuildLolGroup();
            gameTab.Controls.Add(_lolGroup);
            y += _lolGroup.Height + 16;

            // === 游戏环境异常监控 分组 ===
            _gameGroup = new GroupBox
            {
                Text = "游戏环境智能优化 / 异常监控",
                Left = 12,
                Top = y,
                Width = tabs.Width - 36,
                Height = 270,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            BuildGameMonitorGroup();
            gameTab.Controls.Add(_gameGroup);

            int btnY = ClientSize.Height - 42;
            _okBtn = new Button { Text = "保存", Left = ClientSize.Width - 200, Top = btnY, Width = 90, Height = 30 };
            _okBtn.Click += (s, e) =>
            {
                if (TryCommitToCfg())
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            _cancelBtn = new Button { Text = "取消", Left = ClientSize.Width - 100, Top = btnY, Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
            Controls.Add(_okBtn);
            Controls.Add(_cancelBtn);
            AcceptButton = _okBtn;
            CancelButton = _cancelBtn;
        }

        private void BuildLiveGroup()
        {
            Font normalFont = new Font("Microsoft YaHei UI", 9F);
            int labelLeft = 12;
            int valueLeft = 92;
            int valueWidth = _liveGroup.Width - valueLeft - 16;
            int y = 24;

            _liveGroup.Controls.Add(new Label { Text = "当前状态：", Left = labelLeft, Top = y + 2, Width = 76, Font = normalFont });
            _liveStatusValueLabel = new Label
            {
                Left = valueLeft, Top = y + 2, Width = valueWidth, Height = 20,
                Font = normalFont, ForeColor = Color.FromArgb(0, 96, 160), Text = "等待状态..."
            };
            _liveGroup.Controls.Add(_liveStatusValueLabel);
            y += 24;

            _liveGroup.Controls.Add(new Label { Text = "获取时间：", Left = labelLeft, Top = y + 2, Width = 76, Font = normalFont });
            _liveTimeValueLabel = new Label { Left = valueLeft, Top = y + 2, Width = valueWidth, Height = 20, Font = normalFont, Text = "-" };
            _liveGroup.Controls.Add(_liveTimeValueLabel);
            y += 24;

            _liveGroup.Controls.Add(new Label { Text = "下一步：", Left = labelLeft, Top = y + 2, Width = 76, Font = normalFont });
            _liveNextValueLabel = new Label { Left = valueLeft, Top = y + 2, Width = valueWidth, Height = 20, Font = normalFont, Text = "-" };
            _liveGroup.Controls.Add(_liveNextValueLabel);
            y += 26;

            _liveLogText = new TextBox
            {
                Left = labelLeft, Top = y, Width = _liveGroup.Width - 24, Height = _liveGroup.Height - y - 12,
                Font = new Font("Consolas", 8.5F), Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, WordWrap = false, MaxLength = 200000
            };
            _liveGroup.Controls.Add(_liveLogText);
        }

        private void BuildLockShortcutTab(TabPage tab, int tabWidth)
        {
            int left = 16;
            int width = tabWidth - 44;
            int y = 18;
            Font normalFont = new Font("Microsoft YaHei UI", 9F);

            tab.Controls.Add(new Label
            {
                Left = left,
                Top = y,
                Width = width,
                Height = 54,
                Font = normalFont,
                ForeColor = Color.DimGray,
                Text = "手机离开后按顺序执行：目标程序已在最前面就直接按快捷键；不在最前面就先按前置快捷键（如 Ctrl+Alt+W 叫出微信），再把它还原、切到最前面后按快捷键（如 Ctrl+L）。选中一行可修改。"
            });
            y += 60;

            _lockShortcutList = new ListView
            {
                Left = left,
                Top = y,
                Width = width,
                Height = 168,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                MultiSelect = true,
                Font = normalFont
            };
            _lockShortcutList.Columns.Add("前置快捷键 → 快捷键", 190);
            _lockShortcutList.Columns.Add("目标程序", 90);
            _lockShortcutList.Columns.Add("备注作用", width - 300);
            _lockShortcutList.SelectedIndexChanged += (s, e) =>
            {
                UpdateLockShortcutButtons();
                LoadSelectedLockShortcutIntoEditors();
            };
            tab.Controls.Add(_lockShortcutList);
            y += _lockShortcutList.Height + 12;

            tab.Controls.Add(new Label { Text = "前置快捷键：", Left = left, Top = y + 4, Width = 86, Font = normalFont });
            _lockShortcutPreCaptureText = new TextBox
            {
                Left = left + 90,
                Top = y,
                Width = 180,
                ReadOnly = true,
                Font = normalFont,
                Text = PreCapturePlaceholder
            };
            _lockShortcutPreCaptureText.KeyDown += CaptureLockShortcutKeyDown;
            _lockShortcutPreCaptureText.KeyUp += CaptureLockShortcutKeyUp;
            _lockShortcutPreCaptureText.Enter += (s, e) => StartShortcutCapture(_lockShortcutPreCaptureText);
            _lockShortcutPreCaptureText.Leave += (s, e) =>
            {
                _lockShortcutWinDown = false;
                StopShortcutCapture();
            };
            tab.Controls.Add(_lockShortcutPreCaptureText);
            tab.Controls.Add(new Label
            {
                Text = "（先按它，比如把微信叫出来；在框里按 Backspace 清空）",
                Left = left + 288,
                Top = y + 4,
                Width = width - 288,
                ForeColor = Color.Gray,
                Font = normalFont
            });
            y += 34;

            tab.Controls.Add(new Label { Text = "快捷键：", Left = left, Top = y + 4, Width = 86, Font = normalFont });
            _lockShortcutCaptureText = new TextBox
            {
                Left = left + 90,
                Top = y,
                Width = 180,
                ReadOnly = true,
                Font = normalFont,
                Text = "点击后按快捷键"
            };
            _lockShortcutCaptureText.KeyDown += CaptureLockShortcutKeyDown;
            _lockShortcutCaptureText.KeyUp += CaptureLockShortcutKeyUp;
            _lockShortcutCaptureText.Enter += (s, e) => StartShortcutCapture(_lockShortcutCaptureText);
            _lockShortcutCaptureText.Leave += (s, e) =>
            {
                _lockShortcutWinDown = false;
                StopShortcutCapture();
            };
            tab.Controls.Add(_lockShortcutCaptureText);

            _captureHook = new ShortcutCaptureHook(OnHookShortcutCaptured, OnHookShortcutCleared);
            Deactivate += (s, e) => StopShortcutCapture();
            Activated += (s, e) =>
            {
                if (_lockShortcutPreCaptureText.Focused) StartShortcutCapture(_lockShortcutPreCaptureText);
                else if (_lockShortcutCaptureText.Focused) StartShortcutCapture(_lockShortcutCaptureText);
            };
            FormClosed += (s, e) => _captureHook.Dispose();

            tab.Controls.Add(new Label { Text = "备注：", Left = left + 288, Top = y + 4, Width = 48, Font = normalFont });
            _lockShortcutNoteText = new TextBox
            {
                Left = left + 338,
                Top = y,
                Width = width - 338,
                Font = normalFont
            };
            tab.Controls.Add(_lockShortcutNoteText);
            y += 34;

            tab.Controls.Add(new Label { Text = "目标程序：", Left = left, Top = y + 4, Width = 86, Font = normalFont });
            _lockShortcutFinder = new Label
            {
                Text = "⊕",
                Left = left + 90,
                Top = y,
                Width = 30,
                Height = 25,
                Font = new Font("Segoe UI Symbol", 12F),
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.FixedSingle,
                Cursor = Cursors.Cross
            };
            _lockShortcutFinder.MouseDown += FinderMouseDown;
            _lockShortcutFinder.MouseMove += FinderMouseMove;
            _lockShortcutFinder.MouseUp += FinderMouseUp;
            tab.Controls.Add(_lockShortcutFinder);
            _lockShortcutTargetText = new TextBox { Left = left + 126, Top = y, Width = 144, Font = normalFont };
            tab.Controls.Add(_lockShortcutTargetText);
            tab.Controls.Add(new Label
            {
                Text = "（按住 ⊕ 拖到微信/QQ 窗口上松开；留空=只全局按键）",
                Left = left + 288,
                Top = y + 4,
                Width = width - 288,
                ForeColor = Color.Gray,
                Font = normalFont
            });
            y += 36;

            _lockShortcutAddBtn = new Button { Text = "新增", Left = left + 90, Top = y, Width = 70, Height = 28, Font = normalFont };
            _lockShortcutAddBtn.Click += (s, e) => AddLockShortcutMapping();
            tab.Controls.Add(_lockShortcutAddBtn);

            _lockShortcutUpdateBtn = new Button { Text = "修改选中", Left = left + 166, Top = y, Width = 82, Height = 28, Font = normalFont };
            _lockShortcutUpdateBtn.Click += (s, e) => UpdateSelectedLockShortcutMapping();
            tab.Controls.Add(_lockShortcutUpdateBtn);

            _lockShortcutRemoveBtn = new Button { Text = "删除选中", Left = left + 254, Top = y, Width = 82, Height = 28, Font = normalFont };
            _lockShortcutRemoveBtn.Click += (s, e) => RemoveSelectedLockShortcuts();
            tab.Controls.Add(_lockShortcutRemoveBtn);

            _lockShortcutClearBtn = new Button { Text = "清空", Left = left + 342, Top = y, Width = 60, Height = 28, Font = normalFont };
            _lockShortcutClearBtn.Click += (s, e) => ClearLockShortcuts();
            tab.Controls.Add(_lockShortcutClearBtn);

            _lockShortcutTestBtn = new Button { Text = "测试触发", Left = left + 408, Top = y, Width = 82, Height = 28, Font = normalFont };
            _lockShortcutTestBtn.Click += (s, e) => TestLockShortcuts();
            tab.Controls.Add(_lockShortcutTestBtn);
            y += 38;

            tab.Controls.Add(new Label { Text = "触发后等待：", Left = left, Top = y + 4, Width = 90, Font = normalFont });
            _lockShortcutSettleNum = new NumericUpDown
            {
                Left = left + 90,
                Top = y,
                Width = 100,
                Minimum = 0,
                Maximum = 10000,
                Increment = 100,
                Font = normalFont
            };
            tab.Controls.Add(_lockShortcutSettleNum);
            tab.Controls.Add(new Label
            {
                Text = "毫秒（默认3000；同时开启锁屏时，按完快捷键等这么久再锁屏）",
                Left = left + 200,
                Top = y + 4,
                Width = width - 200,
                ForeColor = Color.Gray,
                Font = normalFont
            });
            y += 34;

            tab.Controls.Add(new Label { Text = "前置键后等待：", Left = left, Top = y + 4, Width = 90, Font = normalFont });
            _lockShortcutPreDelayNum = new NumericUpDown
            {
                Left = left + 90,
                Top = y,
                Width = 100,
                Minimum = 0,
                Maximum = 10000,
                Increment = 100,
                Font = normalFont
            };
            tab.Controls.Add(_lockShortcutPreDelayNum);
            tab.Controls.Add(new Label
            {
                Text = "毫秒（默认1000；按完前置快捷键，等这么久再按快捷键）",
                Left = left + 200,
                Top = y + 4,
                Width = width - 200,
                ForeColor = Color.Gray,
                Font = normalFont
            });
            y += 34;

            _lockShortcutStatusLabel = new Label
            {
                Left = left + 90,
                Top = y,
                Width = width - 90,
                Height = 36,
                ForeColor = Color.DimGray,
                Font = normalFont,
                Text = "测试触发会按上面的顺序真的执行一遍；微信/QQ 锁上后需要用手机解锁。"
            };
            tab.Controls.Add(_lockShortcutStatusLabel);

            UpdateLockShortcutButtons();
        }

        private void StartShortcutCapture(TextBox box)
        {
            if (_captureHook == null || box == null) return;
            _captureHookTarget = box;
            if (!_captureHook.Start()) return;
            box.BackColor = Color.LightYellow;
            SetLockShortcutStatus("正在录制：直接按组合键。录制时会先拦下其他程序的同名快捷键，单独按 Backspace 清空。", Color.FromArgb(0, 96, 160));
        }

        private void StopShortcutCapture()
        {
            if (_captureHook != null) _captureHook.Stop();
            if (_captureHookTarget != null) _captureHookTarget.ResetBackColor();
            _captureHookTarget = null;
        }

        private void OnHookShortcutCaptured(string shortcut)
        {
            if (_captureHookTarget == _lockShortcutPreCaptureText)
            {
                _capturedPreShortcut = shortcut;
                _lockShortcutPreCaptureText.Text = shortcut;
            }
            else if (_captureHookTarget == _lockShortcutCaptureText)
            {
                _capturedLockShortcut = shortcut;
                _lockShortcutCaptureText.Text = shortcut;
            }
        }

        private void OnHookShortcutCleared()
        {
            if (_captureHookTarget == _lockShortcutPreCaptureText)
            {
                _capturedPreShortcut = "";
                _lockShortcutPreCaptureText.Text = PreCapturePlaceholder;
            }
            else if (_captureHookTarget == _lockShortcutCaptureText)
            {
                _capturedLockShortcut = "";
                _lockShortcutCaptureText.Text = "点击后按快捷键";
            }
        }

        private void CaptureLockShortcutKeyDown(object sender, KeyEventArgs e)
        {
            bool isPre = sender != null && sender == _lockShortcutPreCaptureText;
            bool winDown = _lockShortcutWinDown || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin;
            if (e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
            {
                _lockShortcutWinDown = true;
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }

            if (isPre && e.Modifiers == Keys.None && (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete))
            {
                _capturedPreShortcut = "";
                _lockShortcutPreCaptureText.Text = PreCapturePlaceholder;
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }

            string shortcut;
            if (LockShortcutMapping.TryFromKeyEvent(e.KeyCode, e.Modifiers, winDown, out shortcut))
            {
                if (isPre)
                {
                    _capturedPreShortcut = shortcut;
                    _lockShortcutPreCaptureText.Text = shortcut;
                }
                else
                {
                    _capturedLockShortcut = shortcut;
                    _lockShortcutCaptureText.Text = shortcut;
                }
            }

            e.SuppressKeyPress = true;
            e.Handled = true;
        }

        private void CaptureLockShortcutKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
                _lockShortcutWinDown = false;
            e.SuppressKeyPress = true;
            e.Handled = true;
        }

        private void AddLockShortcutMapping()
        {
            LockShortcutMapping mapping;
            if (!TryBuildMappingFromEditors(-1, out mapping)) return;

            _lockShortcutMappings.Add(mapping);
            ResetLockShortcutEditors();
            RefreshLockShortcutList();
        }

        private void UpdateSelectedLockShortcutMapping()
        {
            if (_lockShortcutList.SelectedIndices.Count != 1)
            {
                SetLockShortcutStatus("请先在列表里选中一行，再点“修改选中”。", Color.DarkOrange);
                return;
            }

            int index = _lockShortcutList.SelectedIndices[0];
            LockShortcutMapping mapping;
            if (!TryBuildMappingFromEditors(index, out mapping)) return;

            _lockShortcutMappings[index] = mapping;
            RefreshLockShortcutList();
            if (index < _lockShortcutList.Items.Count) _lockShortcutList.Items[index].Selected = true;
            SetLockShortcutStatus("已修改第 " + (index + 1) + " 行，点“保存”后生效。", Color.FromArgb(0, 96, 160));
        }

        private bool TryBuildMappingFromEditors(int editingIndex, out LockShortcutMapping mapping)
        {
            mapping = null;
            string shortcut = _capturedLockShortcut;
            if (string.IsNullOrWhiteSpace(shortcut))
            {
                MessageBox.Show("请先点击“快捷键”框，并按下要触发的快捷键。",
                    "蓝牙自动锁屏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string preShortcut = _capturedPreShortcut ?? "";
            string target = LockShortcutMapping.CleanTargetProcess(_lockShortcutTargetText.Text);
            for (int i = 0; i < _lockShortcutMappings.Count; i++)
            {
                if (i == editingIndex) continue;
                LockShortcutMapping existing = _lockShortcutMappings[i];
                if (string.Equals(existing.Shortcut, shortcut, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.PreShortcut ?? "", preShortcut, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.TargetProcess ?? "", target, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("列表里已经有一模一样的一行（前置快捷键、快捷键、目标程序都相同）。",
                        "蓝牙自动锁屏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }

            mapping = new LockShortcutMapping(shortcut, _lockShortcutNoteText.Text, preShortcut, target);
            return true;
        }

        private void ResetLockShortcutEditors()
        {
            _capturedLockShortcut = "";
            _capturedPreShortcut = "";
            _lockShortcutCaptureText.Text = "点击后按快捷键";
            _lockShortcutPreCaptureText.Text = PreCapturePlaceholder;
            _lockShortcutNoteText.Clear();
            _lockShortcutTargetText.Clear();
        }

        private void LoadSelectedLockShortcutIntoEditors()
        {
            if (_lockShortcutList == null || _lockShortcutList.SelectedIndices.Count != 1) return;
            int index = _lockShortcutList.SelectedIndices[0];
            if (index < 0 || index >= _lockShortcutMappings.Count) return;

            LockShortcutMapping mapping = _lockShortcutMappings[index];
            _capturedLockShortcut = mapping.Shortcut ?? "";
            _capturedPreShortcut = mapping.PreShortcut ?? "";
            _lockShortcutCaptureText.Text = _capturedLockShortcut.Length > 0 ? _capturedLockShortcut : "点击后按快捷键";
            _lockShortcutPreCaptureText.Text = _capturedPreShortcut.Length > 0 ? _capturedPreShortcut : PreCapturePlaceholder;
            _lockShortcutNoteText.Text = mapping.Note ?? "";
            _lockShortcutTargetText.Text = mapping.TargetProcess ?? "";
        }

        private void FinderMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _findingTarget = true;
            _lockShortcutFinder.Capture = true;
            Cursor.Current = Cursors.Cross;
            SetLockShortcutStatus("按住不放，拖到要锁定的程序窗口上再松开……", Color.FromArgb(0, 96, 160));
        }

        private void FinderMouseMove(object sender, MouseEventArgs e)
        {
            if (!_findingTarget) return;
            Cursor.Current = Cursors.Cross;
            string processName;
            string windowTitle;
            if (TryGetProcessUnderCursor(out processName, out windowTitle))
                SetLockShortcutStatus("松开即选中：" + DescribeTarget(processName, windowTitle), Color.FromArgb(0, 96, 160));
        }

        private void FinderMouseUp(object sender, MouseEventArgs e)
        {
            if (!_findingTarget) return;
            _findingTarget = false;
            _lockShortcutFinder.Capture = false;
            Cursor.Current = Cursors.Default;

            string processName;
            string windowTitle;
            if (!TryGetProcessUnderCursor(out processName, out windowTitle))
            {
                SetLockShortcutStatus("没认出程序窗口：请按住 ⊕ 拖到微信/QQ 的窗口上再松开。", Color.DarkOrange);
                return;
            }

            _lockShortcutTargetText.Text = processName;
            SetLockShortcutStatus("已选中目标程序：" + DescribeTarget(processName, windowTitle) + "。改完点“新增”或“修改选中”。", Color.FromArgb(0, 96, 160));
        }

        private static string DescribeTarget(string processName, string windowTitle)
        {
            return string.IsNullOrWhiteSpace(windowTitle) ? processName : (processName + "（窗口：" + windowTitle + "）");
        }

        private static bool TryGetProcessUnderCursor(out string processName, out string windowTitle)
        {
            processName = "";
            windowTitle = "";
            try
            {
                Point position = Cursor.Position;
                IntPtr hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = position.X, Y = position.Y });
                if (hwnd == IntPtr.Zero) return false;
                IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
                if (root == IntPtr.Zero) root = hwnd;

                uint pid;
                NativeMethods.GetWindowThreadProcessId(root, out pid);
                if (pid == 0) return false;
                using (Process self = Process.GetCurrentProcess())
                {
                    if (pid == (uint)self.Id) return false;
                }
                using (Process process = Process.GetProcessById((int)pid))
                    processName = process.ProcessName;

                var title = new StringBuilder(256);
                NativeMethods.GetWindowText(root, title, title.Capacity);
                windowTitle = title.ToString();
                return processName.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private void RemoveSelectedLockShortcuts()
        {
            if (_lockShortcutList.SelectedIndices.Count == 0) return;

            var indices = new List<int>();
            foreach (int index in _lockShortcutList.SelectedIndices)
                indices.Add(index);
            indices.Sort();
            for (int i = indices.Count - 1; i >= 0; i--)
                _lockShortcutMappings.RemoveAt(indices[i]);

            RefreshLockShortcutList();
        }

        private void ClearLockShortcuts()
        {
            if (_lockShortcutMappings.Count == 0) return;
            _lockShortcutMappings.Clear();
            RefreshLockShortcutList();
        }

        private void TestLockShortcuts()
        {
            var mappings = new List<LockShortcutMapping>();
            if (_lockShortcutList.SelectedIndices.Count > 0)
            {
                foreach (int index in _lockShortcutList.SelectedIndices)
                    mappings.Add(_lockShortcutMappings[index]);
            }
            else
            {
                mappings.AddRange(_lockShortcutMappings);
            }

            if (mappings.Count == 0)
            {
                SetLockShortcutStatus("还没有可测试的快捷键。请先新增至少一个映射。", Color.DarkOrange);
                return;
            }

            int preDelay = _lockShortcutPreDelayNum == null ? 1000 : (int)_lockShortcutPreDelayNum.Value;
            _lockShortcutTestBtn.Enabled = false;
            SetLockShortcutStatus("正在测试：按顺序按下前置快捷键和快捷键……", Color.FromArgb(0, 96, 160));
            Logger log = _log;
            if (log != null) log.Info("Lock shortcut test requested from settings; testing " + mappings.Count + " shortcut(s).");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var warnings = new List<string>();
                int sent = 0;
                try
                {
                    sent = LockShortcutRunner.TriggerAll(
                        mappings,
                        preDelay,
                        message => { if (log != null) log.Info(message); },
                        message =>
                        {
                            lock (warnings) warnings.Add(message);
                            if (log != null) log.Warn(message);
                        });
                }
                catch (Exception ex)
                {
                    lock (warnings) warnings.Add(ex.Message);
                }
                PostLockShortcutTestResult(sent, warnings);
            });
        }

        private void PostLockShortcutTestResult(int sent, List<string> warnings)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)(() =>
                {
                    if (IsDisposed) return;
                    UpdateLockShortcutButtons();
                    if (warnings.Count > 0)
                        SetLockShortcutStatus("测试按下了 " + sent + " 组快捷键；有 " + warnings.Count + " 组失败：" + warnings[0], Color.DarkOrange);
                    else
                        SetLockShortcutStatus("测试已按下 " + sent + " 组快捷键，请看微信/QQ 是否已锁定。", Color.FromArgb(0, 96, 160));
                }));
            }
            catch
            {
            }
        }

        private void SetLockShortcutStatus(string text, Color color)
        {
            if (_lockShortcutStatusLabel == null) return;
            _lockShortcutStatusLabel.ForeColor = color;
            _lockShortcutStatusLabel.Text = text ?? "";
        }

        private void RefreshLockShortcutList()
        {
            if (_lockShortcutList == null) return;

            _lockShortcutList.BeginUpdate();
            try
            {
                _lockShortcutList.Items.Clear();
                for (int i = 0; i < _lockShortcutMappings.Count; i++)
                {
                    LockShortcutMapping mapping = _lockShortcutMappings[i];
                    var item = new ListViewItem(mapping.ToDisplayText());
                    item.SubItems.Add(mapping.TargetProcess ?? "");
                    item.SubItems.Add(mapping.Note ?? "");
                    _lockShortcutList.Items.Add(item);
                }
            }
            finally
            {
                _lockShortcutList.EndUpdate();
            }

            UpdateLockShortcutButtons();
        }

        private void UpdateLockShortcutButtons()
        {
            if (_lockShortcutRemoveBtn != null)
                _lockShortcutRemoveBtn.Enabled = _lockShortcutList != null && _lockShortcutList.SelectedIndices.Count > 0;
            if (_lockShortcutUpdateBtn != null)
                _lockShortcutUpdateBtn.Enabled = _lockShortcutList != null && _lockShortcutList.SelectedIndices.Count == 1;
            if (_lockShortcutClearBtn != null)
                _lockShortcutClearBtn.Enabled = _lockShortcutMappings.Count > 0;
            if (_lockShortcutTestBtn != null)
                _lockShortcutTestBtn.Enabled = _lockShortcutMappings.Count > 0;
        }

        private void StartLiveStatusTimer()
        {
            RefreshLiveStatus();
            _liveTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _liveTimer.Tick += (s, e) => RefreshLiveStatus();
            _liveTimer.Start();
        }

        private void RefreshLiveStatus()
        {
            MonitorStatusSnapshot snapshot;
            if (_statusProvider == null)
                snapshot = MonitorStatusSnapshot.Create("仅设置模式", "从托盘打开设置可查看实时监控状态");
            else
                snapshot = _statusProvider() ?? MonitorStatusSnapshot.Create("初始化中", "等待监控线程上报状态");

            _liveStatusValueLabel.Text = snapshot.Status;
            _liveTimeValueLabel.Text = snapshot.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss");
            _liveNextValueLabel.Text = snapshot.NextAction;

            if (snapshot.Sequence != _lastLiveSequence)
            {
                _lastLiveSequence = snapshot.Sequence;
                AppendLiveLog(snapshot);
            }
        }

        private void AppendLiveLog(MonitorStatusSnapshot snapshot)
        {
            if (_liveLogText == null) return;
            if (snapshot == null) return;

            string key = BuildLiveLogKey(snapshot);
            string line = snapshot.ToLogLine();
            if (string.IsNullOrWhiteSpace(line)) return;

            RemoveDuplicateLiveLogEntries(key);
            _liveLogEntries.Add(new LiveLogEntry { Key = key, Line = line });

            while (_liveLogEntries.Count > MaxLiveLogEntries)
                _liveLogEntries.RemoveAt(0);

            string[] lines = new string[_liveLogEntries.Count];
            for (int i = 0; i < _liveLogEntries.Count; i++)
                lines[i] = _liveLogEntries[i].Line;

            _liveLogText.Lines = lines;
            _liveLogText.SelectionStart = _liveLogText.TextLength;
            _liveLogText.ScrollToCaret();
        }

        private static string BuildLiveLogKey(MonitorStatusSnapshot snapshot)
        {
            string status = NormalizeVolatileLiveLogText(snapshot.Status);
            string nextAction = NormalizeVolatileLiveLogText(snapshot.NextAction);
            return status + "\u001f" + nextAction;
        }

        private static string NormalizeVolatileLiveLogText(string text)
        {
            if (text == null) return "";
            string normalized = text.Trim();

            // Treat countdown-only changes as the same live-log event so the
            // previous row is replaced instead of appending one row per second.
            normalized = RemainingSecondsLiveLogRegex.Replace(
                normalized,
                "$1" + LiveLogCountdownPlaceholder + "$2");
            normalized = LockCountdownStatusLiveLogRegex.Replace(
                normalized,
                LiveLogCountdownPlaceholder + " 秒后才可能锁屏");

            return normalized;
        }

        private void RemoveDuplicateLiveLogEntries(string key)
        {
            for (int i = _liveLogEntries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_liveLogEntries[i].Key, key, StringComparison.Ordinal))
                    _liveLogEntries.RemoveAt(i);
            }
        }

        private void BuildLolGroup()
        {
            int innerLabelLeft = 12;
            int innerCtrlLeft = 140;
            int innerLabelW = 122;
            int innerCtrlW = _lolGroup.Width - innerCtrlLeft - 16;
            int innerRowH = 30;
            int yy = 24;

            Font normalFont = new Font("Microsoft YaHei UI", 9F);

            _lolEnableChk = new CheckBox
            {
                Left = innerLabelLeft,
                Top = yy,
                Width = _lolGroup.Width - 24,
                Height = 22,
                Text = "启用：LoL 启动时自动优化 WSL / 禁用虚拟显示器，退出时恢复",
                Font = normalFont
            };
            _lolEnableChk.CheckedChanged += (s, e) => UpdateLolEnabledState();
            _lolGroup.Controls.Add(_lolEnableChk);
            yy += innerRowH + 2;

            _lolRemoteCloseChk = new CheckBox
            {
                Left = innerLabelLeft,
                Top = yy,
                Width = _lolGroup.Width - 24,
                Height = 22,
                Text = "游戏时自动关闭 AskLink / ToDesk；结束后等 " +
                       Clamp(_cfg.RemoteRestartQuietMinutes, 1, 120).ToString() + " 分钟，恢复时静默到托盘",
                Font = normalFont
            };
            _lolGroup.Controls.Add(_lolRemoteCloseChk);
            yy += innerRowH + 2;

            _lolGroup.Controls.Add(new Label
            {
                Text = "LoL 进程名：", Left = innerLabelLeft, Top = yy + 4, Width = innerLabelW, Font = normalFont
            });
            _lolProcessText = new TextBox { Left = innerCtrlLeft, Top = yy, Width = innerCtrlW, Font = normalFont };
            _lolGroup.Controls.Add(_lolProcessText);
            yy += innerRowH;

            _lolGroup.Controls.Add(new Label
            {
                Text = "虚拟显示器 ID：", Left = innerLabelLeft, Top = yy + 4, Width = innerLabelW, Font = normalFont
            });
            _lolVdIdText = new TextBox { Left = innerCtrlLeft, Top = yy, Width = innerCtrlW, Font = normalFont };
            _lolGroup.Controls.Add(_lolVdIdText);
            yy += innerRowH;

            _lolGroup.Controls.Add(new Label
            {
                Text = "轮询间隔（秒）：", Left = innerLabelLeft, Top = yy + 4, Width = innerLabelW, Font = normalFont
            });
            _lolPollNum = new NumericUpDown
            {
                Left = innerCtrlLeft, Top = yy, Width = 80, Minimum = 1, Maximum = 60, Font = normalFont
            };
            _lolGroup.Controls.Add(_lolPollNum);
            _lolGroup.Controls.Add(new Label
            {
                Text = "（1 – 60，越小响应越快）", Left = innerCtrlLeft + 90, Top = yy + 4,
                Width = innerCtrlW - 90, Font = normalFont, ForeColor = Color.Gray
            });
            yy += innerRowH + 6;

            _lolHintLabel = new Label
            {
                Left = innerLabelLeft,
                Top = yy,
                Width = _lolGroup.Width - 24,
                Height = 40,
                ForeColor = Color.DimGray,
                Font = normalFont,
                Text = "WSL：仅在 LoL 启动前回收一次 Linux 页缓存，不限制 vmmem/VmmemWSL CPU。\n" +
                       "远程软件仅恢复本程序确实结束、且游戏前已运行的实例；静默启动只用于本程序恢复路径。"
            };
            _lolGroup.Controls.Add(_lolHintLabel);
        }

        private void BuildGameMonitorGroup()
        {
            int innerLabelLeft = 12;
            int innerCtrlLeft = 140;
            int innerLabelW = 122;
            int innerCtrlW = _gameGroup.Width - innerCtrlLeft - 16;
            int innerRowH = 30;
            int yy = 24;

            Font normalFont = new Font("Microsoft YaHei UI", 9F);

            _gameMonitorEnableChk = new CheckBox
            {
                Left = innerLabelLeft,
                Top = yy,
                Width = _gameGroup.Width - 24,
                Height = 22,
                Text = "启用：每 5 分钟分析异常应用；仅严重残留/崩溃/疑似死循环时托盘提醒",
                Font = normalFont
            };
            _gameMonitorEnableChk.CheckedChanged += (s, e) => UpdateGameMonitorEnabledState();
            _gameGroup.Controls.Add(_gameMonitorEnableChk);
            yy += innerRowH;

            _gameAiEnableChk = new CheckBox
            {
                Left = innerLabelLeft,
                Top = yy,
                Width = _gameGroup.Width - 24,
                Height = 22,
                Text = "使用 AI 辅助判断（仅本地发现异常候选时请求 Ark Responses API）",
                Font = normalFont
            };
            _gameAiEnableChk.CheckedChanged += (s, e) => UpdateGameMonitorEnabledState();
            _gameGroup.Controls.Add(_gameAiEnableChk);
            yy += innerRowH;

            _gameGroup.Controls.Add(new Label
            {
                Text = "分析间隔（分钟）：", Left = innerLabelLeft, Top = yy + 4, Width = innerLabelW, Font = normalFont
            });
            _gameIntervalNum = new NumericUpDown
            {
                Left = innerCtrlLeft, Top = yy, Width = 80, Minimum = 1, Maximum = 1440, Font = normalFont
            };
            _gameGroup.Controls.Add(_gameIntervalNum);
            _gameGroup.Controls.Add(new Label
            {
                Text = "（默认5；提醒会自动去重）", Left = innerCtrlLeft + 90, Top = yy + 4,
                Width = innerCtrlW - 90, Font = normalFont, ForeColor = Color.Gray
            });
            yy += innerRowH;

            _gameGroup.Controls.Add(new Label
            {
                Text = "AI Endpoint：", Left = innerLabelLeft, Top = yy + 4, Width = innerLabelW, Font = normalFont
            });
            _gameAiEndpointText = new TextBox { Left = innerCtrlLeft, Top = yy, Width = innerCtrlW, Font = normalFont };
            _gameGroup.Controls.Add(_gameAiEndpointText);
            yy += innerRowH;

            _gameGroup.Controls.Add(new Label
            {
                Text = "AI Model：", Left = innerLabelLeft, Top = yy + 4, Width = innerLabelW, Font = normalFont
            });
            _gameAiModelText = new TextBox { Left = innerCtrlLeft, Top = yy, Width = innerCtrlW, Font = normalFont };
            _gameGroup.Controls.Add(_gameAiModelText);
            yy += innerRowH;

            _gameGroup.Controls.Add(new Label
            {
                Text = "AI Key：", Left = innerLabelLeft, Top = yy + 4, Width = innerLabelW, Font = normalFont
            });
            _gameAiKeyText = new TextBox
            {
                Left = innerCtrlLeft, Top = yy, Width = innerCtrlW, Font = normalFont,
                PasswordChar = '●'
            };
            _gameGroup.Controls.Add(_gameAiKeyText);
            yy += innerRowH + 4;

            _gameHintLabel = new Label
            {
                Left = innerLabelLeft,
                Top = yy,
                Width = _gameGroup.Width - 24,
                Height = 48,
                ForeColor = Color.DimGray,
                Font = normalFont,
                Text = "右键菜单“智能优化游戏环境”会按 LOL 卡顿总结清理异常 node/omx/python 群和 LoL 崩溃残留；\n" +
                       "长期监控只记录/提醒，不自动杀进程。"
            };
            _gameGroup.Controls.Add(_gameHintLabel);
        }

        private void UpdateLolEnabledState()
        {
            bool en = _lolEnableChk.Checked;
            _lolRemoteCloseChk.Enabled = en;
            _lolProcessText.Enabled = en;
            _lolVdIdText.Enabled = en;
            _lolPollNum.Enabled = en;
        }

        private void UpdateGameMonitorEnabledState()
        {
            bool en = _gameMonitorEnableChk.Checked;
            bool ai = en && _gameAiEnableChk.Checked;
            _gameAiEnableChk.Enabled = en;
            _gameIntervalNum.Enabled = en;
            _gameAiEndpointText.Enabled = ai;
            _gameAiModelText.Enabled = ai;
            _gameAiKeyText.Enabled = ai;
        }

        private void LoadDevices()
        {
            string selectedAddress = SelectedOrConfiguredDeviceAddress();
            string configuredAddress = _cfg.DeviceAddress;
            int generation = Interlocked.Increment(ref _deviceLoadGeneration);

            BeginDeviceLoad(selectedAddress);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                DeviceLoadResult result = CollectDeviceItems(selectedAddress, configuredAddress);
                PostDeviceLoadResult(generation, result);
            });
        }

        private string SelectedOrConfiguredDeviceAddress()
        {
            DeviceItem oldSelected = _deviceCombo.SelectedItem as DeviceItem;
            if (oldSelected != null && !string.IsNullOrWhiteSpace(oldSelected.Address))
                return oldSelected.Address;
            return _cfg.DeviceAddress;
        }

        private void BeginDeviceLoad(string selectedAddress)
        {
            _deviceCombo.Items.Clear();

            string normalized = NormalizeAddress(selectedAddress);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                _deviceCombo.Items.Add(new DeviceItem
                {
                    Address = normalized,
                    DisplayName = _cfg.DeviceName,
                    Paired = true
                });
                _deviceCombo.SelectedIndex = 0;
            }

            _refreshBtn.Enabled = false;
            _refreshBtn.Text = "后台扫描";
            _hintLabel.Text = "设置窗口已先显示；设备列表正在后台扫描，完成后会自动刷新。";
        }

        private DeviceLoadResult CollectDeviceItems(string selectedAddress, string configuredAddress)
        {
            var result = new DeviceLoadResult { SelectedAddress = selectedAddress };
            try
            {
                var map = new Dictionary<string, DeviceItem>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    List<DeviceSnapshot> devices = BluetoothMonitor.EnumerateDevices();
                    foreach (DeviceSnapshot d in devices)
                    {
                        string address = NormalizeAddress(d.AddressText);
                        if (string.IsNullOrWhiteSpace(address)) continue;
                        DeviceItem item = GetOrCreateDeviceItem(map, address);
                        if (!string.IsNullOrWhiteSpace(d.Name)) item.DisplayName = d.Name;
                        item.Connected = item.Connected || d.Connected;
                        item.Paired = item.Paired || d.Authenticated || d.Remembered;
                    }
                }
                catch (Exception ex)
                {
                    result.EnumerateError = ex;
                }

                TryMergeActiveScanItems(map, configuredAddress, result);

                var items = new List<DeviceItem>(map.Values);
                items.Sort(CompareDeviceItems);
                result.Items = items;
            }
            catch (Exception ex)
            {
                result.EnumerateError = ex;
            }

            return result;
        }

        private void TryMergeActiveScanItems(Dictionary<string, DeviceItem> map, string configuredAddress, DeviceLoadResult result)
        {
            try
            {
                string normalizedConfigured = NormalizeAddress(configuredAddress);
                List<ScanHit> hits = BluetoothScanner.Scan(6);
                foreach (ScanHit h in hits)
                {
                    if (!IsClassicHit(h)) continue;
                    string address = NormalizeAddress(h.Address);
                    if (string.IsNullOrWhiteSpace(address)) continue;

                    bool nearby = HasNearbyClassicEvidence(h);
                    bool paired = h.Paired.HasValue && h.Paired.Value;
                    bool connected = h.Connected.HasValue && h.Connected.Value;
                    bool isConfigured = !string.IsNullOrWhiteSpace(normalizedConfigured) &&
                        string.Equals(address, normalizedConfigured, StringComparison.OrdinalIgnoreCase);

                    // Show live nearby Classic devices, plus the configured/paired
                    // Classic identity even when it is currently not live.
                    if (!nearby && !paired && !connected && !isConfigured) continue;

                    DeviceItem item = GetOrCreateDeviceItem(map, address);
                    if (!string.IsNullOrWhiteSpace(h.Name)) item.DisplayName = h.Name;
                    item.Connected = item.Connected || connected;
                    item.Paired = item.Paired || paired;
                    item.Nearby = item.Nearby || nearby;
                    if (h.RssiDbm.HasValue) item.RssiDbm = h.RssiDbm;
                }
            }
            catch (Exception ex)
            {
                result.ScanError = ex;
            }
        }

        private void PostDeviceLoadResult(int generation, DeviceLoadResult result)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)(() => ApplyDeviceLoadResult(generation, result)));
            }
            catch
            {
            }
        }

        private void ApplyDeviceLoadResult(int generation, DeviceLoadResult result)
        {
            if (IsDisposed || generation != _deviceLoadGeneration) return;
            if (result == null) result = new DeviceLoadResult();

            _deviceCombo.Items.Clear();
            foreach (DeviceItem item in result.Items)
                _deviceCombo.Items.Add(item);

            int select = FindDeviceIndex(result.SelectedAddress);
            if (select >= 0) _deviceCombo.SelectedIndex = select;

            _refreshBtn.Text = "刷新";
            _refreshBtn.Enabled = true;
            _hintLabel.Text = BluetoothHintText;

            if (result.EnumerateError != null)
            {
                MessageBox.Show("无法枚举蓝牙设备：" + result.EnumerateError.Message,
                    "蓝牙自动锁屏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (result.ScanError != null)
            {
                MessageBox.Show("附近 Classic 蓝牙主动扫描失败；已显示系统已配对设备。\n" + result.ScanError.Message,
                    "蓝牙自动锁屏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private int FindDeviceIndex(string selectedAddress)
        {
            string normalizedSelected = NormalizeAddress(selectedAddress);
            if (string.IsNullOrWhiteSpace(normalizedSelected)) return -1;

            for (int i = 0; i < _deviceCombo.Items.Count; i++)
            {
                var di = _deviceCombo.Items[i] as DeviceItem;
                if (di != null && string.Equals(di.Address, normalizedSelected, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static DeviceItem GetOrCreateDeviceItem(Dictionary<string, DeviceItem> map, string address)
        {
            DeviceItem item;
            if (!map.TryGetValue(address, out item))
            {
                item = new DeviceItem { Address = address, DisplayName = "" };
                map[address] = item;
            }
            return item;
        }

        private static string NormalizeAddress(string text)
        {
            ulong address;
            if (!NativeMethods.TryParseBluetoothAddress(text, out address)) return "";
            return NativeMethods.FormatBluetoothAddress(address);
        }

        private static bool IsClassicHit(ScanHit hit)
        {
            return hit != null && string.Equals(hit.Kind, "Classic", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasNearbyClassicEvidence(ScanHit hit)
        {
            return IsClassicHit(hit) &&
                ClassicBluetoothEvidence.HasCredibleRssi(hit.RssiDbm);
        }

        private static int CompareDeviceItems(DeviceItem a, DeviceItem b)
        {
            int scoreA = DeviceItemScore(a);
            int scoreB = DeviceItemScore(b);
            int score = scoreB.CompareTo(scoreA);
            if (score != 0) return score;
            int name = string.Compare(a.DisplayName ?? "", b.DisplayName ?? "", StringComparison.OrdinalIgnoreCase);
            if (name != 0) return name;
            return string.Compare(a.Address ?? "", b.Address ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static int DeviceItemScore(DeviceItem item)
        {
            if (item == null) return 0;
            int score = 0;
            if (item.Connected) score += 100;
            if (item.Nearby) score += 50;
            if (item.Paired) score += 10;
            return score;
        }

        private void BindFromCfg()
        {
            _delayNum.Value = Clamp(_cfg.DisconnectDelaySeconds, 1, 3600);
            _confirmNum.Value = Clamp(_cfg.DisconnectConfirmSeconds, 1, 10);
            _pollNum.Value = Clamp(_cfg.PollingIntervalSeconds, 1, 60);

            int idx = _logLevelCombo.Items.IndexOf(_cfg.LogLevel ?? "Info");
            _logLevelCombo.SelectedIndex = idx >= 0 ? idx : 1;

            _logPathText.Text = _cfg.LogPath ?? "";
            _lockScreenChk.Checked = _cfg.LockScreenEnabled;
            _lockAppsChk.Checked = _cfg.LockShortcutsEnabled;
            _lockShortcutPreDelayNum.Value = Clamp(_cfg.LockShortcutPreDelayMilliseconds, 0, 10000);
            _lockShortcutSettleNum.Value = Clamp(_cfg.LockShortcutSettleMilliseconds, 0, 10000);

            _lockShortcutMappings.Clear();
            if (_cfg.LockShortcutMappings != null)
            {
                foreach (LockShortcutMapping mapping in _cfg.LockShortcutMappings)
                {
                    if (mapping == null) continue;
                    _lockShortcutMappings.Add(new LockShortcutMapping(mapping.Shortcut, mapping.Note, mapping.PreShortcut, mapping.TargetProcess));
                }
            }
            RefreshLockShortcutList();

            _lolEnableChk.Checked = _cfg.LolOptimizerEnabled;
            _lolRemoteCloseChk.Checked = _cfg.LolAutoCloseRemoteEnabled;
            _lolProcessText.Text = _cfg.LolProcessName ?? "League of Legends";
            _lolVdIdText.Text = _cfg.VirtualDisplayDeviceId ?? "";
            _lolPollNum.Value = Clamp(_cfg.LolOptimizerPollSeconds, 1, 60);

            _gameMonitorEnableChk.Checked = _cfg.GameProblemMonitorEnabled;
            _gameAiEnableChk.Checked = _cfg.GameProblemMonitorAiEnabled;
            _gameIntervalNum.Value = Clamp(_cfg.GameProblemMonitorIntervalMinutes, 1, 1440);
            _gameAiEndpointText.Text = _cfg.GameProblemMonitorAiEndpoint ?? "https://ark.cn-beijing.volces.com/api/v3/responses";
            _gameAiModelText.Text = _cfg.GameProblemMonitorAiModel ?? "doubao-seed-2-0-lite-260215";
            _gameAiKeyText.Text = _cfg.GameProblemMonitorAiApiKey ?? "";

            UpdateLolEnabledState();
            UpdateGameMonitorEnabledState();
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private bool TryCommitToCfg()
        {
            DeviceItem sel = _deviceCombo.SelectedItem as DeviceItem;
            if (sel == null || string.IsNullOrWhiteSpace(sel.Address))
            {
                MessageBox.Show("请先点击“刷新”，并从列表中选择一个带唯一 Classic 蓝牙地址的设备。",
                    "蓝牙自动锁屏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            _cfg.DeviceAddress = sel.Address;
            _cfg.DeviceName = sel.DisplayName;
            _cfg.DisconnectDelaySeconds = (int)_delayNum.Value;
            _cfg.DisconnectConfirmSeconds = (int)_confirmNum.Value;
            _cfg.PollingIntervalSeconds = (int)_pollNum.Value;
            _cfg.LogLevel = _logLevelCombo.SelectedItem as string ?? "Info";
            _cfg.LogPath = _logPathText.Text.Trim();
            _cfg.LockScreenEnabled = _lockScreenChk.Checked;
            _cfg.LockShortcutsEnabled = _lockAppsChk.Checked;
            _cfg.LockShortcutPreDelayMilliseconds = (int)_lockShortcutPreDelayNum.Value;
            _cfg.LockShortcutSettleMilliseconds = (int)_lockShortcutSettleNum.Value;
            _cfg.LockShortcutMappings = new List<LockShortcutMapping>();
            foreach (LockShortcutMapping mapping in _lockShortcutMappings)
            {
                if (mapping == null || string.IsNullOrWhiteSpace(mapping.Shortcut)) continue;
                _cfg.LockShortcutMappings.Add(new LockShortcutMapping(mapping.Shortcut, mapping.Note, mapping.PreShortcut, mapping.TargetProcess));
            }

            _cfg.LolOptimizerEnabled = _lolEnableChk.Checked;
            _cfg.LolAutoCloseRemoteEnabled = _lolRemoteCloseChk.Checked;
            _cfg.LolProcessName = (_lolProcessText.Text ?? "").Trim();
            _cfg.VirtualDisplayDeviceId = (_lolVdIdText.Text ?? "").Trim();
            _cfg.LolOptimizerPollSeconds = (int)_lolPollNum.Value;

            _cfg.GameProblemMonitorEnabled = _gameMonitorEnableChk.Checked;
            _cfg.GameProblemMonitorAiEnabled = _gameAiEnableChk.Checked;
            _cfg.GameProblemMonitorIntervalMinutes = (int)_gameIntervalNum.Value;
            _cfg.GameProblemMonitorAiEndpoint = (_gameAiEndpointText.Text ?? "").Trim();
            _cfg.GameProblemMonitorAiModel = (_gameAiModelText.Text ?? "").Trim();
            _cfg.GameProblemMonitorAiApiKey = (_gameAiKeyText.Text ?? "").Trim();
            return true;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Interlocked.Increment(ref _deviceLoadGeneration);
            if (_liveTimer != null)
            {
                _liveTimer.Stop();
                _liveTimer.Dispose();
                _liveTimer = null;
            }
            base.OnFormClosed(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LoadDevices();
        }
    }
}
