using System.Buffers;
using System.Runtime.CompilerServices;
using Loupix.Core.Imaging;

namespace Loupix.Raster;

/// <summary>
/// Finds text-like pixels in screenshots.
/// <para>
/// 1. The area (plus a context margin) is split into "flat" regions: 4-connected pixels whose neighbours differ
///    by at most <see cref="FlatTolerance"/> (so gradients stay one region, while anti-aliased glyph edges break off).
///    Regions are built from horizontal runs with a union-find, so cost scales with the number of runs.
/// 2. Large flat regions are background (page, button fills, borders, rules, gradients, smooth photo areas).
///    A region is a candidate when it is glyph-sized, or when it is a thin, text-line-shaped stroke structure
///    (height ≤ <see cref="MaxGlyph"/>, no straight run longer than <see cref="MaxRule"/>, sparse), and it is not
///    a solid area thicker than a glyph stroke (≥ <see cref="MinThick"/> px both ways and ≥ 70% filled).
/// 3. Candidate pixels are grouped into 8-connected blobs (glyph core + anti-aliasing + counters). A blob is text
///    when it has a glyph/text-line shape, the background around it is near-uniform, and it contrasts with that
///    background. Photos fail (one huge blob, or no contrast), big icons and shapes fail on size.
/// 4. Accepted blobs are dilated by one pixel to cover anti-aliasing.
/// </para>
/// </summary>
internal static class TextDetector
{
    internal const int FlatTolerance = 10;
    internal const int MaxGlyph = 48;
    internal const int MaxRule = 64;
    internal const int Context = 64;
    internal const int MinContrast = 40;
    internal const int MaxRingSpread = 48;
    internal const int MinThick = 12;

    /// <summary>One accepted text blob: its dilated bounds (image coordinates, clipped) and its background colour.</summary>
    internal readonly record struct TextBlob(RectI Bounds, uint Background);

    internal sealed record Result(BitMask Mask, List<TextBlob> Blobs);

    /// <summary>Output rows per band; each band is analysed with <see cref="Context"/> extra rows above and below.</summary>
    internal const int BandRows = 256;

    public static Result Detect(PixelBuffer img, RectI r)
    {
        var area = r.ClampTo(img.Width, img.Height);
        if (area.IsEmpty) return new Result(new BitMask(r.X, r.Y, 0, 0), []);
        var ext = area.Inflate(Context, Context).ClampTo(img.Width, img.Height);
        var mask = new BitMask(area);
        int bands = (area.Height + BandRows - 1) / BandRows;
        var bandBlobs = new List<TextBlob>[bands];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4) };
        Parallel.For(0, bands, options, k =>
        {
            int oy0 = area.Y + k * BandRows, oy1 = Math.Min(area.Bottom, oy0 + BandRows);
            var band = RectI.FromLTRB(ext.X, Math.Max(ext.Y, oy0 - Context), ext.Right, Math.Min(ext.Bottom, oy1 + Context));
            var (bandMask, blobs) = DetectBand(img, band, area, oy0, oy1);
            // Bands write disjoint rows of the result.
            for (int y = oy0; y < oy1; y++) mask.CopyRow(bandMask, y - band.Y, area.X - band.X, y - area.Y);
            bandBlobs[k] = blobs;
        });
        var all = new List<TextBlob>();
        foreach (var b in bandBlobs) all.AddRange(b);
        return new Result(mask, all);
    }

    /// <summary>
    /// Analyses <paramref name="band"/> (image coordinates). Returns its dilated mask (band coordinates) and the
    /// accepted blobs whose top row lies in [oy0, oy1). Regions touching a band edge where the image continues
    /// are of unknown size and are treated as background; glyphs never touch them thanks to the context rows.
    /// </summary>
    private static (BitMask Mask, List<TextBlob> Blobs) DetectBand(PixelBuffer img, RectI band, RectI area, int oy0, int oy1)
    {
        // The per-run arrays are the big allocations (one entry per run); rent them so repeated calls don't churn the GC.
        var pool = ArrayPool<int>.Shared;
        var rented = new List<int[]>();
        try { return DetectBand(img, band, area, oy0, oy1, n => { var a = pool.Rent(Math.Max(1, n)); rented.Add(a); return a; }); }
        finally { foreach (var a in rented) pool.Return(a); }
    }

    private static (BitMask Mask, List<TextBlob> Blobs) DetectBand(PixelBuffer img, RectI band, RectI area, int oy0, int oy1, Func<int, int[]> rent)
    {
        int w = band.Width, h = band.Height, stride = img.Width;
        bool cutL = band.X > 0, cutR = band.Right < img.Width, cutT = band.Y > 0, cutB = band.Bottom < img.Height;
        ReadOnlySpan<uint> px = Px.Pixels(img);

        // ---- Phase 1: flat runs (counted first so arrays are exact) and flat regions -------------------------
        var rowStart = new int[h + 1];
        for (int y = 0; y < h; y++)
        {
            var row = px.Slice((band.Y + y) * stride + band.X, w);
            int n = 1;
            for (int x = 1; x < w; x++) if (!Px.Near(row[x - 1], row[x], FlatTolerance)) n++;
            rowStart[y + 1] = rowStart[y] + n;
        }
        int nRuns = rowStart[h];
        var xs = rent(nRuns);
        for (int y = 0; y < h; y++)
        {
            var row = px.Slice((band.Y + y) * stride + band.X, w);
            int i = rowStart[y];
            xs[i++] = 0;
            for (int x = 1; x < w; x++) if (!Px.Near(row[x - 1], row[x], FlatTolerance)) xs[i++] = x;
        }
        var label = rent(nRuns);
        for (int i = 0; i < nRuns; i++) label[i] = i;

        for (int y = 1; y < h; y++)
        {
            var up = px.Slice((band.Y + y - 1) * stride + band.X, w);
            var cur = px.Slice((band.Y + y) * stride + band.X, w);
            int i = rowStart[y - 1], iEnd = rowStart[y], j = rowStart[y], jEnd = rowStart[y + 1];
            while (i < iEnd && j < jEnd)
            {
                int a0 = xs[i], a1 = i + 1 < iEnd ? xs[i + 1] : w;
                int b0 = xs[j], b1 = j + 1 < jEnd ? xs[j + 1] : w;
                int lo = Math.Max(a0, b0), hi = Math.Min(a1, b1);
                if (lo < hi)
                {
                    int ri = Find(label, i), rj = Find(label, j);
                    if (ri != rj)
                    {
                        for (int x = lo; x < hi; x++)
                        {
                            if (Px.Near(up[x], cur[x], FlatTolerance)) { Union(label, ri, rj); break; }
                        }
                    }
                }
                if (a1 < b1) i++;
                else if (b1 < a1) j++;
                else { i++; j++; }
            }
        }
        int nComps = Relabel(label, nRuns);

        // Region statistics and classification.
        var st = new Stats(nComps, rent);
        for (int y = 0; y < h; y++)
            for (int i = rowStart[y], e = rowStart[y + 1]; i < e; i++)
                st.Include(label[i], xs[i], i + 1 < e ? xs[i + 1] : w, y);
        var candidateComp = new bool[nComps];
        for (int c = 0; c < nComps; c++)
        {
            int bw = st.MaxX[c] - st.MinX[c], bh = st.MaxY[c] - st.MinY[c];
            bool touchesCut = (cutL && st.MinX[c] == 0) || (cutR && st.MaxX[c] == w)
                || (cutT && st.MinY[c] == 0) || (cutB && st.MaxY[c] == h);
            // Solid areas thicker than any glyph stroke (pockets inside outline icons, small filled tiles,
            // icon buttons) are background even when small.
            bool thick = bw >= MinThick && bh >= MinThick && st.Area[c] * 10L >= (long)bw * bh * 7;
            candidateComp[c] = !touchesCut && !thick && ((bw <= MaxGlyph && bh <= MaxGlyph)
                || (bh <= MaxGlyph && st.MaxRun[c] <= MaxRule && st.Area[c] * 2L <= (long)bw * bh));
        }

        // ---- Phase 2: candidate runs and 8-connected blobs -------------------------------------------------
        var cx0 = new IntList(1024);
        var cx1 = new IntList(1024);
        var crow = new int[h + 1];
        for (int y = 0; y < h; y++)
        {
            crow[y] = cx0.Count;
            for (int i = rowStart[y], e = rowStart[y + 1]; i < e; i++)
            {
                if (!candidateComp[label[i]]) continue;
                int s = xs[i], en = i + 1 < e ? xs[i + 1] : w;
                if (cx0.Count > crow[y] && cx1.Items[cx1.Count - 1] == s) cx1.Items[cx1.Count - 1] = en;
                else { cx0.Add(s); cx1.Add(en); }
            }
        }
        crow[h] = cx0.Count;
        int nc = cx0.Count;
        int[] c0 = cx0.Items, c1 = cx1.Items;
        var blobOf = new int[nc];
        for (int i = 0; i < nc; i++) blobOf[i] = i;
        for (int y = 1; y < h; y++)
        {
            int i = crow[y - 1], iEnd = crow[y], j = crow[y], jEnd = crow[y + 1];
            while (i < iEnd && j < jEnd)
            {
                int a0 = c0[i], a1 = c1[i], b0 = c0[j], b1 = c1[j];
                if (a0 <= b1 && b0 <= a1) Union(blobOf, Find(blobOf, i), Find(blobOf, j));
                if (a1 < b1) i++;
                else if (b1 < a1) j++;
                else { i++; j++; }
            }
        }
        int nb = Relabel(blobOf, nc);

        var bs = new Stats(nb, rent);
        var candMask = new BitMask(0, 0, w, h);
        var runRow = new int[nc];
        for (int y = 0; y < h; y++)
        {
            for (int i = crow[y]; i < crow[y + 1]; i++)
            {
                bs.Include(blobOf[i], c0[i], c1[i], y);
                candMask.SetRun(y, c0[i], c1[i]);
                runRow[i] = y;
            }
        }

        // Group candidate runs by blob (counting sort), keeping row order.
        var blobStart = new int[nb + 1];
        for (int i = 0; i < nc; i++) blobStart[blobOf[i] + 1]++;
        for (int b = 0; b < nb; b++) blobStart[b + 1] += blobStart[b];
        var order = new int[nc];
        var fill = (int[])blobStart.Clone();
        for (int i = 0; i < nc; i++) order[fill[blobOf[i]]++] = i;

        // ---- Phase 3: accept text blobs ----------------------------------------------------------------------
        var bandMask = new BitMask(0, 0, w, h);
        var blobs = new List<TextBlob>();
        for (int b = 0; b < nb; b++)
        {
            int bx0 = bs.MinX[b], bx1 = bs.MaxX[b], by0 = bs.MinY[b], by1 = bs.MaxY[b];
            int bw = bx1 - bx0, bh = by1 - by0;
            bool shapeOk = bh <= MaxGlyph &&
                (bw <= MaxGlyph || (bs.MaxRun[b] <= MaxRule && bs.Area[b] * 10L <= (long)bw * bh * 6));
            if (!shapeOk) continue;
            var ring = new RectI(bx0, by0, bw, bh).Inflate(1, 1).ClampTo(w, h);
            if (!RingBackground(px, stride, band, candMask, ring, out uint bg)) continue;

            int contrast = 0;
            for (int k = blobStart[b]; k < blobStart[b + 1] && contrast < MinContrast; k++)
            {
                int run = order[k];
                var row = px.Slice((band.Y + runRow[run]) * stride + band.X, w);
                for (int x = c0[run]; x < c1[run]; x++)
                {
                    int d = Px.Diff(row[x], bg);
                    if (d > contrast) contrast = d;
                }
            }
            if (contrast < MinContrast) continue;

            for (int k = blobStart[b]; k < blobStart[b + 1]; k++)
            {
                int run = order[k];
                bandMask.SetRun(runRow[run], c0[run], c1[run]);
            }
            int top = by0 + band.Y;
            var bounds = new RectI(bx0 + band.X, top, bw, bh).Inflate(1, 1).Intersect(area);
            if (top >= oy0 && top < oy1 && !bounds.IsEmpty) blobs.Add(new TextBlob(bounds, bg));
        }
        return (bandMask.Dilate8(), blobs);
    }

    /// <summary>
    /// Turns a union-find forest (roots are always the smallest index of their set, so parent[i] is at most i)
    /// into dense component labels in place. Returns the number of components.
    /// </summary>
    private static int Relabel(int[] parent, int count)
    {
        int n = 0;
        for (int i = 0; i < count; i++) parent[i] = parent[i] == i ? n++ : parent[parent[i]];
        return n;
    }

    /// <summary>
    /// Mean colour of the non-candidate pixels in <paramref name="ring"/> (the blob's surroundings);
    /// false when there are none or they are not near-uniform.
    /// </summary>
    private static bool RingBackground(ReadOnlySpan<uint> px, int stride, RectI band, BitMask candMask, RectI ring, out uint bg)
    {
        bg = 0;
        long sb = 0, sg = 0, sr = 0;
        int n = 0;
        int minB = 255, minG = 255, minR = 255, maxB = 0, maxG = 0, maxR = 0;
        for (int y = ring.Y; y < ring.Bottom; y++)
        {
            var row = px.Slice((band.Y + y) * stride + band.X, band.Width);
            for (int x = ring.X; x < ring.Right; x++)
            {
                if (candMask[x, y]) continue;
                uint p = row[x];
                int b = Px.B(p), g = Px.G(p), r = Px.R(p);
                sb += b; sg += g; sr += r; n++;
                if (b < minB) minB = b; if (b > maxB) maxB = b;
                if (g < minG) minG = g; if (g > maxG) maxG = g;
                if (r < minR) minR = r; if (r > maxR) maxR = r;
            }
        }
        if (n == 0) return false;
        if (maxB - minB > MaxRingSpread || maxG - minG > MaxRingSpread || maxR - minR > MaxRingSpread) return false;
        bg = Px.Pack((int)(sb / n), (int)(sg / n), (int)(sr / n));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Union(int[] parent, int ra, int rb)
    {
        if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
    }

    /// <summary>Growable int array (no per-element allocation).</summary>
    private sealed class IntList(int capacity)
    {
        public int[] Items = new int[Math.Max(16, capacity)];
        public int Count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(int v)
        {
            if (Count == Items.Length) Array.Resize(ref Items, Items.Length * 2);
            Items[Count++] = v;
        }
    }

    /// <summary>Per-component bounding box (exclusive max), area and longest horizontal run.</summary>
    private sealed class Stats
    {
        public readonly int[] MinX, MaxX, MinY, MaxY, Area, MaxRun;

        /// <summary>Arrays come from <paramref name="rent"/> (possibly longer, not cleared); the first count entries are initialised.</summary>
        public Stats(int count, Func<int, int[]> rent)
        {
            MinX = rent(count); MaxX = rent(count); MinY = rent(count);
            MaxY = rent(count); Area = rent(count); MaxRun = rent(count);
            MinX.AsSpan(0, count).Fill(int.MaxValue);
            MinY.AsSpan(0, count).Fill(int.MaxValue);
            MaxX.AsSpan(0, count).Clear(); MaxY.AsSpan(0, count).Clear();
            Area.AsSpan(0, count).Clear(); MaxRun.AsSpan(0, count).Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Include(int c, int x0, int x1, int y)
        {
            if (x0 < MinX[c]) MinX[c] = x0;
            if (x1 > MaxX[c]) MaxX[c] = x1;
            if (y < MinY[c]) MinY[c] = y;
            if (y + 1 > MaxY[c]) MaxY[c] = y + 1;
            Area[c] += x1 - x0;
            if (x1 - x0 > MaxRun[c]) MaxRun[c] = x1 - x0;
        }
    }
}
