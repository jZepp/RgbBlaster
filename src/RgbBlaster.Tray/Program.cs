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
    private static void Main()
    {
        using var single = new Mutex(true, @"Local\RgbBlaster.Tray", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();

        // Same rgbblaster.json as the service when installed next to it.
        var configPath = Path.Combine(AppContext.BaseDirectory, "rgbblaster.json");
        int port = File.Exists(configPath) ? AppConfig.Load(configPath).Web.Port : new WebConfig().Port;
        var url = $"http://localhost:{port}/";

        using var up = MakeIcon(running: true);
        using var down = MakeIcon(running: false);
        var state = new ToolStripMenuItem("Checking the service...") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open RgbBlaster", null, (_, _) => Open(url)) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(state);
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
        async void Poll()
        {
            bool ok;
            try { ok = (await http.GetAsync(url + "api/status")).IsSuccessStatusCode; }
            catch { ok = false; }
            if (ok == last) return;
            last = ok;
            tray.Icon = ok ? up : down;
            tray.Text = ok ? "RgbBlaster: running" : "RgbBlaster: service not running";
            state.Text = ok ? "Service running" : "Service not running";
        }
        using var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) => Poll();
        timer.Start();
        Poll();

        Application.Run();
        tray.Visible = false;
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
    /// Three-colour ring. When the service is not running the ring is grey with a diagonal bar,
    /// so the two states differ by shape as well as colour.
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
