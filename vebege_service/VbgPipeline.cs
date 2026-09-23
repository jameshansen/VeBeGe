using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace VeBeGe
{
    /// Everything between "a camera frame arrived" and "this is what the
    /// virtual camera shows": the filter, its model load, and the startup
    /// loading screen. Its job is to decouple the two rates involved. The
    /// filter runs at whatever it can manage (~7 fps at 720p), while Present
    /// renders the current output as often as the caller asks for it, repeating
    /// the last processed frame in between. That is what makes the loading
    /// animation smooth at camera rate instead of stepping along at the
    /// filter's, and it is why the loading screen lives here rather than in the
    /// service's pump: the service and both Testing tools drive this same
    /// object, so all three show the same thing.
    ///
    /// Two ways in, one way out:
    ///   Submit  realtime, hands the frame to a worker and returns immediately
    ///   Process offline, runs the filter on the calling thread and blocks
    ///   Present writes the current output frame, at any rate, any number of times
    internal sealed class VbgPipeline : IDisposable
    {
        private const double ModelShare = 0.1;   // of the loading dial, phase one: models up.

        private readonly object _gate = new object();
        private readonly LoadingOverlay _loading = new LoadingOverlay();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _pad, _stayFrames;
        private readonly double _bodyScale;
        private int _resetSeen, _resetShown;

        private Task<VbgFilter> _load;   // model load, off the caller's thread
        private VbgFilter _filter;
        private Task _work;              // in-flight Process (realtime path)
        private Mat _workFrame;          // the frame that worker owns
        private Mat _shown;              // last finished output: what Present repeats
        private double _progress;        // learn progress, sampled when a Process finishes
        private long _processed;
        private readonly double _fps;
        private double _lastStart = -1;  // clock at the last realtime Process start
        private double _owed;            // fractional camera frames carried to the next one

        /// fps is the source's frame rate: the filter's tuning is in seconds
        /// (ini [Filter]) and converts to frames against it. frameSize is what
        /// the source delivers: the models are warmed up at that shape during
        /// the load, so the first real frame isn't the slow one.
        public VbgPipeline(string modelDir, double fps, Size frameSize)
        {
            _pad = Config.Padding;
            _bodyScale = Config.BodyScale;
            _fps = fps;
            _stayFrames = (int)Math.Round(Config.StaySeconds * fps);
            _loading.Budget = Config.StartupSeconds;
            // Loading the ONNX models takes a moment. Off the caller's thread,
            // so the loading screen is live and moving from the very first
            // frame instead of the consuming app sitting on the driver's
            // static placeholder.
            _load = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var f = new VbgFilter(modelDir, frameSize)
                {
                    HeatMinFlow = Config.HeatMinFlow,
                    HeatSpread = Config.HeatSpread,
                    HeatCooldownFrames = (int)Math.Round(Config.HeatCooldownSeconds * fps),
                    MaskHoldFrames = (int)Math.Round(Config.MaskHoldSeconds * fps),
                    QuietShieldFrames = (int)Math.Round(Config.QuietShieldSeconds * fps),
                };
                LoadMs = sw.Elapsed.TotalMilliseconds;
                return f;
            });
        }

        /// Wall time the model load (read + warm-up) took, once it has.
        public double LoadMs { get; private set; }

        /// The filter itself, for the diagnostic views (mask, plate, heat, the
        /// tracked people). Null until the models are up. Valid to read on the
        /// realtime path only when ProcessedFrames has just changed: the worker
        /// is idle exactly then, since only the caller starts it.
        public VbgFilter Filter => _filter;

        /// Models missing or corrupt: the pipeline passes frames straight
        /// through (the camera still "just works") and Present does nothing.
        public bool Broken => LoadError != null;
        public Exception LoadError { get; private set; }

        /// Still blurred behind the loading screen.
        public bool Loading { get { lock (_gate) return !_loading.Done; } }

        /// Bumped every time a new processed frame lands.
        public long ProcessedFrames => Interlocked.Read(ref _processed);

        /// Wall time the last Process took (ms).
        public double LastProcessMs { get; private set; }

        /// Realtime: hand the newest frame to the filter without blocking.
        /// False while the previous one is still in flight, which drops this
        /// frame, exactly what running the filter on the pump thread did
        /// implicitly.
        public bool Submit(Mat frame)
        {
            if (frame == null || frame.Empty()) return false;
            if (_work != null)
            {
                if (!_work.IsCompleted) return false;
                _work = null;
                _workFrame?.Dispose(); _workFrame = null;
            }
            VbgFilter filter = Ready(false);
            if (filter == null) return false;
            _workFrame = frame.Clone();
            Mat wf = _workFrame;
            // The heat cooldown runs on WALL time: the filter is told how many
            // frames of the configured rate went by since its last run. Not a
            // count of delivered frames, a webcam in a dim room drops to 10 fps
            // on its own, which turned a 3 s cooldown into 9 s and let the
            // loading deadline reveal a half-learned plate.
            double now = _clock.Elapsed.TotalSeconds;
            int elapsed = 1;
            if (_lastStart >= 0)
            {
                _owed += (now - _lastStart) * _fps;
                elapsed = Math.Max(1, (int)_owed);
                _owed -= elapsed;
            }
            _lastStart = now;
            _work = Task.Run(() =>
            {
                // The service must keep serving whatever happens, so a filter
                // fault is logged and swallowed here; the synchronous path
                // below lets it out, the Testing tool wants to know.
                try { RunFilter(filter, wf, elapsed); }
                catch (Exception ex) { Log.Write("filter.Process", ex); }
            });
            return true;
        }

        /// Offline: run the filter on this frame, in place, on the calling
        /// thread. The Testing tool deliberately processes every frame;
        /// elapsedFrames > 1 tells the filter it skipped some (its realtime
        /// simulation), so the heat cooldown still runs on video time.
        public void Process(Mat frame, int elapsedFrames = 1)
        {
            VbgFilter filter = Ready(true);
            if (filter == null || frame == null || frame.Empty()) return;
            RunFilter(filter, frame, elapsedFrames);
        }

        private void RunFilter(VbgFilter filter, Mat frame, int elapsedFrames)
        {
            var sw = Stopwatch.StartNew();
            filter.Process(frame, _pad, _stayFrames, _bodyScale, elapsedFrames);
            LastProcessMs = sw.Elapsed.TotalMilliseconds;
            lock (_gate)
            {
                _shown?.Dispose();
                _shown = frame.Clone();
                // The two startup phases the dial reports, in proportion:
                // getting the models up is the first tenth (it either has
                // happened or it hasn't, there is no sub-progress to report,
                // and we are only here because it has), the plate cooling in is
                // the rest. O(N) scan, so only while the dial still needs it.
                if (!_loading.Done) _progress = ModelShare + (1 - ModelShare) * filter.LearnProgress;
                _resetSeen = filter.PlateResets;
            }
            // Last: the tools read the diagnostic views off this changing.
            Interlocked.Increment(ref _processed);
        }

        /// What the camera shows right now, written into dst: the filter's last
        /// output, with the loading screen composited over it while it's up.
        /// Call as often as you want to emit a frame.
        public void Present(Mat dst) => Present(dst, _clock.Elapsed.TotalSeconds);

        /// As Present(dst), with the caller's own clock. The Testing tool
        /// passes VIDEO time, so a clip run faster or slower than real time
        /// still shows the loading screen over its first StartupSeconds of
        /// footage rather than of wall time.
        public void Present(Mat dst, double seconds)
        {
            if (Broken || dst == null || dst.Empty()) return;
            lock (_gate)
            {
                if (_shown != null && _shown.Size() == dst.Size() && _shown.Type() == dst.Type())
                    _shown.CopyTo(dst);
                if (_loading.Done && _resetShown != _resetSeen)
                {
                    _loading.Restart(seconds);   // camera moved, the plate is gone
                    // Progress stops being published once the screen is down, so
                    // what's in there is the "finished" value. Floor it, the next
                    // processed frame publishes the real post-reset figure.
                    _progress = ModelShare;
                }
                _resetShown = _resetSeen;
                _loading.Apply(dst, _progress, seconds);
            }
        }

        // Collect the model load. wait = block for it (the offline path has
        // nothing useful to do without it).
        private VbgFilter Ready(bool wait)
        {
            if (_load == null || !(wait || _load.IsCompleted)) return _filter;
            try { _filter = _load.Result; }
            catch (Exception ex) { LoadError = ex.GetBaseException(); }
            _load = null;
            return _filter;
        }

        public void Dispose()
        {
            if (_load != null)
            {
                try { _load.Result.Dispose(); } catch { }   // finish loading, then bin it
                _load = null;
            }
            if (_work != null)
            {
                try { _work.Wait(); } catch { }
                _work = null;
            }
            _workFrame?.Dispose(); _workFrame = null;
            _shown?.Dispose(); _shown = null;
            _filter?.Dispose(); _filter = null;
        }
    }
}
