using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VeBeGe
{
    /// %ProgramData%\VeBeGe\VeBeGe.ini via the Win32 INI API, the native INI
    /// store, no parser dependency. The file is optional; every key has a
    /// sane default, so a missing ini means "just works" defaults.
    public static class Config
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern uint GetPrivateProfileString(string section, string key, string def,
            StringBuilder val, int size, string path);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern bool WritePrivateProfileString(string section, string key, string val, string path);

        public static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VeBeGe");
        public static readonly string IniPath = Path.Combine(Dir, "VeBeGe.ini");

        // Virtual camera output format. Width/height must be multiples of 4.
        public static int Width => GetInt("VirtualCam", "Width", 1280);
        public static int Height => GetInt("VirtualCam", "Height", 720);
        public static int Fps => GetInt("VirtualCam", "Fps", 30);

        /// Match-loss sustain time: how long (seconds) a lost face keeps being
        /// treated as a person (excluded from the learned background) after
        /// detection drops.
        public static double StaySeconds => GetDouble("Filter", "StaySeconds", 0.4);

        /// Padding: dilate the foreground (subject) mask by this many pixels.
        public static int Padding => GetInt("Filter", "Padding", 5);

        /// Width of the estimated body region, in face widths.
        public static double BodyScale => GetDouble("Filter", "BodyScale", 3.2);

        /// Motion heatmap: pixels of true (optical-flow) displacement that count
        /// as motion. Lower = more sensitive to slow movers.
        public static double HeatMinFlow => GetDouble("Filter", "HeatMinFlow", 4.0);

        /// Motion heatmap: how far (px) heat spreads out from detected motion,
        /// so a person's moving outline shields their motionless interior.
        public static int HeatSpread => GetInt("Filter", "HeatSpread", 10);

        /// Motion heatmap: seconds a pixel stays "hot" (barred from the learned
        /// background) after the last motion near it.
        public static double HeatCooldownSeconds => GetDouble("Filter", "HeatCooldownSeconds", 3.0);

        /// Segmenter-dropout hold: seconds to keep reusing the last good subject
        /// mask when segmentation briefly loses the user, so they don't flash through.
        public static double MaskHoldSeconds => GetDouble("Filter", "MaskHoldSeconds", 1.0);

        /// How often the service re-checks the physical camera list (seconds).
        public static int PollSeconds => Math.Max(2, GetInt("Service", "PollSeconds", 5));

        private const string DefaultExcludeNames =
            "VeBeGe,JustShowMe,OBS Virtual Camera,screen-capture-recorder,SplitCam,Logi Capture,softcam";

        // Always excluded, whatever the user puts in the ini: mirroring our own
        // virtual cameras would recurse (a (VeBeGe) of a (VeBeGe)...). Internal,
        // not editable out.
        private const string MandatoryExcludeNames = "VeBeGe";

        /// Raw, user-editable exclude list (the [Service] ExcludeNames value).
        public static string ExcludeNamesRaw => GetString("Service", "ExcludeNames", DefaultExcludeNames);

        /// Devices whose names contain any of these are never mirrored (our own
        /// cameras and other virtual cameras). The mandatory self-exclusion is
        /// always appended, so editing "VeBeGe" out of the ini has no effect.
        public static string[] ExcludeNames =>
            (ExcludeNamesRaw + "," + MandatoryExcludeNames)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

        /// Write every user option to the ini with its default value, but only
        /// for keys that aren't already there, so the file becomes a full,
        /// editable list of options without ever clobbering a user's choices.
        /// (The runtime-only [Slots] section is left to the reconciler.)
        public static void PrepopulateDefaults()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Directory.CreateDirectory(Dir);
            EnsureKey("VirtualCam", "Width", Width.ToString(inv));
            EnsureKey("VirtualCam", "Height", Height.ToString(inv));
            EnsureKey("VirtualCam", "Fps", Fps.ToString(inv));
            EnsureKey("Filter", "StaySeconds", StaySeconds.ToString(inv));
            EnsureKey("Filter", "Padding", Padding.ToString(inv));
            EnsureKey("Filter", "BodyScale", BodyScale.ToString(inv));
            EnsureKey("Filter", "HeatMinFlow", HeatMinFlow.ToString(inv));
            EnsureKey("Filter", "HeatSpread", HeatSpread.ToString(inv));
            EnsureKey("Filter", "HeatCooldownSeconds", HeatCooldownSeconds.ToString(inv));
            EnsureKey("Filter", "MaskHoldSeconds", MaskHoldSeconds.ToString(inv));
            EnsureKey("Service", "PollSeconds", PollSeconds.ToString(inv));
            EnsureKey("Service", "ExcludeNames", ExcludeNamesRaw);
        }

        private static void EnsureKey(string section, string key, string value)
        {
            if (!KeyExists(section, key))
                WritePrivateProfileString(section, key, value, IniPath);
        }

        private static bool KeyExists(string section, string key)
        {
            // A sentinel default comes back only when the key is truly absent.
            const string sentinel = "__unset__";
            return GetString(section, key, sentinel) != sentinel;
        }

        // Sticky slot assignment so a camera keeps its virtual twin (and CLSID)
        // across unplugs and reboots. Keyed by DirectShow device path.
        public static string GetSlotDevice(int slot) => GetString("Slots", "Slot" + slot, "");
        public static void SetSlotDevice(int slot, string devicePath)
        {
            Directory.CreateDirectory(Dir);
            WritePrivateProfileString("Slots", "Slot" + slot, devicePath, IniPath);
        }

        private static string GetString(string s, string k, string def)
        {
            var sb = new StringBuilder(2048);
            GetPrivateProfileString(s, k, def, sb, sb.Capacity, IniPath);
            return sb.ToString();
        }

        private static int GetInt(string s, string k, int def) =>
            int.TryParse(GetString(s, k, def.ToString()), out int v) ? v : def;

        private static double GetDouble(string s, string k, double def) =>
            double.TryParse(GetString(s, k, def.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;
    }
}
