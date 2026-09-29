using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BluetoothAutoLock
{
    internal static class Program
    {
        private static NativeMethods.ConsoleCtrlDelegate _ctrlHandler;
        private static Mutex _singleInstanceMutex;

        public const string Version = "1.1.33";
        private const string SingleInstanceMutexName = @"Local\BluetoothAutoLock";

        [STAThread]
        private static int Main(string[] args)
        {
            // .NET Framework 4.x 默认未启用 TLS 1.2，需显式开启，否则任何 HTTPS 调用
            // (如火山方舟 AI API) 都会失败："请求被中止: 未能创建 SSL/TLS 安全通道"。
            EnableModernTlsProtocols();

            bool listOnly = false;
            bool runOnce = false;
            bool printVersion = false;
            bool printHelp = false;
            bool configDialog = false;
            bool scanMode = false;
            bool testLockShortcuts = false;
            int scanSeconds = 12;

            for (int i = 0; i < args.Length; i++)
            {
                string s = (args[i] ?? "").Trim().ToLowerInvariant();
                if (s == "--list" || s == "-l") listOnly = true;
                else if (s == "--once") runOnce = true;
                else if (s == "--version" || s == "-v") printVersion = true;
                else if (s == "--help" || s == "-h" || s == "/?") printHelp = true;
                else if (s == "--config" || s == "--settings") configDialog = true;
                else if (s == "--test-lock-shortcuts") testLockShortcuts = true;
                else if (s == "--scan")
                {
                    scanMode = true;
                    int n;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out n) && n > 0) { scanSeconds = n; i++; }
                }
            }

            bool consoleMode = listOnly || runOnce || printVersion || printHelp || scanMode || testLockShortcuts;
            bool trayMode = !consoleMode && !configDialog;
            if (trayMode && !AcquireSingleInstance())
            {
                return 0;
            }

            try
            {
                if (consoleMode)
                {
                    NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
                    try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
                }

                if (printHelp) { PrintHelp(); return 0; }
                if (printVersion) { Console.WriteLine("BluetoothAutoLock " + Version); return 0; }

                Config cfg = Config.Load((lvl, msg) =>
                {
                    if (consoleMode) Console.Error.WriteLine("[" + lvl + "] " + msg);
                });
                Logger log = new Logger(cfg.LogPath, cfg.LogLevel, cfg.MaxLogSizeMB, alsoConsole: consoleMode);

                log.Info("Starting BluetoothAutoLock " + Version + " | " + cfg.Describe());

                if (listOnly) return DoList();

                if (scanMode) return DoScan(scanSeconds);

                if (testLockShortcuts) return DoTestLockShortcuts(cfg, log);

                if (configDialog) return RunConfigDialog(cfg, log);

                if (runOnce)
                {
                    try
                    {
                        var monitor = new BluetoothMonitor(cfg, log, () => true);
                        monitor.RunOnce();
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        log.Error("Fatal: " + ex);
                        return 2;
                    }
                }

                return RunTrayApp(cfg, log);
            }
            finally
            {
                ReleaseSingleInstance();
            }
        }

        private static void EnableModernTlsProtocols()
        {
            // 用数值常量 cast，确保即使 .NET Framework 早期版本也能编译；
            // Tls=192(0xC0), Tls11=768(0x300), Tls12=3072(0xC00)。
            try
            {
                SecurityProtocolType desired =
                    (SecurityProtocolType)192 |
                    (SecurityProtocolType)768 |
                    (SecurityProtocolType)3072;
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | desired;
            }
            catch
            {
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
                catch { }
            }
        }

        private static bool AcquireSingleInstance()
        {
            bool createdNew;
            try
            {
                _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
                if (!createdNew)
                {
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                }
                return createdNew;
            }
            catch
            {
                // Do not block startup if the OS denies or fails mutex creation.
                return true;
            }
        }

        private static void ReleaseSingleInstance()
        {
            if (_singleInstanceMutex == null) return;
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            try { _singleInstanceMutex.Dispose(); } catch { }
            _singleInstanceMutex = null;
        }

        private static int RunConfigDialog(Config cfg, Logger log)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new SettingsForm(cfg))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    string saved = Config.Save(cfg);
                    log.Info("Settings saved (standalone) to " + saved);
                    return 0;
                }
                log.Info("Settings dialog cancelled.");
                return 0;
            }
        }

        private static int RunTrayApp(Config cfg, Logger log)
        {
            InstallProcessHandlers(log);
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var ctx = new TrayApp(cfg, log))
                {
                    Application.Run(ctx);
                }
                log.Info("Tray application exited normally.");
                return 0;
            }
            catch (Exception ex)
            {
                log.Error("Fatal in tray app: " + ex);
                return 2;
            }
        }

        private static int DoScan(int seconds)
        {
            try
            {
                Console.WriteLine("Scanning for nearby Bluetooth devices for " + seconds + "s (Classic + LE)...");
                var hits = BluetoothScanner.Scan(seconds);
                Console.WriteLine();
                Console.WriteLine("Found " + hits.Count + " device(s):");
                Console.WriteLine("  KIND     ADDR                RSSI     LIVE  PAIR  CONN  NAME");
                foreach (var h in hits)
                {
                    string kind = h.Kind ?? "?";
                    string addr = (h.Address ?? "").PadRight(17);
                    string rssi = h.RssiDbm.HasValue ? (h.RssiDbm.Value + " dBm") : "  ?  ";
                    string live = h.LiveSignal ? "yes" : "no ";
                    string pair = h.Paired.HasValue ? (h.Paired.Value ? "yes" : "no ") : " ? ";
                    string conn = h.Connected.HasValue ? (h.Connected.Value ? "yes" : "no ") : " ? ";
                    Console.WriteLine(string.Format("  {0,-7}  {1}  {2,-7}  {3}   {4}   {5}   {6}",
                        kind, addr, rssi, live, pair, conn, h.Name ?? "(unnamed)"));
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Scan failed: " + ex.Message);
                return 1;
            }
        }

        private static int DoList()
        {
            try
            {
                var devices = BluetoothMonitor.EnumerateDevices();
                if (devices.Count == 0)
                {
                    Console.WriteLine("(no paired Bluetooth devices found)");
                    return 0;
                }

                Console.WriteLine("Paired Bluetooth devices (" + devices.Count + "):");
                Console.WriteLine("  STATE       ADDRESS            NAME");
                foreach (var d in devices)
                {
                    string state = d.Connected ? "CONNECTED" : (d.Authenticated ? "paired" : "remembered");
                    Console.WriteLine(string.Format("  {0,-11} {1}  {2}", state, d.AddressText, d.Name));
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("List failed: " + ex.Message);
                return 1;
            }
        }

        private static void InstallProcessHandlers(Logger log)
        {
            _ctrlHandler = ctrlType =>
            {
                log.Info("Console signal received (ctrlType=" + ctrlType + ").");
                Application.Exit();
                return true;
            };
            try { NativeMethods.SetConsoleCtrlHandler(_ctrlHandler, true); } catch { }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { log.Error("UnhandledException: " + (e.ExceptionObject ?? "<null>")); } catch { }
            };
            Application.ThreadException += (s, e) =>
            {
                try { log.Error("UI thread exception: " + e.Exception); } catch { }
            };
        }

        private static void PrintHelp()
        {
            Console.WriteLine("BluetoothAutoLock " + Version);
            Console.WriteLine("Locks Windows N seconds after a paired Bluetooth device disconnects.");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  BluetoothAutoLock.exe              Run as a tray application (default).");
            Console.WriteLine("  BluetoothAutoLock.exe --config     Open the settings dialog only (no tray).");
            Console.WriteLine("  BluetoothAutoLock.exe --list       List all paired Bluetooth devices.");
            Console.WriteLine("  BluetoothAutoLock.exe --once       Run a single check pass and exit.");
            Console.WriteLine("  BluetoothAutoLock.exe --test-lock-shortcuts");
            Console.WriteLine("                                            Trigger configured lock shortcuts only, then exit.");
            Console.WriteLine("  BluetoothAutoLock.exe --version    Print version.");
            Console.WriteLine("  BluetoothAutoLock.exe --help       Show this help.");
            Console.WriteLine();
            Console.WriteLine("Configuration is read from <exe-dir>/config.ini, then %ProgramData%/BluetoothAutoLock/config.ini.");
            Console.WriteLine("Right-click the tray icon to open Settings and edit configuration interactively.");
        }

        private static int DoTestLockShortcuts(Config cfg, Logger log)
        {
            if (cfg.LockShortcutMappings == null || cfg.LockShortcutMappings.Count == 0)
            {
                log.Warn("Manual lock shortcut test requested but no shortcuts are configured.");
                Console.WriteLine("未配置锁屏快捷键。");
                return 1;
            }

            log.Info("Manual lock shortcut test requested; triggering " + cfg.LockShortcutMappings.Count + " shortcut(s), no workstation lock.");
            int sent = LockShortcutRunner.TriggerAll(
                cfg.LockShortcutMappings,
                message => log.Info(message),
                message => log.Warn(message));

            int settleMs = Math.Max(0, Math.Min(10000, cfg.LockShortcutSettleMilliseconds));
            if (settleMs > 0)
            {
                log.Info("Manual lock shortcut test waiting " + settleMs + "ms so global hotkey handlers can run.");
                Thread.Sleep(settleMs);
            }

            log.Info("Manual lock shortcut test completed; triggered " + sent + "/" + cfg.LockShortcutMappings.Count + " shortcut(s).");
            Console.WriteLine("后台测试已触发 " + sent + "/" + cfg.LockShortcutMappings.Count + " 个锁屏快捷键。");
            return sent > 0 ? 0 : 1;
        }
    }
}
