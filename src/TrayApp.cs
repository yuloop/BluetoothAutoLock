using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BluetoothAutoLock
{
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly Config _cfg;
        private readonly Logger _log;

        private NotifyIcon _icon;
        private ToolStripMenuItem _statusItem;
        private ToolStripMenuItem _lolStatusItem;
        private ToolStripMenuItem _gameMonitorStatusItem;
        private ToolStripMenuItem _pauseItem;
        private ToolStripMenuItem _lolToggleItem;
        private ToolStripMenuItem _gameMonitorToggleItem;
        private ToolStripMenuItem _gameOptimizeItem;
        private System.Windows.Forms.Timer _statusTimer;
        private System.Windows.Forms.Timer _trimTimer;
        private Control _uiInvoker;
        private SettingsForm _settingsForm;

        private sealed class StopSignal
        {
            public volatile bool Requested;
        }

        private readonly object _monitorGate = new object();
        private BluetoothMonitor _monitor;
        private Thread _monitorThread;
        private StopSignal _monitorStop;
        private volatile bool _paused;

        private readonly object _lolGate = new object();
        private LolOptimizer _lolOptimizer;
        private Thread _lolThread;
        private StopSignal _lolStop;

        private readonly object _gameMonitorGate = new object();
        private GameEnvironmentMonitor _gameMonitor;
        private Thread _gameMonitorThread;
        private StopSignal _gameMonitorStop;
        private volatile bool _gameOptimizeRunning;

        public TrayApp(Config cfg, Logger log)
        {
            _cfg = cfg;
            _log = log;

            BuildTray();
            StartMonitor();
            if (_cfg.LolOptimizerEnabled) StartLolOptimizer();
            if (_cfg.GameProblemMonitorEnabled) StartGameProblemMonitor();

            _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _statusTimer.Tick += (s, e) => UpdateStatusUi();
            _statusTimer.Start();

            // Trim once shortly after startup (lets all assemblies finish loading first).
            // Do not force a periodic full GC during normal long-running operation.
            _trimTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _trimTimer.Tick += (s, e) =>
            {
                _trimTimer.Stop();
                _trimTimer.Dispose();
                _trimTimer = null;
                NativeMethods.TrimWorkingSet();
            };
            _trimTimer.Start();

            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        private void BuildTray()
        {
            _uiInvoker = new Control();
            _uiInvoker.CreateControl();
            if (_uiInvoker.Handle == IntPtr.Zero) { }

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("版本：" + Program.Version) { Enabled = false });
            _statusItem = new ToolStripMenuItem("状态：正在启动...") { Enabled = false };
            menu.Items.Add(_statusItem);
            _lolStatusItem = new ToolStripMenuItem("LoL 优化：未启用") { Enabled = false };
            menu.Items.Add(_lolStatusItem);
            _gameMonitorStatusItem = new ToolStripMenuItem("游戏环境监控：未启用") { Enabled = false };
            menu.Items.Add(_gameMonitorStatusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("设置...", null, (s, e) => OpenSettings()));
            _pauseItem = new ToolStripMenuItem("暂停监控", null, (s, e) => TogglePause());
            menu.Items.Add(_pauseItem);
            _gameOptimizeItem = new ToolStripMenuItem("智能优化游戏环境（游戏前运行）", null, (s, e) => RunSmartGameOptimization());
            menu.Items.Add(_gameOptimizeItem);
            _lolToggleItem = new ToolStripMenuItem(
                _cfg.LolOptimizerEnabled ? "关闭 LoL 自动优化" : "启用 LoL 自动优化",
                null, (s, e) => ToggleLolOptimizer());
            menu.Items.Add(_lolToggleItem);
            _gameMonitorToggleItem = new ToolStripMenuItem(
                _cfg.GameProblemMonitorEnabled ? "关闭游戏环境异常监控" : "启用游戏环境异常监控",
                null, (s, e) => ToggleGameProblemMonitor());
            menu.Items.Add(_gameMonitorToggleItem);
            menu.Items.Add(new ToolStripMenuItem("打开日志文件夹", null, (s, e) => OpenLogFolder()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("关于", null, (s, e) => ShowAbout()));
            menu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => QuitApp()));

            _icon = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = "蓝牙自动锁屏 — 正在启动...",
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.DoubleClick += (s, e) => OpenSettings();
        }

        private void StartMonitor()
        {
            if (_paused) return;

            lock (_monitorGate)
            {
                if (_monitorThread != null && _monitorThread.IsAlive) return;

                var stop = new StopSignal();
                var monitor = new BluetoothMonitor(_cfg, _log, () => stop.Requested);
                var thread = new Thread(monitor.RunLoop)
                {
                    IsBackground = true,
                    Name = "BTMonitor"
                };

                _monitorStop = stop;
                _monitor = monitor;
                _monitorThread = thread;
                thread.Start();
            }
        }

        private bool StopMonitor()
        {
            StopSignal stop;
            Thread thread;

            lock (_monitorGate)
            {
                stop = _monitorStop;
                thread = _monitorThread;
                if (stop != null) stop.Requested = true;
            }

            if (thread != null && thread.IsAlive && !thread.Join(5000))
            {
                _log.Warn("Monitor thread did not stop within 5s; keeping old instance and skipping duplicate start.");
                return false;
            }

            lock (_monitorGate)
            {
                if (object.ReferenceEquals(_monitorThread, thread))
                {
                    _monitorThread = null;
                    _monitorStop = null;
                }
            }

            return true;
        }

        private void StartLolOptimizer()
        {
            lock (_lolGate)
            {
                if (_lolThread != null && _lolThread.IsAlive) return;

                var stop = new StopSignal();
                var optimizer = new LolOptimizer(_cfg, _log, () => stop.Requested);
                var thread = new Thread(optimizer.RunLoop)
                {
                    IsBackground = true,
                    Name = "LolOptimizer"
                };

                _lolStop = stop;
                _lolOptimizer = optimizer;
                _lolThread = thread;
                thread.Start();
            }
            _log.Info("LoL optimizer started by user.");
        }

        private bool StopLolOptimizer()
        {
            StopSignal stop;
            Thread thread;

            lock (_lolGate)
            {
                stop = _lolStop;
                thread = _lolThread;
                if (stop != null) stop.Requested = true;
            }

            if (thread != null && thread.IsAlive && !thread.Join(8000))
            {
                _log.Warn("LolOptimizer thread did not stop within 8s.");
                return false;
            }

            lock (_lolGate)
            {
                if (object.ReferenceEquals(_lolThread, thread))
                {
                    _lolThread = null;
                    _lolStop = null;
                    _lolOptimizer = null;
                }
            }

            return true;
        }

        private void StartGameProblemMonitor()
        {
            lock (_gameMonitorGate)
            {
                if (_gameMonitorThread != null && _gameMonitorThread.IsAlive) return;

                var stop = new StopSignal();
                var monitor = new GameEnvironmentMonitor(_cfg, _log, () => stop.Requested, OnGameEnvironmentNotification);
                var thread = new Thread(monitor.RunLoop)
                {
                    IsBackground = true,
                    Name = "GameEnvMonitor"
                };

                _gameMonitorStop = stop;
                _gameMonitor = monitor;
                _gameMonitorThread = thread;
                thread.Start();
            }
            _log.Info("Game environment monitor started by user/config.");
        }

        private bool StopGameProblemMonitor()
        {
            StopSignal stop;
            Thread thread;

            lock (_gameMonitorGate)
            {
                stop = _gameMonitorStop;
                thread = _gameMonitorThread;
                if (stop != null) stop.Requested = true;
            }

            if (thread != null && thread.IsAlive && !thread.Join(5000))
            {
                _log.Warn("Game environment monitor thread did not stop within 5s.");
                return false;
            }

            lock (_gameMonitorGate)
            {
                if (object.ReferenceEquals(_gameMonitorThread, thread))
                {
                    _gameMonitorThread = null;
                    _gameMonitorStop = null;
                    _gameMonitor = null;
                }
            }

            return true;
        }

        private void ToggleGameProblemMonitor()
        {
            bool willEnable = !_cfg.GameProblemMonitorEnabled;
            _cfg.GameProblemMonitorEnabled = willEnable;

            try { Config.Save(_cfg); }
            catch (Exception ex) { _log.Warn("Failed to persist GameProblemMonitorEnabled: " + ex.Message); }

            if (willEnable)
            {
                StartGameProblemMonitor();
            }
            else
            {
                StopGameProblemMonitor();
                _log.Info("Game environment monitor stopped by user.");
            }
            UpdateStatusUi();
        }

        private void RunSmartGameOptimization()
        {
            if (_gameOptimizeRunning) return;
            _gameOptimizeRunning = true;
            if (_gameOptimizeItem != null)
            {
                _gameOptimizeItem.Enabled = false;
                _gameOptimizeItem.Text = "正在智能优化游戏环境...";
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                GameEnvironmentOptimizationResult result = null;
                string error = null;
                try
                {
                    result = GameEnvironmentOptimizer.RunManualOptimization(_cfg, _log);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _log.Warn("Smart game environment optimization failed: " + ex);
                }

                RunOnUi(() =>
                {
                    _gameOptimizeRunning = false;
                    if (_gameOptimizeItem != null)
                    {
                        _gameOptimizeItem.Enabled = true;
                        _gameOptimizeItem.Text = "智能优化游戏环境（游戏前运行）";
                    }

                    if (!string.IsNullOrEmpty(error))
                    {
                        ShowBalloon("智能优化失败", error, ToolTipIcon.Warning);
                    }
                    else if (result != null)
                    {
                        ToolTipIcon icon = result.Changed ? ToolTipIcon.Info : ToolTipIcon.None;
                        ShowBalloon("智能优化游戏环境", result.BuildUserSummary(), icon);
                    }
                    UpdateStatusUi();
                });
            });
        }

        private void OnGameEnvironmentNotification(GameEnvironmentNotification notification)
        {
            if (notification == null) return;
            RunOnUi(() =>
            {
                ShowBalloon(notification.Title, notification.Message, ToolTipIcon.Warning);
            });
        }

        private void RunOnUi(Action action)
        {
            if (action == null) return;
            try
            {
                if (_uiInvoker != null && _uiInvoker.IsHandleCreated && _uiInvoker.InvokeRequired)
                    _uiInvoker.BeginInvoke(action);
                else
                    action();
            }
            catch
            {
                try { action(); } catch { }
            }
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            try
            {
                if (_icon == null) return;
                if (string.IsNullOrEmpty(title)) title = "蓝牙自动锁屏";
                if (string.IsNullOrEmpty(text)) text = "";
                if (text.Length > 240) text = text.Substring(0, 237) + "...";
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = text;
                _icon.BalloonTipIcon = icon;
                _icon.ShowBalloonTip(8000);
            }
            catch (Exception ex)
            {
                _log.Warn("ShowBalloon failed: " + ex.Message);
            }
        }

        private void ToggleLolOptimizer()
        {
            bool willEnable = !_cfg.LolOptimizerEnabled;
            _cfg.LolOptimizerEnabled = willEnable;

            try { Config.Save(_cfg); }
            catch (Exception ex) { _log.Warn("Failed to persist LolOptimizerEnabled: " + ex.Message); }

            if (willEnable)
            {
                StartLolOptimizer();
            }
            else
            {
                StopLolOptimizer();
                _log.Info("LoL optimizer stopped by user.");
            }
            UpdateStatusUi();
        }

        private void TogglePause()
        {
            _paused = !_paused;
            _pauseItem.Text = _paused ? "恢复监控" : "暂停监控";
            _log.Info(_paused ? "Monitor paused by user." : "Monitor resumed by user.");
            if (_paused)
            {
                if (_monitor != null) _monitor.OverrideStatusForUi("已暂停", "等待用户恢复监控");
                StopMonitor();
            }
            else
            {
                StartMonitor();
            }
            UpdateStatusUi();
        }

        private MonitorStatusSnapshot GetMonitorStatusSnapshot()
        {
            if (_paused) return MonitorStatusSnapshot.Create("已暂停", "等待用户恢复监控");

            BluetoothMonitor monitor;
            lock (_monitorGate)
            {
                monitor = _monitor;
            }
            if (monitor == null) return MonitorStatusSnapshot.Create("初始化中", "等待监控线程启动");
            return monitor.GetStatusSnapshot();
        }

        private void UpdateStatusUi()
        {
            MonitorStatusSnapshot snapshot = GetMonitorStatusSnapshot();
            string status = snapshot != null ? snapshot.Status : "初始化中";
            if (string.IsNullOrEmpty(status)) status = "初始化中";

            _statusItem.Text = "状态：" + status;
            string tip = "蓝牙自动锁屏 — " + status;
            if (tip.Length > 63) tip = tip.Substring(0, 60) + "...";
            _icon.Text = tip;

            if (_lolStatusItem != null)
            {
                string lolStatus;
                if (!_cfg.LolOptimizerEnabled) lolStatus = "未启用";
                else if (_lolOptimizer != null) lolStatus = _lolOptimizer.CurrentStatusText ?? "运行中";
                else lolStatus = "启动中";
                _lolStatusItem.Text = "LoL 优化：" + lolStatus;
            }
            if (_lolToggleItem != null)
            {
                _lolToggleItem.Text = _cfg.LolOptimizerEnabled ? "关闭 LoL 自动优化" : "启用 LoL 自动优化";
            }
            if (_gameMonitorStatusItem != null)
            {
                string monitorStatus;
                if (!_cfg.GameProblemMonitorEnabled) monitorStatus = "未启用";
                else if (_gameMonitor != null) monitorStatus = _gameMonitor.CurrentStatusText ?? "运行中";
                else monitorStatus = "启动中";
                _gameMonitorStatusItem.Text = "游戏环境监控：" + monitorStatus;
            }
            if (_gameMonitorToggleItem != null)
            {
                _gameMonitorToggleItem.Text = _cfg.GameProblemMonitorEnabled ? "关闭游戏环境异常监控" : "启用游戏环境异常监控";
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e == null) return;
            if (e.Reason != SessionSwitchReason.SessionUnlock &&
                e.Reason != SessionSwitchReason.SessionLogon)
                return;

            BluetoothMonitor monitor;
            lock (_monitorGate)
            {
                monitor = _monitor;
            }
            if (monitor == null) return;

            try { monitor.ReArmAfterSessionUnlock(); }
            catch (Exception ex) { _log.Warn("Session unlock re-arm failed: " + ex.Message); }
        }

        private void OpenSettings()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                try
                {
                    if (_settingsForm.WindowState == FormWindowState.Minimized)
                        _settingsForm.WindowState = FormWindowState.Normal;
                    _settingsForm.Show();
                    _settingsForm.Activate();
                    _settingsForm.BringToFront();
                }
                catch { }
                return;
            }

            using (var form = new SettingsForm(_cfg, GetMonitorStatusSnapshot))
            {
                _settingsForm = form;
                try
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        string saved;
                        try
                        {
                            saved = Config.Save(_cfg);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("保存配置失败：" + ex.Message, "蓝牙自动锁屏",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        _log.Info("Settings saved to " + saved + ". Restarting monitor.");
                        if (!_paused)
                        {
                            if (StopMonitor()) StartMonitor();
                        }
                        else if (_monitor != null)
                        {
                            _monitor.OverrideStatusForUi("已暂停", "等待用户恢复监控");
                        }

                        // 重启 LolOptimizer 以应用新配置
                        StopLolOptimizer();
                        if (_cfg.LolOptimizerEnabled) StartLolOptimizer();

                        // 重启游戏环境监控以应用间隔 / AI 配置
                        StopGameProblemMonitor();
                        if (_cfg.GameProblemMonitorEnabled) StartGameProblemMonitor();
                        UpdateStatusUi();
                    }
                }
                finally
                {
                    _settingsForm = null;
                }
            }
            NativeMethods.TrimWorkingSet();
        }

        private void OpenLogFolder()
        {
            try
            {
                string dir = Path.GetDirectoryName(_cfg.LogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(_cfg.LogPath))
                    Process.Start("explorer.exe", "/select,\"" + _cfg.LogPath + "\"");
                else if (!string.IsNullOrEmpty(dir))
                    Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                _log.Warn("OpenLogFolder failed: " + ex.Message);
            }
        }

        private void ShowAbout()
        {
            string msg =
                "蓝牙自动锁屏 " + Program.Version + "\n\n" +
                "当配置的蓝牙设备断开后，按设定秒数延时自动锁定 Windows。\n\n" +
                "配置文件：" + (_cfg.LoadedFrom ?? "<默认值>") + "\n" +
                "日志文件：" + _cfg.LogPath;
            MessageBox.Show(msg, "关于 蓝牙自动锁屏", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void QuitApp()
        {
            try
            {
                try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
                if (_statusTimer != null) { _statusTimer.Stop(); _statusTimer.Dispose(); }
                if (_trimTimer != null) { _trimTimer.Stop(); _trimTimer.Dispose(); }
                if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
                StopMonitor();
                StopLolOptimizer();
                StopGameProblemMonitor();
                if (_uiInvoker != null) { _uiInvoker.Dispose(); _uiInvoker = null; }
            }
            finally
            {
                ExitThread();
            }
        }
    }
}
