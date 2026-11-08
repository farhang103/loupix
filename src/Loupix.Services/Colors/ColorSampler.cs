using Loupix.Core.Imaging;

namespace Loupix.Services.Colors;

/// <summary>Reads colours out of a <see cref="PixelBuffer"/>. Alpha is ignored (captures are opaque).</summary>
public static class ColorSampler
{
    /// <summary>The pixel at (x, y). Coordinates outside the image are clamped to the nearest edge pixel.</summary>
    public static Rgb At(PixelBuffer img, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(img);
        x = Math.Clamp(x, 0, img.Width - 1);
        y = Math.Clamp(y, 0, img.Height - 1);
        var (r, g, b) = img.GetRgb(x, y);
        return new Rgb(r, g, b);
    }

    /// <summary>Mean colour of the rectangle, clamped to the image.</summary>
    /// <exception cref="ArgumentException">The rectangle does not overlap the image.</exception>
    public static Rgb Average(PixelBuffer img, RectI r)
    {
        ArgumentNullException.ThrowIfNull(img);
        var rect = r.ClampTo(img.Width, img.Height);
        if (rect.IsEmpty) throw new ArgumentException($"Rectangle {r} does not overlap the {img.Width}x{img.Height} image.", nameof(r));

        long sr = 0, sg = 0, sb = 0;
        var px = img.Bgra;
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            int i = img.IndexOf(rect.X, y);
            for (int x = 0; x < rect.Width; x++, i += 4)
            {
                sb += px[i];
                sg += px[i + 1];
                sr += px[i + 2];
            }
        }
        long n = (long)rect.Width * rect.Height;
        return new Rgb((byte)((sr + n / 2) / n), (byte)((sg + n / 2) / n), (byte)((sb + n / 2) / n));
    }

    /// <summary>
    /// The darkest pixel (lowest relative luminance) in the <paramref name="size"/>×<paramref name="size"/> square
    /// centred on (cx, cy), clamped to the image. Used for "copy text colour": the darkest pixel under the cursor
    /// is the glyph ink rather than its anti-aliased edge. Ties go to the first pixel in row-major order.
    /// </summary>
    /// <exception cref="ArgumentException">The square does not overlap the image.</exception>
    public static Rgb Darkest(PixelBuffer img, int cx, int cy, int size = 20)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        int x0 = cx - size / 2, y0 = cy - size / 2;
        var rect = new RectI(x0, y0, size, size).ClampTo(img.Width, img.Height);
        if (rect.IsEmpty) throw new ArgumentException($"Point ({cx}, {cy}) is too far outside the {img.Width}x{img.Height} image.");

        var px = img.Bgra;
        double best = double.MaxValue;
        Rgb darkest = default;
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            int i = img.IndexOf(rect.X, y);
            for (int x = 0; x < rect.Width; x++, i += 4)
            {
                var c = new Rgb(px[i + 2], px[i + 1], px[i]);
                double l = c.RelativeLuminance;
                if (l < best)
                {
                    best = l;
                    darkest = c;
                }
            }
        }
        return darkest;
    }
}
