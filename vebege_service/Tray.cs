using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace VeBeGe
{
    /// The only user-facing surface of an otherwise headless app: a tray icon
    /// whose right-click menu doubles as a pseudo-GUI, a live status readout
    /// ("N Cameras Found" / "N VeBeGe Cameras Active") plus a few actions.
    /// More features hang off this menu later.
    internal sealed class Tray : IDisposable
    {
        private const string Website = "https://vebege.io";

        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _found;
        private readonly ToolStripMenuItem _active;

        public Tray()
        {
            _found  = new ToolStripMenuItem { Enabled = false };
            _active = new ToolStripMenuItem { Enabled = false };

            var menu = new ContextMenuStrip();
            menu.Items.Add(_found);
            menu.Items.Add(_active);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open Data Folder", null, (s, e) => Open(Config.Dir));
            menu.Items.Add("VeBeGe Website", null, (s, e) => Open(Website));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Application.Exit());
            menu.Opening += (s, e) => RefreshCounts();   // fresh numbers on every open

            _icon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),   // icon_sm.ico, tuned for the tray's small size
                Text = "VeBeGe",
                Visible = true,
                ContextMenuStrip = menu,
            };
            RefreshCounts();
        }

        private void RefreshCounts()
        {
            int found = Program.CamerasFound, active = Program.MirrorsActive;
            _found.Text  = $"{found} Camera{(found == 1 ? "" : "s")} Found";
            _active.Text = $"{active} Compatible Camera{(active == 1 ? "" : "s")}, VeBeGe Enabled";
        }

        private static Icon LoadTrayIcon()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon_sm.ico"))
                return new Icon(s, SystemInformation.SmallIconSize);   // pick the small frame from the .ico
        }

        private static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Tray.Open " + target, ex); }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
