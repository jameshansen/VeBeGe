using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace VeBeGe
{
    /// VeBeGe, Virtual Background, no GUI, it just works.
    ///
    /// A headless per-user background app (started at login) that mirrors every
    /// physical webcam as "&lt;name&gt; (VeBeGe)", a virtual camera whose feed is the
    /// virtual-background-filtered version of the real one. It keeps the mirror
    /// list in sync as devices come and go, and only holds a physical camera
    /// open while some app is actually streaming from its virtual twin.
    ///
    /// ponytail: this is a login-session background process, not a Windows
    /// service, session 0 can't access cameras (privacy + session isolation),
    /// so a "camera service" HAS to live in the user session.
    internal static class Program
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        // Everything the driver and filter need at runtime lives here, per the
        // install contract: %ProgramData%\VeBeGe (support dlls + AI models).
        private static readonly string[] StagedFiles =
        {
            "vebege_cam.dll",
            "face_detection_yunet_2023mar.onnx",
            "human_segmentation_pphumanseg_2023mar.onnx",
            "OpenCvSharpExtern.dll",
            "opencv_videoio_ffmpeg4110_64.dll",
        };

        private static readonly Dictionary<int, SlotPump> Pumps = new Dictionary<int, SlotPump>();
        private const int MaxSlots = 8;   // must match MAX_SLOTS in the driver

        // Live status for the tray menu (pseudo-GUI). Refreshed every Reconcile.
        internal static volatile int CamerasFound;   // all video devices the system sees
        internal static volatile int MirrorsActive;  // compatible cameras we mirror (VeBeGe enabled)

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        [STAThread]   // WinForms message loop (tray icon) needs an STA thread
        private static void Main(string[] args)
        {
            // Uninstaller cleanup entry point. Handled BEFORE the single-instance
            // mutex on purpose: during uninstall the resident service still holds
            // the mutex, so gating this on `first` would silently skip the cleanup.
            if (args.Any(a => a.Equals("-unregister", StringComparison.OrdinalIgnoreCase)))
            {
                UnregisterAllCams();
                Log.Write("All virtual cameras unregistered.");
                return;
            }

            using (var mutex = new Mutex(true, "VeBeGe.Service.SingleInstance", out bool first))
            {
                if (!first) return;   // already running

                Log.Write("VeBeGe service starting");
                Stage();
                Config.PrepopulateDefaults();   // lay out every option in the ini, defaults for any missing
                CleanupRunKeys();
                SetDllDirectory(Config.Dir);   // resolve vebege_cam.dll + OpenCV natives from ProgramData

                AppDomain.CurrentDomain.ProcessExit += (s, e) => Cleanup();

                // Camera work runs off the UI thread; the main thread owns the
                // tray icon's message loop and blocks in Application.Run.
                new Thread(ReconcileLoop) { IsBackground = true, Name = "VeBeGe reconcile" }.Start();

                OpenSetupOnFirstRun();

                using (new Tray())
                    System.Windows.Forms.Application.Run();

                Cleanup();
            }
        }

        private static void ReconcileLoop()
        {
            while (true)
            {
                try { Reconcile(); }
                catch (Exception ex) { Log.Write("Reconcile", ex); }
                Thread.Sleep(TimeSpan.FromSeconds(Config.PollSeconds));
            }
        }

        /// Collapse HKCU Run entries that point at vebege_service.exe down to a
        /// single one for this exact install. The MSI already writes one entry;
        /// this removes duplicates and stale entries left by earlier installs
        /// (different name or path) so the app never gets a pile of Run keys.
        /// It only ever deletes, installers own adding the key.
        /// ponytail: substring match on the value, no path parsing/quoting.
        private static void CleanupRunKeys()
        {
            try
            {
                string me = Process.GetCurrentProcess().MainModule.FileName;
                using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
                {
                    if (run == null) return;
                    bool keptOurs = false;
                    foreach (var name in run.GetValueNames())
                    {
                        if (!(run.GetValue(name) is string val) || val.Length == 0) continue;
                        if (val.IndexOf("vebege_service.exe", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        bool isThisInstall = val.IndexOf(me, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (isThisInstall && !keptOurs) { keptOurs = true; continue; }   // keep exactly one
                        run.DeleteValue(name, false);
                        Log.Write($"Removed stale/duplicate Run entry '{name}' -> {val}");
                    }
                }
            }
            catch (Exception ex) { Log.Write("CleanupRunKeys", ex); }
        }

        /// First launch after install: open the setup page once, tracked by a
        /// marker in the data folder. This is how the URL gets opened "after
        /// install" for both the MSI and the MSIX, a per-user MSIX can't launch
        /// a browser itself, so the app does it on its own first run.
        /// ponytail: marker file, not a registry flag, one line to check.
        private static void OpenSetupOnFirstRun()
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                string marker = Path.Combine(Config.Dir, "setup-opened");
                if (File.Exists(marker)) return;
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://vebege.io/setup") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Write("open setup", ex); }
        }

        /// Copy the driver, AI models and native support dlls from the install
        /// folder into %ProgramData%\VeBeGe. Best effort per file: the driver
        /// dll is locked while any camera app has a virtual camera open.
        private static void Stage()
        {
            Directory.CreateDirectory(Config.Dir);
            string src = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var name in StagedFiles)
            {
                string from = Path.Combine(src, name), to = Path.Combine(Config.Dir, name);
                try
                {
                    if (!File.Exists(from)) { Log.Write($"Stage: missing {from}"); continue; }
                    if (File.Exists(to) && File.GetLastWriteTimeUtc(to) >= File.GetLastWriteTimeUtc(from)) continue;
                    File.Copy(from, to, true);
                    Log.Write($"Staged {name}");
                }
                catch (Exception ex) { Log.Write($"Stage {name}: {ex.Message}"); }
            }
        }

        /// One pass of "make reality match the device list": every physical
        /// webcam gets a registered virtual twin and a pump; twins of removed
        /// webcams are unregistered and their pumps stopped.
        private static void Reconcile()
        {
            var devices = CameraEnumerator.GetDevices();
            CamerasFound = devices.Count;   // every video device the system sees (incl. our own twins)
            var exclude = Config.ExcludeNames;
            var physical = devices.Where(d =>
                    !string.IsNullOrEmpty(d.DevicePath) &&
                    !exclude.Any(x => d.Name.IndexOf(x.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            // Sticky slot mapping by device path (kept in the ini across reboots).
            var slotToPath = new string[MaxSlots];
            for (int s = 0; s < MaxSlots; s++) slotToPath[s] = Config.GetSlotDevice(s);

            var desired = new Dictionary<int, CameraDevice>();   // slot -> device
            foreach (var dev in physical)
            {
                int slot = Array.IndexOf(slotToPath, dev.DevicePath);
                if (slot < 0)
                {
                    // New device: first never-used slot, else steal one whose
                    // device is currently absent.
                    slot = Array.FindIndex(slotToPath, p => string.IsNullOrEmpty(p));
                    if (slot < 0)
                        slot = Array.FindIndex(slotToPath, p => !desired.Values.Any(d2 => d2.DevicePath == p)
                                                                && physical.All(d2 => d2.DevicePath != p));
                    if (slot < 0) { Log.Write($"No free slot for \"{dev.Name}\", skipped"); continue; }
                    slotToPath[slot] = dev.DevicePath;
                    Config.SetSlotDevice(slot, dev.DevicePath);
                    Log.Write($"Assigned slot {slot} to \"{dev.Name}\"");
                }
                if (!desired.ContainsKey(slot)) desired.Add(slot, dev);
            }

            string dllPath = Path.Combine(Config.Dir, "vebege_cam.dll");

            for (int slot = 0; slot < MaxSlots; slot++)
            {
                if (desired.TryGetValue(slot, out var dev))
                {
                    string name = dev.Name + " (VeBeGe)";
                    if (DriverRegistrar.RegisteredName(slot) != name)
                        DriverRegistrar.Register(slot, name, dllPath);
                    if (Pumps.TryGetValue(slot, out var pump)) pump.DeviceIndex = dev.Index;
                    else Pumps[slot] = new SlotPump(slot, dev.Index, name);
                }
                else
                {
                    if (Pumps.TryGetValue(slot, out var pump))
                    {
                        pump.Dispose();
                        Pumps.Remove(slot);
                    }
                    if (DriverRegistrar.IsRegistered(slot))
                        DriverRegistrar.Unregister(slot);
                }
            }

            MirrorsActive = desired.Count;
        }

        private static void StopAllPumps()
        {
            foreach (var p in Pumps.Values) { try { p.Dispose(); } catch { } }
            Pumps.Clear();
        }

        /// Remove every slot's DirectShow registration from HKCU so no dead
        /// "(VeBeGe)" cameras linger in device pickers after we're gone.
        private static void UnregisterAllCams()
        {
            for (int s = 0; s < MaxSlots; s++) DriverRegistrar.Unregister(s);
        }

        /// Full teardown on exit: stop the pumps and drop all virtual cameras.
        /// Idempotent, so it's safe from both ProcessExit and the normal return.
        /// ponytail: best-effort on graceful exit/logoff/shutdown; a hard kill
        /// (TerminateProcess) skips this, but the next launch's Reconcile drops
        /// any orphaned slots, so nothing accumulates.
        private static void Cleanup()
        {
            StopAllPumps();
            UnregisterAllCams();
        }
    }
}
