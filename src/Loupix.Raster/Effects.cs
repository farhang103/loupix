using System.Numerics;
using Loupix.Core.Imaging;

namespace Loupix.Raster;

/// <summary>Redaction and retouching effects. All work in place and are clipped to the image.</summary>
public static class Effects
{
    /// <summary>Areas with fewer blocks than this across either side get their blocks shuffled.</summary>
    private const int ShuffleBelowBlocks = 4;

    /// <summary>
    /// Replaces each <paramref name="blockSize"/>² block (aligned to the rectangle's origin) with its average.
    /// When the area is small (fewer than 4 blocks across either side) the block colours are also shuffled
    /// deterministically so that short text cannot be recovered from the averages.
    /// </summary>
    public static void Pixelate(PixelBuffer img, RectI r, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(img);
        var c = r.ClampTo(img.Width, img.Height);
        if (c.IsEmpty || blockSize < 1) return;
        int nx = (c.Width + blockSize - 1) / blockSize, ny = (c.Height + blockSize - 1) / blockSize;
        var colors = new uint[nx * ny];
        var px = Px.Pixels(img);
        int stride = img.Width;

        for (int by = 0; by < ny; by++)
        {
            int y0 = c.Y + by * blockSize, y1 = Math.Min(y0 + blockSize, c.Bottom);
            for (int bx = 0; bx < nx; bx++)
            {
                int x0 = c.X + bx * blockSize, x1 = Math.Min(x0 + blockSize, c.Right);
                long sb = 0, sg = 0, sr = 0, sa = 0;
                for (int y = y0; y < y1; y++)
                {
                    foreach (uint p in px.Slice(y * stride + x0, x1 - x0))
                    {
                        sb += p & 0xFF; sg += (p >> 8) & 0xFF; sr += (p >> 16) & 0xFF; sa += p >> 24;
                    }
                }
                long n = (long)(x1 - x0) * (y1 - y0), half = n / 2;
                colors[by * nx + bx] = Px.Pack((int)((sb + half) / n), (int)((sg + half) / n), (int)((sr + half) / n), (int)((sa + half) / n));
            }
        }

        if ((nx < ShuffleBelowBlocks || ny < ShuffleBelowBlocks) && colors.Length > 1)
            Shuffle(colors, nx * 31 + ny);

        for (int by = 0; by < ny; by++)
        {
            int y0 = c.Y + by * blockSize, y1 = Math.Min(y0 + blockSize, c.Bottom);
            for (int bx = 0; bx < nx; bx++)
            {
                int x0 = c.X + bx * blockSize, x1 = Math.Min(x0 + blockSize, c.Right);
                uint col = colors[by * nx + bx];
                for (int y = y0; y < y1; y++) px.Slice(y * stride + x0, x1 - x0).Fill(col);
            }
        }
    }

    /// <summary>Deterministic Fisher-Yates shuffle that never returns the identity order.</summary>
    private static void Shuffle(uint[] a, int seed)
    {
        var rng = new Random(seed);
        var idx = new int[a.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        for (int i = idx.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (idx[i], idx[j]) = (idx[j], idx[i]);
        }
        bool identity = true;
        for (int i = 0; i < idx.Length && identity; i++) identity = idx[i] == i;
        var src = (uint[])a.Clone();
        for (int i = 0; i < a.Length; i++) a[i] = src[identity ? (i + 1) % a.Length : idx[i]];
    }

    /// <summary>
    /// Gaussian blur approximated by three box passes per axis. <paramref name="radius"/> is the Gaussian's
    /// standard deviation in pixels. Pixels outside <paramref name="r"/> (clamped to the image) are sampled,
    /// so the blurred area blends into its surroundings; only pixels inside <paramref name="r"/> change.
    /// </summary>
    public static void Blur(PixelBuffer img, RectI r, int radius)
    {
        ArgumentNullException.ThrowIfNull(img);
        var c = r.ClampTo(img.Width, img.Height);
        if (c.IsEmpty || radius < 1) return;
        Span<int> halves = stackalloc int[3];
        BoxHalfWidths(radius, halves);
        int pad = halves[0] + halves[1] + halves[2];
        var e = c.Inflate(pad, pad).ClampTo(img.Width, img.Height);
        int w = e.Width, h = e.Height;

        var a = new byte[w * h * 4];
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(img.Bgra, img.IndexOf(e.X, e.Y + y), a, y * w * 4, w * 4);

        for (int pass = 0; pass < 3; pass++)
        {
            int hw = halves[pass];
            Parallel.For(0, h, y => BoxRow(a.AsSpan(y * w * 4, w * 4), b.AsSpan(y * w * 4, w * 4), w, hw));
            (a, b) = (b, a);
        }
        for (int pass = 0; pass < 3; pass++)
        {
            BoxColumns(a, b, w, h, halves[pass]);
            (a, b) = (b, a);
        }

        for (int y = c.Y; y < c.Bottom; y++)
            Buffer.BlockCopy(a, ((y - e.Y) * w + (c.X - e.X)) * 4, img.Bgra, img.IndexOf(c.X, y), c.Width * 4);
    }

    /// <summary>Half-widths of three box filters approximating a Gaussian of the given sigma.</summary>
    private static void BoxHalfWidths(double sigma, Span<int> halves)
    {
        const int n = 3;
        double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        double mIdeal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4.0 * wl - 4);
        int m = (int)Math.Round(mIdeal);
        for (int i = 0; i < n; i++) halves[i] = ((i < m ? wl : wu) - 1) / 2;
    }

    private static void BoxRow(ReadOnlySpan<byte> src, Span<byte> dst, int w, int hw)
    {
        if (hw <= 0) { src.CopyTo(dst); return; }
        int div = 2 * hw + 1, half = div / 2;
        int sb = 0, sg = 0, sr = 0, sa = 0;
        for (int k = -hw; k <= hw; k++)
        {
            int i = Math.Clamp(k, 0, w - 1) * 4;
            sb += src[i]; sg += src[i + 1]; sr += src[i + 2]; sa += src[i + 3];
        }
        for (int x = 0; x < w; x++)
        {
            int o = x * 4;
            dst[o] = (byte)((sb + half) / div); dst[o + 1] = (byte)((sg + half) / div);
            dst[o + 2] = (byte)((sr + half) / div); dst[o + 3] = (byte)((sa + half) / div);
            int add = Math.Min(x + hw + 1, w - 1) * 4, sub = Math.Max(x - hw, 0) * 4;
            sb += src[add] - src[sub]; sg += src[add + 1] - src[sub + 1];
            sr += src[add + 2] - src[sub + 2]; sa += src[add + 3] - src[sub + 3];
        }
    }

    /// <summary>Vertical box filter with running column sums, processed row by row in parallel column strips.</summary>
    private static void BoxColumns(byte[] src, byte[] dst, int w, int h, int hw)
    {
        int stride = w * 4;
        if (hw <= 0) { Buffer.BlockCopy(src, 0, dst, 0, stride * h); return; }
        const int StripBytes = 1024;
        int strips = (stride + StripBytes - 1) / StripBytes;
        int div = 2 * hw + 1, half = div / 2;
        Parallel.For(0, strips, s =>
        {
            int x0 = s * StripBytes, x1 = Math.Min(x0 + StripBytes, stride), n = x1 - x0;
            var sums = new int[n];
            for (int k = -hw; k <= hw; k++)
            {
                var row = src.AsSpan(Math.Clamp(k, 0, h - 1) * stride + x0, n);
                for (int i = 0; i < n; i++) sums[i] += row[i];
            }
            for (int y = 0; y < h; y++)
            {
                var d = dst.AsSpan(y * stride + x0, n);
                for (int i = 0; i < n; i++) d[i] = (byte)((sums[i] + half) / div);
                var add = src.AsSpan(Math.Min(y + hw + 1, h - 1) * stride + x0, n);
                var sub = src.AsSpan(Math.Max(y - hw, 0) * stride + x0, n);
                for (int i = 0; i < n; i++) sums[i] += add[i] - sub[i];
            }
        });
    }

    /// <summary>
    /// Removes whatever is inside <paramref name="r"/> as if it was never there. A 2px ring just outside the
    /// rectangle is sampled: if it is near-uniform the area is filled with its median colour, otherwise with a
    /// Coons-patch interpolation of the four edges (reproduces gradients and stripes running across).
    /// Sides on the image border have no ring and are extrapolated from the others.
    /// </summary>
    public static void Erase(PixelBuffer img, RectI r)
    {
        ArgumentNullException.ThrowIfNull(img);
        var c = r.ClampTo(img.Width, img.Height);
        if (c.IsEmpty) return;
        int W = img.Width, H = img.Height;
        int l = c.X, t = c.Y, rt = c.Right, b = c.Bottom, w = c.Width, h = c.Height;
        bool hasT = t > 0, hasB = b < H, hasL = l > 0, hasR = rt < W;
        if (!hasT && !hasB && !hasL && !hasR) return;
        var px = Px.Pixels(img);

        // Uniform ring? Fill with its median.
        var ring = new List<uint>(4 * (w + h) + 16);
        for (int d = 1; d <= 2; d++)
        {
            for (int x = Math.Max(0, l - d); x < Math.Min(W, rt + d); x++)
            {
                if (t - d >= 0) ring.Add(px[(t - d) * W + x]);
                if (b + d - 1 < H) ring.Add(px[(b + d - 1) * W + x]);
            }
            for (int y = t; y < b; y++)
            {
                if (l - d >= 0) ring.Add(px[y * W + l - d]);
                if (rt + d - 1 < W) ring.Add(px[y * W + rt + d - 1]);
            }
        }
        var ringSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ring);
        uint median = Px.Median(ringSpan);
        int near = 0;
        foreach (uint p in ringSpan) if (Px.Near(p, median, 12)) near++;
        if (near >= ring.Count * 0.9)
        {
            for (int y = t; y < b; y++) px.Slice(y * W + l, w).Fill(median);
            return;
        }

        // Boundary curves on the lines adjacent to the rectangle, indexed from l-1..rt and t-1..b.
        var T = new Vector3[w + 2]; var B = new Vector3[w + 2];
        var L = new Vector3[h + 2]; var R = new Vector3[h + 2];
        if (hasT) for (int i = 0; i < w + 2; i++) T[i] = V(px[(t - 1) * W + Math.Clamp(l - 1 + i, 0, W - 1)]);
        if (hasB) for (int i = 0; i < w + 2; i++) B[i] = V(px[b * W + Math.Clamp(l - 1 + i, 0, W - 1)]);
        if (hasL) for (int j = 0; j < h + 2; j++) L[j] = V(px[Math.Clamp(t - 1 + j, 0, H - 1) * W + l - 1]);
        if (hasR) for (int j = 0; j < h + 2; j++) R[j] = V(px[Math.Clamp(t - 1 + j, 0, H - 1) * W + rt]);

        // Missing sides are extrapolated from the opposite side, shifted by a linear correction so that its ends
        // agree with the perpendicular sides (keeps stripes and gradients running across). With no opposite side
        // (the area spans the whole image in that direction) the perpendicular sides' ends are interpolated.
        if (!hasT || !hasB)
        {
            Vector3 cl = default, cr = default;
            for (int i = 0; i < w + 2; i++)
            {
                float u = i / (float)(w + 1);
                if (hasT == hasB) // both missing
                {
                    T[i] = Between(L[0], R[0], hasL, hasR, u);
                    B[i] = Between(L[h + 1], R[h + 1], hasL, hasR, u);
                    continue;
                }
                var src = hasT ? T : B;
                int end = hasT ? h + 1 : 0; // the missing side's end on L and R
                if (i == 0)
                {
                    Vector3? l0 = hasL ? L[end] - src[0] : null, r0 = hasR ? R[end] - src[w + 1] : null;
                    cl = l0 ?? r0 ?? Vector3.Zero;
                    cr = r0 ?? l0 ?? Vector3.Zero;
                }
                (hasT ? B : T)[i] = src[i] + Vector3.Lerp(cl, cr, u);
            }
        }
        if (!hasL || !hasR)
        {
            for (int j = 0; j < h + 2; j++)
            {
                float v = j / (float)(h + 1);
                if (hasL == hasR) // both missing
                {
                    L[j] = Vector3.Lerp(T[0], B[0], v);
                    R[j] = Vector3.Lerp(T[w + 1], B[w + 1], v);
                }
                else if (hasR) L[j] = R[j] + Vector3.Lerp(T[0] - R[0], B[0] - R[h + 1], v);
                else R[j] = L[j] + Vector3.Lerp(T[w + 1] - L[0], B[w + 1] - L[h + 1], v);
            }
        }

        Vector3 c00 = (T[0] + L[0]) * 0.5f, c10 = (T[w + 1] + R[0]) * 0.5f;
        Vector3 c01 = (B[0] + L[h + 1]) * 0.5f, c11 = (B[w + 1] + R[h + 1]) * 0.5f;
        for (int j = 1; j <= h; j++)
        {
            float v = j / (float)(h + 1);
            var row = px.Slice((t + j - 1) * W + l, w);
            for (int i = 1; i <= w; i++)
            {
                float u = i / (float)(w + 1);
                Vector3 p = (1 - v) * T[i] + v * B[i] + (1 - u) * L[j] + u * R[j]
                    - ((1 - u) * (1 - v) * c00 + u * (1 - v) * c10 + (1 - u) * v * c01 + u * v * c11);
                row[i - 1] = Px.Pack((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));
            }
        }

        static Vector3 V(uint p) => new(Px.B(p), Px.G(p), Px.R(p));
        static Vector3 Between(Vector3 a, Vector3 b, bool hasA, bool hasB, float u) =>
            hasA && hasB ? Vector3.Lerp(a, b, u) : hasA ? a : b;
    }

    /// <summary>
    /// A mask of text-like pixels in <paramref name="r"/>: pixels that differ from their local background and
    /// belong to glyph-sized components (or thin strokes of a text line), dilated 1px to cover anti-aliasing.
    /// Large shapes, borders, long rules, big icons and photos are not masked. The mask covers r clipped to the
    /// image; its indexer takes image coordinates.
    /// </summary>
    public static BitMask DetectText(PixelBuffer img, RectI r)
    {
        ArgumentNullException.ThrowIfNull(img);
        return TextDetector.Detect(img, r).Mask;
    }

    /// <summary>
    /// Replaces the pixels found by <see cref="DetectText"/> with the local background: each masked pixel is
    /// interpolated from the nearest unmasked pixels left/right and above/below (so gradients survive), with any
    /// neighbour that is not background-like replaced by the text blob's background colour. A dark button with
    /// white text becomes a plain dark button; everything else stays untouched.
    /// </summary>
    public static void EraseText(PixelBuffer img, RectI r)
    {
        ArgumentNullException.ThrowIfNull(img);
        var det = TextDetector.Detect(img, r);
        var mask = det.Mask;
        if (det.Blobs.Count == 0) return;
        var px = Px.Pixels(img);
        int W = img.Width;

        // Compute all fills first (from unmasked pixels only), then write, so overlapping blobs agree.
        var fills = new List<(int Index, uint Color)>();
        var done = new BitMask(mask.Bounds);
        foreach (var blob in det.Blobs)
        {
            uint bg = blob.Background;
            for (int y = blob.Bounds.Y; y < blob.Bounds.Bottom; y++)
            {
                for (int x = blob.Bounds.X; x < blob.Bounds.Right; x++)
                {
                    if (!mask[x, y] || done[x, y]) continue;
                    done[x, y] = true;
                    int xl = x - 1, xr = x + 1, yt = y - 1, yb = y + 1;
                    while (mask[xl, y]) xl--;
                    while (mask[xr, y]) xr++;
                    while (mask[x, yt]) yt--;
                    while (mask[x, yb]) yb++;
                    Vector3 hv = Vector3.Lerp(Sample(img, xl, y, bg), Sample(img, xr, y, bg), (x - xl) / (float)(xr - xl));
                    Vector3 vv = Vector3.Lerp(Sample(img, x, yt, bg), Sample(img, x, yb, bg), (y - yt) / (float)(yb - yt));
                    Vector3 v = (hv + vv) * 0.5f;
                    fills.Add((y * W + x, Px.Pack((int)MathF.Round(v.X), (int)MathF.Round(v.Y), (int)MathF.Round(v.Z))));
                }
            }
        }
        foreach (var (index, color) in fills) px[index] = color;

        // A neighbour outside the image, or one that is not background-like, is replaced by the blob's background.
        static Vector3 Sample(PixelBuffer img, int sx, int sy, uint bg)
        {
            uint p = img.Contains(sx, sy) ? Px.Pixels(img)[sy * img.Width + sx] : bg;
            if (!Px.Near(p, bg, TextDetector.MaxRingSpread)) p = bg;
            return new Vector3(Px.B(p), Px.G(p), Px.R(p));
        }
    }

    /// <summary>
    /// Pixelates only the bounding boxes of the text lines found by <see cref="DetectText"/> inside
    /// <paramref name="r"/>, leaving the rest of the area sharp.
    /// </summary>
    public static void PixelateText(PixelBuffer img, RectI r, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(img);
        var det = TextDetector.Detect(img, r);
        foreach (var line in TextLines(det.Blobs))
            Pixelate(img, line, blockSize);
    }

    /// <summary>
    /// Groups text blobs into lines: blobs whose vertical extents overlap by at least half the smaller height and
    /// whose horizontal gap is at most the taller height are merged (union-find over an x-sorted sweep).
    /// </summary>
    internal static List<RectI> TextLines(List<TextDetector.TextBlob> blobs)
    {
        var rects = blobs.Select(b => b.Bounds).OrderBy(b => b.X).ToArray();
        int n = rects.Length;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        for (int i = 0; i < n; i++)
        {
            var a = rects[i];
            for (int j = i + 1; j < n; j++)
            {
                var b = rects[j];
                if (b.X > a.Right + TextDetector.MaxGlyph) break;
                int gap = b.X - a.Right;
                int overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
                if (gap <= Math.Max(a.Height, b.Height) && overlap * 2 >= Math.Min(a.Height, b.Height))
                {
                    int ra = Root(i), rb = Root(j);
                    if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
                }
            }
        }
        var lines = new Dictionary<int, RectI>();
        for (int i = 0; i < n; i++)
        {
            int root = Root(i);
            lines[root] = lines.TryGetValue(root, out var u) ? u.Union(rects[i]) : rects[i];
        }
        return [.. lines.Values];

        int Root(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
    }
}
