using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.Versioning;
using P = RgbBlaster.Drivers.Lcd.LinkLcdProtocol;

namespace RgbBlaster.Drivers.Lcd;

/// <summary>Renders the LCD status screen: CPU temperature large with an arc gauge, GPU temperature and pump speed below.</summary>
[SupportedOSPlatform("windows")]
public sealed class LcdDashboard : IDisposable
{
    private readonly Bitmap _bmp = new(P.Width, P.Height, PixelFormat.Format24bppRgb);
    private readonly ImageCodecInfo _jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
    private readonly EncoderParameters _quality = new(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, 85L) } };
    private readonly Font _big = new("Segoe UI", 110, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _label = new("Segoe UI", 30, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _small = new("Segoe UI", 38, FontStyle.Bold, GraphicsUnit.Pixel);

    /// <summary>Gauge range for the CPU arc, °C.</summary>
    public float GaugeMin { get; set; } = 30;
    public float GaugeMax { get; set; } = 95;

    public byte[] Render(float? cpuC, float? gpuC, int? pumpRpm)
    {
        using (var g = Graphics.FromImage(_bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Black);

            // Arc gauge: 270 degrees, open at the bottom.
            var rect = new RectangleF(30, 30, P.Width - 60, P.Height - 60);
            using (var track = new Pen(Color.FromArgb(40, 40, 40), 22) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(track, rect, 135, 270);
            if (cpuC is float c)
            {
                float k = Math.Clamp((c - GaugeMin) / (GaugeMax - GaugeMin), 0, 1);
                using var fill = new Pen(GaugeColor(k), 22) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (k > 0) g.DrawArc(fill, rect, 135, 270 * k);
            }

            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("CPU", _label, Brushes.Gray, new RectangleF(0, 110, P.Width, 40), center);
            g.DrawString(cpuC is float t ? $"{t:F0}°" : "--", _big, Brushes.White, new RectangleF(0, 150, P.Width, 140), center);
            g.DrawString($"GPU {(gpuC is float gt ? $"{gt:F0}°" : "--")}", _small, Brushes.LightGray, new RectangleF(0, 300, P.Width, 50), center);
            g.DrawString($"Pump {(pumpRpm?.ToString() ?? "--")} rpm", _label, Brushes.Gray, new RectangleF(0, 355, P.Width, 40), center);
        }
        using var ms = new MemoryStream();
        _bmp.Save(ms, _jpeg, _quality);
        return ms.ToArray();
    }

    /// <summary>Encodes any image file as a 480x480 frame, scaled to fill and centre-cropped.</summary>
    public byte[] FromFile(string path)
    {
        using var src = Image.FromFile(path);
        using (var g = Graphics.FromImage(_bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float s = Math.Max((float)P.Width / src.Width, (float)P.Height / src.Height);
            float w = src.Width * s, h = src.Height * s;
            g.Clear(Color.Black);
            g.DrawImage(src, (P.Width - w) / 2, (P.Height - h) / 2, w, h);
        }
        using var ms = new MemoryStream();
        _bmp.Save(ms, _jpeg, _quality);
        return ms.ToArray();
    }

    /// <summary>Blue at the cool end, through yellow, to red at the hot end. Brightness also rises with heat.</summary>
    private static Color GaugeColor(float k) =>
        k < 0.5f ? Lerp(Color.FromArgb(0, 120, 255), Color.FromArgb(255, 200, 0), k * 2)
                 : Lerp(Color.FromArgb(255, 200, 0), Color.FromArgb(255, 30, 0), (k - 0.5f) * 2);

    private static Color Lerp(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public void Dispose()
    {
        _bmp.Dispose();
        _quality.Dispose();
        _big.Dispose();
        _label.Dispose();
        _small.Dispose();
    }
}
