using System;
using System.Threading;
using OpenCvSharp;

namespace VeBeGe
{
    /// The frame pump for one virtual camera slot: keeps the sender side of
    /// the virtual camera alive, and, only while some app is actually
    /// streaming from it, holds the physical camera open, runs the virtual
    /// background filter, and pushes frames. When nobody is watching, the
    /// physical camera is released (LED off).
    public sealed class SlotPump : IDisposable
    {
        private static readonly TimeSpan ReceiverTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan IdleLinger = TimeSpan.FromSeconds(10);

        private readonly int _slot;
        private readonly Thread _thread;
        private volatile bool _running = true;
        private volatile int _deviceIndex;

        public string DeviceName { get; }

        /// The physical device's position in the current full DirectShow list
        /// (what OpenCV's DSHOW backend indexes by). The reconciler refreshes
        /// this as devices come and go; used on the next capture (re)open.
        public int DeviceIndex { get => _deviceIndex; set => _deviceIndex = value; }

        public SlotPump(int slot, int deviceIndex, string deviceName)
        {
            _slot = slot;
            _deviceIndex = deviceIndex;
            DeviceName = deviceName;
            _thread = new Thread(Loop) { IsBackground = true, Name = "VeBeGe pump " + slot };
            _thread.Start();
        }

        private void Loop()
        {
            int w = Config.Width, h = Config.Height, fps = Config.Fps;

            VirtualCamera vcam = null;
            VideoCapture cap = null;
            VbgPipeline pipe = null;
            bool loggedBroken = false;      // models missing/corrupt → passthrough, log once
            bool loggedLoad = false;
            uint lastHb = 0;
            DateTime lastHbChange = DateTime.MinValue;

            try
            {
                vcam = new VirtualCamera(_slot, w, h, fps);
                if (!vcam.IsActive)
                {
                    Log.Write($"slot {_slot}: could not create virtual camera sender (buffer in use?)");
                    return;
                }
                Log.Write($"slot {_slot}: pump up for \"{DeviceName}\" ({w}x{h}@{fps})");

                using (var frame = new Mat())
                while (_running)
                {
                    uint hb = vcam.ReceiverHeartbeat;
                    var now = DateTime.UtcNow;
                    if (hb != lastHb) { lastHb = hb; lastHbChange = now; }
                    bool streaming = now - lastHbChange < ReceiverTimeout;

                    if (!streaming)
                    {
                        if (cap != null && now - lastHbChange > IdleLinger)
                        {
                            cap.Release(); cap.Dispose(); cap = null;
                            pipe?.Dispose(); pipe = null;
                            Log.Write($"slot {_slot}: idle, released \"{DeviceName}\"");
                        }
                        Thread.Sleep(300);
                        continue;
                    }

                    if (cap == null)
                    {
                        // New session, new pipeline: models load in the
                        // background, overlapping the DirectShow open below
                        // (both take a good part of a second), and the loading
                        // screen starts with the first frame. Kept across a
                        // capture glitch, that must not wipe the learned plate
                        // or replay the loading screen.
                        if (pipe == null) pipe = new VbgPipeline(Config.Dir, fps, new Size(w, h));
                        cap = new VideoCapture(_deviceIndex, VideoCaptureAPIs.DSHOW);
                        if (!cap.IsOpened())
                        {
                            cap.Dispose(); cap = null;
                            Thread.Sleep(1000);
                            continue;
                        }
                        cap.Set(VideoCaptureProperties.FrameWidth, w);
                        cap.Set(VideoCaptureProperties.FrameHeight, h);
                        Log.Write($"slot {_slot}: streaming, opened \"{DeviceName}\" as index {_deviceIndex}");
                    }

                    if (!cap.Read(frame) || frame.Empty())
                    {
                        // Device glitch or unplug; drop the capture and retry
                        // (the reconciler stops this pump if the device is gone).
                        cap.Release(); cap.Dispose(); cap = null;
                        Thread.Sleep(500);
                        continue;
                    }

                    // Hand the newest frame to the filter (dropped if it's still
                    // busy with the last one) and emit the current output. The
                    // pump therefore runs at CAMERA rate, not filter rate: the
                    // output stage repeats the last processed frame, which is
                    // what makes the loading animation smooth and what keeps a
                    // steady stream going to the consuming app.
                    pipe.Submit(frame);
                    pipe.Present(frame);
                    if (pipe.LoadMs > 0 && !loggedLoad)
                    {
                        loggedLoad = true;
                        Log.Write($"slot {_slot}: models up in {pipe.LoadMs:0} ms");
                    }
                    if (pipe.Broken && !loggedBroken)
                    {
                        loggedBroken = true;   // still serve raw frames, camera "just works"
                        Log.Write($"slot {_slot}: filter unavailable, passing frames through", pipe.LoadError);
                    }

                    vcam.SendFrame(frame);   // paces to fps internally
                }
            }
            catch (Exception ex)
            {
                Log.Write($"slot {_slot}: pump died", ex);
            }
            finally
            {
                cap?.Release(); cap?.Dispose();
                pipe?.Dispose();
                vcam?.Dispose();
                Log.Write($"slot {_slot}: pump down");
            }
        }

        public void Dispose()
        {
            _running = false;
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
                Log.Write($"slot {_slot}: pump thread did not stop in time");
        }
    }
}
