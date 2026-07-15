using System;
using System.IO;

namespace VeBeGe
{
    /// Dead-simple append log to %ProgramData%\VeBeGe\log.txt, the service is
    /// headless, so this is the only way to see what it's doing.
    public static class Log
    {
        public static readonly string Path_ = Path.Combine(Config.Dir, "log.txt");
        private static readonly object _lock = new object();

        public static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                lock (_lock)
                {
                    // 5MB cap, one rotation. Enough history, never fills a disk.
                    var fi = new FileInfo(Path_);
                    if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                        File.Copy(Path_, Path_ + ".old", true);
                    if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                        File.Delete(Path_);
                    File.AppendAllText(Path_,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}");
                }
            }
            catch { /* logging must never throw */ }
        }

        public static void Write(string context, Exception ex) => Write($"{context}: {ex}");
    }
}
