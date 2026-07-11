using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace VeBeGe
{
    /// One DirectShow video input device, in enumeration order, the same
    /// order OpenCV's DSHOW backend uses, so Index lines up with
    /// VideoCapture(Index, VideoCaptureAPIs.DSHOW).
    public sealed class CameraDevice
    {
        public int Index;          // position in the FULL device list (incl. virtual cams)
        public string Name;        // FriendlyName, or "Camera N" if unreadable
        public string DevicePath;  // unique, stable id for physical devices (null for some software filters)
    }

    /// Lists DirectShow video input devices with friendly name + device path.
    public static class CameraEnumerator
    {
        private static readonly Guid CLSID_SystemDeviceEnum = new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        private static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        private static readonly Guid IID_IPropertyBag = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");

        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICreateDevEnum
        {
            [PreserveSig]
            int CreateClassEnumerator(ref Guid pType, out IEnumMoniker ppEnumMoniker, int dwFlags);
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyBag
        {
            [PreserveSig]
            int Read([MarshalAs(UnmanagedType.LPWStr)] string pszPropName, ref object pVar, IntPtr pErrorLog);
            [PreserveSig]
            int Write([MarshalAs(UnmanagedType.LPWStr)] string pszPropName, ref object pVar);
        }

        public static List<CameraDevice> GetDevices()
        {
            var devices = new List<CameraDevice>();
            var t = Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum);
            if (t == null) return devices;

            var devEnum = (ICreateDevEnum)Activator.CreateInstance(t);
            try
            {
                var cat = CLSID_VideoInputDeviceCategory;
                int hr = devEnum.CreateClassEnumerator(ref cat, out IEnumMoniker moniker, 0);
                if (hr != 0 || moniker == null) return devices; // S_FALSE: no devices
                try
                {
                    var one = new IMoniker[1];
                    int i = 0;
                    while (moniker.Next(1, one, IntPtr.Zero) == 0)
                    {
                        devices.Add(new CameraDevice
                        {
                            Index = i,
                            Name = ReadProp(one[0], "FriendlyName") ?? ("Camera " + i),
                            DevicePath = ReadProp(one[0], "DevicePath"),
                        });
                        Marshal.ReleaseComObject(one[0]);
                        i++;
                    }
                }
                finally { Marshal.ReleaseComObject(moniker); }
            }
            finally { Marshal.ReleaseComObject(devEnum); }
            return devices;
        }

        private static string ReadProp(IMoniker m, string prop)
        {
            try
            {
                Guid iid = IID_IPropertyBag;
                m.BindToStorage(null, null, ref iid, out object bagObj);
                var bag = (IPropertyBag)bagObj;
                object val = null;
                if (bag.Read(prop, ref val, IntPtr.Zero) == 0 && val != null)
                    return val.ToString();
            }
            catch { /* fall through to null */ }
            return null;
        }
    }
}
