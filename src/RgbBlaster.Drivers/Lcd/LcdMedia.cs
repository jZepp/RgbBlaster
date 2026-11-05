using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using P = RgbBlaster.Drivers.Lcd.LinkLcdProtocol;

namespace RgbBlaster.Drivers.Lcd;

/// <summary>One encoded LCD frame and how long it stays on screen.</summary>
public sealed record LcdFrame(byte[] Jpeg, int DelayMs);

/// <summary>Turns image files (PNG, JPEG, BMP, single or animated GIF) into 480x480 LCD frames.</summary>
[SupportedOSPlatform("windows")]
public static class LcdMedia
{
    /// <summary>Shortest frame time. Frames are about 12 KB each; faster rates have not been tested on the device.</summary>
    public const int MinDelayMs = 50;
    public const int MaxFrames = 600;
    private const int GifDelayTag = 0x5100;

    public static List<LcdFrame> Decode(Stream data)
    {
        using var img = Image.FromStream(data);
        var frames = new List<LcdFrame>();
        int count = img.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? img.GetFrameCount(FrameDimension.Time) : 1;
        int[] delays = count > 1 ? GifDelays(img, count) : [1000];
        for (int i = 0; i < Math.Min(count, MaxFrames); i++)
        {
            if (count > 1) img.SelectActiveFrame(FrameDimension.Time, i);
            frames.Add(new LcdFrame(Encode(img), Math.Max(MinDelayMs, delays[i])));
        }
        return frames;
    }

    /// <summary>Scales to fill 480x480, center-cropped, and encodes as JPEG.</summary>
    public static byte[] Encode(Image src, long quality = 85)
    {
        using var bmp = new Bitmap(P.Width, P.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float s = Math.Max((float)P.Width / src.Width, (float)P.Height / src.Height);
            float w = src.Width * s, h = src.Height * s;
            g.Clear(Color.Black);
            g.DrawImage(src, (P.Width - w) / 2, (P.Height - h) / 2, w, h);
        }
        var codec = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var q = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, quality) } };
        using var ms = new MemoryStream();
        bmp.Save(ms, codec, q);
        return ms.ToArray();
    }

    private static int[] GifDelays(Image img, int count)
    {
        var d = new int[count];
        var item = img.PropertyIdList.Contains(GifDelayTag) ? img.GetPropertyItem(GifDelayTag) : null;
        for (int i = 0; i < count; i++)
        {
            // GIF delays are in 1/100 s, 4 bytes per frame. 0 means "as fast as possible"; browsers use 100 ms.
            int cs = item?.Value is { } v && v.Length >= (i + 1) * 4 ? BitConverter.ToInt32(v, i * 4) : 10;
            d[i] = cs <= 1 ? 100 : cs * 10;
        }
        return d;
    }
}
