using System;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VeBeGe
{
    /// Sender side of one VeBeGe virtual camera slot. Thin P/Invoke wrapper
    /// over vebege_cam.dll's C "sc" API.
    public sealed class VirtualCamera : IDisposable
    {
        private const string Dll = "vebege_cam.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr scCreateCamera(int slot, int width, int height, float framerate);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void scDeleteCamera(IntPtr camera);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void scSendFrame(IntPtr camera, IntPtr image_bits);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint scReceiverHeartbeat(IntPtr camera);

        private IntPtr _camera = IntPtr.Zero;
        private readonly int _width, _height;
        private byte[] _buffer;

        public bool IsActive => _camera != IntPtr.Zero;

        public VirtualCamera(int slot, int width, int height, float fps)
        {
            _width = width; _height = height;
            _buffer = new byte[width * height * 3];
            _camera = scCreateCamera(slot, width, height, fps);
        }

        /// Bumped by the driver on every frame it serves to a consuming app.
        /// Static value = nobody is streaming from this virtual camera.
        public uint ReceiverHeartbeat => IsActive ? scReceiverHeartbeat(_camera) : 0;

        public void SendFrame(Mat frame)
        {
            if (!IsActive || frame == null || frame.Empty()) return;
            using (var resized = new Mat())
            {
                Cv2.Resize(frame, resized, new Size(_width, _height));
                // DirectShow RGB24 is physically B,G,R in memory, OpenCV's
                // native BGR order. Send as-is; the driver flips vertically.
                Marshal.Copy(resized.Data, _buffer, 0, _buffer.Length);
                var h = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
                try { scSendFrame(_camera, h.AddrOfPinnedObject()); }
                finally { h.Free(); }
            }
        }

        public void Dispose()
        {
            if (IsActive) { scDeleteCamera(_camera); _camera = IntPtr.Zero; }
        }
    }
}
