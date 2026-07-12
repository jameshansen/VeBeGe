using System;
using System.Globalization;
using System.IO;
using System.Diagnostics;

namespace VeBeGe.Testing
{
    /// Tiny per-frame performance tracker shared by the mp4 and live tools.
    /// Times the filter work per frame, writes a tab-separated per-second
    /// throughput line to a log file, and a summary on dispose. Optionally
    /// echoes the per-second lines to the console (used by the live tool).
    internal sealed class PerfLog : IDisposable
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly StreamWriter _w;
        private readonly bool _echo;
        private readonly Stopwatch _wall = Stopwatch.StartNew();

        private long _count;
        private double _sumMs, _minMs = double.MaxValue, _maxMs;

        // Rolling one-second bucket for instantaneous fps.
        private long _bucketCount;
        private double _bucketStartSec;

        public PerfLog(string path, string header, bool echoToConsole = false)
        {
            _echo = echoToConsole;
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _w = new StreamWriter(path, append: false) { AutoFlush = true };
            Line("# " + header);
            Line("# started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Line("# times are the real filter.Process() cost per frame (excludes IO/display)");
            Line("elapsed_s\tframes\tinst_fps\tavg_ms\tavg_fps");
        }

        /// Record one frame's filter processing time in milliseconds.
        public void Record(double ms)
        {
            _count++;
            _sumMs += ms;
            if (ms < _minMs) _minMs = ms;
            if (ms > _maxMs) _maxMs = ms;
            _bucketCount++;

            double now = _wall.Elapsed.TotalSeconds;
            double span = now - _bucketStartSec;
            if (span >= 1.0)
            {
                string s = string.Format(Inv, "{0,8:0.0}\t{1,6}\t{2,7:0.0}\t{3,6:0.0}\t{4,6:0.0}",
                    now, _count, _bucketCount / span, _sumMs / _count, _count / now);
                Line(s);
                if (_echo) Console.WriteLine(s.Trim());
                _bucketStartSec = now;
                _bucketCount = 0;
            }
        }

        public void Dispose()
        {
            double sec = _wall.Elapsed.TotalSeconds;
            Line("# ---- summary ----");
            Line(string.Format(Inv, "# frames         : {0}", _count));
            Line(string.Format(Inv, "# wall_time_s    : {0:0.00}", sec));
            if (_count > 0)
            {
                Line(string.Format(Inv, "# avg_ms         : {0:0.00}", _sumMs / _count));
                Line(string.Format(Inv, "# min_ms         : {0:0.00}", _minMs));
                Line(string.Format(Inv, "# max_ms         : {0:0.00}", _maxMs));
                Line(string.Format(Inv, "# throughput_fps : {0:0.0}", _count / sec));
            }
            _w.Dispose();
        }

        private void Line(string s) => _w.WriteLine(s);
    }
}
