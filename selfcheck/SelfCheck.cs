using System;
using OpenCvSharp;

namespace VeBeGe
{
    /// Headless self-check for the virtual-background pipeline: compiles the
    /// service's filter sources into this exe (internal access) and runs them
    /// on synthetic frames. No camera, no COM, no GUI, safe anywhere.
    /// Exits 0 on pass, 1 on fail.
    internal static class SelfCheck
    {
        private static int _failures;

        private static void Main()
        {
            try
            {
                TrackerStaytime();
                FilterPipeline();
                HeatGate();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                _failures++;
            }
            Console.WriteLine(_failures == 0 ? "SELF-CHECK PASS" : $"SELF-CHECK FAIL ({_failures})");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok  " : " FAIL ") + what);
            if (!ok) _failures++;
        }

        /// A lost face must keep its track alive for exactly MaxAge frames.
        private static void TrackerStaytime()
        {
            var t = new FaceTracker { MaxAge = 3 };
            var box = new[] { new Rect(100, 100, 50, 50) };
            Check(t.Update(box).Count == 1, "tracker: detection creates a track");
            var none = new Rect[0];
            Check(t.Update(none).Count == 1, "tracker: survives dropout frame 1");
            Check(t.Update(none).Count == 1, "tracker: survives dropout frame 2");
            Check(t.Update(none).Count == 1, "tracker: survives dropout frame 3");
            Check(t.Update(none).Count == 0, "tracker: expires after staytime");
            // Re-detection near the old spot must not crash and yields one track.
            t.Update(box);
            Check(t.Update(new[] { new Rect(110, 105, 50, 50) }).Count == 1, "tracker: IoU re-match keeps one track");
        }

        /// Run the full VB pipeline (segment → detect → track → learn → fill →
        /// composite) on synthetic frames; it must not throw and must keep
        /// returning frames of the input size and type.
        private static void FilterPipeline()
        {
            string modelDir = AppDomain.CurrentDomain.BaseDirectory;
            using (var filter = new VbgFilter(modelDir))
            using (var scene = new Mat(new Size(640, 480), MatType.CV_8UC3))
            {
                // A textured static "room": flat colour + shapes.
                using (var grad = new Mat(new Size(640, 480), MatType.CV_8UC3, new Scalar(40, 90, 140)))
                    grad.CopyTo(scene);
                Cv2.Rectangle(scene, new Rect(50, 60, 200, 150), new Scalar(200, 200, 200), -1);
                Cv2.Line(scene, 0, 240, 640, 240, new Scalar(0, 0, 255), 3);

                for (int i = 0; i < 10; i++)
                {
                    using (var frame = scene.Clone())
                    {
                        // A "walking intruder" block that changes position.
                        Cv2.Rectangle(frame, new Rect(300 + i * 10, 200, 60, 200), new Scalar(10, 10, 10), -1);
                        filter.Process(frame, padPx: 5, stayFrames: 30, bodyScale: 3.2);
                        Check(!frame.Empty() && frame.Width == 640 && frame.Height == 480 && frame.Type() == MatType.CV_8UC3,
                              $"pipeline: frame {i} processed in place");
                        if (_failures > 0) return; // don't spam 10 failures
                    }
                }
                Console.WriteLine("  ok   pipeline: 10 frames, no exceptions");
            }
        }

        /// The motion-heat gate tracks MOTION, not pixel change: a slowly moving
        /// textured block (a "person" the face detector can't see) must never
        /// bake into the plate, the plate must show the scene behind it, while
        /// a brightness ramp (lighting change, zero displacement) and the static
        /// room must both keep learning in.
        private static void HeatGate()
        {
            string modelDir = AppDomain.CurrentDomain.BaseDirectory;
            var faderRect = new Rect(100, 300, 80, 80);
            using (var filter = new VbgFilter(modelDir) { HeatCooldownFrames = 20 })
            using (var scene = new Mat(new Size(640, 480), MatType.CV_8UC3, new Scalar(40, 90, 140)))
            {
                Cv2.Rectangle(scene, new Rect(50, 60, 200, 150), new Scalar(200, 200, 200), -1);

                // Phase 1: empty room. Cooldown is 20 frames; 40 frames lets the
                // whole plate learn.
                for (int i = 0; i < 40; i++)
                    using (var frame = scene.Clone())
                        filter.Process(frame, padPx: 5, stayFrames: 30, bodyScale: 3.2);

                // Phase 2: a slow textured mover walks through the learned scene
                // (1 px/frame, per-frame flow is sub-threshold, but displacement
                // accumulates against the ~0.5 s baseline), while a "light" ramps
                // up with zero displacement.
                for (int i = 0; i < 50; i++)
                {
                    using (var frame = scene.Clone())
                    {
                        Cv2.Rectangle(frame, faderRect, Scalar.All(40 + 2 * i), -1);
                        var mover = new Rect(340 + i, 200, 60, 60);
                        Cv2.Rectangle(frame, mover, new Scalar(15, 15, 15), -1);
                        for (int s = 0; s < 60; s += 12)
                            Cv2.Rectangle(frame, new Rect(mover.X + s, 200, 5, 60), new Scalar(230, 230, 230), -1);
                        filter.Process(frame, padPx: 5, stayFrames: 30, bodyScale: 3.2);
                    }
                }

                Mat plate = filter.VirtualBackground;
                Check(plate != null && !plate.Empty(), "heat: plate exists");
                if (plate == null || plate.Empty()) return;

                Vec3b still = plate.Get<Vec3b>(135, 150);   // inside the static white rectangle
                Vec3b fade = plate.Get<Vec3b>(340, 140);    // centre of the lighting ramp
                Vec3b moved = plate.Get<Vec3b>(230, 420);   // where the mover now stands
                Check(Math.Abs(still.Item0 - 200) < 30 && Math.Abs(still.Item1 - 200) < 30 && Math.Abs(still.Item2 - 200) < 30,
                      "heat: static background learned into the plate");
                Check(fade.Item0 > 100 && fade.Item1 > 100 && fade.Item2 > 100,
                      "heat: lighting change (no motion) learns in live");
                Check(Math.Abs(moved.Item0 - 40) < 40 && Math.Abs(moved.Item1 - 90) < 40 && Math.Abs(moved.Item2 - 140) < 40,
                      "heat: slow mover never bakes in, plate keeps the scene behind it");

                // A global exposure step (whole frame brightens at once) has no
                // displacement either: the plate must survive and keep tracking.
                for (int i = 0; i < 15; i++)
                {
                    using (var frame = scene.Clone())
                    {
                        Cv2.Add(frame, Scalar.All(20), frame);
                        filter.Process(frame, padPx: 5, stayFrames: 30, bodyScale: 3.2);
                    }
                }
                plate = filter.VirtualBackground;
                still = plate.Get<Vec3b>(135, 150);
                Check(still.Item0 > 150 && still.Item1 > 150 && still.Item2 > 150,
                      "heat: plate survives a global exposure step");
            }
        }
    }
}
