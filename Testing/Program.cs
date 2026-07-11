using System;
using System.IO;
using OpenCvSharp;
using VeBeGe;

namespace VeBeGe.Testing
{
    /// Runs an MP4 through the real VeBeGe virtual-background filter (the same
    /// VbgFilter the service uses) and writes five MP4s: the processed video,
    /// the per-frame foreground mask (white subject on black), the virtual
    /// background plate as it fills in (black where still unknown), the motion
    /// heatmap (Jet colormap: red = hot/shielded, blue = cold/learnable), and the
    /// tier-two background (plate + delogo inpaint fallback) the subject is
    /// composited onto.
    ///
    /// The input is looped N times back-to-back (default 2) so the background
    /// model has time to converge and you can watch it settle over the passes.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: vebege_testing <input.mp4> [loops=2] [outputDir]");
                return 2;
            }

            string input = Path.GetFullPath(args[0]);
            if (!File.Exists(input)) { Console.Error.WriteLine("Input not found: " + input); return 2; }

            int loops = 2;
            if (args.Length >= 2 && (!int.TryParse(args[1], out loops) || loops < 1))
            {
                Console.Error.WriteLine("loops must be a positive integer.");
                return 2;
            }

            string outDir = args.Length >= 3 ? args[2] : Path.GetDirectoryName(input);
            if (string.IsNullOrEmpty(outDir)) outDir = Directory.GetCurrentDirectory();
            Directory.CreateDirectory(outDir);

            // Models + OpenCV natives are copied beside this exe by the build.
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            int width, height;
            double fps;
            using (var probe = new VideoCapture(input))
            {
                if (!probe.IsOpened()) { Console.Error.WriteLine("Could not open input video."); return 1; }
                width = probe.FrameWidth;
                height = probe.FrameHeight;
                fps = probe.Fps > 1 ? probe.Fps : 30;
            }
            var size = new Size(width, height);

            string stem = Path.GetFileNameWithoutExtension(input);
            string pProcessed = Path.Combine(outDir, stem + "_processed.mp4");
            string pMask      = Path.Combine(outDir, stem + "_mask.mp4");
            string pBg        = Path.Combine(outDir, stem + "_background.mp4");
            string pHeat      = Path.Combine(outDir, stem + "_heat.mp4");
            string pTier2     = Path.Combine(outDir, stem + "_tier2_background.mp4");

            // Reuse the service's real filter tuning (ini defaults if unset).
            int pad = Config.Padding;
            double bodyScale = Config.BodyScale;
            int stayFrames = (int)Math.Round(Config.StaySeconds * fps);
            int heatCooldownFrames = (int)Math.Round(Config.HeatCooldownSeconds * fps);
            int fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');

            Console.WriteLine($"Input : {input}");
            Console.WriteLine($"Video : {width}x{height} @ {fps:0.##}fps, looping {loops}x");
            Console.WriteLine($"Filter: pad={pad}, bodyScale={bodyScale}, stayFrames={stayFrames}");
            Console.WriteLine($"Heat  : minFlow={Config.HeatMinFlow}px, spread={Config.HeatSpread}px, cooldown={heatCooldownFrames} frames");

            using (var filter = new VbgFilter(baseDir)
                   {
                       HeatMinFlow = Config.HeatMinFlow,
                       HeatSpread = Config.HeatSpread,
                       HeatCooldownFrames = heatCooldownFrames,
                       MaskHoldFrames = (int)Math.Round(Config.MaskHoldSeconds * fps),
                   })
            using (var wProc = new VideoWriter(pProcessed, fourcc, fps, size))
            using (var wMask = new VideoWriter(pMask, fourcc, fps, size))
            using (var wBg   = new VideoWriter(pBg, fourcc, fps, size))
            using (var wHeat = new VideoWriter(pHeat, fourcc, fps, size))
            using (var wTier2 = new VideoWriter(pTier2, fourcc, fps, size))
            {
                if (!wProc.IsOpened() || !wMask.IsOpened() || !wBg.IsOpened() || !wHeat.IsOpened() || !wTier2.IsOpened())
                {
                    Console.Error.WriteLine("Could not open an MP4 writer (mp4v/ffmpeg backend missing?).");
                    return 1;
                }

                long total = 0;
                using (var maskBgr = new Mat())
                using (var heatVis = new Mat())
                using (var heatBgr = new Mat())
                using (var blackMask = new Mat(size, MatType.CV_8UC3, Scalar.All(0)))
                using (var blackBg   = new Mat(size, MatType.CV_8UC3, Scalar.All(0)))
                {
                    for (int pass = 1; pass <= loops; pass++)
                    {
                        // Re-open each pass: mp4 seeking is flaky, and the filter
                        // state (learned background) deliberately carries over.
                        using (var cap = new VideoCapture(input))
                        using (var frame = new Mat())
                        {
                            while (cap.Read(frame) && !frame.Empty())
                            {
                                filter.Process(frame, pad, stayFrames, bodyScale);
                                wProc.Write(frame);

                                Mat mask = filter.ForegroundMask;
                                if (mask != null && !mask.Empty())
                                {
                                    Cv2.CvtColor(mask, maskBgr, ColorConversionCodes.GRAY2BGR);
                                    wMask.Write(maskBgr);
                                }
                                else wMask.Write(blackMask);

                                Mat bg = filter.VirtualBackground;
                                wBg.Write(bg != null && !bg.Empty() ? bg : blackBg);

                                // Full background composite (tier one plate + tier two
                                // inpaint) the subject is painted onto, the erased scene.
                                Mat tier2 = filter.TierTwoBackground;
                                wTier2.Write(tier2 != null && !tier2.Empty() ? tier2 : blackBg);

                                // Heat is "cooldown frames remaining": stretch to full
                                // range and colorize (blue = cold/learnable, red = hot).
                                Mat heat = filter.MotionHeat;
                                if (heat != null && !heat.Empty())
                                {
                                    heat.ConvertTo(heatVis, MatType.CV_8UC1, 255.0 / Math.Max(1, Math.Min(255, heatCooldownFrames)));
                                    Cv2.ApplyColorMap(heatVis, heatBgr, ColormapTypes.Jet);
                                    wHeat.Write(heatBgr);
                                }
                                else wHeat.Write(blackBg);

                                total++;
                            }
                        }
                        Console.WriteLine($"  pass {pass}/{loops} done, {total} frames written");
                    }
                }
            }

            Console.WriteLine("Done. Wrote:");
            Console.WriteLine("  " + pProcessed);
            Console.WriteLine("  " + pMask);
            Console.WriteLine("  " + pBg);
            Console.WriteLine("  " + pHeat);
            Console.WriteLine("  " + pTier2);
            return 0;
        }
    }
}
