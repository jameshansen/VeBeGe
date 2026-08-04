using System;
using OpenCvSharp;

namespace VeBeGe
{
    /// Startup feedback for one pump. Until the models are up and the
    /// background plate has settled, the feed is blurred out with a progress
    /// dial in the middle; without it the raw camera comes up instantly and the
    /// scene visibly clears in patches for ten-odd seconds, which reads as a
    /// broken filter rather than a loading one. When loading finishes (or the
    /// budget expires, the plate often never reaches 100%) the dial fades, then
    /// the blur cross-fades away in a second.
    ///
    /// Drawn by VbgPipeline's output stage, which calls it once per OUTPUT
    /// frame, so the animation is as smooth as the caller's output rate rather
    /// than the filter's throughput. The clock is supplied by the caller: wall
    /// time for a live camera, video time when the Testing tool runs a clip
    /// through faster or slower than real time.
    internal sealed class LoadingOverlay
    {
        private const double RingFadeSeconds = 0.3;   // dial dissolves first,
        private const double BlurFadeSeconds = 1.0;   // then the blur lifts.
        private const double EaseSeconds = 0.15;      // dial chases real progress, it doesn't step to it.
        private const int Downscale = 16;             // blur = shrink, smooth, grow back.
        private const int Shift = 4;                  // 1/16-px fixed point: sub-pixel dial geometry.
        private const int S = 1 << Shift;
        private static readonly Scalar White = Scalar.All(255);

        private double _t0;          // caller-clock value this run started at.
        private double _lastT;
        private double _target;      // ratcheted progress: never winds backwards.
        private double _shown;       // eased progress actually drawn.
        private double _doneAt = -1; // seconds at which loading completed.

        /// Seconds to wait for the plate before revealing the feed anyway.
        /// 0 or less turns the loading screen off altogether (ini
        /// [Service] StartupSeconds).
        public double Budget { get; set; } = 10;

        /// True once the fade has run out: Apply is a no-op from here on, and
        /// the caller can stop asking the filter for its progress.
        public bool Done { get; private set; }

        /// Put the loading screen back up, from `now` on the caller's clock.
        /// The plate was thrown away (the camera moved), so there is nothing
        /// learned to show again until it has been rebuilt.
        public void Restart(double now)
        {
            _t0 = now;
            _lastT = _target = _shown = 0;
            _doneAt = -1;
            Done = false;
        }

        /// Blurs the frame and draws the dial on it, in place. progress is the
        /// filter's own 0..1 learn progress; t is the caller's clock, in
        /// seconds since the stream started.
        public void Apply(Mat frame, double progress, double t)
        {
            if (Done || frame == null || frame.Empty()) return;
            if (Budget <= 0) { Done = true; return; }
            t -= _t0;

            double dt = Math.Max(0, t - _lastT);
            _lastT = t;
            // The dial reports the real thing: models up, then the plate cooling
            // in. Ratcheted, since a re-igniting scene must not wind it back.
            // The budget is a deadline, not the animation: when it runs out the
            // dial fills and the feed is revealed however far the plate got,
            // because with someone parked in the shot it may never reach 100%.
            _target = t >= Budget ? 1 : Math.Min(1, Math.Max(_target, progress));
            // Progress only updates when a frame finishes filtering (~6 fps), so
            // chase it rather than stepping to it, or the dial stutters between
            // updates at exactly the output rate this all exists to smooth.
            _shown += (_target - _shown) * (1 - Math.Exp(-dt / EaseSeconds));
            if (_target - _shown < 0.002) _shown = _target;
            if (_doneAt < 0 && _shown >= 1) _doneAt = t;

            double blur = 1, ring = 1;
            if (_doneAt >= 0)
            {
                double fade = t - _doneAt;
                ring = 1 - fade / RingFadeSeconds;
                blur = Math.Min(1, 1 - (fade - RingFadeSeconds) / BlurFadeSeconds);
                if (blur <= 0) { Done = true; return; }
            }

            using (var small = new Mat())
            using (var soft = new Mat())
            {
                var ss = new Size(Math.Max(2, frame.Width / Downscale), Math.Max(2, frame.Height / Downscale));
                Cv2.Resize(frame, small, ss, 0, 0, InterpolationFlags.Area);
                Cv2.GaussianBlur(small, small, new Size(0, 0), 2);   // no box-filter facets on the way back up
                Cv2.Resize(small, soft, frame.Size(), 0, 0, InterpolationFlags.Cubic);
                if (blur >= 1) soft.CopyTo(frame);
                else Cv2.AddWeighted(soft, blur, frame, 1 - blur, 0, frame);
            }

            if (ring <= 0) return;
            // Drawn on a copy and blended, the only way to get a translucent
            // shape out of Cv2's opaque drawing calls. Startup-only, so the
            // full-frame clone costs nothing that matters.
            using (var o = frame.Clone())
            {
                DrawDial(o, new Point(frame.Width / 2, frame.Height / 2),
                         (int)(frame.Height * 0.11), Math.Max(2, frame.Height / 90), _shown);
                if (ring >= 1) o.CopyTo(frame);
                else Cv2.AddWeighted(o, ring, frame, 1 - ring, 0, frame);
            }
        }

        // Outline circle plus a filled sector sweeping clockwise from 12
        // o'clock. The sector is ONE polygon: a separate 12 o'clock tick line
        // would poke out past its leading edge and wobble as the sector grows.
        // Its leading edge is EXACTLY the 12 o'clock radius at every progress
        // (nothing is centred on 12, that tilts the edge by half the minimum
        // sweep and the hand visibly leans). At zero progress the sector
        // degenerates to a tick-width sliver hanging off that radius, which is
        // the clock hand, and at full it fills the circle. Coordinates are
        // 1/16-px fixed point (shift), so the edges move sub-pixel smoothly
        // instead of snapping between whole pixels.
        private static void DrawDial(Mat img, Point c, int r, int th, double progress)
        {
            Cv2.Circle(img, new Point(c.X * S, c.Y * S), r * S, White, th, LineTypes.AntiAlias, Shift);

            int rw = Math.Max(1, r - th / 2);   // sector stops at the ring's inner edge
            // Below a hand's width of sweep the sector is padded out to one and
            // straddles 12 o'clock, so a dial at zero is a hand pointing
            // straight up rather than a sliver hanging off to the right. The
            // padding shrinks to nothing as real sweep takes over, which rotates
            // the leading edge onto 12 o'clock exactly and leaves it there.
            double minSweep = 2 * Math.Atan2(th / 2.0, rw) * 180 / Math.PI;
            double sweep = 360 * progress, lean = 0;
            if (sweep < minSweep) { lean = (minSweep - sweep) / 2; sweep = minSweep; }
            double start = -90 - lean;                   // 12 o'clock, straight up
            int steps = Math.Max(2, (int)(sweep / 2));   // ~2 deg per segment
            var pts = new Point[steps + 2];
            pts[0] = new Point(c.X * S, c.Y * S);
            for (int i = 0; i <= steps; i++)
            {
                double a = (start + sweep * i / steps) * Math.PI / 180;
                pts[i + 1] = new Point((int)Math.Round((c.X + rw * Math.Cos(a)) * S),
                                       (int)Math.Round((c.Y + rw * Math.Sin(a)) * S));
            }
            Cv2.FillPoly(img, new[] { pts }, White, LineTypes.AntiAlias, Shift);
        }
    }
}
