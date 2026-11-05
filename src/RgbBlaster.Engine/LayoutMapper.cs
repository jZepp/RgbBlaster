using RgbBlaster.Core;

namespace RgbBlaster.Engine;

/// <summary>Turns a zone placement into one canvas pixel index per LED. Computed once; per-frame sampling is a gather.</summary>
public static class LayoutMapper
{
    public static int[] Map(ZonePlacement p, int ledCount, int width, int height, IReadOnlyList<(float X, float Y)>? positions = null)
    {
        var idx = new int[ledCount];
        if (ledCount == 0) return idx;

        switch (p.Shape)
        {
            case PlacementShape.Ring:
            {
                int fans = Math.Max(1, p.Fans);
                int perFan = ledCount / fans;
                for (int i = 0; i < ledCount; i++)
                {
                    int fan = Math.Min(i / Math.Max(1, perFan), fans - 1);
                    int k = i - fan * perFan;
                    int n = fan == fans - 1 ? ledCount - fan * perFan : perFan;
                    // LED 1 at 12 o'clock plus Rotation; clockwise unless Reverse.
                    double a = (Reverse(p) ? -1 : 1) * 2 * Math.PI * k / n - Math.PI / 2 + p.Rotation * Math.PI / 180;
                    float cx = p.X + fan * p.FanDx;
                    float cy = p.Y + fan * p.FanDy;
                    idx[i] = Pixel(cx + p.Radius * (float)Math.Cos(a), cy + p.Radius * (float)Math.Sin(a), width, height);
                }
                break;
            }
            case PlacementShape.Grid when positions is not null:
            {
                // Rotate in pixel space so a turned grid keeps its shape on the non-square canvas.
                double r = p.Rotation * Math.PI / 180, cos = Math.Cos(r), sin = Math.Sin(r);
                float cx = (p.X + p.X2) / 2 * width, cy = (p.Y + p.Y2) / 2 * height;
                for (int i = 0; i < ledCount; i++)
                {
                    var (gx, gy) = i < positions.Count ? positions[i] : (0.5f, 0.5f);
                    float px = (p.X + (p.X2 - p.X) * gx) * width - cx, py = (p.Y + (p.Y2 - p.Y) * gy) * height - cy;
                    idx[i] = Pixel((float)((cx + px * cos - py * sin) / width), (float)((cy + px * sin + py * cos) / height), width, height);
                }
                break;
            }
            case PlacementShape.Grid:
            case PlacementShape.Line:
                for (int i = 0; i < ledCount; i++)
                {
                    float f = ledCount == 1 ? 0.5f : (float)i / (ledCount - 1);
                    if (Reverse(p)) f = 1 - f;
                    idx[i] = Pixel(p.X + (p.X2 - p.X) * f, p.Y + (p.Y2 - p.Y) * f, width, height);
                }
                break;
        }
        return idx;
    }

    private static bool Reverse(ZonePlacement p) => p.Reverse;

    private static int Pixel(float x, float y, int w, int h)
    {
        int px = Math.Clamp((int)(x * w), 0, w - 1);
        int py = Math.Clamp((int)(y * h), 0, h - 1);
        return py * w + px;
    }
}
