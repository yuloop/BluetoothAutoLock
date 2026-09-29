using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BluetoothAutoLock
{
    internal sealed class Config
    {
        public string DeviceName { get; set; }
        public string DeviceAddress { get; set; }
        public int DisconnectDelaySeconds { get; set; }
        public int DisconnectConfirmSeconds { get; set; }
        public int PollingIntervalSeconds { get; set; }
        public string LogPath { get; set; }
        public string LogLevel { get; set; }
        public int MaxLogSizeMB { get; set; }
        public string LoadedFrom { get; set; }
        public List<LockShortcutMapping> LockShortcutMappings { get; set; }
        public int LockShortcutSettleMilliseconds { get; set; }
        public bool LockScreenEnabled { get; set; }
        public bool LockShortcutsEnabled { get; set; }
        public string WeChatShowWindowShortcut { get; set; }

        // === LoL 启动/退出 → 游戏性能自动优化（可选） ===
        public bool LolOptimizerEnabled { get; set; }
        public string LolProcessName { get; set; }
        public string VirtualDisplayDeviceId { get; set; }
        public int LolOptimizerPollSeconds { get; set; }
        public bool LolWslOptimizeEnabled { get; set; }
        public string WslDistro { get; set; }
        public bool LolAutoCloseRemoteEnabled { get; set; }
        public string RemoteCloseProcessNames { get; set; }
        public int RemoteRestartQuietMinutes { get; set; }

        // === 游戏环境智能优化 / 异常监控 ===
        public bool GameProblemMonitorEnabled { get; set; }
        public int GameProblemMonitorIntervalMinutes { get; set; }
        public bool GameProblemMonitorAiEnabled { get; set; }
        public string GameProblemMonitorAiEndpoint { get; set; }
        public string GameProblemMonitorAiModel { get; set; }
        public string GameProblemMonitorAiApiKey { get; set; }
        public string GameProblemMonitorAiApiKeyEnv { get; set; }
        public int GameProblemNotifyRepeatMinutes { get; set; }
        public int GameProblemRunawayCpuPercent { get; set; }
        public int GameOptimizerToolZombieCountThreshold { get; set; }
        public int GameOptimizerToolZombieMemoryMB { get; set; }
        public int GameOptimizerLolRenderCountThreshold { get; set; }
        public string GameOptimizerToolCleanupProcessNames { get; set; }
        public string GameOptimizerLolCleanupProcessNames { get; set; }

        public static Config Defaults()
        {
            return new Config
            {
                DeviceName = "",
                DeviceAddress = "",
                DisconnectDelaySeconds = 150,
                DisconnectConfirmSeconds = 3,
                PollingIntervalSeconds = 8,
                LogPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "BluetoothAutoLock", "service.log"),
                LogLevel = "Info",
                MaxLogSizeMB = 5,
                LoadedFrom = "<defaults>",
                LockShortcutMappings = new List<LockShortcutMapping>(),
                LockShortcutSettleMilliseconds = 3000,
                LockScreenEnabled = true,
                LockShortcutsEnabled = true,
                WeChatShowWindowShortcut = "Ctrl+Alt+W",

                LolOptimizerEnabled = false,
                LolProcessName = "League of Legends",
                VirtualDisplayDeviceId = "",
                LolOptimizerPollSeconds = 3,
                LolWslOptimizeEnabled = true,
                WslDistro = "Ubuntu",
                LolAutoCloseRemoteEnabled = true,
                RemoteCloseProcessNames = "AskLink,ToDesk",
                RemoteRestartQuietMinutes = 10,

                GameProblemMonitorEnabled = true,
                GameProblemMonitorIntervalMinutes = 5,
                GameProblemMonitorAiEnabled = true,
                GameProblemMonitorAiEndpoint = "https://ark.cn-beijing.volces.com/api/v3/responses",
                GameProblemMonitorAiModel = "doubao-seed-2-0-lite-260215",
                GameProblemMonitorAiApiKey = "",
                GameProblemMonitorAiApiKeyEnv = "ARK_API_KEY",
                GameProblemNotifyRepeatMinutes = 30,
                GameProblemRunawayCpuPercent = 90,
                GameOptimizerToolZombieCountThreshold = 20,
                GameOptimizerToolZombieMemoryMB = 2048,
                GameOptimizerLolRenderCountThreshold = 2,
                GameOptimizerToolCleanupProcessNames = "node,omx-node-stdio-hidden,python",
                GameOptimizerLolCleanupProcessNames = "LeagueCrashHandler64,LeagueClientUxRender,LeagueClient,League of Legends,RiotClientServices"
            };
        }

        public static IEnumerable<string> SearchPaths()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            yield return Path.Combine(exeDir, "config.ini");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "BluetoothAutoLock", "config.ini");
        }

        public static Config Load(Action<string, string> warn)
        {
            Config cfg = Defaults();
            foreach (string path in SearchPaths())
            {
                if (File.Exists(path))
                {
                    try
                    {
                        ApplyIni(cfg, path, warn);
                        cfg.LoadedFrom = path;
                        return cfg;
                    }
                    catch (Exception ex)
                    {
                        if (warn != null) warn("Warn", "Failed to read " + path + ": " + ex.Message);
                    }
                }
            }
            return cfg;
        }

        private static void ApplyIni(Config cfg, string path, Action<string, string> warn)
        {
            cfg.LockShortcutMappings = new List<LockShortcutMapping>();
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (raw == null) continue;
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#' || line[0] == ';') continue;
                if (line.StartsWith("[") && line.EndsWith("]")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();

                Apply(cfg, key, value, path, i + 1, warn);
            }

            if (cfg.DisconnectDelaySeconds < 1) cfg.DisconnectDelaySeconds = 1;
            if (cfg.DisconnectDelaySeconds > 3600) cfg.DisconnectDelaySeconds = 3600;
            if (cfg.DisconnectConfirmSeconds < 1) cfg.DisconnectConfirmSeconds = 1;
            if (cfg.DisconnectConfirmSeconds > 10) cfg.DisconnectConfirmSeconds = 10;
            if (cfg.PollingIntervalSeconds < 1) cfg.PollingIntervalSeconds = 1;
            if (cfg.PollingIntervalSeconds > 60) cfg.PollingIntervalSeconds = 60;
            if (cfg.MaxLogSizeMB < 1) cfg.MaxLogSizeMB = 1;
            if (cfg.MaxLogSizeMB > 1024) cfg.MaxLogSizeMB = 1024;
            if (cfg.LockShortcutSettleMilliseconds < 0) cfg.LockShortcutSettleMilliseconds = 0;
            if (cfg.LockShortcutSettleMilliseconds > 10000) cfg.LockShortcutSettleMilliseconds = 10000;

            if (cfg.LolOptimizerPollSeconds < 1) cfg.LolOptimizerPollSeconds = 1;
            if (cfg.LolOptimizerPollSeconds > 60) cfg.LolOptimizerPollSeconds = 60;
            if (string.IsNullOrWhiteSpace(cfg.WslDistro)) cfg.WslDistro = "Ubuntu";
            if (string.IsNullOrWhiteSpace(cfg.RemoteCloseProcessNames)) cfg.RemoteCloseProcessNames = "AskLink,ToDesk";
            if (cfg.RemoteRestartQuietMinutes < 1) cfg.RemoteRestartQuietMinutes = 1;
            if (cfg.RemoteRestartQuietMinutes > 120) cfg.RemoteRestartQuietMinutes = 120;

            if (cfg.GameProblemMonitorIntervalMinutes < 1) cfg.GameProblemMonitorIntervalMinutes = 1;
            if (cfg.GameProblemMonitorIntervalMinutes > 1440) cfg.GameProblemMonitorIntervalMinutes = 1440;
            if (string.IsNullOrWhiteSpace(cfg.GameProblemMonitorAiEndpoint)) cfg.GameProblemMonitorAiEndpoint = "https://ark.cn-beijing.volces.com/api/v3/responses";
            if (string.IsNullOrWhiteSpace(cfg.GameProblemMonitorAiModel)) cfg.GameProblemMonitorAiModel = "doubao-seed-2-0-lite-260215";
            if (string.IsNullOrWhiteSpace(cfg.GameProblemMonitorAiApiKeyEnv)) cfg.GameProblemMonitorAiApiKeyEnv = "ARK_API_KEY";
            if (cfg.GameProblemNotifyRepeatMinutes < 5) cfg.GameProblemNotifyRepeatMinutes = 5;
            if (cfg.GameProblemNotifyRepeatMinutes > 1440) cfg.GameProblemNotifyRepeatMinutes = 1440;
            if (cfg.GameProblemRunawayCpuPercent < 50) cfg.GameProblemRunawayCpuPercent = 50;
            if (cfg.GameProblemRunawayCpuPercent > 800) cfg.GameProblemRunawayCpuPercent = 800;
            if (cfg.GameOptimizerToolZombieCountThreshold < 3) cfg.GameOptimizerToolZombieCountThreshold = 3;
            if (cfg.GameOptimizerToolZombieCountThreshold > 500) cfg.GameOptimizerToolZombieCountThreshold = 500;
            if (cfg.GameOptimizerToolZombieMemoryMB < 256) cfg.GameOptimizerToolZombieMemoryMB = 256;
            if (cfg.GameOptimizerToolZombieMemoryMB > 65536) cfg.GameOptimizerToolZombieMemoryMB = 65536;
            if (cfg.GameOptimizerLolRenderCountThreshold < 1) cfg.GameOptimizerLolRenderCountThreshold = 1;
            if (cfg.GameOptimizerLolRenderCountThreshold > 20) cfg.GameOptimizerLolRenderCountThreshold = 20;
            if (string.IsNullOrWhiteSpace(cfg.GameOptimizerToolCleanupProcessNames)) cfg.GameOptimizerToolCleanupProcessNames = "node,omx-node-stdio-hidden,python";
            if (string.IsNullOrWhiteSpace(cfg.GameOptimizerLolCleanupProcessNames)) cfg.GameOptimizerLolCleanupProcessNames = "LeagueCrashHandler64,LeagueClientUxRender,LeagueClient,League of Legends,RiotClientServices";
        }

        private static bool ParseBoolValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string t = value.Trim().ToLowerInvariant();
            return t == "true" || t == "1" || t == "yes" || t == "on" || t == "enable" || t == "enabled";
        }

        private static void Apply(Config cfg, string key, string value, string path, int lineNo, Action<string, string> warn)
        {
            int intVal;
            if (IsLockShortcutKey(key))
            {
                LockShortcutMapping mapping;
                if (LockShortcutMapping.TryParseConfigValue(value, out mapping))
                    cfg.LockShortcutMappings.Add(mapping);
                else if (warn != null)
                    warn("Warn", path + ":" + lineNo + " invalid lock shortcut mapping: " + value);
                return;
            }

            switch (key)
            {
                case "DeviceName": cfg.DeviceName = value; break;
                case "DeviceAddress": cfg.DeviceAddress = value; break;
                case "DisconnectDelaySeconds":
                    if (int.TryParse(value, out intVal)) cfg.DisconnectDelaySeconds = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for DisconnectDelaySeconds: " + value);
                    break;
                case "DisconnectConfirmSeconds":
                    if (int.TryParse(value, out intVal)) cfg.DisconnectConfirmSeconds = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for DisconnectConfirmSeconds: " + value);
                    break;
                case "PollingIntervalSeconds":
                    if (int.TryParse(value, out intVal)) cfg.PollingIntervalSeconds = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for PollingIntervalSeconds: " + value);
                    break;
                case "LogPath": cfg.LogPath = value; break;
                case "LogLevel": cfg.LogLevel = value; break;
                case "MaxLogSizeMB":
                    if (int.TryParse(value, out intVal)) cfg.MaxLogSizeMB = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for MaxLogSizeMB: " + value);
                    break;
                case "LockShortcutSettleMilliseconds":
                    if (int.TryParse(value, out intVal)) cfg.LockShortcutSettleMilliseconds = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for LockShortcutSettleMilliseconds: " + value);
                    break;
                case "LockScreenEnabled": cfg.LockScreenEnabled = ParseBoolValue(value); break;
                case "LockShortcutsEnabled": cfg.LockShortcutsEnabled = ParseBoolValue(value); break;
                case "WeChatShowWindowShortcut":
                    string showShortcut;
                    if (string.IsNullOrWhiteSpace(value)) cfg.WeChatShowWindowShortcut = "";
                    else if (LockShortcutMapping.TryNormalizeShortcutText(value, out showShortcut)) cfg.WeChatShowWindowShortcut = showShortcut;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid shortcut for WeChatShowWindowShortcut: " + value);
                    break;
                case "LolOptimizerEnabled": cfg.LolOptimizerEnabled = ParseBoolValue(value); break;
                case "LolProcessName": cfg.LolProcessName = value; break;
                case "VirtualDisplayDeviceId": cfg.VirtualDisplayDeviceId = value; break;
                case "LolWslOptimizeEnabled": cfg.LolWslOptimizeEnabled = ParseBoolValue(value); break;
                case "WslDistro": cfg.WslDistro = value; break;
                case "LolAutoCloseRemoteEnabled": cfg.LolAutoCloseRemoteEnabled = ParseBoolValue(value); break;
                case "RemoteCloseProcessNames": cfg.RemoteCloseProcessNames = value; break;
                case "RemoteRestartQuietMinutes":
                    if (int.TryParse(value, out intVal)) cfg.RemoteRestartQuietMinutes = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for RemoteRestartQuietMinutes: " + value);
                    break;
                case "GameProblemMonitorEnabled": cfg.GameProblemMonitorEnabled = ParseBoolValue(value); break;
                case "GameProblemMonitorIntervalMinutes":
                    if (int.TryParse(value, out intVal)) cfg.GameProblemMonitorIntervalMinutes = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for GameProblemMonitorIntervalMinutes: " + value);
                    break;
                case "GameProblemMonitorAiEnabled": cfg.GameProblemMonitorAiEnabled = ParseBoolValue(value); break;
                case "GameProblemMonitorAiEndpoint": cfg.GameProblemMonitorAiEndpoint = value; break;
                case "GameProblemMonitorAiModel": cfg.GameProblemMonitorAiModel = value; break;
                case "GameProblemMonitorAiApiKey":
                    cfg.GameProblemMonitorAiApiKey = value;
                    if (!string.IsNullOrWhiteSpace(value) && warn != null)
                        warn("Warn", path + ":" + lineNo + " GameProblemMonitorAiApiKey 明文写在 config.ini 存在泄露风险，建议改用环境变量 " + (string.IsNullOrWhiteSpace(cfg.GameProblemMonitorAiApiKeyEnv) ? "ARK_API_KEY" : cfg.GameProblemMonitorAiApiKeyEnv) + "，并确保不要提交该文件到 Git。");
                    break;
                case "GameProblemMonitorAiApiKeyEnv": cfg.GameProblemMonitorAiApiKeyEnv = value; break;
                case "GameProblemNotifyRepeatMinutes":
                    if (int.TryParse(value, out intVal)) cfg.GameProblemNotifyRepeatMinutes = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for GameProblemNotifyRepeatMinutes: " + value);
                    break;
                case "GameProblemRunawayCpuPercent":
                    if (int.TryParse(value, out intVal)) cfg.GameProblemRunawayCpuPercent = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for GameProblemRunawayCpuPercent: " + value);
                    break;
                case "GameOptimizerToolZombieCountThreshold":
                    if (int.TryParse(value, out intVal)) cfg.GameOptimizerToolZombieCountThreshold = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for GameOptimizerToolZombieCountThreshold: " + value);
                    break;
                case "GameOptimizerToolZombieMemoryMB":
                    if (int.TryParse(value, out intVal)) cfg.GameOptimizerToolZombieMemoryMB = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for GameOptimizerToolZombieMemoryMB: " + value);
                    break;
                case "GameOptimizerLolRenderCountThreshold":
                    if (int.TryParse(value, out intVal)) cfg.GameOptimizerLolRenderCountThreshold = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for GameOptimizerLolRenderCountThreshold: " + value);
                    break;
                case "GameOptimizerToolCleanupProcessNames": cfg.GameOptimizerToolCleanupProcessNames = value; break;
                case "GameOptimizerLolCleanupProcessNames": cfg.GameOptimizerLolCleanupProcessNames = value; break;
                // 兼容旧版本字段（当前已废弃，仅吃掉避免警告）
                case "LolPriority":
                case "LolAffinityMask":
                case "LolAntiCheatProcesses":
                case "LolAntiCheatAffinityMask":
                case "WslCpuThreads":
                case "WslCpuPriority":
                    break;
                case "LolOptimizerPollSeconds":
                    if (int.TryParse(value, out intVal)) cfg.LolOptimizerPollSeconds = intVal;
                    else if (warn != null) warn("Warn", path + ":" + lineNo + " invalid integer for LolOptimizerPollSeconds: " + value);
                    break;
                default:
                    if (warn != null) warn("Warn", path + ":" + lineNo + " unknown key: " + key);
                    break;
            }
        }

        public string Describe()
        {
            return string.Format(
                "Config(loadedFrom={0}, DeviceName='{1}', DeviceAddress='{2}', IdleBeforeBluetoothCheck=30s, BluetoothAbsenceBeforeLock={3}s, FinalRecheckAttempts={4}, Polling=random {5}-{13}s, LogPath={6}, LogLevel={7}, MaxLogSizeMB={8}, LolOpt={9}, LolProc='{10}', VDId='{11}', LolPoll={12}s, WslCacheReclaim={14}, WslDistro='{15}', RemoteClose={16}, RemoteQuiet={17}m, GameMonitor={18}, GameMonitorInterval={19}m, GameAi={20}, GameAiModel='{21}', GameAiKey={22}, ToolZombieCount={23}, ToolZombieMemMB={24}, LolRenderThreshold={25}, LockShortcuts={26}, LockShortcutSettleMs={27}, LockScreen={28}, LockWeChatQQ={29}, WeChatShowKey='{30}')",
                LoadedFrom, DeviceName, DeviceAddress, DisconnectDelaySeconds, DisconnectConfirmSeconds, PollingIntervalSeconds, LogPath, LogLevel, MaxLogSizeMB,
                LolOptimizerEnabled, LolProcessName, VirtualDisplayDeviceId, LolOptimizerPollSeconds,
                Math.Max(1, Math.Min(60, PollingIntervalSeconds + 7)),
                LolWslOptimizeEnabled, WslDistro, LolAutoCloseRemoteEnabled, RemoteRestartQuietMinutes,
                GameProblemMonitorEnabled, GameProblemMonitorIntervalMinutes, GameProblemMonitorAiEnabled, GameProblemMonitorAiModel,
                string.IsNullOrWhiteSpace(GameProblemMonitorAiApiKey) ? "env/empty" : "configured",
                GameOptimizerToolZombieCountThreshold, GameOptimizerToolZombieMemoryMB, GameOptimizerLolRenderCountThreshold,
                LockShortcutMappings == null ? 0 : LockShortcutMappings.Count,
                LockShortcutSettleMilliseconds,
                LockScreenEnabled, LockShortcutsEnabled, WeChatShowWindowShortcut);
        }

        public static string Save(Config cfg)
        {
            string path = cfg.LoadedFrom;
            if (string.IsNullOrEmpty(path) || path.StartsWith("<"))
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");

            var kvs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "DeviceName", cfg.DeviceName ?? "" },
                { "DeviceAddress", cfg.DeviceAddress ?? "" },
                { "DisconnectDelaySeconds", cfg.DisconnectDelaySeconds.ToString(CultureInfo.InvariantCulture) },
                { "DisconnectConfirmSeconds", cfg.DisconnectConfirmSeconds.ToString(CultureInfo.InvariantCulture) },
                { "PollingIntervalSeconds", cfg.PollingIntervalSeconds.ToString(CultureInfo.InvariantCulture) },
                { "LogPath", cfg.LogPath ?? "" },
                { "LogLevel", cfg.LogLevel ?? "Info" },
                { "MaxLogSizeMB", cfg.MaxLogSizeMB.ToString(CultureInfo.InvariantCulture) },
                { "LockShortcutSettleMilliseconds", cfg.LockShortcutSettleMilliseconds.ToString(CultureInfo.InvariantCulture) },
                { "LockScreenEnabled", cfg.LockScreenEnabled ? "true" : "false" },
                { "LockShortcutsEnabled", cfg.LockShortcutsEnabled ? "true" : "false" },
                { "WeChatShowWindowShortcut", cfg.WeChatShowWindowShortcut ?? "" },
                { "LolOptimizerEnabled", cfg.LolOptimizerEnabled ? "true" : "false" },
                { "LolProcessName", cfg.LolProcessName ?? "" },
                { "VirtualDisplayDeviceId", cfg.VirtualDisplayDeviceId ?? "" },
                { "LolOptimizerPollSeconds", cfg.LolOptimizerPollSeconds.ToString(CultureInfo.InvariantCulture) },
                { "LolWslOptimizeEnabled", cfg.LolWslOptimizeEnabled ? "true" : "false" },
                { "WslDistro", cfg.WslDistro ?? "" },
                { "LolAutoCloseRemoteEnabled", cfg.LolAutoCloseRemoteEnabled ? "true" : "false" },
                { "RemoteCloseProcessNames", cfg.RemoteCloseProcessNames ?? "" },
                { "RemoteRestartQuietMinutes", cfg.RemoteRestartQuietMinutes.ToString(CultureInfo.InvariantCulture) },
                { "GameProblemMonitorEnabled", cfg.GameProblemMonitorEnabled ? "true" : "false" },
                { "GameProblemMonitorIntervalMinutes", cfg.GameProblemMonitorIntervalMinutes.ToString(CultureInfo.InvariantCulture) },
                { "GameProblemMonitorAiEnabled", cfg.GameProblemMonitorAiEnabled ? "true" : "false" },
                { "GameProblemMonitorAiEndpoint", cfg.GameProblemMonitorAiEndpoint ?? "" },
                { "GameProblemMonitorAiModel", cfg.GameProblemMonitorAiModel ?? "" },
                { "GameProblemMonitorAiApiKey", "" },
                { "GameProblemMonitorAiApiKeyEnv", cfg.GameProblemMonitorAiApiKeyEnv ?? "ARK_API_KEY" },
                { "GameProblemNotifyRepeatMinutes", cfg.GameProblemNotifyRepeatMinutes.ToString(CultureInfo.InvariantCulture) },
                { "GameProblemRunawayCpuPercent", cfg.GameProblemRunawayCpuPercent.ToString(CultureInfo.InvariantCulture) },
                { "GameOptimizerToolZombieCountThreshold", cfg.GameOptimizerToolZombieCountThreshold.ToString(CultureInfo.InvariantCulture) },
                { "GameOptimizerToolZombieMemoryMB", cfg.GameOptimizerToolZombieMemoryMB.ToString(CultureInfo.InvariantCulture) },
                { "GameOptimizerLolRenderCountThreshold", cfg.GameOptimizerLolRenderCountThreshold.ToString(CultureInfo.InvariantCulture) },
                { "GameOptimizerToolCleanupProcessNames", cfg.GameOptimizerToolCleanupProcessNames ?? "" },
                { "GameOptimizerLolCleanupProcessNames", cfg.GameOptimizerLolCleanupProcessNames ?? "" },
            };

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var output = new List<string>();

            if (File.Exists(path))
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string trimmed = (raw ?? "").Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';' ||
                        (trimmed.StartsWith("[") && trimmed.EndsWith("]")))
                    {
                        output.Add(raw); continue;
                    }
                    int eq = trimmed.IndexOf('=');
                    if (eq <= 0) { output.Add(raw); continue; }
                    string key = trimmed.Substring(0, eq).Trim();
                    if (IsLockShortcutKey(key)) continue;
                    string repl;
                    if (kvs.TryGetValue(key, out repl))
                    {
                        output.Add(key + "=" + repl);
                        seen.Add(key);
                    }
                    else { output.Add(raw); }
                }
            }
            else
            {
                output.Add("# BluetoothAutoLock configuration");
                output.Add("# Format: KEY=VALUE   ('#' or ';' starts a comment)");
                output.Add("# SECURITY: Do NOT commit real API keys. Use env var ARK_API_KEY instead.");
                output.Add("");
            }

            foreach (var kv in kvs)
                if (!seen.Contains(kv.Key)) output.Add(kv.Key + "=" + kv.Value);

            AppendLockShortcutMappings(output, cfg.LockShortcutMappings);

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllLines(path, output, new UTF8Encoding(false));
            cfg.LoadedFrom = path;
            return path;
        }

        private static void AppendLockShortcutMappings(List<string> output, List<LockShortcutMapping> mappings)
        {
            if (output == null || mappings == null || mappings.Count == 0) return;

            int index = 1;
            foreach (LockShortcutMapping mapping in mappings)
            {
                if (mapping == null || string.IsNullOrWhiteSpace(mapping.Shortcut)) continue;
                output.Add("LockShortcut" + index.ToString(CultureInfo.InvariantCulture) + "=" + mapping.ToConfigValue());
                index++;
            }
        }

        private static bool IsLockShortcutKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (string.Equals(key, "LockShortcut", StringComparison.Ordinal)) return true;
            if (!key.StartsWith("LockShortcut", StringComparison.Ordinal)) return false;
            if (key.Length == "LockShortcut".Length) return true;
            for (int i = "LockShortcut".Length; i < key.Length; i++)
                if (!char.IsDigit(key[i])) return false;
            return true;
        }
    }
}
