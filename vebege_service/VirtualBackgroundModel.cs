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

        /// Wall time (ms) UpdateHeat took inside the last Update call, plus its
        /// internal phase breakdown. Diagnostics for the Testing harness.
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

        // Reduce a binary mask to its single largest connected component.
        private static void KeepLargestBlob(Mat mask)
        {
            using (var labels = new Mat())
            using (var stats = new Mat())
            using (var centroids = new Mat())
            {
                int n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
                if (n <= 2) return;   // background + at most one blob: nothing to prune
                int best = 1, bestArea = -1;
                for (int i = 1; i < n; i++)   // skip label 0 = background
                {
                    int area = stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);   // At<T> needs an unshipped assembly
                    if (area > bestArea) { bestArea = area; best = i; }
                }
                Cv2.Compare(labels, best, mask, CmpType.EQ);   // 255 where label == largest
            }
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

        /// Learn the background wherever there's no person: not the segmented
        /// subject, and not any tracked background person (so they never bake
        /// in). Newly-revealed pixels become "known". Resets on a scene change.
        public void Update(Mat frame, IReadOnlyList<Rect> people)
        {
            if (_fgMask == null) return;
            var heatSw = System.Diagnostics.Stopwatch.StartNew();
            bool cameraEvent = UpdateHeat(frame, people);
            LastHeatMs = heatSw.Elapsed.TotalMilliseconds;
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
                    int cooldown = Math.Max(1, Math.Min(255, HeatCooldownFrames));
                    using (var coldHard = new Mat())   // 255 = fully-cold background (seed/known gate)
                    using (var softBand = new Mat())   // 255 where heat is mid-ramp (needs float blend)
                    {
                        Cv2.Compare(_heat, 0, coldHard, CmpType.EQ);
                        Cv2.BitwiseAnd(bg, coldHard, coldHard);
                        Cv2.InRange(_heat, new Scalar(1), new Scalar(cooldown - 1), softBand);

                        using (var knownBefore = _known.Clone())
                        using (var fresh = new Mat())
                        using (var blended = new Mat())
                        {
                            if (Cv2.CountNonZero(softBand) == 0)
                            {
                                // Quiet scene: heat is binary (0 or full cooldown), so the
                                // soft per-pixel ramp in the else branch degenerates to a
                                // constant LearnRate on fully-cold background and 0
                                // everywhere else. One SIMD 8-bit AddWeighted + masked
                                // copy is exact and ~4x cheaper than the float path; this
                                // is the steady state whenever nothing has moved for a
                                // full cooldown.
                                using (var learnMask = new Mat())
                                {
                                    Cv2.AddWeighted(frame, LearnRate, _bg, 1 - LearnRate, 0, blended);
                                    Cv2.BitwiseAnd(knownBefore, coldHard, learnMask);
                                    blended.CopyTo(_bg, learnMask);
                                }
                            }
                            else
                            {
                                // Soft motion gate. coldW in [0,1] is per-pixel "coldness": 1 where
                                // fully cold (heat 0 ⇒ learn at full rate), 0 where fully hot, ramping
                                // across the heat's soft edge, gated to background (no person) pixels.
                                // Scaling the learn rate by it fades the plate in across the boundary
                                // instead of switching it on at a hard ring.
                                using (var coldW = new Mat())  // CV_32F, background-gated coldness
                                using (var wr3 = new Mat())    // per-pixel learn rate, CV_32FC3
                                using (var bgF = new Mat())
                                using (var frF = new Mat())
                                {
                                    _heat.ConvertTo(coldW, MatType.CV_32FC1, -1.0 / cooldown, 1.0);   // 1..0
                                    using (var bgf = new Mat())
                                    {
                                        bg.ConvertTo(bgf, MatType.CV_32FC1, 1.0 / 255.0);            // background 0/1
                                        Cv2.Multiply(coldW, bgf, coldW);
                                    }
                                    coldW.ConvertTo(coldW, MatType.CV_32FC1, LearnRate);         // coldW*LearnRate
                                    Cv2.CvtColor(coldW, wr3, ColorConversionCodes.GRAY2BGR);
                                    _bg.ConvertTo(bgF, MatType.CV_32FC3);
                                    frame.ConvertTo(frF, MatType.CV_32FC3);
                                    Cv2.Subtract(frF, bgF, frF);        // blended = bgF + (frF-bgF)*wr3
                                    Cv2.Multiply(frF, wr3, frF);
                                    Cv2.Add(bgF, frF, blended);
                                    blended.ConvertTo(blended, _bg.Type());
                                }
                                blended.CopyTo(_bg, knownBefore);   // only smooth already-known pixels
                            }

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
            using (var inv = new Mat())
            using (var af = new Mat())
            using (var invf = new Mat())
            using (var cf = new Mat())
            using (var ff = new Mat())
            {
                Cv2.GaussianBlur(fgMask, alpha, new Size(FeatherKernel, FeatherKernel), 0);
                Cv2.BitwiseNot(alpha, inv);                               // 255 - alpha, so af + invf = 1

                Cv2.CvtColor(alpha, af, ColorConversionCodes.GRAY2BGR);
                Cv2.CvtColor(inv, invf, ColorConversionCodes.GRAY2BGR);
                af.ConvertTo(af, MatType.CV_32FC3, 1.0 / 255.0);
                invf.ConvertTo(invf, MatType.CV_32FC3, 1.0 / 255.0);
                clean.ConvertTo(cf, MatType.CV_32FC3);
                frame.ConvertTo(ff, MatType.CV_32FC3);

                Cv2.Multiply(cf, af, cf);
                Cv2.Multiply(ff, invf, ff);
                Cv2.Add(cf, ff, ff);
                ff.ConvertTo(frame, frame.Type());
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
                if (n == 0) return;
                // Guard: when the holes swamp the frame (startup, camera event) there's
                // no clean border to interpolate from and inpaint smears garbage, keep
                // the live frame instead.
                // ponytail: fraction guard as a cheap proxy for "enough known border".
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
        // people (face detection) ignite their body regions directly, so when
        // detection loses them the heatmap takes over seamlessly. Starts fully
        // hot: the scene must prove itself quiet before anything is learned.
        //
        // Returns true on a camera event (a pan too big to align, or most of
        // the frame in true motion at once): the map freezes for that frame,
        // people keep their heat, nothing false-ignites.
        private bool UpdateHeat(Mat frame, IReadOnlyList<Rect> people)
        {
            LastHeatStages.Clear();
            _heatSw.Restart();
            _heatLast = 0;
            bool cameraEvent = false;
            int cooldown = Math.Max(1, Math.Min(255, HeatCooldownFrames));
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
                        using (var allowedSmall = BackgroundOnlyMask())
                        using (var motionSmall = new Mat(curSmall.Size(), MatType.CV_8UC1, Scalar.All(0)))
                        using (var motion = new Mat())
                        {
                            cameraEvent = !FlowIgnite(curSmall, _prevSmall, allowedSmall, scale, motionSmall);
                            HeatMark("h_flow1");
                            if (!cameraEvent && _oldSmall != null)
                                FlowIgnite(curSmall, _oldSmall, allowedSmall, scale, motionSmall);
                            HeatMark("h_flow2");
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
                                    Cv2.Subtract(_heat, Scalar.All(1), _heat);   // cool one frame (floors at 0)
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
            _prevFgMask = _fgMask?.Clone();

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
            if (src.Cols <= DownW * 2)
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
        // corners. RANSAC discards people moving through as outliers, so only
        // the camera's own motion is measured. Small-scale coordinates.
        // Null = nothing trackable (featureless scene) or no consensus.
        private static Mat EstimateCameraMotion(Mat fromSmall, Mat toSmall, Mat allowedSmall)
        {
            Point2f[] corners = Cv2.GoodFeaturesToTrack(fromSmall, 60, 0.01, 8, allowedSmall, 3, false, 0.04);
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
        // rigid motion, ORs pixels whose residual displacement exceeds
        // HeatMinFlow into motionSmall. Brightness-only change produces no
        // displacement and is ignored. False = camera moved too far to align
        // (a real pan). A featureless scene estimates no camera motion and
        // falls back to raw flow, which is what a fixed camera gives anyway.
        private bool FlowIgnite(Mat curSmall, Mat baseSmall, Mat allowedSmall, double scale, Mat motionSmall)
        {
            double a = 1, b = 0, tx = 0, c = 0, d = 1, ty = 0;
            using (Mat m = EstimateCameraMotion(curSmall, baseSmall, allowedSmall))
            {
                if (m != null)
                {
                    a = m.Get<double>(0, 0); b = m.Get<double>(0, 1); tx = m.Get<double>(0, 2);
                    c = m.Get<double>(1, 0); d = m.Get<double>(1, 1); ty = m.Get<double>(1, 2);
                    if (Math.Abs(tx) * scale > MaxDriftPx || Math.Abs(ty) * scale > MaxDriftPx)
                        return false;
                }
            }
            EnsureGrids(curSmall.Size());
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
                using (var m8 = new Mat())
                {
                    // The rigid camera motion predicts flow (A·p + t) − p per pixel;
                    // subtract it so only motion relative to the scene remains.
                    Cv2.AddWeighted(_gridX, a - 1, _gridY, b, tx, px);
                    Cv2.AddWeighted(_gridX, c, _gridY, d - 1, ty, py);
                    Cv2.Subtract(ch[0], px, ch[0]);
                    Cv2.Subtract(ch[1], py, ch[1]);
                    Cv2.Magnitude(ch[0], ch[1], mag);
                    Cv2.Threshold(mag, mag, Math.Max(0.25, HeatMinFlow / scale), 255, ThresholdTypes.Binary);
                    mag.ConvertTo(m8, MatType.CV_8UC1);
                    Cv2.BitwiseOr(motionSmall, m8, motionSmall);
                }
            }
            return true;
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
