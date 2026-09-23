// Self-check for the output stage (VbgPipeline + LoadingOverlay), the part the
// service pump and both Testing tools share. Three things:
//   1. stills of the dial: ring000/001/012/060/100.png
//   2. the loading screen on a synthetic clock (instant, exact): the deadline
//      holds the blur, real progress drives the dial, it never winds back
//   3. the real pipeline over a clip: output rate, filter rate, loading.mp4
// Compiled and run by pipeline-check.ps1; not part of any csproj.
using System;
using System.Diagnostics;
using System.Threading;
using OpenCvSharp;

namespace VeBeGe
{
    internal static class PipelineCheck
    {
        private static int Fail(string msg, int code)
        {
            Console.WriteLine("FAIL: " + msg);
            return code;
        }

        // White pixels: the dial's size, on a black source frame.
        private static int DialArea(Mat f)
        {
            using (var g = new Mat())
            {
                Cv2.CvtColor(f, g, ColorConversionCodes.BGR2GRAY);
                Cv2.Threshold(g, g, 200, 255, ThresholdTypes.Binary);
                return Cv2.CountNonZero(g);
            }
        }

        private static double Diff(Mat a, Mat b)
        {
            using (var d = new Mat())
            {
                Cv2.Absdiff(a, b, d);
                return Cv2.Mean(d).Val0;
            }
        }

        private static int Main(string[] args)
        {
            string clip = args.Length > 0 ? args[0] : "bbc.mp4";
            string outDir = args.Length > 1 ? args[1] : ".";
            string modelDir = AppDomain.CurrentDomain.BaseDirectory;
            var size = new Size(1280, 720);
            var src = new Mat();
            using (var cap = new VideoCapture(clip))
                if (!cap.IsOpened() || !cap.Read(src) || src.Empty())
                    return Fail("could not read " + clip, 1);
            Cv2.Resize(src, src, size);
            const double step = 1 / 30.0;

            // 1. Stills. The dial eases toward its target, so run the clock on a
            // while at each one instead of asking for it cold.
            foreach (double p in new[] { 0.0, 0.01, 0.12, 0.6, 1.0 })
            {
                var ov = new LoadingOverlay { Budget = 1e6 };
                using (var f = src.Clone())
                {
                    for (double t = 0; t < 1.5; t += step)
                        using (var warm = src.Clone()) ov.Apply(warm, p, t);
                    ov.Apply(f, p, 1.5);
                    Cv2.ImWrite(string.Format("{0}/ring{1:000}.png", outDir, p * 100), f);
                }
            }

            // 2. Deadline: with progress stuck at 0 (someone parked in the shot,
            // the plate never settles), the blur holds for the whole budget and
            // only then does the dial fill and the feed come up.
            double budget = Config.StartupSeconds;
            var sched = new LoadingOverlay { Budget = budget };
            double full, doneAt = -1, lastBlur = -1;
            using (var f0 = src.Clone())
            {
                sched.Apply(f0, 0, 0);
                full = Diff(src, f0);
                if (!(full > 5)) return Fail("first frame not blurred, diff=" + full, 2);
            }
            for (double t = step; t < budget + 5 && doneAt < 0; t += step)
                using (var f = src.Clone())
                {
                    sched.Apply(f, 0, t);
                    double d = Diff(src, f);
                    if (t < budget && d < full * 0.9) return Fail("blur lifted before the budget, at " + t, 3);
                    if (sched.Done) doneAt = t; else lastBlur = d;
                }
            if (!(lastBlur >= 0 && lastBlur < full * 0.2))
                return Fail("blur did not fade out, last level " + lastBlur + " of " + full, 4);
            if (!(doneAt > budget + 1.5 && doneAt < budget + 3))
                return Fail("deadline reveal at " + doneAt + " s, expected ~" + (budget + 2.2), 5);
            Console.WriteLine($"deadline: budget {budget:0.#} s, blur {full:0.0} held, revealed at {doneAt:0.00} s");

            // 2b. And it is the PROGRESS that drives the dial, not that clock:
            // a plate that settles in 2 s finishes long before the deadline, and
            // a scene that re-ignites never winds the dial backwards.
            var resp = new LoadingOverlay { Budget = budget };
            double respDone = -1, peak = 0;
            using (var black = new Mat(size, MatType.CV_8UC3, Scalar.All(0)))
                for (double t = 0; t < budget && respDone < 0; t += step)
                    using (var f = black.Clone())
                    {
                        // Up to 0.6, collapses (someone walks in), recovers, hits 1 at 3 s.
                        double p = t < 1.2 ? t / 2 : (t < 2.0 ? 0 : Math.Min(1, (t - 1.0) / 2));
                        resp.Apply(f, p, t);
                        if (t < 2.5)
                        {
                            // Slack: the minimum hand ROTATES onto 12 o'clock as
                            // real sweep takes over, and anti-aliasing moves a
                            // few pixels around while it does.
                            double area = DialArea(f);
                            if (area + 8 < peak) return Fail($"dial wound backwards at {t:0.00} s", 12);
                            peak = Math.Max(peak, area);
                        }
                        if (resp.Done) respDone = t;
                    }
            if (!(respDone > 1.5 && respDone < budget - 2))
                return Fail("dial is not tracking real progress, finished at " + respDone, 13);

            // 2c. Restart (the camera was moved): the screen comes back, and its
            // deadline runs from the restart, not from the original start.
            using (var f = src.Clone())
            {
                resp.Restart(respDone);
                if (resp.Done) return Fail("restart did not put the loading screen back up", 14);
                resp.Apply(f, 0, respDone);
                if (!(Diff(src, f) > full * 0.9)) return Fail("restart did not blur the feed again", 15);
            }
            double reDone = -1;
            for (double t = respDone; t < respDone + budget + 5 && reDone < 0; t += step)
                using (var f = src.Clone())
                {
                    resp.Apply(f, 0, t);
                    if (resp.Done) reDone = t - respDone;
                }
            if (!(reDone > budget)) return Fail("restarted screen gave up after " + reDone + " s", 16);
            Console.WriteLine($"restart: screen back up, second deadline ran its own {reDone:0.00} s");
            Console.WriteLine($"progress-driven: plate settled at 3.0 s, revealed at {respDone:0.00} s, "
                              + $"{budget - respDone:0.0} s inside the deadline");

            // 3. Off switch.
            var off = new LoadingOverlay { Budget = 0 };
            using (var f = src.Clone())
            {
                off.Apply(f, 0, 0);
                if (!off.Done || Diff(src, f) != 0) return Fail("StartupSeconds=0 did not disable the loading screen", 6);
            }

            // 4. The real pipeline over the clip, driven like the pump: submit
            // the newest frame, present at camera rate, write what a consuming
            // app would see.
            int outFrames = 0;
            double elapsed;
            using (var pipe = new VbgPipeline(modelDir, 30, size))
            using (var play = new VideoCapture(clip))
            using (var w = new VideoWriter(outDir + "/loading.mp4", FourCC.MP4V, 30, size))
            using (var frame = new Mat())
            {
                var clock = Stopwatch.StartNew();
                double revealed = -1;   // keep filming a couple of seconds past the reveal
                while (clock.Elapsed.TotalSeconds < 3 * budget)
                {
                    if (!pipe.Loading && revealed < 0) revealed = clock.Elapsed.TotalSeconds;
                    if (revealed >= 0 && clock.Elapsed.TotalSeconds >= revealed + 2) break;
                    if (!play.Read(frame) || frame.Empty()) { play.Set(VideoCaptureProperties.PosFrames, 0); continue; }
                    Cv2.Resize(frame, frame, size);
                    pipe.Submit(frame);
                    pipe.Present(frame);
                    if (pipe.Broken) return Fail("models did not load: " + pipe.LoadError.Message, 7);
                    w.Write(frame);
                    outFrames++;
                    int wait = (int)(outFrames * 1000.0 / 30 - clock.Elapsed.TotalMilliseconds);
                    if (wait > 0) Thread.Sleep(wait);
                }
                elapsed = clock.Elapsed.TotalSeconds;
                double cooldownS = Config.HeatCooldownSeconds;
                Console.WriteLine($"pipeline: models up in {pipe.LoadMs:0} ms, {outFrames} frames out in {elapsed:0.00} s "
                                  + $"({outFrames / elapsed:0.0} fps), filter ran {pipe.ProcessedFrames} times "
                                  + $"({pipe.ProcessedFrames / elapsed:0.0} fps), revealed at {revealed:0.00} s");
                elapsed = revealed > 0 ? revealed : elapsed;
                if (pipe.Loading) return Fail("loading screen never finished", 8);
                if (pipe.ProcessedFrames < 5) return Fail("filter barely ran", 9);
                // The plate cannot exist until the heatmap has had a full
                // cooldown of CAMERA time (it cools by elapsed frames, however
                // many the filter processed). Revealing before that shows the
                // unerased frame this screen exists to hide.
                if (elapsed < cooldownS)
                    return Fail($"revealed at {elapsed:0.00} s, before the plate can exist ({cooldownS} s)", 14);
                if (outFrames / elapsed < 25) return Fail("output stage below camera rate", 10);
                if (outFrames < pipe.ProcessedFrames * 2)
                    return Fail("output stage is not outrunning the filter, it is not decoupled", 11);
            }

            // 5. Loading time on a quiet scene. The heat cools on CAMERA time
            // (Submit counts every frame, the filter cools by that many), so a
            // static scene settles at HeatCooldownSeconds whatever the filter's
            // throughput, and the screen is down at about cooldown + the two
            // fades. Cooling per PROCESSED frame took cooldown x (camera fps /
            // filter fps) instead, ~13 s at 7 fps, which is what this guards.
            // Twice: the camera delivering its nominal 30 fps, and a dim-room
            // 10 fps with the pipeline still told 30, which is the real-webcam
            // case that used to take 9 s and reveal a half-learned plate.
            double quietCool = Config.HeatCooldownSeconds;
            foreach (double deliver in new[] { 30.0, 10.0 })
            {
                double quietDone;
                using (var pipe = new VbgPipeline(modelDir, 30, size))
                using (var frame = new Mat())
                {
                    var clock = Stopwatch.StartNew();
                    int n = 0;
                    while (pipe.Loading && clock.Elapsed.TotalSeconds < 3 * budget)
                    {
                        src.CopyTo(frame);
                        pipe.Submit(frame);
                        pipe.Present(frame);
                        n++;
                        int wait = (int)(n * 1000.0 / deliver - clock.Elapsed.TotalMilliseconds);
                        if (wait > 0) Thread.Sleep(wait);
                    }
                    quietDone = clock.Elapsed.TotalSeconds;
                    Console.WriteLine($"quiet scene at {deliver:0} fps: revealed at {quietDone:0.00} s "
                                      + $"(cooldown {quietCool:0.#} s, filter ran at {pipe.ProcessedFrames / quietDone:0.0} fps)");
                }
                if (!(quietDone > quietCool && quietDone < quietCool + 3))
                    return Fail($"quiet scene at {deliver:0} fps revealed at {quietDone:0.00} s, expected ~{quietCool + 1.5:0.0} s", 17);
            }

            Console.WriteLine("PipelineCheck passed");
            return 0;
        }
    }
}
