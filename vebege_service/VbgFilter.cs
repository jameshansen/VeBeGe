using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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
        // Detection runs on EVERY frame. Every-2nd-frame was tried and reverted:
        // at low effective fps a walker moves too far between samples, the
        // tracker loses them, and they walk through unblurred.

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

        /// How far the background plate has come, 0..1: how much of the motion
        /// cooldown the scene has worked through (mean coldness, a smooth ramp
        /// for the dial), and exactly 1 once tier-two has nothing large left to
        /// smear, i.e. the output is trustworthy. Coldness alone never quite
        /// gets there on a real webcam: sensor-noise flow keeps a few percent
        /// of the map hot for good, so a dial waiting for 1.0 only ever ended
        /// on the deadline. Stalls below 1 while someone parked in the shot
        /// keeps a body region unlearned. Drives the startup loading screen.
        public double LearnProgress =>
            _vbm.TierTwoHoles <= DoneHoles ? 1 : Math.Min(0.99, _vbm.Coldness);
        private const double DoneHoles = 0.02;   // of the frame; below this the smear is invisible

        /// Bumped whenever a sustained scene change (the camera moved) throws
        /// the learned plate away: the pipeline puts its loading screen back up.
        public int PlateResets => _vbm.PlateResets;

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

        /// The tracked face boxes that shielded the background this frame, index-
        /// aligned with LastPeople (LastFaces[i] is the face BodyRegion produced
        /// LastPeople[i] from). Diagnostic: the Testing tools draw the detected
        /// face and its inferred body on the face-detection view.
        public IReadOnlyList<Rect> LastFaces { get; private set; } = new Rect[0];

        /// Both models must be present (the service stages them into
        /// %ProgramData%\VeBeGe); throws with a clear message if not.
        /// frameSize: the frames that will be processed. OpenCV's DNN builds its
        /// layer plan on the first forward for a given input shape, so that
        /// cost is paid here (inside the pipeline's background load, off the
        /// camera thread) rather than on the first real frame.
        public VbgFilter(string modelDir, Size frameSize)
        {
            // OpenCV defaults to one worker per LOGICAL core, and on an SMT
            // machine that over-subscribes: measured 24 → 12 threads took the
            // whole frame from 83 to 74 ms and the two nets from 25/22 to
            // 19/14 ms (Ryzen 3900-class). Process-wide, so once is enough.
            // ponytail: assumes 2-way SMT; query the physical count if a
            // non-SMT many-core box ever shows up as slow.
            Cv2.SetNumThreads(Math.Min(Environment.ProcessorCount, Math.Max(4, Environment.ProcessorCount / 2)));
            _detector = new YuNetFaceDetector(RequireModel(modelDir, YuNetModel));
            var segmenter = new PersonSegmenter(RequireModel(modelDir, SegModel));
            _vbm = new VirtualBackgroundModel(segmenter);
            if (frameSize.Width > 0 && frameSize.Height > 0)
                using (var blank = new Mat(frameSize, MatType.CV_8UC3, Scalar.All(0)))
                {
                    segmenter.Segment(blank).Dispose();
                    _detector.Detect(blank);
                }
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
        /// keeps shielding the background; bodyScale: body width in face widths;
        /// elapsedFrames: camera frames since the last processed one (1 when
        /// every frame is processed), so the heat cooldown runs on camera time
        /// even when the filter can't keep up.
        public void Process(Mat frame, int padPx, int stayFrames, double bodyScale, int elapsedFrames = 1)
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
                // Face detection (on the background cutout only, so the webcam
                // user is never treated as a background person) and the flow
                // half of the heat update are independent, each on its own
                // net/state and both only reading the mask, so they run side
                // by side. The tracked people are baked into the heat after.
                IReadOnlyList<Rect> detections = null;
                bool cameraEvent = false;
                double detectMs = 0;
                Parallel.Invoke(
                    () =>
                    {
                        var sw = Stopwatch.StartNew();
                        using (var cut = _vbm.BackgroundCutout(frame))
                            detections = _detector.Detect(cut);
                        detectMs = sw.Elapsed.TotalMilliseconds;
                    },
                    () => cameraEvent = _vbm.UpdateMotion(frame, elapsedFrames));
                Mark("det|heat");   // wall time of the pair; each one's own cost follows
                LastStageMs.Add(new KeyValuePair<string, double>("detect", detectMs));
                LastStageMs.Add(new KeyValuePair<string, double>("heat", _vbm.LastHeatMs));
                LastStageMs.AddRange(_vbm.LastHeatStages);   // heat's internal phases
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

                _tracker.MaxAge = Math.Max(0, stayFrames);
                var people = new List<Rect>();
                var faces = new List<Rect>();
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
                    {
                        people.Add(body);
                        faces.Add(t.Box);
                    }
                }
                LastPeople = people;
                LastFaces = faces;

                // Learn the scene where nobody is, replace the whole background
                // with the learned plate (unlearned areas keep the live frame),
                // then composite the live subject back on top.
                _vbm.Update(frame, people, cameraEvent);
                Mark("learn");
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
        // A rectangle, not a body model, over-shielding is the safe
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
