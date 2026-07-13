using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using OpenCvSharp;
using VeBeGe;

namespace VeBeGe.Testing
{
    /// Real-time version of the testing tool: pick a webcam, and watch the REAL
    /// VeBeGe filter (the same VbgFilter the service uses) run on the live feed.
    /// Five OpenCV windows update per frame: the processed video, the foreground
    /// mask, the learned virtual background, the motion heatmap (Jet: red = hot/
    /// shielded, blue = cold/learnable), and the tier-two background composite.
    /// Press ESC (or Q) in any window to quit.
    internal static class LiveProgram
    {
        private static int Main(string[] args)
        {
            var cams = CameraEnumerator.GetDevices();
            if (cams.Count == 0) { Console.Error.WriteLine("No video devices found."); return 1; }

            int index = PickCamera(cams, args);
            if (index < 0) return 2;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            using (var cap = new VideoCapture(index, VideoCaptureAPIs.DSHOW))
            {
                if (!cap.IsOpened()) { Console.Error.WriteLine("Could not open camera index " + index); return 1; }

                // Ask for the service's configured capture size; the driver picks the nearest.
                cap.Set(VideoCaptureProperties.FrameWidth, Config.Width);
                cap.Set(VideoCaptureProperties.FrameHeight, Config.Height);

                double fps = cap.Fps > 1 ? cap.Fps : Config.Fps;
                int pad = Config.Padding;
                double bodyScale = Config.BodyScale;
                int stayFrames = (int)Math.Round(Config.StaySeconds * fps);
                int heatCooldownFrames = (int)Math.Round(Config.HeatCooldownSeconds * fps);

                Console.WriteLine($"Camera: [{index}] {cams.Find(c => c.Index == index)?.Name}");
                Console.WriteLine($"Filter: pad={pad}, bodyScale={bodyScale}, stayFrames={stayFrames}, cooldown={heatCooldownFrames}");
                string perfPath = Path.Combine(baseDir, "vebege_live_perf.log");
                Console.WriteLine($"Perf  : {perfPath}");
                Console.WriteLine("Press ESC or Q in any window to quit.");

                using (var filter = new VbgFilter(baseDir)
                       {
                           HeatMinFlow = Config.HeatMinFlow,
                           HeatSpread = Config.HeatSpread,
                           HeatCooldownFrames = heatCooldownFrames,
                           MaskHoldFrames = (int)Math.Round(Config.MaskHoldSeconds * fps),
                           QuietShieldFrames = (int)Math.Round(Config.QuietShieldSeconds * fps),
                       })
                using (var perf = new PerfLog(perfPath, $"live: [{index}] {cams.Find(c => c.Index == index)?.Name} @ {Config.Width}x{Config.Height}", echoToConsole: true))
                using (var frame = new Mat())
                using (var maskBgr = new Mat())
                using (var heatVis = new Mat())
                using (var heatBgr = new Mat())
                {
                    var sw = new Stopwatch();
                    while (cap.Read(frame) && !frame.Empty())
                    {
                        sw.Restart();
                        filter.Process(frame, pad, stayFrames, bodyScale);
                        perf.Record(sw.Elapsed.TotalMilliseconds, filter.LastStageMs);
                        Cv2.ImShow("processed", frame);

                        Mat mask = filter.ForegroundMask;
                        if (mask != null && !mask.Empty())
                        {
                            Cv2.CvtColor(mask, maskBgr, ColorConversionCodes.GRAY2BGR);
                            Cv2.ImShow("mask", maskBgr);
                        }

                        Mat bg = filter.VirtualBackground;
                        if (bg != null && !bg.Empty()) Cv2.ImShow("background", bg);

                        Mat tier2 = filter.TierTwoBackground;
                        if (tier2 != null && !tier2.Empty()) Cv2.ImShow("tier2_background", tier2);

                        Mat heat = filter.MotionHeat;
                        if (heat != null && !heat.Empty())
                        {
                            heat.ConvertTo(heatVis, MatType.CV_8UC1, 255.0 / Math.Max(1, Math.Min(255, heatCooldownFrames)));
                            Cv2.ApplyColorMap(heatVis, heatBgr, ColormapTypes.Jet);
                            // Green = body regions actively shielding this frame.
                            foreach (var r in filter.LastPeople)
                                Cv2.Rectangle(heatBgr, r, new Scalar(0, 255, 0), 2);
                            Cv2.ImShow("heat", heatBgr);
                        }

                        int key = Cv2.WaitKey(1);
                        if (key == 27 || key == 'q' || key == 'Q') break;
                    }
                }
            }

            Cv2.DestroyAllWindows();
            return 0;
        }

        /// Use a camera index passed on the command line if valid; otherwise list
        /// the devices and prompt for one.
        private static int PickCamera(List<CameraDevice> cams, string[] args)
        {
            if (args.Length >= 1 && int.TryParse(args[0], out int arg) && cams.Exists(c => c.Index == arg))
                return arg;

            Console.WriteLine("Video devices:");
            foreach (var c in cams)
                Console.WriteLine($"  [{c.Index}] {c.Name}");
            Console.Write($"Select camera index (0-{cams.Count - 1}): ");

            string line = Console.ReadLine();
            if (int.TryParse(line, out int pick) && cams.Exists(c => c.Index == pick))
                return pick;

            Console.Error.WriteLine("Invalid selection.");
            return -1;
        }
    }
}
