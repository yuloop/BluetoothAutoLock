using System;
using System.IO;
using System.Text;

namespace BluetoothAutoLock
{
    internal sealed class Logger
    {
        public enum Level { Debug = 0, Info = 1, Warn = 2, Error = 3 }

        private readonly object _gate = new object();
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly Level _minLevel;
        private readonly bool _alsoConsole;

        public Logger(string path, string minLevel, int maxMB, bool alsoConsole)
        {
            _path = path;
            _maxBytes = (long)maxMB * 1024L * 1024L;
            _alsoConsole = alsoConsole;
            _minLevel = ParseLevel(minLevel);
            EnsureDirectory();
        }

        private static Level ParseLevel(string s)
        {
            if (string.IsNullOrEmpty(s)) return Level.Info;
            switch (s.Trim().ToLowerInvariant())
            {
                case "debug": return Level.Debug;
                case "info": return Level.Info;
                case "warn": case "warning": return Level.Warn;
                case "error": case "err": return Level.Error;
                default: return Level.Info;
            }
        }

        private void EnsureDirectory()
        {
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch { /* fall back to console-only on next write */ }
        }

        public void Debug(string msg) { Write(Level.Debug, msg); }
        public void Info(string msg) { Write(Level.Info, msg); }
        public void Warn(string msg) { Write(Level.Warn, msg); }
        public void Error(string msg) { Write(Level.Error, msg); }

        public void Write(Level level, string msg)
        {
            if (level < _minLevel) return;
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1,-5}] {2}",
                DateTime.Now, level.ToString().ToUpperInvariant(), msg);

            lock (_gate)
            {
                if (_alsoConsole)
                {
                    try { Console.WriteLine(line); } catch { }
                }

                try
                {
                    RotateIfNeeded();
                    using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete))
                    using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        sw.WriteLine(line);
                    }
                }
                catch
                {
                    // Logging must never throw out of the call site.
                }
            }
        }

        private void RotateIfNeeded()
        {
            try
            {
                var fi = new FileInfo(_path);
                if (!fi.Exists || fi.Length < _maxBytes) return;

                string rotated = _path + ".1";
                if (File.Exists(rotated)) File.Delete(rotated);
                File.Move(_path, rotated);
            }
            catch { /* if rotation fails, keep appending */ }
        }
    }
}
