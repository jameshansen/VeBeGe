using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace VeBeGe
{
    /// Registers/unregisters the per-slot VeBeGe virtual cameras as DirectShow
    /// video input devices, per-user, under HKCU\Software\Classes, so no
    /// elevation is ever needed. Two things per slot:
    ///   1. the COM class (CLSID -> InprocServer32 -> vebege_cam.dll)
    ///   2. the device entry in the VideoInputDeviceCategory Instance list
    ///      (this is what makes it show up in Zoom/Chrome/OBS device pickers)
    public static class DriverRegistrar
    {
        private const string CategoryKey =
            @"Software\Classes\CLSID\{860BB310-5D01-11d0-BD3B-00A0C911CE86}\Instance";

        // FilterData blob for "one RGB video output pin, MERIT_DO_NOT_USE",
        // captured verbatim from an IFilterMapper2::RegisterFilter registration
        // of the same pin layout. Static because our pin layout never changes.
        private static readonly byte[] FilterData =
        {
            0x02,0x00,0x00,0x00,0x00,0x00,0x20,0x00,0x01,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
            0x30,0x70,0x69,0x33,0x08,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x01,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x30,0x74,0x79,0x33,0x00,0x00,0x00,0x00,
            0x38,0x00,0x00,0x00,0x48,0x00,0x00,0x00,0x76,0x69,0x64,0x73,0x00,0x00,0x10,0x00,
            0x80,0x00,0x00,0xAA,0x00,0x38,0x9B,0x71,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        };

        /// CLSID for a slot. Must match the DEFINE_GUID family in vebege_cam.cpp.
        public static string Clsid(int slot) =>
            "{e1b45c30-8a2d-4f6b-9c47-5a3e1d2b70" + slot.ToString("x2") + "}";

        public static void Register(int slot, string friendlyName, string dllPath)
        {
            string clsid = Clsid(slot);
            using (var cls = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + clsid))
            {
                cls.SetValue(null, friendlyName);
                using (var srv = cls.CreateSubKey("InprocServer32"))
                {
                    srv.SetValue(null, dllPath);
                    srv.SetValue("ThreadingModel", "Both");
                }
            }
            using (var inst = Registry.CurrentUser.CreateSubKey(CategoryKey + @"\" + clsid))
            {
                inst.SetValue("CLSID", clsid);
                inst.SetValue("FriendlyName", friendlyName);
                inst.SetValue("FilterData", FilterData, RegistryValueKind.Binary);
            }
            Log.Write($"Registered slot {slot} as \"{friendlyName}\"");
        }

        public static void Unregister(int slot)
        {
            string clsid = Clsid(slot);
            Registry.CurrentUser.DeleteSubKeyTree(CategoryKey + @"\" + clsid, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\" + clsid, false);
            Log.Write($"Unregistered slot {slot}");
        }

        public static bool IsRegistered(int slot)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(CategoryKey + @"\" + Clsid(slot)))
                return k != null;
        }

        [DllImport("user32", SetLastError = true)]
        private static extern int BroadcastSystemMessage(
            uint flags, ref uint recipients, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint BSF_IGNORECURRENTTASK = 0x00000002, BSF_NOHANG = 0x00000008,
                           BSF_POSTMESSAGE = 0x00000010, BSF_FORCEIFHUNG = 0x00000020;
        private const uint BSM_APPLICATIONS = 0x00000008;
        private const uint WM_DEVICECHANGE = 0x0219;
        private const uint DBT_DEVNODES_CHANGED = 0x0007, DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVTYP_DEVICEINTERFACE = 5;

        // The camera interface classes apps subscribe to with
        // RegisterDeviceNotification. Both of these are in Zoom's video dll
        // (nydus.dll); its audio dll uses the KSCATEGORY_CAPTURE/RENDER pair,
        // which we deliberately leave alone.
        private static readonly Guid[] CameraInterfaces =
        {
            new Guid("6994ad05-93ef-11d0-a3cc-00a0c9223196"),   // KSCATEGORY_VIDEO
            new Guid("e5323777-f976-4f5b-9b55-b94699c46e44"),   // KSCATEGORY_VIDEO_CAMERA
        };

        // DEV_BROADCAST_DEVICEINTERFACE. The name buffer is not optional even
        // though the name never arrives: user32 refuses to broadcast a payload
        // sized as the bare 32-byte struct (returns 0), and truncates what it
        // does send back down to 32 bytes at the receiver. So the path costs
        // nothing to fill in and the send fails without room for it. What the
        // receiver actually gets, and all we need, is the class GUID.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevBroadcastDeviceInterface
        {
            public int Size, DeviceType, Reserved;
            public Guid ClassGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        }

        /// Tell running apps the camera list changed, so Zoom/Chrome/OBS
        /// re-enumerate and our twins appear without being restarted. Our
        /// cameras are COM registry entries, not PnP devices, so nothing
        /// announces them otherwise.
        ///
        /// Two messages, because apps listen for different things:
        /// DBT_DEVNODES_CHANGED (the generic "something changed") and a
        /// DBT_DEVICEARRIVAL per camera interface class, which is what
        /// RegisterDeviceNotification subscribers actually act on.
        ///
        /// ponytail: broadcast only, so it reaches top-level windows and not
        /// message-only ones. An app listening on HWND_MESSAGE still needs a
        /// restart; faking a genuine PnP arrival needs a real device node,
        /// i.e. MFCreateVirtualCamera on Win11.
        ///
        /// Runs on its own thread unless `wait`, because the arrival broadcast
        /// is a synchronous cross-process send and a receiver that re-enumerates
        /// its cameras sits on it (measured: 35 s for KSCATEGORY_VIDEO_CAMERA on
        /// the reference machine). BSF_NOHANG bounds a *hung* app, not a merely
        /// slow one, and neither the reconcile loop nor process exit may wait on
        /// someone else's camera scan.
        public static void NotifyDeviceChange(bool wait = false)
        {
            // One announcement at a time: a flapping camera must not pile up
            // 35-second threads.
            if (Interlocked.CompareExchange(ref _announcing, 1, 0) == 1) return;
            if (wait) Announce();
            else new Thread(Announce) { IsBackground = true, Name = "VeBeGe device notify" }.Start();
        }

        private static int _announcing;

        private static void Announce()
        {
            try { AnnounceCore(); }
            catch (Exception ex) { Log.Write("NotifyDeviceChange", ex); }
            finally { Interlocked.Exchange(ref _announcing, 0); }
        }

        private static void AnnounceCore()
        {
            uint recipients = BSM_APPLICATIONS;
            BroadcastSystemMessage(BSF_POSTMESSAGE | BSF_IGNORECURRENTTASK, ref recipients,
                WM_DEVICECHANGE, (IntPtr)DBT_DEVNODES_CHANGED, IntPtr.Zero);

            var dbi = new DevBroadcastDeviceInterface
            {
                Size = Marshal.SizeOf(typeof(DevBroadcastDeviceInterface)),
                DeviceType = DBT_DEVTYP_DEVICEINTERFACE,
            };
            IntPtr buf = Marshal.AllocHGlobal(dbi.Size);
            try
            {
                foreach (var iface in CameraInterfaces)
                {
                    dbi.ClassGuid = iface;
                    dbi.Name = @"\\?\root#media#0000#{" + iface + @"}\global";
                    Marshal.StructureToPtr(dbi, buf, false);
                    // Sent, not posted: user32 only marshals the payload for a
                    // sent message, a posted one hands the receiver a dangling
                    // pointer. NOHANG|FORCEIFHUNG bounds it against a wedged app.
                    recipients = BSM_APPLICATIONS;
                    int rc = BroadcastSystemMessage(BSF_IGNORECURRENTTASK | BSF_NOHANG | BSF_FORCEIFHUNG,
                        ref recipients, WM_DEVICECHANGE, (IntPtr)DBT_DEVICEARRIVAL, buf);
                    // rc > 0 is success; anything else means nobody was told, and
                    // silently swallowing that is what hid a malformed payload.
                    Log.Write(rc > 0
                        ? $"Announced camera arrival ({iface})"
                        : $"Camera arrival broadcast FAILED ({iface}), rc={rc} err={Marshal.GetLastWin32Error()}");
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// Current FriendlyName of a registered slot, or null.
        public static string RegisteredName(int slot)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(CategoryKey + @"\" + Clsid(slot)))
                return k?.GetValue("FriendlyName") as string;
        }
    }
}
