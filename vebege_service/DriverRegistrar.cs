using System;
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

        /// Current FriendlyName of a registered slot, or null.
        public static string RegisteredName(int slot)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(CategoryKey + @"\" + Clsid(slot)))
                return k?.GetValue("FriendlyName") as string;
        }
    }
}
