using Loupix.Core.Imaging;

namespace Loupix.Raster;

/// <summary>
/// High-quality separable resizing: area (box) averaging along an axis that shrinks, Catmull-Rom bicubic
/// along an axis that grows, a straight copy along an axis that keeps its size.
/// </summary>
public static class Resampler
{
    public static PixelBuffer Resize(PixelBuffer src, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (width == src.Width && height == src.Height) return src.Clone();

        // Horizontal pass first when it shrinks (less work for the vertical pass), otherwise vertical first.
        if (width <= src.Width)
        {
            var tmp = width == src.Width ? src : Horizontal(src, width);
            return height == src.Height ? (ReferenceEquals(tmp, src) ? src.Clone() : tmp) : Vertical(tmp, height);
        }
        else
        {
            var tmp = height == src.Height ? src : Vertical(src, height);
            return Horizontal(tmp, width);
        }
    }

    /// <summary>Filter taps: output sample i uses source Index[i*Taps + t] with Weight[i*Taps + t].</summary>
    private sealed class Kernel
    {
        public int Taps;
        public int[] Index = [];
        public float[] Weight = [];
    }

    private static Kernel Build(int srcLen, int dstLen)
    {
        var k = new Kernel();
        double scale = (double)srcLen / dstLen;
        if (dstLen < srcLen)
        {
            // Area averaging: output i covers [i*scale, (i+1)*scale) in source coordinates.
            k.Taps = (int)Math.Ceiling(scale) + 1;
            k.Index = new int[dstLen * k.Taps];
            k.Weight = new float[dstLen * k.Taps];
            for (int i = 0; i < dstLen; i++)
            {
                double a = i * scale, b = (i + 1) * scale;
                int j0 = (int)Math.Floor(a);
                for (int t = 0; t < k.Taps; t++)
                {
                    int j = j0 + t;
                    double overlap = Math.Min(j + 1, b) - Math.Max(j, a);
                    k.Index[i * k.Taps + t] = Math.Min(j, srcLen - 1);
                    k.Weight[i * k.Taps + t] = overlap > 0 && j < srcLen ? (float)(overlap / scale) : 0f;
                }
            }
        }
        else
        {
            // Catmull-Rom (a = -0.5) bicubic, edge-clamped.
            k.Taps = 4;
            k.Index = new int[dstLen * 4];
            k.Weight = new float[dstLen * 4];
            for (int i = 0; i < dstLen; i++)
            {
                double c = (i + 0.5) * scale - 0.5;
                int fl = (int)Math.Floor(c);
                double f = c - fl;
                double sum = 0;
                for (int t = 0; t < 4; t++)
                {
                    double wgt = CatmullRom(f - (t - 1));
                    k.Index[i * 4 + t] = Math.Clamp(fl - 1 + t, 0, srcLen - 1);
                    k.Weight[i * 4 + t] = (float)wgt;
                    sum += wgt;
                }
                for (int t = 0; t < 4; t++) k.Weight[i * 4 + t] /= (float)sum;
            }
        }
        return k;
    }

    private static double CatmullRom(double x)
    {
        x = Math.Abs(x);
        if (x < 1) return 1.5 * x * x * x - 2.5 * x * x + 1;
        if (x < 2) return -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2;
        return 0;
    }

    private static byte Clamp(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

    private static PixelBuffer Horizontal(PixelBuffer src, int width)
    {
        var k = Build(src.Width, width);
        var dst = new PixelBuffer(width, src.Height);
        byte[] s = src.Bgra, d = dst.Bgra;
        int sStride = src.Stride, dStride = dst.Stride, taps = k.Taps;
        Parallel.For(0, src.Height, y =>
        {
            int so = y * sStride, dOff = y * dStride;
            for (int x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                int kb = x * taps;
                for (int t = 0; t < taps; t++)
                {
                    float wgt = k.Weight[kb + t];
                    if (wgt == 0) continue;
                    int si = so + k.Index[kb + t] * 4;
                    b += s[si] * wgt; g += s[si + 1] * wgt; r += s[si + 2] * wgt; a += s[si + 3] * wgt;
                }
                int di = dOff + x * 4;
                d[di] = Clamp(b); d[di + 1] = Clamp(g); d[di + 2] = Clamp(r); d[di + 3] = Clamp(a);
            }
        });
        return dst;
    }

    private static PixelBuffer Vertical(PixelBuffer src, int height)
    {
        var k = Build(src.Height, height);
        var dst = new PixelBuffer(src.Width, height);
        byte[] s = src.Bgra, d = dst.Bgra;
        int stride = src.Stride, taps = k.Taps;
        Parallel.For(0, height, () => new float[stride], (y, _, acc) =>
        {
            Array.Clear(acc);
            int kb = y * taps;
            for (int t = 0; t < taps; t++)
            {
                float wgt = k.Weight[kb + t];
                if (wgt == 0) continue;
                var row = s.AsSpan(k.Index[kb + t] * stride, stride);
                for (int i = 0; i < stride; i++) acc[i] += row[i] * wgt;
            }
            var drow = d.AsSpan(y * stride, stride);
            for (int i = 0; i < stride; i++) drow[i] = Clamp(acc[i]);
            return acc;
        }, _ => { });
        return dst;
    }
}
