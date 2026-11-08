using Loupix.Core.Imaging;

namespace Loupix.Raster;

/// <summary>Selection helpers: snapping a rough rectangle to an object, and flood-fill bounds.</summary>
public static class SmartSelect
{
    private const int MaxRounds = 4;

    /// <summary>
    /// Snaps a rough selection to the object it surrounds. For each edge: if the edge line is uniform
    /// (all pixels within <paramref name="tolerance"/> of each other per channel) it moves inward until the
    /// line is no longer uniform (trims empty margins); otherwise it moves outward while the next line out
    /// is non-uniform, stopping just inside the first uniform line or at the image border. A line is tested
    /// across the selection's current extent plus one pixel at each end (so an edge lying on a box border stays).
    /// Edges are re-evaluated (up to a few rounds) because moving one edge changes the extent of the others.
    /// Returns <paramref name="sel"/> unchanged if the result would be empty.
    /// </summary>
    public static RectI AutoAdjust(PixelBuffer img, RectI sel, int tolerance = 8)
    {
        ArgumentNullException.ThrowIfNull(img);
        var r = sel.ClampTo(img.Width, img.Height);
        if (r.IsEmpty) return sel;
        var ctx = new Ctx(img, tolerance);
        int l = r.X, t = r.Y, rt = r.Right, b = r.Bottom;
        for (int round = 0; round < MaxRounds; round++)
        {
            int l0 = l, t0 = t, r0 = rt, b0 = b;
            if (!ctx.Adjust(Edge.Left, ref l, ref t, ref rt, ref b)) return sel;
            if (!ctx.Adjust(Edge.Top, ref l, ref t, ref rt, ref b)) return sel;
            if (!ctx.Adjust(Edge.Right, ref l, ref t, ref rt, ref b)) return sel;
            if (!ctx.Adjust(Edge.Bottom, ref l, ref t, ref rt, ref b)) return sel;
            if (l == l0 && t == t0 && rt == r0 && b == b0) break;
        }
        return RectI.FromLTRB(l, t, rt, b);
    }

    /// <summary>Same as <see cref="AutoAdjust"/>, for one edge only.</summary>
    public static RectI AutoAdjustEdge(PixelBuffer img, RectI sel, Edge edge, int tolerance = 8)
    {
        ArgumentNullException.ThrowIfNull(img);
        var r = sel.ClampTo(img.Width, img.Height);
        if (r.IsEmpty) return sel;
        int l = r.X, t = r.Y, rt = r.Right, b = r.Bottom;
        if (!new Ctx(img, tolerance).Adjust(edge, ref l, ref t, ref rt, ref b)) return sel;
        return RectI.FromLTRB(l, t, rt, b);
    }

    private readonly struct Ctx(PixelBuffer img, int tol)
    {
        private readonly PixelBuffer _img = img;
        private readonly int _tol = tol;

        /// <summary>Adjusts one edge in place; false if the rectangle would become empty.</summary>
        public bool Adjust(Edge edge, ref int l, ref int t, ref int r, ref int b)
        {
            int w = _img.Width, h = _img.Height;
            switch (edge)
            {
                case Edge.Left:
                    if (ColUniform(l, t, b)) { do l++; while (l < r && ColUniform(l, t, b)); return l < r; }
                    while (l > 0 && !ColUniform(l - 1, t, b)) l--;
                    return true;
                case Edge.Right:
                    if (ColUniform(r - 1, t, b)) { do r--; while (r > l && ColUniform(r - 1, t, b)); return r > l; }
                    while (r < w && !ColUniform(r, t, b)) r++;
                    return true;
                case Edge.Top:
                    if (RowUniform(t, l, r)) { do t++; while (t < b && RowUniform(t, l, r)); return t < b; }
                    while (t > 0 && !RowUniform(t - 1, l, r)) t--;
                    return true;
                default:
                    if (RowUniform(b - 1, l, r)) { do b--; while (b > t && RowUniform(b - 1, l, r)); return b > t; }
                    while (b < h && !RowUniform(b, l, r)) b++;
                    return true;
            }
        }

        // Lines are tested one pixel beyond the current extent at both ends, so an edge that sits exactly on an
        // object's border (a uniform line along the box) is not mistaken for an empty margin.
        private bool RowUniform(int y, int x0, int x1)
        {
            x0 = Math.Max(0, x0 - 1); x1 = Math.Min(_img.Width, x1 + 1);
            return Uniform(Px.Pixels(_img).Slice(y * _img.Width + x0, x1 - x0), 1);
        }

        private bool ColUniform(int x, int y0, int y1)
        {
            y0 = Math.Max(0, y0 - 1); y1 = Math.Min(_img.Height, y1 + 1);
            return Uniform(Px.Pixels(_img).Slice(y0 * _img.Width + x, (y1 - y0 - 1) * _img.Width + 1), _img.Width);
        }

        private bool Uniform(ReadOnlySpan<uint> px, int step)
        {
            uint first = px[0];
            int minB = Px.B(first), maxB = minB, minG = Px.G(first), maxG = minG, minR = Px.R(first), maxR = minR;
            for (int i = step; i < px.Length; i += step)
            {
                uint p = px[i];
                if (((p ^ first) & Px.RgbMask) == 0) continue;
                int c = Px.B(p); if (c < minB) minB = c; else if (c > maxB) maxB = c;
                c = Px.G(p); if (c < minG) minG = c; else if (c > maxG) maxG = c;
                c = Px.R(p); if (c < minR) minR = c; else if (c > maxR) maxR = c;
                if (maxB - minB > _tol || maxG - minG > _tol || maxR - minR > _tol) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Bounding box of the 4-connected region whose colour is within <paramref name="tolerance"/>
    /// (per channel) of the seed pixel. Scanline flood fill with a visited bitset.
    /// Returns an empty rectangle when the seed is outside the image.
    /// </summary>
    public static RectI FloodBounds(PixelBuffer img, int x, int y, int tolerance = 6)
    {
        ArgumentNullException.ThrowIfNull(img);
        if (!img.Contains(x, y)) return default;
        int w = img.Width, h = img.Height;
        var px = Px.Pixels(img);
        uint seed = px[y * w + x];
        int wpr = (w + 63) >> 6;
        var visited = new ulong[wpr * h];
        var stack = new Stack<long>();
        stack.Push(Key(x, y));
        int minX = x, maxX = x, minY = y, maxY = y;

        while (stack.Count > 0)
        {
            long k = stack.Pop();
            int sx = (int)(k & 0xFFFFFFFF), sy = (int)(k >> 32);
            var vrow = visited.AsSpan(sy * wpr, wpr);
            if (Test(vrow, sx)) continue;
            var row = px.Slice(sy * w, w);
            int lx = sx, rx = sx + 1;
            while (lx > 0 && Px.Near(row[lx - 1], seed, tolerance) && !Test(vrow, lx - 1)) lx--;
            while (rx < w && Px.Near(row[rx], seed, tolerance) && !Test(vrow, rx)) rx++;
            SetRange(vrow, lx, rx);
            if (lx < minX) minX = lx;
            if (rx - 1 > maxX) maxX = rx - 1;
            if (sy < minY) minY = sy;
            if (sy > maxY) maxY = sy;
            if (sy > 0) PushRuns(stack, px.Slice((sy - 1) * w, w), visited.AsSpan((sy - 1) * wpr, wpr), lx, rx, sy - 1, seed, tolerance);
            if (sy + 1 < h) PushRuns(stack, px.Slice((sy + 1) * w, w), visited.AsSpan((sy + 1) * wpr, wpr), lx, rx, sy + 1, seed, tolerance);
        }
        return RectI.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static long Key(int x, int y) => (long)y << 32 | (uint)x;

    /// <summary>Pushes one seed per run of unvisited, matching pixels in [x0, x1) of a neighbouring row.</summary>
    private static void PushRuns(Stack<long> stack, ReadOnlySpan<uint> row, ReadOnlySpan<ulong> vrow,
        int x0, int x1, int ny, uint seed, int tol)
    {
        bool inRun = false;
        int i = x0;
        while (i < x1)
        {
            // Skip whole visited words quickly (the row we came from is usually fully visited).
            if ((i & 63) == 0 && i + 64 <= x1 && vrow[i >> 6] == ulong.MaxValue) { inRun = false; i += 64; continue; }
            bool ok = !Test(vrow, i) && Px.Near(row[i], seed, tol);
            if (ok && !inRun) stack.Push(Key(i, ny));
            inRun = ok;
            i++;
        }
    }

    private static bool Test(ReadOnlySpan<ulong> bits, int i) => (bits[i >> 6] & (1UL << (i & 63))) != 0;

    private static void SetRange(Span<ulong> bits, int x0, int x1)
    {
        while (x0 < x1)
        {
            int word = x0 >> 6, bit = x0 & 63;
            int n = Math.Min(64 - bit, x1 - x0);
            bits[word] |= n == 64 ? ulong.MaxValue : ((1UL << n) - 1) << bit;
            x0 += n;
        }
    }
}
