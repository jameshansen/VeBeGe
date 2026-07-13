using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using OpenCvSharp;

namespace VeBeGe
{
    /// JustShowMe's "Virtual Background" mode, and nothing else: the live
    /// subject is composited over an accumulated people-free background, so
    /// anyone walking through the shot never appears. No face recognition,
    /// detection + tracking exist only to keep background people out of the
    /// learned background plate.
    internal sealed class VbgFilter : IDisposable
    {
        private const string YuNetModel = "face_detection_yunet_2023mar.onnx";
        private const string SegModel = "human_segmentation_pphumanseg_2023mar.onnx";

        private readonly YuNetFaceDetector _detector;
        private readonly VirtualBackgroundModel _vbm;
        private readonly FaceTracker _tracker = new FaceTracker();
        private Mat _tierTwoBg;   // background composite (tier one + tier two) before the user goes on.

        // YuNet costs ~20 ms/frame, but skipping frames proved too lossy: at low
        // effective fps a walker moves too far between samples and slips through.
        // Keep 1 (detect every frame) unless the fps budget truly demands it.
        private const int DetectEvery = 1;
        private int _frameNo;
        private IReadOnlyList<Rect> _lastDetections = new Rect[0];

        /// Diagnostic hooks (used by the Testing harness): the per-frame
        /// foreground mask (255 = subject), the accumulated virtual background
        /// plate (black where still unknown), and the motion heatmap (cooldown
        /// frames remaining; 0 = cold/learnable). Valid after Process.
        public Mat ForegroundMask => _vbm.ForegroundMask;
        public Mat VirtualBackground => _vbm.Background;
        public Mat MotionHeat => _vbm.Heat;

        /// The full background the live subject is composited onto: the learned
        /// plate (tier one) with the ffmpeg-delogo inpaint fallback (tier two)
        /// filling any still-unknown movers. Snapshot before the user is painted
        /// back on. Valid after Process.
        public Mat TierTwoBackground => _tierTwoBg;

        /// Per-stage wall times (ms) of the last Process call, in pipeline order.
        /// Diagnostics for the Testing harness; costs ~nothing to maintain.
        public readonly List<KeyValuePair<string, double>> LastStageMs =
            new List<KeyValuePair<string, double>>();
        private readonly Stopwatch _stageSw = new Stopwatch();
        private double _stageLast;

        private void Mark(string stage)
        {
            double now = _stageSw.Elapsed.TotalMilliseconds;
            LastStageMs.Add(new KeyValuePair<string, double>(stage, now - _stageLast));
            _stageLast = now;
        }

        /// Motion-heatmap tuning, forwarded to the background model
        /// (ini [Filter] HeatMinFlow / HeatSpread / HeatCooldownSeconds).
        public double HeatMinFlow { get => _vbm.HeatMinFlow; set => _vbm.HeatMinFlow = value; }
        public int HeatSpread { get => _vbm.HeatSpread; set => _vbm.HeatSpread = value; }
        public int HeatCooldownFrames { get => _vbm.HeatCooldownFrames; set => _vbm.HeatCooldownFrames = value; }

        /// Segmenter-dropout hold (ini [Filter] MaskHoldSeconds), forwarded to the
        /// background model: frames to reuse the last good subject mask.
        public int MaskHoldFrames { get => _vbm.MaskHoldFrames; set => _vbm.MaskHoldFrames = value; }

        /// Quiet-shield expiry (ini [Filter] QuietShieldSeconds): a tracked face
        /// whose whole body region has shown zero true motion for this many
        /// frames is static scenery the detector keeps false-matching (wall art,
        /// posters), not a person. It stops shielding so the area can cool,
        /// learn, and heal from tier-two blur to the real tier-one plate. Any
        /// motion in the region re-arms the shield instantly. 0 = never expire.
        public int QuietShieldFrames { get; set; } = 300;

        /// The body regions that shielded the background this frame (diagnostic:
        /// the Testing tools draw them onto the heat view).
        public IReadOnlyList<Rect> LastPeople { get; private set; } = new Rect[0];

        /// Both models must be present (the service stages them into
        /// %ProgramData%\VeBeGe); throws with a clear message if not.
        public VbgFilter(string modelDir)
        {
            _detector = new YuNetFaceDetector(RequireModel(modelDir, YuNetModel));
            _vbm = new VirtualBackgroundModel(new PersonSegmenter(RequireModel(modelDir, SegModel)));
        }

        private static string RequireModel(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path))
                throw new FileNotFoundException($"AI model missing: {path}", path);
            return path;
        }

        /// Processes the BGR frame in place.
        /// padPx: dilate the subject mask; stayFrames: how long a lost face
        /// keeps shielding the background; bodyScale: body width in face widths.
        public void Process(Mat frame, int padPx, int stayFrames, double bodyScale)
        {
            if (frame == null || frame.Empty()) return;
            LastStageMs.Clear();
            _stageSw.Restart();
            _stageLast = 0;

            // Split the frame into the live subject and the background plane.
            _vbm.UpdateMask(frame, padPx);
            Mark("seg");
            if (_vbm.ForegroundMask == null) return;

            using (var clean = frame.Clone())
            {
                // Detect faces on the background cutout only, so the webcam
                // user is never treated as a background person.
                IReadOnlyList<Rect> detections;
                if (_frameNo++ % DetectEvery == 0)
                {
                    using (var cut = _vbm.BackgroundCutout(frame))
                        detections = _detector.Detect(cut);
                    _lastDetections = detections;
                }
                else detections = _lastDetections;
                // Segmentation can lag a frame when the user moves fast, leaving
                // their face uncovered in the cutout; detected, it would become a
                // phantom "background person" whose body region blurs most of
                // the frame. A face right on top of (or beside) the subject's
                // mask is that phantom, not a background person, drop it. Anyone
                // real standing that close to the subject is still covered by
                // the motion heatmap.
                var kept = new List<Rect>();
                foreach (var d in detections)
                    if (!_vbm.NearSubject(d)) kept.Add(d);
                Mark("detect");

                _tracker.MaxAge = Math.Max(0, stayFrames);
                var people = new List<Rect>();
                foreach (var t in _tracker.Update(kept))
                {
                    Rect body = BodyRegion(t.Box, frame.Size(), bodyScale);
                    // Quiet-shield expiry: real people always produce flow motion
                    // somewhere in their body region; static scenery the detector
                    // false-fires on (wall art) never does. Without this, a false
                    // face re-shields its column at full heat every frame forever,
                    // so the area can never learn and stays tier-two blurred.
                    t.Quiet = _vbm.HasRecentMotion(body) ? 0 : t.Quiet + 1;
                    if (QuietShieldFrames <= 0 || t.Quiet < QuietShieldFrames)
                        people.Add(body);
                }
                LastPeople = people;

                // Learn the scene where nobody is, replace the whole background
                // with the learned plate (unlearned areas keep the live frame),
                // then composite the live subject back on top.
                _vbm.Update(frame, people);
                Mark("update");
                // Split update into its two halves for the diagnostics.
                LastStageMs[LastStageMs.Count - 1] = new KeyValuePair<string, double>(
                    "learn", LastStageMs[LastStageMs.Count - 1].Value - _vbm.LastHeatMs);
                LastStageMs.Insert(LastStageMs.Count - 1,
                    new KeyValuePair<string, double>("heat", _vbm.LastHeatMs));
                LastStageMs.AddRange(_vbm.LastHeatStages);   // heat's internal phases
                _vbm.FillKnownBackground(frame, new Rect(0, 0, frame.Width, frame.Height));
                Mark("fill");
                // Fallback for movers we can't erase (no learned background behind them
                // yet): inpaint out everything hot-but-unknown from its surroundings.
                _vbm.FillTierTwo(frame);
                Mark("tier2");
                _tierTwoBg?.Dispose();
                _tierTwoBg = frame.Clone();   // the background composite, before the user goes on
                _vbm.CompositeForeground(clean, frame);
                Mark("comp");
            }
        }

        // A whole-person region anchored on the face: bodyScale face-widths
        // wide, from a face-height above the head down to the frame bottom.
        // ponytail: a rectangle, not a body model, over-shielding is the safe
        // error (it only delays background learning in that area).
        private static Rect BodyRegion(Rect face, Size bounds, double widthFactor)
        {
            int w = (int)(face.Width * widthFactor);
            int x = face.X + face.Width / 2 - w / 2;
            int top = face.Y - face.Height;
            if (x < 0) { w += x; x = 0; }
            if (top < 0) top = 0;
            if (x + w > bounds.Width) w = bounds.Width - x;
            int h = bounds.Height - top;
            return new Rect(x, top, Math.Max(0, w), Math.Max(0, h));
        }

        public void Dispose()
        {
            _detector?.Dispose();
            _vbm?.Dispose();
            _tierTwoBg?.Dispose();
        }
    }
}
