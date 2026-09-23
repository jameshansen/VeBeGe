using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VeBeGe
{
    /// Person segmentation plus the accumulated "virtual background": the scene
    /// with the person cut out, built up over time wherever the real background
    /// becomes visible.
    ///
    /// The person's pixels NEVER enter the model, only the background cutout is
    /// read, so the user's face/body can't contaminate it. Pixels not yet
    /// revealed stay unknown (tracked in a separate mask) so they read as
    /// transparent and are never painted out.
    internal sealed class VirtualBackgroundModel : IDisposable
    {
        private const double LearnRate = 0.1;            // how fast revealed background settles in.
        private const double SceneChangeThreshold = 30;  // mean abs grey diff (background only) ⇒ camera moved.
        private const int SceneChangePersist = 10;       // consecutive frames over threshold before the plate resets.
        private const int FeatherKernel = 15;            // odd; softens the foreground cut-out edge.
        private const int OldBaselineEvery = 15;         // refresh the slow-motion reference every N frames (~0.5 s @ 30fps).
        private const double GlobalMotionFraction = 0.4; // more of the frame moving at once = camera event, not people.
        private const int CorrW = 320;                   // downscale width used for camera-drift estimation.
        private const double MaxDriftPx = 40;            // full-res drift beyond this = a real pan → camera event.

        private readonly PersonSegmenter _segmenter;
        private Mat _fgMask;     // 255 = person, this frame.
        private Mat _prevFgMask; // 255 = person, last frame (to immunise the subject's own swept path).
        private Mat _lastGoodMask; // last mask with a real subject blob, reused during dropouts.
        private int _maskHeld;   // consecutive frames the mask has been held through a dropout.
        private Point2d? _blobCentre; // tracked subject centroid; null = not locked onto anyone yet.
        private Mat _bg;         // BGR accumulated background (unknown areas are black placeholder).
        private Mat _known;      // 255 where _bg holds real, revealed background.
        private Mat _prevSmall;  // downscaled previous frame (fast-motion flow baseline).
        private Mat _oldSmall;   // downscaled frame from ~0.5 s ago (slow-motion flow baseline).
        private Mat _gridX;      // cached pixel-coordinate ramps (small scale), for
        private Mat _gridY;      //   evaluating the rigid camera motion per pixel.
        private Mat _heat;       // motion heatmap: cooldown frames remaining per pixel (0 = cold, learnable).
        private Mat _lastMotion; // last frame's true-motion mask (full res, subject excluded).
        private int _frameNo;
        private int _sceneRun;   // consecutive frames the scene-change test has fired.
        private int _hotRun;     // consecutive frames the heatmap has been nearly all-hot.

        /// Motion-heatmap tuning (ini [Filter] Heat*): a pixel is "hot", barred
        /// from entering the plate, while MOTION happened within HeatSpread px
        /// of it in the last HeatCooldownFrames frames. Motion means optical-flow
        /// displacement with the camera's own motion subtracted, pixel CHANGE
        /// without displacement (flickering lights, exposure, lighting) is not
        /// motion, never ignites, and so keeps learning into the plate live.
        public double HeatMinFlow { get; set; } = 4.0;    // px of true displacement that counts as motion.
        public int HeatSpread { get; set; } = 10;         // px the heat bleeds out from motion (soft falloff).
        public int HeatCooldownFrames { get; set; } = 90; // frames a hot pixel takes to cool (clamped to 255).

        /// Segmenter-dropout hold: how many frames to keep reusing the last good
        /// subject mask when segmentation vanishes or shrinks to a speck, so a
        /// one-frame flicker doesn't flash the real user through.
        public int MaskHoldFrames { get; set; } = 30;

        /// The live motion heatmap (cooldown frames remaining; 0 = cold). Diagnostic view.
        public Mat Heat => _heat;

        /// Fraction of the frame the last FillTierTwo had to fall back on
        /// (hot over never-learned background, subject halo excluded): what
        /// would be smeared if the feed were shown now. 1 until the plate
        /// exists. The loading screen's "done" signal: mean coldness never
        /// quite reaches 1 on a real webcam (sensor-noise flow keeps a few
        /// percent hot forever), but once the plate covers the scene there is
        /// nothing left to hide.
        public double TierTwoHoles { get; private set; } = 1;

        /// How far the scene has cooled, 0..1: mean coldness over the frame,
        /// i.e. how much of the cooldown the heatmap has worked through. The
        /// map ignites FULLY at startup and cools one frame per frame, so this
        /// ramps steadily from 0 to 1 as the plate becomes learnable, and it
        /// stalls (or stops rising) wherever something keeps moving. Mean, not
        /// "fraction fully cold": every pixel starts at the same value and so
        /// crosses zero at the same moment, which would make that a step, not a
        /// progress signal. Startup feedback, the O(N) scan only runs while the
        /// caller asks for it.
        public double Coldness =>
            _heat == null ? 0
                : 1 - Cv2.Mean(_heat).Val0 / Math.Max(1, Math.Min(255, HeatCooldownFrames));

        /// Wall time (ms) the last UpdateMotion took, plus its internal phase
        /// breakdown. Diagnostics for the Testing harness.
        public double LastHeatMs { get; private set; }
        public readonly List<KeyValuePair<string, double>> LastHeatStages =
            new List<KeyValuePair<string, double>>();
        private readonly System.Diagnostics.Stopwatch _heatSw = new System.Diagnostics.Stopwatch();
        private double _heatLast;

        private void HeatMark(string stage)
        {
            double now = _heatSw.Elapsed.TotalMilliseconds;
            LastHeatStages.Add(new KeyValuePair<string, double>(stage, now - _heatLast));
            _heatLast = now;
        }

        public VirtualBackgroundModel(PersonSegmenter segmenter) { _segmenter = segmenter; }

        public Mat ForegroundMask => _fgMask;

        /// The accumulated background plate; unknown (not-yet-revealed) pixels
        /// are left black. Read-only view for diagnostics/testing.
        public Mat Background => _bg;

        /// How many times a sustained scene change has thrown the plate away
        /// and started it over (the camera was moved). Everything learned is
        /// gone at that point, so the caller puts its loading screen back up.
        public int PlateResets { get; private set; }

        /// Segment the frame into the foreground mask (255 = person).
        /// pad > 0 dilates the mask so the kept foreground area grows by that many pixels.
        public void UpdateMask(Mat frame, int pad)
        {
            _fgMask?.Dispose();
            _fgMask = _segmenter.Segment(frame);
            if (_fgMask == null) return;
            // The subject is the webcam user: keep only the largest connected blob,
            // so a background person the segmenter also grabbed isn't composited
            // back over the erased scene (popping through). Prune before dilating,
            // or the pad could bridge separate blobs into one.
            KeepLargestBlob(_fgMask);

            // Dropout hold: segmentation occasionally drops the subject (or shrinks
            // to a speck) for a frame or two. Reuse the last good mask for up to
            // MaskHoldFrames so the real user isn't briefly composited through.
            int minArea = (int)(_fgMask.Total() * 0.01);   // < ~1% of frame = a dropout, not a subject.
            if (Cv2.CountNonZero(_fgMask) >= minArea)
            {
                _lastGoodMask?.Dispose();
                _lastGoodMask = _fgMask.Clone();
                _maskHeld = 0;
            }
            else if (_lastGoodMask != null && _lastGoodMask.Size() == _fgMask.Size()
                     && _maskHeld < MaskHoldFrames)
            {
                _lastGoodMask.CopyTo(_fgMask);
                _maskHeld++;
            }
            // else: held long enough, accept the empty/tiny mask, the subject is really gone.

            if (pad > 0)
                using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(pad * 2 + 1, pad * 2 + 1)))
                    Cv2.Dilate(_fgMask, _fgMask, k);
        }

        // How far (fraction of frame width) the subject centre may travel between
        // frames before a candidate blob is treated as a different person rather
        // than the subject moving. ~0.20 of 1280px = 256px/frame, generous for real
        // movement at 30fps but far short of snapping to someone standing beside you.
        // Single per-frame gate; add velocity prediction only if fast pans slip.
        private const double MaxBlobJumpFrac = 0.20;

        // Reduce a binary mask to the subject's connected component. We track the
        // subject's centroid across frames and stay locked onto the blob nearest it,
        // so a larger person stepping in beside you doesn't suddenly steal the
        // foreground. We only re-lock onto the largest blob when the lock is lost
        // (subject left frame / their blob shrank away) or on the first frame.
        private void KeepLargestBlob(Mat mask)
        {
            using (var labels = new Mat())
            using (var stats = new Mat())
            using (var centroids = new Mat())
            {
                int n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
                if (n <= 1) { _blobCentre = null; return; }          // all background: lock lost
                if (n == 2)                                          // one blob: that's the subject
                {
                    _blobCentre = new Point2d(centroids.Get<double>(1, 0), centroids.Get<double>(1, 1));
                    return;
                }

                double minKeep = mask.Total() * 0.01;   // < ~1% of frame = too small to hold the lock

                // Prefer staying locked: nearest blob to our tracked centre, if it's
                // close enough and still a real subject (not a shrinking dropout).
                if (_blobCentre is Point2d prev)
                {
                    int near = -1; double nearDist = double.MaxValue;
                    for (int i = 1; i < n; i++)   // skip label 0 = background
                    {
                        if (stats.Get<int>(i, (int)ConnectedComponentsTypes.Area) < minKeep) continue;
                        double dx = centroids.Get<double>(i, 0) - prev.X;
                        double dy = centroids.Get<double>(i, 1) - prev.Y;
                        double d2 = dx * dx + dy * dy;
                        if (d2 < nearDist) { nearDist = d2; near = i; }
                    }
                    double maxJump = mask.Width * MaxBlobJumpFrac;
                    if (near > 0 && nearDist <= maxJump * maxJump) { SelectBlob(near, mask, labels, centroids); return; }
                }

                // No lock, or the tracked subject is gone: (re)lock onto the largest blob.
                int best = 1, bestArea = -1;
                for (int i = 1; i < n; i++)
                {
                    int area = stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);   // At<T> needs an unshipped assembly
                    if (area > bestArea) { bestArea = area; best = i; }
                }
                SelectBlob(best, mask, labels, centroids);
            }
        }

        // Keep only the given label in the mask and record its centroid as the lock.
        private void SelectBlob(int label, Mat mask, Mat labels, Mat centroids)
        {
            _blobCentre = new Point2d(centroids.Get<double>(label, 0), centroids.Get<double>(label, 1));
            Cv2.Compare(labels, label, mask, CmpType.EQ);   // 255 where label == subject
        }

        /// Did any true (camera-compensated, non-subject) motion land inside r on
        /// the most recent heat update? Corroboration for tracked people: a real
        /// person produces flow motion; static scenery a detector false-fires on
        /// does not. No flow data yet (startup) counts as motion, shielding wins.
        public bool HasRecentMotion(Rect r)
        {
            if (_lastMotion == null) return true;
            r &= new Rect(0, 0, _lastMotion.Width, _lastMotion.Height);
            if (r.Width <= 0 || r.Height <= 0) return false;
            using (var roi = new Mat(_lastMotion, r))
                return Cv2.CountNonZero(roi) > 32;   // a couple of small-scale flow pixels = noise floor
        }

        /// Does r (inflated by half its size) touch a meaningful chunk of the
        /// subject's mask? Used to drop face detections that are really the
        /// webcam user leaking through a lagging segmentation, not a background
        /// person; the inflation catches the mask sitting one frame behind.
        public bool NearSubject(Rect r)
        {
            if (_fgMask == null) return false;
            double area = Math.Max(1, (double)r.Width * r.Height);
            r = Rect.Inflate(r, r.Width / 2, r.Height / 2)
                & new Rect(0, 0, _fgMask.Width, _fgMask.Height);
            if (r.Width <= 0 || r.Height <= 0) return false;
            using (var roi = new Mat(_fgMask, r))
                return Cv2.CountNonZero(roi) > area * 0.25;
        }

        /// A copy of the frame with the foreground subject blacked out, so face
        /// detection only ever sees the background, the webcam user is never
        /// detected. Caller disposes.
        public Mat BackgroundCutout(Mat frame)
        {
            var cut = frame.Clone();
            if (_fgMask != null) cut.SetTo(Scalar.All(0), _fgMask);
            return cut;
        }

        /// Phase two of the per-frame update, after UpdateMotion: bake the
        /// tracked people into the heat, then learn the background wherever
        /// there's no person: not the segmented subject, and not any tracked
        /// background person (so they never bake in). Newly-revealed pixels
        /// become "known". Resets on a sustained scene change. cameraEvent is
        /// what UpdateMotion returned for this frame.
        public void Update(Mat frame, IReadOnlyList<Rect> people, bool cameraEvent)
        {
            if (_fgMask == null || _heat == null) return;
            int cooldown = Math.Max(1, Math.Min(255, HeatCooldownFrames));

            // Face detection feeds the heatmap: anywhere the tracker says a
            // person is (including the staytime after a lost face) is hot by
            // definition. When the track finally expires, the heat it left
            // decays over the cooldown, the layers hand off to each other.
            if (people != null)
                foreach (var r in people)
                    if (r.Width > 0 && r.Height > 0)
                        Cv2.Rectangle(_heat, r, Scalar.All(cooldown), -1);

            // Escape hatch: if virtually everything has stayed hot for several
            // cooldowns (an unstable camera the stabiliser can't fully cancel),
            // the motion signal is telling us nothing. Drop it and degrade to
            // detection-only shielding rather than blocking learning forever.
            _hotRun = Cv2.CountNonZero(_heat) > _heat.Total() * 0.9 ? _hotRun + 1 : 0;
            if (_hotRun > cooldown * 3)
            {
                _heat.SetTo(Scalar.All(0));
                _hotRun = 0;
            }

            // Remember this frame's subject mask so next frame can immunise the
            // path it sweeps as it moves.
            _prevFgMask?.Dispose();
            _prevFgMask = _fgMask.Clone();

            // Camera event (shake / pan / exposure step): the frame tells us
            // nothing about people, so change nothing, don't learn, don't
            // reset. The plate survives transients intact.
            if (cameraEvent && _bg != null && _bg.Size() == frame.Size()) return;
            using (var bg = new Mat())   // 255 = background cutout (no person)
            {
                Cv2.BitwiseNot(_fgMask, bg);
                if (people != null)
                    foreach (var r in people)
                        if (r.Width > 0 && r.Height > 0)
                            Cv2.Rectangle(bg, r, Scalar.All(0), -1);   // -1 = filled
                // Only a SUSTAINED scene change resets the plate, a one-frame
                // spike (walk-through, flicker) must not wipe what we learned.
                _sceneRun = _bg != null && _bg.Size() == frame.Size() && SceneChanged(frame, bg)
                    ? _sceneRun + 1 : 0;
                bool reset = _bg == null || _bg.Size() != frame.Size() || _sceneRun >= SceneChangePersist;
                if (reset)
                {
                    // A SUSTAINED scene change, i.e. someone moved the camera:
                    // the plate is thrown away and has to be rebuilt from
                    // nothing, so the startup loading screen goes back up. The
                    // first-frame build is not a reset, nobody had a plate yet.
                    if (_bg != null && _sceneRun >= SceneChangePersist) PlateResets++;
                    // No immediate seeding: the plate refills through the motion
                    // gate below (unknown areas pass the live frame through, so
                    // there's no visual gap, just a cooldown of relearning).
                    // The heatmap carries over: a camera move ignites everything anyway.
                    _bg?.Dispose(); _known?.Dispose();
                    _bg = new Mat(frame.Size(), frame.Type(), Scalar.All(0));
                    _known = new Mat(frame.Size(), MatType.CV_8UC1, Scalar.All(0));
                    _sceneRun = 0;
                }
                else
                {
                    using (var coldHard = new Mat())   // 255 = fully-cold background (seed/known gate)
                    using (var softBand = new Mat())   // 255 where heat is mid-ramp (needs float blend)
                    {
                        Cv2.Compare(_heat, 0, coldHard, CmpType.EQ);
                        Cv2.BitwiseAnd(bg, coldHard, coldHard);
                        Cv2.InRange(_heat, new Scalar(1), new Scalar(cooldown - 1), softBand);

                        using (var knownBefore = _known.Clone())
                        using (var fresh = new Mat())
                        using (var blended = new Mat())
                        using (var learnMask = new Mat())
                        {
                            // Fully-cold background learns at a constant LearnRate: one
                            // SIMD 8-bit AddWeighted over the frame, exact. Fully-hot
                            // pixels don't learn at all. Only the heat's soft edge in
                            // between needs the per-pixel float ramp, and that band is
                            // small (HeatSpread px around whatever moved), so the float
                            // work runs on its bounding box alone, on top of the same
                            // blend. Identical to running the ramp over the whole frame,
                            // at a fraction of the cost.
                            Cv2.AddWeighted(frame, LearnRate, _bg, 1 - LearnRate, 0, blended);
                            Cv2.BitwiseAnd(knownBefore, coldHard, learnMask);
                            if (Cv2.CountNonZero(softBand) > 0)
                            {
                                Rect band = Cv2.BoundingRect(softBand);
                                using (var heatR = new Mat(_heat, band))
                                using (var bgR = new Mat(bg, band))
                                using (var plateR = new Mat(_bg, band))
                                using (var frameR = new Mat(frame, band))
                                using (var outR = new Mat(blended, band))
                                    SoftBlend(heatR, bgR, plateR, frameR, outR, cooldown);
                                Cv2.BitwiseOr(learnMask, softBand, learnMask);
                                Cv2.BitwiseAnd(learnMask, knownBefore, learnMask);
                            }
                            blended.CopyTo(_bg, learnMask);   // only smooth already-known pixels

                            // Freshly-revealed background (visible now, not yet known) is
                            // SEEDED at full value, blending it up from black would copy
                            // near-black for many frames, but only where fully cold.
                            Cv2.BitwiseNot(knownBefore, fresh);
                            Cv2.BitwiseAnd(fresh, coldHard, fresh);
                            frame.CopyTo(_bg, fresh);           // seed fresh, fully-cold pixels
                            Cv2.BitwiseOr(_known, coldHard, _known);   // once fully revealed, stays known
                        }
                    }
                }
            }
        }

        // Soft motion gate. coldW in [0,1] is per-pixel "coldness": 1 where
        // fully cold (heat 0 ⇒ learn at full rate), 0 where fully hot, ramping
        // across the heat's soft edge, gated to background (no person) pixels.
        // Scaling the learn rate by it fades the plate in across the boundary
        // instead of switching it on at a hard ring. out = plate + (frame-plate)*coldW*LearnRate.
        private static void SoftBlend(Mat heat, Mat bg, Mat plate, Mat frame, Mat outp, int cooldown)
        {
            using (var w1 = new Mat())   // CV_32F per-pixel learn rate: coldness*LearnRate, 0 on person pixels
            using (var w2 = new Mat())   // 1 - w1
            using (var person = new Mat())
            {
                heat.ConvertTo(w1, MatType.CV_32FC1, -LearnRate / cooldown, LearnRate);
                Cv2.BitwiseNot(bg, person);
                w1.SetTo(Scalar.All(0), person);
                w1.ConvertTo(w2, MatType.CV_32FC1, -1, 1);
                // out = (frame*w1 + plate*w2)/(w1+w2), one pass over the 8-bit
                // images with float weights: no 3-channel float round trips.
                Cv2.BlendLinear(frame, plate, w1, w2, outp);
            }
        }

        /// Paint the live subject (from a clean snapshot of the input) back over
        /// the processed frame. The mask edge is feathered (soft alpha) so the
        /// cut-out blends instead of showing a hard, jagged outline:
        /// out = frame*(1-a) + clean*a, with a = blurred mask in [0,1].
        public void CompositeForeground(Mat clean, Mat frame)
        {
            if (_fgMask == null) return;
            // Alpha is zero outside the mask + feather bleed, so the float blend
            // only needs the mask's bounding box (plus a feather margin), not the
            // whole frame. Filters on a sub-Mat still read real parent pixels at
            // the ROI edge, so the result is identical to the full-frame blend.
            Rect roi = Cv2.BoundingRect(_fgMask);
            if (roi.Width <= 0 || roi.Height <= 0) return;   // no subject: frame is all background
            roi = Rect.Inflate(roi, FeatherKernel, FeatherKernel)
                  & new Rect(0, 0, frame.Width, frame.Height);
            using (var fgRoi = new Mat(_fgMask, roi))
            using (var cleanRoi = new Mat(clean, roi))
            using (var frameRoi = new Mat(frame, roi))
                CompositeRoi(fgRoi, cleanRoi, frameRoi);
        }

        private static void CompositeRoi(Mat fgMask, Mat clean, Mat frame)
        {
            using (var alpha = new Mat())
            using (var w1 = new Mat())
            using (var w2 = new Mat())
            {
                Cv2.GaussianBlur(fgMask, alpha, new Size(FeatherKernel, FeatherKernel), 0);
                // cv::blendLinear is exactly out = (clean*w1 + frame*w2)/(w1+w2),
                // one pass over 8-bit pixels with single-channel float weights,
                // instead of converting both images to float and back.
                alpha.ConvertTo(w1, MatType.CV_32FC1);
                alpha.ConvertTo(w2, MatType.CV_32FC1, -1, 255);   // 255 - alpha
                Cv2.BlendLinear(clean, frame, w1, w2, frame);
            }
        }

        /// Overlay the KNOWN virtual background into region r. Unknown pixels
        /// are left untouched, so unlearned areas keep the live frame (never a
        /// black hole). No-op until some background has been learned.
        public void FillKnownBackground(Mat frame, Rect r)
        {
            if (_bg == null || r.Width <= 0 || r.Height <= 0) return;
            using (var src = new Mat(_bg, r))
            using (var dst = new Mat(frame, r))
            using (var m = new Mat(_known, r))
                src.CopyTo(dst, m);
        }

        /// Tier-two background. Anywhere something is present (the motion heatmap
        /// is hot, which includes every tracked person, baked in by UpdateHeat)
        /// over background we've NEVER learned (still unknown, so the plate can't
        /// erase it), inpaint those pixels from their surroundings, ffmpeg-delogo
        /// style, outside-in interpolation, so they smear into the scene instead
        /// of showing the mover. Driving off the heat (not just live detections)
        /// keeps a walker covered through the whole cooldown after face detection
        /// drops them, so nobody pops back into view. The real learned plate (tier
        /// one) already sits on top wherever it exists; this only fills the
        /// leftover holes. Call after FillKnownBackground, before compositing the
        /// live subject back on.
        public void FillTierTwo(Mat frame)
        {
            if (_known == null || _heat == null ||
                _known.Size() != frame.Size() || _heat.Size() != frame.Size()) return;
            using (var holes = new Mat())
            using (var unknown = new Mat())
            using (var hot = new Mat())
            {
                Cv2.BitwiseNot(_known, unknown);          // 255 where background never learned
                Cv2.Compare(_heat, 0, hot, CmpType.GT);   // 255 where a mover / tracked person is
                Cv2.BitwiseAnd(unknown, hot, holes);      // unremovable: no plate AND something's there
                // Never inpaint the subject or a wide halo around them. The live user is
                // still in the frame here (compositing comes after), so a hole bordering
                // them would smear their body outward (the "blurred person" ghost), and
                // that area must instead show live and learn into the plate. SubjectPad
                // keeps every remaining hole clear of the subject, so inpaint only ever
                // sources real background.
                if (_fgMask != null && _fgMask.Size() == frame.Size())
                    using (var fgHalo = new Mat())
                    {
                        DilateFast(_fgMask, fgHalo, SubjectPad(frame.Width));
                        Cv2.Subtract(holes, fgHalo, holes);   // holes AND NOT (subject + halo)
                    }
                int n = Cv2.CountNonZero(holes);
                TierTwoHoles = (double)n / frame.Total();
                if (n == 0) return;
                // Guard: when the holes swamp the frame (startup, camera event) there's
                // no clean border to interpolate from and inpaint smears garbage, keep
                // the live frame instead.
                // Fraction guard as a cheap proxy for "enough known border".
                if (n > frame.Total() * 0.5) return;
                // Cv2.Inpaint is exactly "interpolate the masked region from its
                // borders inward", but full-res Telea cost scales with hole area
                // (300+ ms on a 720p walker). The fill is a low-frequency smear by
                // design, so inpaint downscaled and upscale the result; visually
                // identical. Holes are dilated 2 px at small scale so the seed
                // border never samples mover pixels mixed in by the downscale.
                using (var smallFrame = new Mat())
                using (var smallHoles = new Mat())
                using (var smallFilled = new Mat())
                using (var filled = new Mat())
                {
                    var ss = new Size(320, Math.Max(2, frame.Rows * 320 / frame.Cols));
                    Cv2.Resize(frame, smallFrame, ss, 0, 0, InterpolationFlags.Area);
                    Cv2.Resize(holes, smallHoles, ss, 0, 0, InterpolationFlags.Nearest);
                    using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5)))
                        Cv2.Dilate(smallHoles, smallHoles, k);
                    Cv2.Inpaint(smallFrame, smallHoles, smallFilled, 3, InpaintMethod.Telea);
                    Cv2.Resize(smallFilled, filled, frame.Size(), 0, 0, InterpolationFlags.Linear);
                    filled.CopyTo(frame, holes);
                }
            }
        }

        // Motion heatmap. Ignite where dense optical flow, minus the camera's
        // own rigid motion, shows genuine displacement against the previous
        // frame (fast motion) OR against a ~0.5 s old frame (slow movers whose
        // per-frame displacement is sub-threshold accumulate over the gap).
        // Pixel change without displacement (flicker, lights, exposure) is NOT
        // motion: it never ignites, so lighting keeps learning into the plate
        // live. Ignition spreads HeatSpread px so a body's moving outline
        // covers its interior, the map smears 1 px/frame (heat drags along
        // with movement), and everything cools one frame per frame. Tracked
        // people (face detection) ignite their body regions directly, in
        // Update, so when detection loses them the heatmap takes over
        // seamlessly. Starts fully hot: the scene must prove itself quiet
        // before anything is learned.
        //
        // Phase one of the per-frame update, and independent of face
        // detection (it only reads the subject mask), so the filter runs the
        // two side by side. steps = camera frames elapsed since the last
        // processed one: the map cools by that many, so a pixel stays hot for
        // HeatCooldownSeconds of WALL time whatever the filter's throughput.
        // Cooling one step per processed frame instead stretched a 3 s cooldown
        // to ~13 s at 7 fps, and the startup loading screen with it.
        //
        // Returns true on a camera event (a pan too big to align, or most of
        // the frame in true motion at once): the map freezes for that frame,
        // people keep their heat, nothing false-ignites.
        public bool UpdateMotion(Mat frame, int steps)
        {
            LastHeatStages.Clear();
            _heatSw.Restart();
            _heatLast = 0;
            bool cameraEvent = false;
            int cooldown = Math.Max(1, Math.Min(255, HeatCooldownFrames));
            if (_fgMask == null) return false;
            using (var gray = new Mat())
            {
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);   // denoise: steadier flow
                HeatMark("h_gray");
                if (_heat == null || _heat.Size() != gray.Size())
                {
                    _heat?.Dispose();
                    _heat = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.All(cooldown));
                    _prevSmall?.Dispose(); _prevSmall = null;
                    _oldSmall?.Dispose(); _oldSmall = null;
                    _frameNo = 0;
                }
                using (var curSmall = ToSmall(gray))
                {
                    if (_prevSmall != null)
                    {
                        double scale = gray.Cols / (double)CorrW;
                        EnsureGrids(curSmall.Size());
                        Mat motionSmall = null, slow = null;
                        using (var allowedSmall = BackgroundOnlyMask())
                        using (var motion = new Mat())
                        {
                            // Background corners for the camera-motion fit, picked
                            // once for both baselines. The fast (previous frame) and
                            // slow (~0.5 s old) flows are independent: run them at once.
                            Point2f[] corners = Cv2.GoodFeaturesToTrack(curSmall, 60, 0.01, 8, allowedSmall, 3, false, 0.04);
                            Mat prev = _prevSmall, old = _oldSmall;
                            System.Threading.Tasks.Parallel.Invoke(
                                () => motionSmall = FlowMotion(curSmall, prev, corners, scale),
                                () => { if (old != null) slow = FlowMotion(curSmall, old, corners, scale); });
                            HeatMark("h_flow");
                            cameraEvent = motionSmall == null;
                            if (!cameraEvent && slow != null) Cv2.BitwiseOr(motionSmall, slow, motionSmall);
                            slow?.Dispose();
                            if (!cameraEvent)
                            {
                                // Border flow is unreliable; lone specks are noise.
                                Cv2.Rectangle(motionSmall, new Rect(0, 0, motionSmall.Width, motionSmall.Height),
                                              Scalar.All(0), 6);
                                using (var k3 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3)))
                                    Cv2.MorphologyEx(motionSmall, motionSmall, MorphTypes.Open, k3);
                                Cv2.Resize(motionSmall, motion, gray.Size(), 0, 0, InterpolationFlags.Nearest);
                                // The heatmap is a BACKGROUND-layer concept: the subject
                                // is already excluded from learning by the foreground
                                // mask. Exclude the subject's whole SWEPT path too, this
                                // frame's mask AND last frame's, plus a SubjectPad halo
                                // wide enough to swallow the flow the silhouette bleeds,
                                // so their own movement (which at low fps outruns a small
                                // halo) can't ignite a hot arch around them. That arch
                                // would otherwise go hot+unknown and tier-two would smear
                                // it; excluded, the area around them stays cold, shows
                                // live, and learns into the plate.
                                if (_fgMask != null && _fgMask.Size() == motion.Size())
                                    using (var fgHalo = new Mat())
                                    {
                                        // Union of both masks first: one downscaled
                                        // dilate instead of two.
                                        if (_prevFgMask != null && _prevFgMask.Size() == motion.Size())
                                            Cv2.BitwiseOr(_fgMask, _prevFgMask, fgHalo);
                                        else
                                            _fgMask.CopyTo(fgHalo);
                                        DilateFast(fgHalo, fgHalo, SubjectPad(motion.Width));
                                        motion.SetTo(Scalar.All(0), fgHalo);
                                    }
                                // Kept for track corroboration (HasRecentMotion).
                                _lastMotion?.Dispose();
                                _lastMotion = motion.Clone();
                                cameraEvent = Cv2.CountNonZero(motion) > gray.Total() * GlobalMotionFraction;
                                HeatMark("h_post");
                                if (!cameraEvent)
                                {
                                    using (var k3 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3)))
                                        Cv2.Dilate(_heat, _heat, k3);            // smear: heat flows 1 px/frame
                                    Cv2.Subtract(_heat, Scalar.All(Math.Max(1, steps)), _heat);   // cool by elapsed camera frames (floors at 0)
                                    HeatMark("h_cool");
                                    // Soft ignition: instead of a hard-edged disc of full
                                    // cooldown, ramp the heat DOWN from full at the motion
                                    // to 0 at HeatSpread px away (distance transform). The
                                    // learn gate downstream reads this as a fading alpha,
                                    // so the plate blends in across the edge instead of
                                    // showing a hard ring artifact in the video.
                                    using (var ignite = new Mat())
                                    {
                                        if (HeatSpread > 0)
                                            using (var notMotion = new Mat())
                                            using (var dist = new Mat())
                                            {
                                                Cv2.BitwiseNot(motion, notMotion);       // motion = 0, else 255
                                                Cv2.DistanceTransform(notMotion, dist, DistanceTypes.L2,
                                                                      DistanceTransformMasks.Mask3);
                                                // cooldown*(1 - dist/HeatSpread), saturating to [0, cooldown].
                                                dist.ConvertTo(ignite, MatType.CV_8UC1,
                                                               -(double)cooldown / HeatSpread, cooldown);
                                            }
                                        else
                                            motion.ConvertTo(ignite, MatType.CV_8UC1, cooldown / 255.0);
                                        Cv2.Max(_heat, ignite, _heat);           // re-ignite (soft)
                                    }
                                    HeatMark("h_ignite");
                                }
                            }
                        }
                        motionSmall?.Dispose();
                    }
                    _prevSmall?.Dispose();
                    _prevSmall = curSmall.Clone();
                    if (cameraEvent || _frameNo % OldBaselineEvery == 0)
                    {
                        _oldSmall?.Dispose();
                        _oldSmall = curSmall.Clone();
                    }
                    _frameNo++;
                }
            }
            LastHeatMs = _heatSw.Elapsed.TotalMilliseconds;
            return cameraEvent;
        }

        // Margin (px) to exclude AROUND the subject when keeping their own motion
        // out of the heatmap and out of tier-two. The silhouette bleeds optical
        // flow far beyond the mask, flow runs on a downscaled frame, and the
        // per-frame displacement is large at low fps, so a HeatSpread-sized halo
        // (tuned for real background motion) is nowhere near enough. Scales with
        // resolution; independent of HeatSpread.
        private static int SubjectPad(int cols) => Math.Max(24, cols / 20);

        // Large-radius dilate, downscaled. An exact ellipse dilate is O(r²) per
        // pixel; at SubjectPad radius on 720p it was ~100 ms per call and
        // dominated the whole frame. The halo is a safety margin, so run it at
        // DownW width with the radius rounded UP (never a smaller halo than
        // asked) and nearest-neighbour edges. dst may alias src.
        private static void DilateFast(Mat src, Mat dst, int radius)
        {
            const int DownW = 320;
            if (src.Cols <= DownW)
            {
                using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse,
                           new Size(radius * 2 + 1, radius * 2 + 1)))
                    Cv2.Dilate(src, dst, k);
                return;
            }
            double f = src.Cols / (double)DownW;
            int r = (int)Math.Ceiling(radius / f) + 1;
            using (var small = new Mat())
            {
                Cv2.Resize(src, small, new Size(DownW, Math.Max(2, (int)Math.Round(src.Rows / f))),
                           0, 0, InterpolationFlags.Nearest);
                using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(r * 2 + 1, r * 2 + 1)))
                    Cv2.Dilate(small, small, k);
                Cv2.Resize(small, dst, src.Size(), 0, 0, InterpolationFlags.Nearest);
            }
        }

        // Aspect-preserving downscale used for drift estimation.
        private static Size SmallSize(Mat gray) =>
            new Size(CorrW, Math.Max(2, gray.Rows * CorrW / gray.Cols));

        private static Mat ToSmall(Mat gray)
        {
            var s = new Mat();
            Cv2.Resize(gray, s, SmallSize(gray));
            return s;
        }

        // Where drift corners may be picked: everywhere except the subject
        // (downscaled inverse of the foreground mask). Null when unavailable.
        private Mat BackgroundOnlyMask()
        {
            if (_fgMask == null) return null;
            var m = new Mat();
            Cv2.Resize(_fgMask, m, SmallSize(_fgMask));
            Cv2.BitwiseNot(m, m);
            return m;
        }

        // Camera motion between two small frames: a rigid transform (rotation +
        // translation + scale) RANSAC-fitted to the sparse flow of background
        // corners (picked on fromSmall by the caller). RANSAC discards people
        // moving through as outliers, so only the camera's own motion is
        // measured. Small-scale coordinates.
        // Null = nothing trackable (featureless scene) or no consensus.
        private static Mat EstimateCameraMotion(Mat fromSmall, Mat toSmall, Point2f[] corners)
        {
            if (corners.Length < 8) return null;
            var moved = (Point2f[])corners.Clone();   // initial guess: no motion
            Cv2.CalcOpticalFlowPyrLK(fromSmall, toSmall, corners, ref moved, out byte[] status, out float[] err);
            var from = new List<Point2f>();
            var to = new List<Point2f>();
            for (int i = 0; i < corners.Length; i++)
            {
                if (status[i] == 0) continue;
                from.Add(corners[i]);
                to.Add(moved[i]);
            }
            if (from.Count < 8) return null;
            using (var fromArr = InputArray.Create(from))
            using (var toArr = InputArray.Create(to))
            {
                Mat m = Cv2.EstimateAffinePartial2D(fromArr, toArr);
                if (m == null || m.Empty()) { m?.Dispose(); return null; }
                return m;
            }
        }

        // Dense optical flow from the current frame BACK to the baseline (so
        // results are indexed at current pixel positions), minus the camera's
        // rigid motion: returns an 8-bit small-scale mask of pixels whose
        // residual displacement exceeds HeatMinFlow (caller disposes).
        // Brightness-only change produces no displacement and is ignored.
        // Null = camera moved too far to align (a real pan). A featureless
        // scene estimates no camera motion and falls back to raw flow, which is
        // what a fixed camera gives anyway. Reads only shared state (the grids,
        // HeatMinFlow), so two of these run concurrently.
        private Mat FlowMotion(Mat curSmall, Mat baseSmall, Point2f[] corners, double scale)
        {
            double a = 1, b = 0, tx = 0, c = 0, d = 1, ty = 0;
            using (Mat m = EstimateCameraMotion(curSmall, baseSmall, corners))
            {
                if (m != null)
                {
                    a = m.Get<double>(0, 0); b = m.Get<double>(0, 1); tx = m.Get<double>(0, 2);
                    c = m.Get<double>(1, 0); d = m.Get<double>(1, 1); ty = m.Get<double>(1, 2);
                    if (Math.Abs(tx) * scale > MaxDriftPx || Math.Abs(ty) * scale > MaxDriftPx)
                        return null;
                }
            }
            using (var flow = new Mat())
            {
                Cv2.CalcOpticalFlowFarneback(curSmall, baseSmall, flow,
                    0.5, 3, 15, 2, 5, 1.1, OpticalFlowFlags.None);
                Mat[] ch = flow.Split();
                using (ch[0])
                using (ch[1])
                using (var px = new Mat())
                using (var py = new Mat())
                using (var mag = new Mat())
                {
                    // The rigid camera motion predicts flow (A·p + t) − p per pixel;
                    // subtract it so only motion relative to the scene remains.
                    Cv2.AddWeighted(_gridX, a - 1, _gridY, b, tx, px);
                    Cv2.AddWeighted(_gridX, c, _gridY, d - 1, ty, py);
                    Cv2.Subtract(ch[0], px, ch[0]);
                    Cv2.Subtract(ch[1], py, ch[1]);
                    Cv2.Magnitude(ch[0], ch[1], mag);
                    Cv2.Threshold(mag, mag, Math.Max(0.25, HeatMinFlow / scale), 255, ThresholdTypes.Binary);
                    var m8 = new Mat();
                    mag.ConvertTo(m8, MatType.CV_8UC1);
                    return m8;
                }
            }
        }

        // Pixel-coordinate ramps used to evaluate the rigid motion per pixel.
        private void EnsureGrids(Size s)
        {
            if (_gridX != null && _gridX.Size() == s) return;
            _gridX?.Dispose();
            _gridY?.Dispose();
            var gx = new float[s.Width * s.Height];
            var gy = new float[s.Width * s.Height];
            for (int y = 0, i = 0; y < s.Height; y++)
                for (int x = 0; x < s.Width; x++, i++) { gx[i] = x; gy[i] = y; }
            using (var tx = Mat.FromPixelData(s.Height, s.Width, MatType.CV_32FC1, gx))
                _gridX = tx.Clone();
            using (var ty = Mat.FromPixelData(s.Height, s.Width, MatType.CV_32FC1, gy))
                _gridY = ty.Clone();
        }

        // Camera-moved test: mean abs grey diff, measured ONLY where the background is both
        // visible now and already known (never over unknown black holes ⇒ no false trigger).
        private bool SceneChanged(Mat frame, Mat bgCutout)
        {
            using (var a = new Mat())
            using (var b = new Mat())
            using (var d = new Mat())
            using (var m = new Mat())
            {
                Cv2.CvtColor(frame, a, ColorConversionCodes.BGR2GRAY);
                Cv2.CvtColor(_bg, b, ColorConversionCodes.BGR2GRAY);
                Cv2.Absdiff(a, b, d);
                Cv2.BitwiseAnd(bgCutout, _known, m);
                return Cv2.Mean(d, m).Val0 > SceneChangeThreshold;
            }
        }

        public void Dispose()
        {
            _segmenter?.Dispose();
            _fgMask?.Dispose();
            _prevFgMask?.Dispose();
            _lastGoodMask?.Dispose();
            _bg?.Dispose();
            _known?.Dispose();
            _prevSmall?.Dispose();
            _oldSmall?.Dispose();
            _gridX?.Dispose();
            _gridY?.Dispose();
            _heat?.Dispose();
            _lastMotion?.Dispose();
        }
    }
}
