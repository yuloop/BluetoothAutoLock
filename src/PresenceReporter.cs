using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace BluetoothAutoLock
{
    // 在线状态上报：把本机的“在场 / 离开”状态定期 POST 给远端 HTTP 接口，
    // 让 Linux 侧服务知道用户此刻是否坐在这台电脑前。
    //
    // 与接收端约定的协议（改动前先确认接收端）：
    //   POST <Url>            Content-Type: application/json; charset=utf-8
    //   Header                X-Btlock-Token: <token>
    //   Body                  {"ts","host","version","idleSeconds","absentSeconds","inRange","state","paused","event"}
    //   event                 start（启动后第一份）/ heartbeat（周期）/ change（状态刚变化）/ stop（退出）
    //
    // 工程约束：只能用 C# 5 语法（目标机是 %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe），
    // warnaserror+ 下零警告，任何网络路径都不许抛异常、不许阻塞监控线程或 UI 线程 ——
    // 所有请求都跑在本类自己的后台线程上（IsBackground = true），监控线程只更新状态。
    internal sealed class PresenceReporter
    {
        internal const string EventStart = "start";
        internal const string EventHeartbeat = "heartbeat";
        internal const string EventChange = "change";
        internal const string EventStop = "stop";

        // 后台线程的睡眠切片：状态变化最迟 250ms 内发出，心跳最迟晚 250ms。
        private const int LoopSliceMilliseconds = 250;

        // 退出时最多等这么久让 stop 上报发出去。必须明显小于 TrayApp.StopMonitor 的 5s join 预算，
        // 否则“设置保存后重启监控”会因为 StopMonitor 判定超时而不再重启监控线程。
        private const int StopJoinMilliseconds = 2000;

        // 最近一次喂进来的状态。由监控线程写、上报线程读，用 _gate 保护。
        private sealed class Snapshot
        {
            public bool InRange = false;
            public int? IdleSeconds = null;
            public int AbsentSeconds = 0;
            public string State = "unknown";
            public bool Paused = false;

            public Snapshot Copy()
            {
                Snapshot copy = new Snapshot();
                copy.InRange = InRange;
                copy.IdleSeconds = IdleSeconds;
                copy.AbsentSeconds = AbsentSeconds;
                copy.State = State;
                copy.Paused = Paused;
                return copy;
            }
        }

        private readonly Logger _log;
        private readonly object _gate = new object();
        private readonly Snapshot _latest = new Snapshot();
        private readonly bool _enabled;
        private readonly string _url;
        private readonly string _token;
        private readonly int _intervalSeconds;
        private readonly int _timeoutMilliseconds;
        private readonly bool _urlAccepted;

        private Thread _thread;
        private volatile bool _stopRequested = false;
        private volatile bool _running = false;

        // 以下四个字段只有上报线程自己读写，不需要加锁。
        private string _sentState = "unknown";
        private bool _sentInRange = false;
        private DateTime _lastSentUtc = DateTime.MinValue;
        private int _consecutiveFailures = 0;

        public PresenceReporter(Config cfg, Logger log)
        {
            _log = log;
            if (cfg == null) cfg = Config.Defaults();

            _enabled = cfg.PresenceReportEnabled;
            _url = (cfg.PresenceReportUrl ?? "").Trim();
            _token = (cfg.PresenceReportToken ?? "").Trim();
            _intervalSeconds = Math.Max(1, cfg.PresenceReportIntervalSeconds);
            _timeoutMilliseconds = Math.Max(1000, Math.Min(60000, cfg.PresenceReportTimeoutSeconds * 1000));
            _urlAccepted = ValidateUrl(_url);
        }

        // 启动上报线程并立刻发出 start 报告。关闭（PresenceReportEnabled=false）、Token 为空或 Url 非法时
        // 只记一条日志就保持空闲，一个字节都不发。
        public void Start()
        {
            if (_running) return;

            if (!_enabled)
            {
                _log.Debug("Presence reporting is disabled (config PresenceReportEnabled=false); staying idle.");
                return;
            }
            if (_token.Length == 0)
            {
                _log.Warn("Presence reporting is idle: the token is empty, so nothing will be sent. Set PresenceReportToken in config.ini to enable it.");
                return;
            }
            if (!_urlAccepted)
            {
                _log.Warn("Presence reporting is idle: Url is empty or is not an absolute http/https URL: '" + _url + "'");
                return;
            }

            _stopRequested = false;
            lock (_gate)
            {
                _lastSentUtc = DateTime.UtcNow;
            }

            Thread thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "PresenceReporter";
            _thread = thread;
            _running = true;
            thread.Start();

            _log.Info("Presence reporting started: url=" + _url + ", interval=" + _intervalSeconds +
                "s, timeout=" + _timeoutMilliseconds + "ms, token=configured");
        }

        // 停止上报：先请上报线程把 stop 报告发出去，再等一个有界的时间。
        // 有界是硬要求——远端卡住也不能拖住退出或监控重启。
        public void Stop()
        {
            Thread thread = _thread;
            if (!_running && thread == null) return;

            _stopRequested = true;
            _running = false;

            if (thread != null && thread.IsAlive)
            {
                if (!thread.Join(StopJoinMilliseconds))
                {
                    _log.Warn("Presence reporter thread did not stop within " + StopJoinMilliseconds +
                        "ms; giving up on the stop report so shutdown is not delayed.");
                }
            }
            _thread = null;
        }

        // 监控线程每个轮询周期调用一次，只做状态搬运，不做网络请求、不阻塞。
        // 即使还没启动（或已关闭）也照样记下来，这样启动后第一份 start 报告就带真实初值。
        public void Report(bool inRange, int? idleSeconds, int absentSeconds, string state, bool paused)
        {
            try
            {
                lock (_gate)
                {
                    _latest.InRange = inRange;
                    _latest.IdleSeconds = idleSeconds;
                    _latest.AbsentSeconds = absentSeconds < 0 ? 0 : absentSeconds;
                    _latest.State = string.IsNullOrEmpty(state) ? "unknown" : state;
                    _latest.Paused = paused;
                }
            }
            catch (Exception ex)
            {
                _log.Debug("Presence state update failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private bool ValidateUrl(string url)
        {
            if (url.Length == 0) return false;
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return false;
            if (string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn("Presence reporting Url uses plain HTTP; the X-Btlock-Token header will travel in cleartext: " + url);
                return true;
            }
            return false;
        }

        private void Loop()
        {
            try
            {
                SendReport(EventStart);

                while (!_stopRequested)
                {
                    Thread.Sleep(LoopSliceMilliseconds);
                    if (_stopRequested) break;

                    string eventName = DecideEvent();
                    if (eventName != null) SendReport(eventName);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Presence reporter loop failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            SendReport(EventStop);
        }

        // 状态变了就立刻发 change，否则每 IntervalSeconds 发一次 heartbeat。
        // 与“最后一次真正发出去的值”比较，所以中间被覆盖的抖动会自然合并。
        private string DecideEvent()
        {
            lock (_gate)
            {
                if (_latest.State != _sentState || _latest.InRange != _sentInRange)
                {
                    _lastSentUtc = DateTime.UtcNow;
                    return EventChange;
                }
                if ((DateTime.UtcNow - _lastSentUtc).TotalSeconds >= _intervalSeconds)
                {
                    _lastSentUtc = DateTime.UtcNow;
                    return EventHeartbeat;
                }
            }
            return null;
        }

        private void SendReport(string eventName)
        {
            try
            {
                Snapshot snapshot;
                lock (_gate)
                {
                    snapshot = _latest.Copy();
                    _sentState = snapshot.State;
                    _sentInRange = snapshot.InRange;
                }

                string body = BuildJson(eventName, snapshot);
                if (Post(body))
                {
                    if (_consecutiveFailures > 0)
                    {
                        _log.Info("Presence report recovered after " + _consecutiveFailures + " consecutive failure(s).");
                        _consecutiveFailures = 0;
                    }
                    if (string.Equals(eventName, EventHeartbeat, StringComparison.Ordinal))
                        _log.Debug("Presence report sent (" + eventName + "): " + body);
                    else
                        _log.Info("Presence report sent (" + eventName + "): " + body);
                }
                else
                {
                    _consecutiveFailures++;
                    if (_consecutiveFailures == 1)
                        _log.Warn("Presence report failed (" + eventName + "); further consecutive failures are logged at debug level.");
                    else
                        _log.Debug("Presence report failed again (" + eventName + ", " + _consecutiveFailures + " consecutive).");
                }
            }
            catch (Exception ex)
            {
                _log.Debug("Presence report skipped: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private bool Post(string body)
        {
            HttpWebRequest request = null;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(_url);
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Accept = "application/json";
                request.Timeout = _timeoutMilliseconds;
                request.ReadWriteTimeout = _timeoutMilliseconds;
                request.KeepAlive = false;
                request.Headers["X-Btlock-Token"] = _token;

                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    int statusCode = (int)response.StatusCode;
                    try
                    {
                        // 读掉响应体（有上限），避免服务端还在写就被断连；内容本身不需要。
                        using (Stream stream = response.GetResponseStream())
                        {
                            if (stream != null)
                            {
                                byte[] buffer = new byte[256];
                                int total = 0;
                                while (total < 4096)
                                {
                                    int read = stream.Read(buffer, 0, buffer.Length);
                                    if (read <= 0) break;
                                    total += read;
                                }
                            }
                        }
                    }
                    catch { }
                    return statusCode >= 200 && statusCode < 300;
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
                            if (!string.IsNullOrEmpty(raw)) detail = detail + " | " + Shorten(raw, 200);
                        }
                    }
                }
                catch { }
                _log.Debug("Presence POST failed: " + detail);
                return false;
            }
            catch (Exception ex)
            {
                _log.Debug("Presence POST failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            finally
            {
                if (request != null)
                {
                    try { request.Abort(); } catch { }
                }
            }
        }

        private static string BuildJson(string eventName, Snapshot snapshot)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("{\"ts\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append("\"");
            sb.Append(",\"host\":\"").Append(JsonEscape(Environment.MachineName)).Append("\"");
            sb.Append(",\"version\":\"").Append(JsonEscape(Program.Version)).Append("\"");
            sb.Append(",\"idleSeconds\":");
            if (snapshot.IdleSeconds.HasValue) sb.Append(snapshot.IdleSeconds.Value.ToString(CultureInfo.InvariantCulture));
            else sb.Append("null");
            sb.Append(",\"absentSeconds\":").Append(snapshot.AbsentSeconds.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"inRange\":").Append(snapshot.InRange ? "true" : "false");
            sb.Append(",\"state\":\"").Append(JsonEscape(snapshot.State)).Append("\"");
            sb.Append(",\"paused\":").Append(snapshot.Paused ? "true" : "false");
            sb.Append(",\"event\":\"").Append(JsonEscape(eventName)).Append("\"");
            sb.Append("}");
            return sb.ToString();
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

        private static string Shorten(string value, int maxLength)
        {
            if (value == null) return "";
            string trimmed = value.Trim();
            if (trimmed.Length <= maxLength) return trimmed;
            return trimmed.Substring(0, maxLength) + "...";
        }
    }
}
