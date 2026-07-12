using System;
using System.Collections.Generic;
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

        // Per-stage aggregates (avg + max), in first-seen order.
        private readonly List<string> _stageOrder = new List<string>();
        private readonly Dictionary<string, double> _stageSum = new Dictionary<string, double>();
        private readonly Dictionary<string, double> _stageMax = new Dictionary<string, double>();

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

        /// Record one frame's filter processing time in milliseconds, with an
        /// optional per-stage breakdown (aggregated into the summary).
        public void Record(double ms, IReadOnlyList<KeyValuePair<string, double>> stages = null)
        {
            if (stages != null)
                foreach (var s in stages)
                {
                    if (!_stageSum.ContainsKey(s.Key))
                    {
                        _stageOrder.Add(s.Key);
                        _stageSum[s.Key] = 0;
                        _stageMax[s.Key] = 0;
                    }
                    _stageSum[s.Key] += s.Value;
                    if (s.Value > _stageMax[s.Key]) _stageMax[s.Key] = s.Value;
                }
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
                foreach (string s in _stageOrder)
                    Line(string.Format(Inv, "# stage {0,-8}: avg {1,7:0.0} ms, max {2,7:0.0} ms",
                        s, _stageSum[s] / _count, _stageMax[s]));
            }
            _w.Dispose();
        }

        /// Free-form comment line into the log (e.g. realtime drop stats).
        public void Note(string s) => Line("# " + s);

        private void Line(string s) => _w.WriteLine(s);
    }
}
