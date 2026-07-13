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
            int stayFrames = (int)Math.Round(Config.StaySeconds * fps);

            VirtualCamera vcam = null;
            VideoCapture cap = null;
            VbgFilter filter = null;
            bool filterBroken = false;      // models missing/corrupt → passthrough, log once
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
                            filter?.Dispose(); filter = null;
                            Log.Write($"slot {_slot}: idle, released \"{DeviceName}\"");
                        }
                        Thread.Sleep(300);
                        continue;
                    }

                    if (cap == null)
                    {
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
                        if (filter == null && !filterBroken)
                        {
                            try
                            {
                                filter = new VbgFilter(Config.Dir)
                                {
                                    HeatMinFlow = Config.HeatMinFlow,
                                    HeatSpread = Config.HeatSpread,
                                    HeatCooldownFrames = (int)Math.Round(Config.HeatCooldownSeconds * fps),
                                    MaskHoldFrames = (int)Math.Round(Config.MaskHoldSeconds * fps),
                                    QuietShieldFrames = (int)Math.Round(Config.QuietShieldSeconds * fps),
                                };
                            }
                            catch (Exception ex)
                            {
                                filterBroken = true;   // still serve raw frames, camera "just works"
                                Log.Write($"slot {_slot}: filter unavailable, passing frames through", ex);
                            }
                        }
                    }

                    if (!cap.Read(frame) || frame.Empty())
                    {
                        // Device glitch or unplug; drop the capture and retry
                        // (the reconciler stops this pump if the device is gone).
                        cap.Release(); cap.Dispose(); cap = null;
                        Thread.Sleep(500);
                        continue;
                    }

                    try { filter?.Process(frame, Config.Padding, stayFrames, Config.BodyScale); }
                    catch (Exception ex) { Log.Write($"slot {_slot}: filter.Process", ex); }

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
                filter?.Dispose();
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
