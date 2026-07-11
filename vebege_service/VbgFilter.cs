using System;
using System.Collections.Generic;
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

        /// Motion-heatmap tuning, forwarded to the background model
        /// (ini [Filter] HeatMinFlow / HeatSpread / HeatCooldownSeconds).
        public double HeatMinFlow { get => _vbm.HeatMinFlow; set => _vbm.HeatMinFlow = value; }
        public int HeatSpread { get => _vbm.HeatSpread; set => _vbm.HeatSpread = value; }
        public int HeatCooldownFrames { get => _vbm.HeatCooldownFrames; set => _vbm.HeatCooldownFrames = value; }

        /// Segmenter-dropout hold (ini [Filter] MaskHoldSeconds), forwarded to the
        /// background model: frames to reuse the last good subject mask.
        public int MaskHoldFrames { get => _vbm.MaskHoldFrames; set => _vbm.MaskHoldFrames = value; }

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

            // Split the frame into the live subject and the background plane.
            _vbm.UpdateMask(frame, padPx);
            if (_vbm.ForegroundMask == null) return;

            using (var clean = frame.Clone())
            {
                // Detect faces on the background cutout only, so the webcam
                // user is never treated as a background person.
                IReadOnlyList<Rect> detections;
                using (var cut = _vbm.BackgroundCutout(frame))
                    detections = _detector.Detect(cut);

                _tracker.MaxAge = Math.Max(0, stayFrames);
                var people = new List<Rect>();
                foreach (var box in _tracker.Update(detections))
                    people.Add(BodyRegion(box, frame.Size(), bodyScale));

                // Learn the scene where nobody is, replace the whole background
                // with the learned plate (unlearned areas keep the live frame),
                // then composite the live subject back on top.
                _vbm.Update(frame, people);
                _vbm.FillKnownBackground(frame, new Rect(0, 0, frame.Width, frame.Height));
                // Fallback for movers we can't erase (no learned background behind them
                // yet): inpaint out everything hot-but-unknown from its surroundings.
                _vbm.FillTierTwo(frame);
                _tierTwoBg?.Dispose();
                _tierTwoBg = frame.Clone();   // the background composite, before the user goes on
                _vbm.CompositeForeground(clean, frame);
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
