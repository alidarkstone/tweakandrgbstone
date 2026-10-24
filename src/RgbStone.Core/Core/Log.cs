using System;
using System.IO;

namespace RgbStone.Core
{
    /// <summary>Local log file next to the exe. Nothing is ever sent anywhere.</summary>
    public static class Log
    {
        static readonly object Sync = new object();
        static string _path;

        public static event Action<string> Line;

        public static void Init(string dir)
        {
            _path = Path.Combine(dir, "rgbstone.log");
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                    File.Copy(_path, _path + ".old", true);
                if (fi.Exists && fi.Length > 1024 * 1024)
                    File.Delete(_path);
            }
            catch { }
        }

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg, Exception ex = null) => Write("ERR ", ex == null ? msg : msg + ": " + ex.Message);

        static void Write(string level, string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + msg;
            lock (Sync)
            {
                try { if (_path != null) File.AppendAllText(_path, line + Environment.NewLine); } catch { }
            }
            Line?.Invoke(line);
        }
    }
}
