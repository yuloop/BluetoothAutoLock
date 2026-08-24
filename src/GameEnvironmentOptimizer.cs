using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace BluetoothAutoLock
{
    internal sealed class GameEnvironmentNotification
    {
        public string Title;
        public string Message;
        public string Severity;
        public string Signature;
    }

    internal sealed class GameEnvironmentMonitor
    {
        private readonly Config _cfg;
        private readonly Logger _log;
        private readonly Func<bool> _shouldStop;
        private readonly Action<GameEnvironmentNotification> _notify;
        private readonly Dictionary<string, DateTime> _lastNotifyUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _runawayConsecutive = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public volatile string CurrentStatusText = "未启用";

        public GameEnvironmentMonitor(Config cfg, Logger log, Func<bool> shouldStop, Action<GameEnvironmentNotification> notify)
        {
            _cfg = cfg;
            _log = log;
            _shouldStop = shouldStop ?? (() => false);
            _notify = notify;
        }

        public void RunLoop()
        {
            int intervalMinutes = Clamp(_cfg.GameProblemMonitorIntervalMinutes, 1, 1440);
            _log.Info("Game environment monitor started. Interval=" + intervalMinutes + "m, AiEnabled=" + _cfg.GameProblemMonitorAiEnabled +
                      ", Endpoint=" + SafeText(_cfg.GameProblemMonitorAiEndpoint) + ", Model=" + SafeText(_cfg.GameProblemMonitorAiModel) + ".");
            CurrentStatusText = "等待首次分析";

            try
            {
                while (!_shouldStop())
                {
                    try
                    {
                        CurrentStatusText = "正在分析异常应用";
                        GameEnvironmentAnalysisResult result = GameEnvironmentOptimizer.Analyze(_cfg, _log, true, _cfg.GameProblemMonitorAiEnabled, _shouldStop);
                        ApplyRunawayConsecutiveFilter(result);

                        if (result.Issues.Count == 0)
                        {
                            CurrentStatusText = "未发现异常";
                        }
                        else
                        {
                            string summary = result.BuildOneLineSummary();
                            _log.Warn("Game environment monitor detected issue(s): " + summary);
                            CurrentStatusText = "发现 " + result.Issues.Count.ToString(CultureInfo.InvariantCulture) + " 个异常候选";

                            GameEnvironmentIssue notifyIssue = result.GetMostSevereNotifiableIssue();
                            if (notifyIssue != null && ShouldNotify(notifyIssue.Signature))
                            {
                                if (_notify != null)
                                {
                                    GameEnvironmentNotification notification = new GameEnvironmentNotification();
                                    notification.Title = "游戏环境异常：" + notifyIssue.Title;
                                    notification.Message = result.BuildNotificationText(notifyIssue);
                                    notification.Severity = notifyIssue.Severity;
                                    notification.Signature = notifyIssue.Signature;
                                    _notify(notification);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CurrentStatusText = "分析失败";
                        _log.Warn("Game environment monitor analysis failed: " + ex.Message);
                    }

                    CurrentStatusText = CurrentStatusText + "；下次约 " + intervalMinutes + " 分钟后";
                    SleepInterruptibly(intervalMinutes * 60 * 1000);
                }
            }
            finally
            {
                CurrentStatusText = "未启用";
                _log.Info("Game environment monitor stopped.");
            }
        }

        private void ApplyRunawayConsecutiveFilter(GameEnvironmentAnalysisResult result)
        {
            if (result == null || result.Issues.Count == 0)
            {
                _runawayConsecutive.Clear();
                return;
            }

            HashSet<string> seenRunaway = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameEnvironmentIssue issue in result.Issues)
            {
                if (!string.Equals(issue.Code, "RUNAWAY_CPU", StringComparison.OrdinalIgnoreCase)) continue;
                seenRunaway.Add(issue.Signature);
                int count;
                _runawayConsecutive.TryGetValue(issue.Signature, out count);
                count++;
                _runawayConsecutive[issue.Signature] = count;

                if (count >= 2)
                {
                    issue.Severity = "High";
                    issue.ShouldNotify = true;
                    issue.Details = issue.Details + "；已连续 " + count.ToString(CultureInfo.InvariantCulture) + " 次采样高 CPU，疑似死循环。";
                }
                else
                {
                    issue.Severity = "Medium";
                    issue.ShouldNotify = false;
                    issue.Details = issue.Details + "；首次高 CPU 采样，仅记录，需连续出现才提醒。";
                }
            }

            List<string> keys = new List<string>(_runawayConsecutive.Keys);
            foreach (string key in keys)
                if (!seenRunaway.Contains(key)) _runawayConsecutive.Remove(key);
        }

        private bool ShouldNotify(string signature)
        {
            if (string.IsNullOrEmpty(signature)) signature = "unknown";
            int repeatMinutes = Clamp(_cfg.GameProblemNotifyRepeatMinutes, 5, 1440);
            DateTime now = DateTime.UtcNow;
            DateTime last;
            if (_lastNotifyUtc.TryGetValue(signature, out last))
            {
                if ((now - last).TotalMinutes < repeatMinutes)
                {
                    _log.Info("Game environment monitor suppressed duplicate notification for " + signature + ".");
                    return false;
                }
            }

            _lastNotifyUtc[signature] = now;
            return true;
        }

        private void SleepInterruptibly(int milliseconds)
        {
            int elapsed = 0;
            while (elapsed < milliseconds && !_shouldStop())
            {
                int slice = Math.Min(1000, milliseconds - elapsed);
                Thread.Sleep(slice);
                elapsed += slice;
            }
        }

        private static string SafeText(string value)
        {
            return string.IsNullOrEmpty(value) ? "<unset>" : value;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    internal sealed class GameEnvironmentOptimizationResult
    {
        public GameEnvironmentAnalysisResult Analysis;
        public readonly List<string> Actions = new List<string>();
        public readonly List<string> Errors = new List<string>();

        public bool Changed
        {
            get { return Actions.Count > 0; }
        }

        public string BuildUserSummary()
        {
            if (Changed)
                return "已优化：" + string.Join("；", Actions.ToArray());
            if (Analysis != null && Analysis.Issues.Count > 0)
                return "发现异常但未自动处理：" + Analysis.BuildOneLineSummary();
            return "未发现需要优化的异常进程。";
        }
    }

    internal sealed class GameEnvironmentAnalysisResult
    {
        public readonly List<GameEnvironmentIssue> Issues = new List<GameEnvironmentIssue>();
        public string AiSummary;
        public string AiSeverity;

        public string BuildOneLineSummary()
        {
            if (Issues.Count == 0) return "none";
            List<string> parts = new List<string>();
            foreach (GameEnvironmentIssue issue in Issues)
                parts.Add(issue.Severity + ":" + issue.Title + "(" + issue.Details + ")");
            if (!string.IsNullOrWhiteSpace(AiSummary))
                parts.Add("AI=" + AiSummary);
            return string.Join(" | ", parts.ToArray());
        }

        public GameEnvironmentIssue GetMostSevereNotifiableIssue()
        {
            GameEnvironmentIssue best = null;
            foreach (GameEnvironmentIssue issue in Issues)
            {
                if (issue == null || !issue.ShouldNotify) continue;
                if (best == null || SeverityRank(issue.Severity) > SeverityRank(best.Severity)) best = issue;
            }
            return best;
        }

        public string BuildNotificationText(GameEnvironmentIssue issue)
        {
            string text = issue.Details;
            if (!string.IsNullOrWhiteSpace(AiSummary))
                text = text + "\nAI分析：" + TrimForUi(AiSummary, 180);
            if (text.Length > 240) text = text.Substring(0, 237) + "...";
            return text;
        }

        public static int SeverityRank(string severity)
        {
            string s = (severity ?? "").Trim().ToLowerInvariant();
            if (s == "critical") return 4;
            if (s == "high") return 3;
            if (s == "medium") return 2;
            if (s == "low") return 1;
            return 0;
        }

        private static string TrimForUi(string value, int max)
        {
            string s = value ?? "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length > max) s = s.Substring(0, max - 3) + "...";
            return s;
        }
    }

    internal sealed class GameEnvironmentIssue
    {
        public string Code;
        public string Title;
        public string Severity;
        public string Details;
        public string Signature;
        public bool ShouldNotify;
        public readonly List<string> CleanupProcessNames = new List<string>();
    }

    internal static class GameEnvironmentOptimizer
    {
        private const int CpuSampleMilliseconds = 2200;

        public static GameEnvironmentOptimizationResult RunManualOptimization(Config cfg, Logger log)
        {
            GameEnvironmentOptimizationResult result = new GameEnvironmentOptimizationResult();
            result.Analysis = Analyze(cfg, log, true, false, null);

            if (result.Analysis.Issues.Count == 0)
            {
                log.Info("Smart game environment optimization: no abnormal process issue found.");
                return result;
            }

            log.Warn("Smart game environment optimization: detected issue(s): " + result.Analysis.BuildOneLineSummary());

            HashSet<string> killedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameEnvironmentIssue issue in result.Analysis.Issues)
            {
                if (issue.CleanupProcessNames.Count == 0)
                {
                    log.Info("Smart game environment optimization: issue not auto-fixed to avoid false kill: " + issue.Title + " | " + issue.Details);
                    continue;
                }

                foreach (string rawName in issue.CleanupProcessNames)
                {
                    string name = NormalizeProcessName(rawName);
                    if (name.Length == 0 || killedNames.Contains(name)) continue;
                    killedNames.Add(name);

                    int killed = KillProcessGroup(name, log);
                    if (killed > 0)
                        result.Actions.Add(name + " ×" + killed.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (result.Actions.Count == 0)
                log.Warn("Smart game environment optimization: issues found, but no configured auto-fix action was executed.");
            else
                log.Warn("Smart game environment optimization finished. Actions=" + string.Join(", ", result.Actions.ToArray()));

            return result;
        }

        public static GameEnvironmentAnalysisResult Analyze(Config cfg, Logger log, bool sampleCpu, bool callAi, Func<bool> shouldStop)
        {
            GameEnvironmentAnalysisResult result = new GameEnvironmentAnalysisResult();
            Dictionary<string, ProcessGroupStats> groups = sampleCpu ? CollectProcessGroupsWithCpu(shouldStop) : CollectProcessGroups();

            DetectToolZombieGroups(cfg, groups, result);
            DetectLolResidues(cfg, groups, result);
            DetectRunawayCpu(cfg, groups, result);

            if (callAi && result.Issues.Count > 0)
            {
                GameEnvironmentAiResult ai = GameEnvironmentAiClient.Analyze(cfg, result, groups, log);
                if (ai != null)
                {
                    result.AiSummary = ai.Summary;
                    result.AiSeverity = ai.Severity;
                    ApplyAiSeverity(result, ai);
                }
            }

            return result;
        }

        private static void DetectToolZombieGroups(Config cfg, Dictionary<string, ProcessGroupStats> groups, GameEnvironmentAnalysisResult result)
        {
            ProcessGroupStats node = Combine(groups, new string[] { "node", "omx-node-stdio-hidden" });
            int countThreshold = Clamp(cfg.GameOptimizerToolZombieCountThreshold, 3, 500);
            int memThresholdMb = Clamp(cfg.GameOptimizerToolZombieMemoryMB, 256, 65536);

            if (node.Count >= countThreshold || node.WorkingSetMB >= memThresholdMb || node.SingleCoreCpuPercent >= 45.0)
            {
                GameEnvironmentIssue issue = new GameEnvironmentIssue();
                issue.Code = "TOOL_ZOMBIE_NODE";
                issue.Title = "node/OMX 残留进程群";
                issue.Severity = "High";
                issue.ShouldNotify = true;
                issue.Signature = "tool-zombie-node";
                issue.Details = "node/omx 数量 " + node.Count.ToString(CultureInfo.InvariantCulture) +
                                "，内存约 " + node.WorkingSetMB.ToString(CultureInfo.InvariantCulture) + " MB，采样CPU约 " +
                                node.SingleCoreCpuPercent.ToString("0", CultureInfo.InvariantCulture) + "% 单核；符合 LOL 卡顿总结中的僵尸进程群特征。";
                AddCleanupIfConfigured(issue, cfg.GameOptimizerToolCleanupProcessNames, new string[] { "node", "omx-node-stdio-hidden" });
                result.Issues.Add(issue);
            }

            ProcessGroupStats python = Combine(groups, new string[] { "python", "pythonw" });
            int pythonCountThreshold = Math.Max(8, countThreshold / 3);
            if (python.Count >= pythonCountThreshold || python.WorkingSetMB >= memThresholdMb || python.SingleCoreCpuPercent >= 85.0)
            {
                GameEnvironmentIssue issue = new GameEnvironmentIssue();
                issue.Code = "TOOL_ZOMBIE_PYTHON";
                issue.Title = "python 残留/死循环进程";
                issue.Severity = "High";
                issue.ShouldNotify = true;
                issue.Signature = "tool-zombie-python";
                issue.Details = "python 数量 " + python.Count.ToString(CultureInfo.InvariantCulture) +
                                "，内存约 " + python.WorkingSetMB.ToString(CultureInfo.InvariantCulture) + " MB，采样CPU约 " +
                                python.SingleCoreCpuPercent.ToString("0", CultureInfo.InvariantCulture) + "% 单核。";
                AddCleanupIfConfigured(issue, cfg.GameOptimizerToolCleanupProcessNames, new string[] { "python", "pythonw" });
                result.Issues.Add(issue);
            }
        }

        private static void DetectLolResidues(Config cfg, Dictionary<string, ProcessGroupStats> groups, GameEnvironmentAnalysisResult result)
        {
            ProcessGroupStats crash = Combine(groups, new string[] { "LeagueCrashHandler64", "LeagueCrashHandler" });
            ProcessGroupStats ux = Combine(groups, new string[] { "LeagueClientUxRender" });
            int renderThreshold = Clamp(cfg.GameOptimizerLolRenderCountThreshold, 1, 20);

            if (crash.Count > 0 || ux.Count > renderThreshold)
            {
                GameEnvironmentIssue issue = new GameEnvironmentIssue();
                issue.Code = "LOL_RESIDUE";
                issue.Title = "LoL 崩溃/客户端残留";
                issue.Severity = "High";
                issue.ShouldNotify = true;
                issue.Signature = "lol-residue";
                issue.Details = "LeagueCrashHandler 数量 " + crash.Count.ToString(CultureInfo.InvariantCulture) +
                                "，LeagueClientUxRender 数量 " + ux.Count.ToString(CultureInfo.InvariantCulture) +
                                "（正常通常 1-2 个）；建议清理后重新启动客户端。";
                AddCleanupIfConfigured(issue, cfg.GameOptimizerLolCleanupProcessNames,
                    new string[] { "LeagueCrashHandler64", "LeagueCrashHandler", "LeagueClientUxRender", "LeagueClient", "League of Legends", "RiotClientServices" });
                result.Issues.Add(issue);
            }
        }

        private static void DetectRunawayCpu(Config cfg, Dictionary<string, ProcessGroupStats> groups, GameEnvironmentAnalysisResult result)
        {
            int threshold = Clamp(cfg.GameProblemRunawayCpuPercent, 50, 800);
            foreach (ProcessGroupStats group in groups.Values)
            {
                if (group == null) continue;
                if (group.SingleCoreCpuPercent < threshold) continue;
                if (IsKnownHighCpuAllowed(group.Name)) continue;
                if (IsSystemProcess(group.Name)) continue;

                GameEnvironmentIssue issue = new GameEnvironmentIssue();
                issue.Code = "RUNAWAY_CPU";
                issue.Title = "疑似死循环进程：" + group.Name;
                issue.Severity = "Medium";
                issue.ShouldNotify = false;
                issue.Signature = "runaway-" + group.Name.ToLowerInvariant();
                issue.Details = group.Name + " 数量 " + group.Count.ToString(CultureInfo.InvariantCulture) +
                                "，采样CPU约 " + group.SingleCoreCpuPercent.ToString("0", CultureInfo.InvariantCulture) +
                                "% 单核，内存约 " + group.WorkingSetMB.ToString(CultureInfo.InvariantCulture) + " MB。";
                result.Issues.Add(issue);
            }
        }

        private static void ApplyAiSeverity(GameEnvironmentAnalysisResult result, GameEnvironmentAiResult ai)
        {
            if (result == null || ai == null) return;
            int aiRank = GameEnvironmentAnalysisResult.SeverityRank(ai.Severity);
            if (aiRank < 3) return;

            foreach (GameEnvironmentIssue issue in result.Issues)
            {
                if (issue == null) continue;
                if (GameEnvironmentAnalysisResult.SeverityRank(issue.Severity) < aiRank)
                    issue.Severity = ai.Severity;
                if (aiRank >= 3) issue.ShouldNotify = true;
            }
        }

        private static void AddCleanupIfConfigured(GameEnvironmentIssue issue, string configured, string[] candidates)
        {
            HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in SplitProcessNames(configured)) allowed.Add(NormalizeProcessName(name));
            foreach (string name in candidates)
            {
                string normalized = NormalizeProcessName(name);
                if (normalized.Length == 0) continue;
                if (allowed.Contains(normalized)) issue.CleanupProcessNames.Add(normalized);
            }
        }

        private static Dictionary<string, ProcessGroupStats> CollectProcessGroups()
        {
            Dictionary<string, ProcessGroupStats> groups = new Dictionary<string, ProcessGroupStats>(StringComparer.OrdinalIgnoreCase);
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return groups; }

            foreach (Process p in processes)
            {
                try
                {
                    string name = NormalizeProcessName(p.ProcessName);
                    if (name.Length == 0) continue;
                    ProcessGroupStats group = GetOrCreate(groups, name);
                    group.Count++;
                    group.WorkingSetBytes += SafeWorkingSet(p);
                    if (group.SamplePids.Count < 8) group.SamplePids.Add(p.Id);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return groups;
        }

        private static Dictionary<string, ProcessGroupStats> CollectProcessGroupsWithCpu(Func<bool> shouldStop)
        {
            var sw = Stopwatch.StartNew();
            Dictionary<int, ProcessSample> before = CaptureSamples();
            SleepInterruptibly(CpuSampleMilliseconds, shouldStop);
            Dictionary<int, ProcessSample> after = CaptureSamples();
            sw.Stop();
            double elapsedMs = Math.Max(1.0, sw.Elapsed.TotalMilliseconds);

            Dictionary<string, ProcessGroupStats> groups = new Dictionary<string, ProcessGroupStats>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcessSample sample in after.Values)
            {
                ProcessGroupStats group = GetOrCreate(groups, sample.Name);
                group.Count++;
                group.WorkingSetBytes += sample.WorkingSetBytes;
                if (group.SamplePids.Count < 8) group.SamplePids.Add(sample.Id);

                ProcessSample old;
                if (before.TryGetValue(sample.Id, out old) && string.Equals(old.Name, sample.Name, StringComparison.OrdinalIgnoreCase))
                {
                    double deltaMs = (sample.TotalProcessorTime - old.TotalProcessorTime).TotalMilliseconds;
                    if (deltaMs > 0) group.SingleCoreCpuPercent += (deltaMs / elapsedMs) * 100.0;
                }
            }

            return groups;
        }

        private static Dictionary<int, ProcessSample> CaptureSamples()
        {
            Dictionary<int, ProcessSample> samples = new Dictionary<int, ProcessSample>();
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return samples; }

            foreach (Process p in processes)
            {
                try
                {
                    if (p.HasExited) continue;
                    ProcessSample sample = new ProcessSample();
                    sample.Id = p.Id;
                    sample.Name = NormalizeProcessName(p.ProcessName);
                    sample.WorkingSetBytes = SafeWorkingSet(p);
                    sample.TotalProcessorTime = SafeTotalProcessorTime(p);
                    if (sample.Name.Length > 0) samples[sample.Id] = sample;
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return samples;
        }

        private static ProcessGroupStats Combine(Dictionary<string, ProcessGroupStats> groups, string[] names)
        {
            ProcessGroupStats combined = new ProcessGroupStats();
            combined.Name = string.Join("+", names);
            if (groups == null || names == null) return combined;
            foreach (string raw in names)
            {
                string name = NormalizeProcessName(raw);
                ProcessGroupStats group;
                if (!groups.TryGetValue(name, out group)) continue;
                combined.Count += group.Count;
                combined.WorkingSetBytes += group.WorkingSetBytes;
                combined.SingleCoreCpuPercent += group.SingleCoreCpuPercent;
                foreach (int pid in group.SamplePids)
                    if (combined.SamplePids.Count < 12) combined.SamplePids.Add(pid);
            }
            return combined;
        }

        private static ProcessGroupStats GetOrCreate(Dictionary<string, ProcessGroupStats> groups, string name)
        {
            ProcessGroupStats group;
            if (!groups.TryGetValue(name, out group))
            {
                group = new ProcessGroupStats();
                group.Name = name;
                groups[name] = group;
            }
            return group;
        }

        private static long SafeWorkingSet(Process p)
        {
            try { return p.WorkingSet64; }
            catch { return 0; }
        }

        private static TimeSpan SafeTotalProcessorTime(Process p)
        {
            try { return p.TotalProcessorTime; }
            catch { return TimeSpan.Zero; }
        }

        private static void SleepInterruptibly(int milliseconds, Func<bool> shouldStop)
        {
            int elapsed = 0;
            while (elapsed < milliseconds)
            {
                if (shouldStop != null && shouldStop()) return;
                int slice = Math.Min(200, milliseconds - elapsed);
                Thread.Sleep(slice);
                elapsed += slice;
            }
        }

        private static int KillProcessGroup(string processName, Logger log)
        {
            string normalized = NormalizeProcessName(processName);
            if (normalized.Length == 0) return 0;

            Process[] procs;
            try { procs = Process.GetProcessesByName(normalized); }
            catch (Exception ex)
            {
                log.Warn("Smart game environment optimization: cannot enumerate " + normalized + ": " + ex.Message);
                return 0;
            }

            int killed = 0;
            foreach (Process p in procs)
            {
                if (p == null) continue;
                try
                {
                    if (p.HasExited) continue;
                    int pid = p.Id;
                    p.Kill();
                    try { p.WaitForExit(3000); } catch { }
                    killed++;
                    log.Warn("Smart game environment optimization: killed " + normalized + " PID=" + pid.ToString(CultureInfo.InvariantCulture) + ".");
                }
                catch (Exception ex)
                {
                    log.Warn("Smart game environment optimization: failed to kill " + normalized + ": " + ex.Message);
                }
                finally { try { p.Dispose(); } catch { } }
            }
            return killed;
        }

        internal static string[] SplitProcessNames(string text)
        {
            string source = text ?? "";
            string[] raw = source.Split(new char[] { ',', ';', '|', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> names = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in raw)
            {
                string name = NormalizeProcessName(item.Trim());
                if (name.Length == 0) continue;
                if (seen.Add(name)) names.Add(name);
            }
            return names.ToArray();
        }

        internal static string NormalizeProcessName(string text)
        {
            string t = (text ?? "").Trim();
            if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 4);
            return t;
        }

        private static bool IsKnownHighCpuAllowed(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            if (n == "league of legends") return true;
            if (n == "leagueclientuxrender") return true;
            if (n == "leagueclient") return true;
            if (n == "riotclientservices") return true;
            if (n == "dwm") return true;
            if (n == "audiodg") return true;
            if (n == "chrome" || n == "msedge" || n == "firefox") return true;
            return false;
        }

        private static bool IsSystemProcess(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            if (n == "system" || n == "idle" || n == "registry" || n == "smss" || n == "csrss" || n == "wininit") return true;
            if (n == "services" || n == "lsass" || n == "svchost" || n == "fontdrvhost" || n == "conhost") return true;
            return false;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private sealed class ProcessSample
        {
            public int Id;
            public string Name;
            public long WorkingSetBytes;
            public TimeSpan TotalProcessorTime;
        }

        private sealed class ProcessGroupStats
        {
            public string Name;
            public int Count;
            public long WorkingSetBytes;
            public double SingleCoreCpuPercent;
            public readonly List<int> SamplePids = new List<int>();

            public int WorkingSetMB
            {
                get { return (int)(WorkingSetBytes / (1024L * 1024L)); }
            }
        }

        private sealed class GameEnvironmentAiResult
        {
            public string Severity;
            public string Summary;
        }

        private static class GameEnvironmentAiClient
        {
            public static GameEnvironmentAiResult Analyze(Config cfg, GameEnvironmentAnalysisResult analysis, Dictionary<string, ProcessGroupStats> groups, Logger log)
            {
                string endpoint = (cfg.GameProblemMonitorAiEndpoint ?? "").Trim();
                string model = (cfg.GameProblemMonitorAiModel ?? "").Trim();
                string apiKey = ResolveApiKey(cfg);

                if (endpoint.Length == 0 || model.Length == 0)
                {
                    log.Warn("Game environment AI analysis skipped: endpoint/model is not configured.");
                    return null;
                }
                if (apiKey.Length == 0)
                {
                    log.Warn("Game environment AI analysis skipped: API key is not configured. Set GameProblemMonitorAiApiKey or environment variable " +
                             SafeText(cfg.GameProblemMonitorAiApiKeyEnv) + ".");
                    return null;
                }

                string prompt = BuildPrompt(analysis, groups);
                string body = "{\"model\":\"" + JsonEscape(model) + "\",\"input\":\"" + JsonEscape(prompt) + "\",\"max_output_tokens\":320}";

                Uri parsedEndpoint;
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out parsedEndpoint) || !string.Equals(parsedEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    log.Warn("Game environment AI analysis skipped: only HTTPS endpoints are allowed. Endpoint=" + SafeText(endpoint));
                    return null;
                }

                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(parsedEndpoint);
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    req.Accept = "application/json";
                    req.Timeout = 20000;
                    req.ReadWriteTimeout = 20000;
                    req.Headers["Authorization"] = "Bearer " + apiKey;

                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    req.ContentLength = bytes.Length;
                    using (Stream stream = req.GetRequestStream())
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream stream = resp.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string raw = reader.ReadToEnd();
                        string text = ExtractResponseText(raw);
                        GameEnvironmentAiResult result = ParseAiText(text);
                        log.Warn("Game environment AI analysis: severity=" + SafeText(result.Severity) + ", summary=" + SafeText(result.Summary));
                        return result;
                    }
                }
                catch (WebException ex)
                {
                    string detail = ex.Message;
                    try
                    {
                        if (ex.Response != null)
                        {
                            using (Stream stream = ex.Response.GetResponseStream())
                            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                            {
                                string raw = reader.ReadToEnd();
                                if (!string.IsNullOrWhiteSpace(raw)) detail = detail + " | " + Trim(raw, 300);
                            }
                        }
                    }
                    catch { }
                    log.Warn("Game environment AI analysis failed: " + detail);
                    return null;
                }
                catch (Exception ex)
                {
                    log.Warn("Game environment AI analysis failed: " + ex.Message);
                    return null;
                }
            }

            private static string ResolveApiKey(Config cfg)
            {
                string key = (cfg.GameProblemMonitorAiApiKey ?? "").Trim();
                if (key.Length > 0) return key;
                string envName = (cfg.GameProblemMonitorAiApiKeyEnv ?? "").Trim();
                if (envName.Length == 0) return "";
                try { return (Environment.GetEnvironmentVariable(envName) ?? "").Trim(); }
                catch { return ""; }
            }

            private static string BuildPrompt(GameEnvironmentAnalysisResult analysis, Dictionary<string, ProcessGroupStats> groups)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("你是 Windows 游戏卡顿排查助手。只判断异常残留、崩溃处理器、僵尸进程群、疑似死循环进程；不要把正常游戏、浏览器、系统服务的短时占用判为严重。");
                sb.AppendLine("请基于本地规则发现的候选问题，输出两行：");
                sb.AppendLine("SEVERITY: none|low|medium|high|critical");
                sb.AppendLine("SUMMARY: 用中文一句话说明是否会严重影响游戏环境以及建议动作。");
                sb.AppendLine();
                sb.AppendLine("候选问题：");
                foreach (GameEnvironmentIssue issue in analysis.Issues)
                    sb.AppendLine("- " + issue.Code + " | " + issue.Severity + " | " + issue.Title + " | " + issue.Details);

                sb.AppendLine();
                sb.AppendLine("重点进程快照：");
                AppendGroup(sb, groups, "node");
                AppendGroup(sb, groups, "omx-node-stdio-hidden");
                AppendGroup(sb, groups, "python");
                AppendGroup(sb, groups, "LeagueCrashHandler64");
                AppendGroup(sb, groups, "LeagueClientUxRender");
                AppendGroup(sb, groups, "LeagueClient");
                AppendGroup(sb, groups, "League of Legends");
                AppendGroup(sb, groups, "RiotClientServices");
                return sb.ToString();
            }

            private static void AppendGroup(StringBuilder sb, Dictionary<string, ProcessGroupStats> groups, string name)
            {
                if (groups == null) return;
                ProcessGroupStats g;
                if (!groups.TryGetValue(NormalizeProcessName(name), out g)) return;
                sb.AppendLine("- " + g.Name + ": count=" + g.Count.ToString(CultureInfo.InvariantCulture) +
                              ", memMB=" + g.WorkingSetMB.ToString(CultureInfo.InvariantCulture) +
                              ", singleCoreCpu=" + g.SingleCoreCpuPercent.ToString("0", CultureInfo.InvariantCulture) + "%");
            }

            private static GameEnvironmentAiResult ParseAiText(string text)
            {
                GameEnvironmentAiResult result = new GameEnvironmentAiResult();
                string normalized = (text ?? "").Replace("\r", "\n");
                string[] lines = normalized.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.StartsWith("SEVERITY", StringComparison.OrdinalIgnoreCase))
                    {
                        int colon = line.IndexOf(':');
                        if (colon >= 0) result.Severity = NormalizeSeverity(line.Substring(colon + 1));
                    }
                    else if (line.StartsWith("SUMMARY", StringComparison.OrdinalIgnoreCase))
                    {
                        int colon = line.IndexOf(':');
                        if (colon >= 0) result.Summary = line.Substring(colon + 1).Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(result.Severity)) result.Severity = InferSeverity(normalized);
                if (string.IsNullOrWhiteSpace(result.Summary)) result.Summary = Trim(normalized, 240);
                return result;
            }

            private static string InferSeverity(string text)
            {
                string t = (text ?? "").ToLowerInvariant();
                if (t.IndexOf("critical") >= 0 || t.IndexOf("严重") >= 0) return "Critical";
                if (t.IndexOf("high") >= 0 || t.IndexOf("高") >= 0) return "High";
                if (t.IndexOf("medium") >= 0 || t.IndexOf("中") >= 0) return "Medium";
                if (t.IndexOf("low") >= 0 || t.IndexOf("低") >= 0) return "Low";
                return "Medium";
            }

            private static string NormalizeSeverity(string value)
            {
                string t = (value ?? "").Trim().ToLowerInvariant();
                if (t.IndexOf("critical") >= 0 || t.IndexOf("严重") >= 0) return "Critical";
                if (t.IndexOf("high") >= 0 || t == "高") return "High";
                if (t.IndexOf("medium") >= 0 || t == "中") return "Medium";
                if (t.IndexOf("low") >= 0 || t == "低") return "Low";
                if (t.IndexOf("none") >= 0 || t.IndexOf("无") >= 0) return "None";
                return "Medium";
            }

            private static string ExtractResponseText(string raw)
            {
                if (string.IsNullOrEmpty(raw)) return "";

                string value = FindJsonStringValue(raw, "output_text");
                if (!string.IsNullOrEmpty(value)) return value;

                value = FindJsonStringValue(raw, "text");
                if (!string.IsNullOrEmpty(value)) return value;

                value = FindJsonStringValue(raw, "content");
                if (!string.IsNullOrEmpty(value)) return value;

                return Trim(raw, 1000);
            }

            private static string FindJsonStringValue(string raw, string key)
            {
                string pattern = "\"" + key + "\"";
                int pos = raw.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                while (pos >= 0)
                {
                    int colon = raw.IndexOf(':', pos + pattern.Length);
                    if (colon < 0) return "";
                    int quote = raw.IndexOf('"', colon + 1);
                    if (quote < 0) return "";
                    int end = FindStringEnd(raw, quote + 1);
                    if (end < 0) return "";
                    string value = JsonUnescape(raw.Substring(quote + 1, end - quote - 1));
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                    pos = raw.IndexOf(pattern, end + 1, StringComparison.OrdinalIgnoreCase);
                }
                return "";
            }

            private static int FindStringEnd(string s, int start)
            {
                bool escaped = false;
                for (int i = start; i < s.Length; i++)
                {
                    char c = s[i];
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (c == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (c == '"') return i;
                }
                return -1;
            }

            private static string JsonEscape(string value)
            {
                if (value == null) return "";
                StringBuilder sb = new StringBuilder(value.Length + 16);
                foreach (char c in value)
                {
                    switch (c)
                    {
                        case '\\': sb.Append("\\\\"); break;
                        case '"': sb.Append("\\\""); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
                return sb.ToString();
            }

            private static string JsonUnescape(string value)
            {
                if (value == null) return "";
                StringBuilder sb = new StringBuilder(value.Length);
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    if (c != '\\' || i + 1 >= value.Length)
                    {
                        sb.Append(c);
                        continue;
                    }
                    char n = value[++i];
                    switch (n)
                    {
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < value.Length)
                            {
                                string hex = value.Substring(i + 1, 4);
                                int code;
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                }
                return sb.ToString();
            }

            private static string Trim(string value, int max)
            {
                string s = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (s.Length > max) s = s.Substring(0, max - 3) + "...";
                return s;
            }

            private static string SafeText(string value)
            {
                return string.IsNullOrWhiteSpace(value) ? "<empty>" : value;
            }
        }
    }
}
