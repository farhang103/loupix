using Loupix.Core.Imaging;

namespace Loupix.Services.Imaging;

/// <summary>Bilinear resampling for BGRA buffers and 8-bit luminance planes (pixel-centre aligned).</summary>
internal static class Resampler
{
    private const int Shift = 8;          // fixed-point weights in [0, 256]
    private const int One = 1 << Shift;

    public static PixelBuffer Resize(PixelBuffer src, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (width == src.Width && height == src.Height) return src.Clone();

        var (x0, x1, wx) = Taps(src.Width, width);
        var (y0, y1, wy) = Taps(src.Height, height);
        var dst = new PixelBuffer(width, height);
        byte[] s = src.Bgra, d = dst.Bgra;
        int sStride = src.Stride;

        for (int y = 0; y < height; y++)
        {
            int rowA = y0[y] * sStride, rowB = y1[y] * sStride;
            int fy = wy[y], gy = One - fy;
            int o = y * dst.Stride;
            for (int x = 0; x < width; x++)
            {
                int a = x0[x] * 4, b = x1[x] * 4;
                int fx = wx[x], gx = One - fx;
                for (int c = 0; c < 4; c++)
                {
                    int top = s[rowA + a + c] * gx + s[rowA + b + c] * fx;
                    int bottom = s[rowB + a + c] * gx + s[rowB + b + c] * fx;
                    d[o++] = (byte)((top * gy + bottom * fy + (1 << (2 * Shift - 1))) >> (2 * Shift));
                }
            }
        }
        return dst;
    }

    public static byte[] ResizeGray(byte[] src, int srcWidth, int srcHeight, int width, int height)
    {
        var (x0, x1, wx) = Taps(srcWidth, width);
        var (y0, y1, wy) = Taps(srcHeight, height);
        var dst = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int rowA = y0[y] * srcWidth, rowB = y1[y] * srcWidth;
            int fy = wy[y], gy = One - fy;
            int o = y * width;
            for (int x = 0; x < width; x++)
            {
                int fx = wx[x], gx = One - fx;
                int top = src[rowA + x0[x]] * gx + src[rowA + x1[x]] * fx;
                int bottom = src[rowB + x0[x]] * gx + src[rowB + x1[x]] * fx;
                dst[o + x] = (byte)((top * gy + bottom * fy + (1 << (2 * Shift - 1))) >> (2 * Shift));
            }
        }
        return dst;
    }

    /// <summary>For each destination coordinate: the two source taps and the fixed-point weight of the second.</summary>
    private static (int[] I0, int[] I1, int[] W) Taps(int srcLen, int dstLen)
    {
        var i0 = new int[dstLen];
        var i1 = new int[dstLen];
        var w = new int[dstLen];
        double ratio = (double)srcLen / dstLen;
        for (int i = 0; i < dstLen; i++)
        {
            double pos = (i + 0.5) * ratio - 0.5;
            if (pos < 0) pos = 0;
            int p = (int)pos;
            if (p >= srcLen - 1) { p = srcLen - 1; pos = p; }
            i0[i] = p;
            i1[i] = Math.Min(p + 1, srcLen - 1);
            w[i] = (int)Math.Round((pos - p) * One);
        }
        return (i0, i1, w);
    }
}
