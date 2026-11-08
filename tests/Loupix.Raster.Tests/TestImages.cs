namespace Loupix.Raster.Tests;

/// <summary>Synthetic screenshot content: flat UI boxes, borders, gradients, noise and pseudo-text.</summary>
internal static class TestImages
{
    public static readonly uint White = Rgb(255, 255, 255);
    public static readonly uint Black = Rgb(0, 0, 0);

    public static uint Rgb(int r, int g, int b) => (uint)(b | g << 8 | r << 16 | 255 << 24);
    public static int R(uint c) => (int)((c >> 16) & 0xFF);
    public static int G(uint c) => (int)((c >> 8) & 0xFF);
    public static int B(uint c) => (int)(c & 0xFF);

    public static int Diff(uint a, uint b) =>
        Math.Max(Math.Abs(R(a) - R(b)), Math.Max(Math.Abs(G(a) - G(b)), Math.Abs(B(a) - B(b))));

    public static uint Blend(uint a, uint b, double t) =>
        Rgb((int)Math.Round(R(a) + (R(b) - R(a)) * t), (int)Math.Round(G(a) + (G(b) - G(a)) * t), (int)Math.Round(B(a) + (B(b) - B(a)) * t));

    public static uint Get(PixelBuffer img, int x, int y) => img.GetPixel(x, y) | 0xFF000000;

    public static void Set(PixelBuffer img, int x, int y, uint c)
    {
        if (img.Contains(x, y)) img.SetPixel(x, y, (byte)R(c), (byte)G(c), (byte)B(c));
    }

    public static PixelBuffer Solid(int w, int h, uint c)
    {
        var img = new PixelBuffer(w, h);
        Fill(img, new RectI(0, 0, w, h), c);
        return img;
    }

    public static void Fill(PixelBuffer img, RectI r, uint c)
    {
        r = r.ClampTo(img.Width, img.Height);
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
                Set(img, x, y, c);
    }

    public static void Border(PixelBuffer img, RectI r, int t, uint c)
    {
        Fill(img, new RectI(r.X, r.Y, r.Width, t), c);
        Fill(img, new RectI(r.X, r.Bottom - t, r.Width, t), c);
        Fill(img, new RectI(r.X, r.Y, t, r.Height), c);
        Fill(img, new RectI(r.Right - t, r.Y, t, r.Height), c);
    }

    /// <summary>Fills r with a horizontal (or vertical) linear gradient from c0 to c1.</summary>
    public static void Gradient(PixelBuffer img, RectI r, uint c0, uint c1, bool vertical = false)
    {
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
            {
                double t = vertical ? (y - r.Y) / (double)Math.Max(1, r.Height - 1) : (x - r.X) / (double)Math.Max(1, r.Width - 1);
                Set(img, x, y, Blend(c0, c1, t));
            }
    }

    /// <summary>A photo-like area: smooth colour field plus per-pixel noise of the given amplitude.</summary>
    public static void Photo(PixelBuffer img, RectI r, int noise, int seed)
    {
        var rng = new Random(seed);
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
            {
                int n() => noise == 0 ? 0 : rng.Next(-noise, noise + 1);
                int rr = 120 + (int)(60 * Math.Sin(x * 0.05) + 40 * Math.Cos(y * 0.07)) + n();
                int gg = 110 + (int)(50 * Math.Cos(x * 0.03 + y * 0.02)) + n();
                int bb = 100 + (int)(70 * Math.Sin(y * 0.04)) + n();
                Set(img, x, y, Rgb(Math.Clamp(rr, 0, 255), Math.Clamp(gg, 0, 255), Math.Clamp(bb, 0, 255)));
            }
    }

    private static readonly string[][] Glyphs =
    [
        ["11111", "10000", "10000", "11110", "10000", "10000", "11111"], // E
        ["01110", "10001", "10001", "10001", "10001", "10001", "01110"], // O
        ["11111", "00100", "00100", "00100", "00100", "00100", "00100"], // T
        ["10001", "10001", "10001", "11111", "10001", "10001", "10001"], // H
        ["10000", "10000", "10000", "10000", "10000", "10000", "11111"], // L
        ["00000", "00000", "10001", "01010", "00100", "01010", "10001"], // x
        ["00100", "00000", "00100", "00100", "00100", "00100", "00100"], // i
        ["00000", "00000", "10110", "11001", "10001", "10001", "10001"], // n
        ["11110", "10001", "10001", "11110", "10100", "10010", "10001"], // R
    ];

    /// <summary>
    /// Draws a line of pseudo-text (5x7 glyphs scaled by <paramref name="scale"/>, with an anti-aliasing halo)
    /// and returns its bounds including the halo. Words of 4-6 glyphs are separated by wider spaces.
    /// </summary>
    public static RectI Text(PixelBuffer img, int x, int y, int chars, uint color, int seed, int scale = 2)
    {
        var rng = new Random(seed);
        var core = new HashSet<(int, int)>();
        int cx = x, word = rng.Next(4, 7);
        for (int i = 0; i < chars; i++)
        {
            var g = Glyphs[rng.Next(Glyphs.Length)];
            for (int gy = 0; gy < 7; gy++)
                for (int gx = 0; gx < 5; gx++)
                    if (g[gy][gx] == '1')
                        for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++)
                                core.Add((cx + gx * scale + sx, y + gy * scale + sy));
            cx += 5 * scale + 2 * scale;
            if (--word == 0) { cx += 3 * scale; word = rng.Next(4, 7); }
        }
        var bounds = new RectI(x - 1, y - 1, 0, 0);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var (px, py) in core)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int ax = px + dx, ay = py + dy;
                    if (core.Contains((ax, ay)) || !img.Contains(ax, ay)) continue;
                    double t = dx == 0 || dy == 0 ? 0.35 : 0.12;
                    Set(img, ax, ay, Blend(Get(img, ax, ay), color, t));
                    minX = Math.Min(minX, ax); maxX = Math.Max(maxX, ax); minY = Math.Min(minY, ay); maxY = Math.Max(maxY, ay);
                }
        }
        foreach (var (px, py) in core) Set(img, px, py, color);
        return core.Count == 0 ? bounds : RectI.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>The set of glyph core pixels: pixels in r within <paramref name="tol"/> of the text colour.</summary>
    public static List<(int X, int Y)> PixelsNear(PixelBuffer img, RectI r, uint color, int tol)
    {
        var list = new List<(int, int)>();
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
                if (Diff(Get(img, x, y), color) <= tol) list.Add((x, y));
        return list;
    }

    /// <summary>Number of pixels in r that differ between a and b by more than tol.</summary>
    public static int CountDiff(PixelBuffer a, PixelBuffer b, RectI r, int tol = 0)
    {
        int n = 0;
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
                if (Diff(Get(a, x, y), Get(b, x, y)) > tol) n++;
        return n;
    }

    public static int MaskCount(BitMask m, RectI r)
    {
        int n = 0;
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
                if (m[x, y]) n++;
        return n;
    }
}
