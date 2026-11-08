using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Loupix.Core.Imaging;
using Loupix.Imaging;

namespace Loupix.Editor;

/// <summary>Produces the bitmaps for raster effects (pixelate, blur, erase, highlighter multiply).</summary>
public static class EffectRenderer
{
    private static readonly int[] PixelSteps = [5, 8, 12, 18, 26];
    private static readonly int[] BlurSteps = [3, 5, 8, 12, 18];

    public static (BitmapSource Bitmap, Rect Rect) Render(EditorImage image, Rect area, EffectMode mode, bool textOnly, int size, double scale)
    {
        var r = ToRectI(area, image);
        if (r.IsEmpty) return (ImageIO.ToBitmapSource96(new PixelBuffer(1, 1)), Rect.Empty);
        int step = Math.Clamp(size, 1, 5) - 1;
        // Work on a padded copy so blur/erase can sample around the area.
        int pad = mode == EffectMode.Pixelate ? 0 : 24;
        var outer = r.Inflate(pad, pad).ClampTo(image.Width, image.Height);
        var work = image.Pixels.Crop(outer.X, outer.Y, outer.Width, outer.Height);
        var local = r.Offset(-outer.X, -outer.Y);
        int block = Math.Max(2, (int)Math.Round(PixelSteps[step] * scale));
        int radius = Math.Max(1, (int)Math.Round(BlurSteps[step] * scale));
        switch (mode)
        {
            case EffectMode.Pixelate:
                if (textOnly) RasterOps.PixelateText(work, local, block);
                else RasterOps.Pixelate(work, local, block);
                break;
            case EffectMode.Blur:
                RasterOps.Blur(work, local, radius);
                break;
            case EffectMode.Erase:
                if (textOnly) RasterOps.EraseText(work, local);
                else RasterOps.Erase(work, local);
                break;
        }
        var result = work.Crop(local.X, local.Y, local.Width, local.Height);
        return (ImageIO.ToBitmapSource96(result), new Rect(r.X, r.Y, r.Width, r.Height));
    }

    /// <summary>Multiply-blends a colour over an area of the base image (for the highlighter).</summary>
    public static (BitmapSource Bitmap, Rect Rect) Multiply(EditorImage image, Rect area, Color color)
    {
        var r = ToRectI(area, image);
        if (r.IsEmpty) return (ImageIO.ToBitmapSource96(new PixelBuffer(1, 1)), Rect.Empty);
        var pb = image.Pixels.Crop(r.X, r.Y, r.Width, r.Height);
        var d = pb.Bgra;
        for (int i = 0; i < d.Length; i += 4)
        {
            d[i] = (byte)(d[i] * color.B / 255);
            d[i + 1] = (byte)(d[i + 1] * color.G / 255);
            d[i + 2] = (byte)(d[i + 2] * color.R / 255);
        }
        return (ImageIO.ToBitmapSource96(pb), new Rect(r.X, r.Y, r.Width, r.Height));
    }

    public static RectI ToRectI(Rect area, EditorImage image)
    {
        if (area.IsEmpty) return default;
        int l = (int)Math.Floor(area.Left), t = (int)Math.Floor(area.Top);
        int rr = (int)Math.Ceiling(area.Right), b = (int)Math.Ceiling(area.Bottom);
        return RectI.FromLTRB(l, t, rr, b).ClampTo(image.Width, image.Height);
    }
}
