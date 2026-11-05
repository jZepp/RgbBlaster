using System.Diagnostics;
using System.Drawing.Drawing2D;
using RgbBlaster.Core;

namespace RgbBlaster.Tray;

/// <summary>
/// Notification-area icon for the RgbBlaster Windows service. A service runs in session 0 and cannot show one itself.
/// Runs unelevated in the user's session; started at logon from the HKCU Run key that install.ps1 writes.
/// </summary>
internal static class Program
{
    private const string ServiceName = "RgbBlaster";

    [STAThread]
    private static void Main(string[] args)
    {
        // Same rgbblaster.json as the service when installed next to it. --port overrides it, for a simulation run.
        var configPath = Path.Combine(AppContext.BaseDirectory, "rgbblaster.json");
        int port = File.Exists(configPath) ? AppConfig.Load(configPath).Web.Port : new WebConfig().Port;
        int pi = Array.IndexOf(args, "--port");
        if (pi >= 0 && pi + 1 < args.Length && int.TryParse(args[pi + 1], out int p)) port = p;
        var url = $"http://localhost:{port}/";

        using var single = new Mutex(true, $@"Local\RgbBlaster.Tray.{port}", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();

        using var up = MakeIcon(running: true);
        using var down = MakeIcon(running: false);
        using var sound = new SoundCapture();
        var state = new ToolStripMenuItem("Checking the service...") { Enabled = false };
        var soundState = new ToolStripMenuItem("Sound: off") { Enabled = false };
        var soundOn = new ToolStripMenuItem("React to sound") { CheckOnClick = true, Checked = SoundEnabled };
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open RgbBlaster", null, (_, _) => Open(url)) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(state);
        menu.Items.Add(new ToolStripMenuItem($"Tray version {AppVersion.Display}") { Enabled = false });
        var conflicts = new ToolStripMenuItem("Conflicting apps: none") { Enabled = false };
        menu.Items.Add(conflicts);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(soundOn);
        menu.Items.Add(soundState);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart service", null, (_, _) => Elevated($"Restart-Service -Name {ServiceName} -Force"));
        menu.Items.Add("Stop service", null, (_, _) => Elevated($"Stop-Service -Name {ServiceName} -Force"));
        menu.Items.Add("Start service", null, (_, _) => Elevated($"Start-Service -Name {ServiceName}"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit tray icon", null, (_, _) => Application.Exit());

        using var tray = new NotifyIcon { Icon = down, Text = "RgbBlaster", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Open(url); };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        bool? last = null;
        bool wanted = false;
        int audioPort = 0;

        // Capture runs only while the service shows an effect that uses sound and the user has not turned it off.
        void UpdateSound()
        {
            if (soundOn.Checked && wanted && audioPort > 0) sound.Start(audioPort); else sound.Stop();
            soundState.Text = !soundOn.Checked ? "Sound: off"
                : !wanted ? "Sound: idle (no sound effect)"
                : sound.Error is { } err ? $"Sound: error, {err}"
                : "Sound: sending";
        }
        soundOn.CheckedChanged += (_, _) => { SoundEnabled = soundOn.Checked; UpdateSound(); };

        async void Poll()
        {
            string? version = null;
            try
            {
                using var r = await http.GetAsync(url + "api/audio");
                if (r.IsSuccessStatusCode)
                {
                    var root = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
                    version = root.GetProperty("version").GetString() ?? "?";
                    wanted = root.GetProperty("wanted").GetBoolean();
                    audioPort = root.GetProperty("port").GetInt32();
                }
            }
            catch { /* service not running */ }
            bool ok = version is not null;
            if (!ok) wanted = false;
            UpdateSound();
            if (ok == last) return;
            last = ok;
            if (ok) CheckVendorApps(); // check again whenever the service starts
            tray.Icon = ok ? up : down;
            tray.Text = ok ? $"RgbBlaster {version}: running" : "RgbBlaster: service not running";
            state.Text = ok ? $"Service {version} running" : "Service not running";
        }
        // Vendor RGB apps fight the service for the devices. Warn once for each app that starts while the service runs;
        // an app that stays open is not shown again until it closes and starts again.
        var warned = new HashSet<string>();
        void CheckVendorApps()
        {
            var found = VendorApps.Running();
            var names = found.Select(a => a.Name).ToHashSet();
            conflicts.Text = found.Count == 0 ? "Conflicting apps: none" : $"Conflicting apps: {string.Join(", ", names)}";
            warned.IntersectWith(names);
            if (last != true) return;
            var fresh = found.Where(a => warned.Add(a.Name)).ToList();
            if (fresh.Count == 0) return;
            var lines = string.Join("\n", fresh.Select(a => $"{a.Name} ({a.Devices})"));
            tray.ShowBalloonTip(15000, "RgbBlaster: close conflicting app",
                $"{lines}\nIt controls the same devices. Lights can flicker and the AIO can drop off USB.", ToolTipIcon.Warning);
        }

        using var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) => Poll();
        timer.Start();
        using var vendorTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        vendorTimer.Tick += (_, _) => CheckVendorApps();
        vendorTimer.Start();
        Poll();
        CheckVendorApps();

        Application.Run();
        tray.Visible = false;
    }

    private const string RegistryKey = @"HKEY_CURRENT_USER\Software\RgbBlaster";

    /// <summary>The user's "React to sound" choice, kept per user. On by default.</summary>
    private static bool SoundEnabled
    {
        get => Microsoft.Win32.Registry.GetValue(RegistryKey, "SoundCapture", 1) is not 0;
        set => Microsoft.Win32.Registry.SetValue(RegistryKey, "SoundCapture", value ? 1 : 0);
    }

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Service control needs administrator rights, so each command asks through UAC.</summary>
    private static void Elevated(string command)
    {
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{command}\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (System.ComponentModel.Win32Exception) { /* UAC prompt declined */ }
    }

    /// <summary>
    /// Three-color ring. When the service is not running the ring is gray with a diagonal bar,
    /// so the two states differ by shape as well as color.
    /// </summary>
    private static Icon MakeIcon(bool running)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(4, 4, 24, 24);
            if (running)
            {
                Color[] colors = [Color.FromArgb(255, 60, 60), Color.FromArgb(60, 220, 90), Color.FromArgb(60, 140, 255)];
                for (int i = 0; i < 3; i++)
                    using (var pen = new Pen(colors[i], 6)) g.DrawArc(pen, rect, -90 + i * 120, 120);
            }
            else
            {
                using var pen = new Pen(Color.FromArgb(150, 150, 150), 6);
                g.DrawEllipse(pen, rect);
                using var bar = new Pen(Color.White, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(bar, 7, 25, 25, 7);
            }
        }
        var h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(h);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
