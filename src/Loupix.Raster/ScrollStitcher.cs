using System.Numerics;
using System.Runtime.InteropServices;
using Loupix.Core.Imaging;

namespace Loupix.Raster;

/// <summary>
/// Builds a scrolling screenshot from frames of the same screen region captured while the page scrolls.
/// <para>
/// Each frame is compared with the previous one. Rows that are identical at the same position at the top and
/// bottom are sticky header/footer bands (estimates only ever shrink, since a coincidentally blank area can look
/// sticky for one pair of frames). Between them, the vertical shift is found by hashing rows (excluding a few
/// scrollbar columns at the right edge), voting for shifts where non-uniform rows match exactly, and verifying
/// candidates with a per-channel tolerance, smallest shift (largest overlap) first. If no hashed candidate
/// verifies, a coarse row-signature search supplies candidates instead (survives re-rendering noise). Matches
/// that rest on too little distinct content, or that would verify one row up or down as well (uniform or
/// vertically invariant areas), are rejected with <see cref="StitchStatus.NoMatch"/> rather than risk a wrong stitch.
/// </para>
/// <para>
/// With <c>reverse</c> the user scrolls up: frames are handled upside down internally, so new content is prepended,
/// the sticky header comes from the last frame and the footer from the first.
/// </para>
/// </summary>
public sealed class ScrollStitcher
{
    private const int ScrollbarMargin = 24;
    private const int Tolerance = 24;
    private const int MaxHashOccurrences = 8;
    private const int MinOverlapRows = 12;
    private const int MinDistinctRows = 3;
    private const int MaxCandidates = 24;
    private const int ChunkRows = 256;
    private const int SignatureBins = 16;

    private readonly bool _reverse;
    private readonly int _maxHeight;
    private readonly List<byte[]> _chunks = [];
    private PixelBuffer? _prev;
    private ulong[] _prevHash = [];
    private bool[] _prevUniform = [];
    private int _w, _h, _cmpPixels, _rows, _footer;
    private bool _limit;

    public ScrollStitcher(bool reverse = false, int maxHeight = 20000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHeight, 1);
        _reverse = reverse;
        _maxHeight = maxHeight;
    }

    /// <summary>Height of the image <see cref="Compose"/> would return now (0 before the first frame).</summary>
    public int Height => _prev is null ? 0 : Math.Min(_maxHeight, _rows + _footer);

    /// <summary>The scroll distance (new rows) found for the last frame; 0 unless it returned Added or LimitReached.</summary>
    public int LastOffset { get; private set; }

    public StitchStatus Add(PixelBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        LastOffset = 0;
        if (_limit) return StitchStatus.LimitReached;

        if (_prev is null)
        {
            _w = frame.Width; _h = frame.Height;
            _cmpPixels = _w > 4 * ScrollbarMargin ? _w - ScrollbarMargin : _w;
            _prev = frame.Clone();
            (_prevHash, _prevUniform) = HashRows(_prev);
            _footer = _h;
            return StitchStatus.First;
        }
        if (frame.Width != _w || frame.Height != _h) return StitchStatus.NoMatch;

        var a = _prev;
        int h = _h;
        var (hashB, uniB) = HashRows(frame);
        var hashA = _prevHash;
        var uniA = _prevUniform;

        // Sticky bands: rows equal at the same position.
        int top = 0;
        while (top < h && (hashA[top] == hashB[top] || RowsNear(a, top, frame, top))) top++;
        if (top == h) return StitchStatus.NoMovement;
        int bot = 0;
        while (bot < h - top && (hashA[h - 1 - bot] == hashB[h - 1 - bot] || RowsNear(a, h - 1 - bot, frame, h - 1 - bot))) bot++;

        int d = FindShift(a, frame, hashA, uniA, hashB, uniB, top, bot);
        if (d <= 0)
        {
            int same = top + bot;
            for (int y = top; y < h - bot; y++) if (hashA[y] == hashB[y] || RowsNear(a, y, frame, y)) same++;
            return same * 50 >= h * 49 ? StitchStatus.NoMovement : StitchStatus.NoMatch;
        }

        int footer = Math.Min(_footer, bot);
        int room = _maxHeight - footer - _rows;
        int fromA = _footer - footer;
        bool limited = fromA + d > room;
        int takeA = Math.Clamp(room, 0, fromA);
        Append(a, h - _footer, h - _footer + takeA);
        int takeB = Math.Clamp(room - takeA, 0, d);
        Append(frame, h - footer - d, h - footer - d + takeB);
        _footer = footer;
        _prev = frame.Clone();
        _prevHash = hashB;
        _prevUniform = uniB;
        LastOffset = d;
        if (limited) { _limit = true; return StitchStatus.LimitReached; }
        return StitchStatus.Added;
    }

    /// <summary>The stitched image: content rows followed by the current footer (flipped back when reversed).</summary>
    public PixelBuffer Compose()
    {
        if (_prev is null) throw new InvalidOperationException("No frames have been added.");
        int stride = _w * 4;
        int content = Math.Min(_rows, _maxHeight);
        int footer = Math.Min(_footer, _maxHeight - content);
        int height = content + footer;
        var result = new PixelBuffer(_w, height);
        for (int i = 0; i < height; i++)
        {
            ReadOnlySpan<byte> src = i < content
                ? _chunks[i / ChunkRows].AsSpan((i % ChunkRows) * stride, stride)
                : Row(_prev, _h - footer + (i - content));
            int dst = _reverse ? height - 1 - i : i;
            src.CopyTo(result.Bgra.AsSpan(dst * stride, stride));
        }
        return result;
    }

    // ---- shift search ------------------------------------------------------------------------------------------

    private int FindShift(PixelBuffer a, PixelBuffer b, ulong[] hashA, bool[] uniA, ulong[] hashB, bool[] uniB, int top, int bot)
    {
        int h = _h;
        int bandEnd = h - bot;
        int maxD = bandEnd - top - MinOverlapRows;
        if (maxD < 1) return 0;

        // Index A's non-uniform rows by hash: head/next linked lists.
        var heads = new Dictionary<ulong, (int Head, int Count)>(bandEnd - top);
        var next = new int[h];
        for (int y = top; y < bandEnd; y++)
        {
            if (uniA[y]) continue;
            if (heads.TryGetValue(hashA[y], out var e)) { next[y] = e.Head; heads[hashA[y]] = (y, e.Count + 1); }
            else { next[y] = -1; heads[hashA[y]] = (y, 1); }
        }
        var votes = new int[maxD + 1];
        for (int y = top; y < bandEnd; y++)
        {
            if (uniB[y] || !heads.TryGetValue(hashB[y], out var e) || e.Count > MaxHashOccurrences) continue;
            for (int ya = e.Head; ya >= 0; ya = next[ya])
            {
                int d = ya - y;
                if (d >= 1 && d <= maxD) votes[d]++;
            }
        }
        var candidates = new List<int>();
        for (int d = 1; d <= maxD; d++) if (votes[d] > 0) candidates.Add(d);
        if (candidates.Count > MaxCandidates)
        {
            candidates.Sort((x, y) => votes[y].CompareTo(votes[x]));
            candidates.RemoveRange(MaxCandidates, candidates.Count - MaxCandidates);
        }
        candidates.Sort();

        int found = 0;
        foreach (int d in candidates)
            if (Verify(a, b, hashA, hashB, uniB, d, top, bandEnd)) { found = d; break; }
        if (found == 0)
        {
            foreach (int d in SignatureCandidates(a, b, top, bandEnd, maxD))
                if (Verify(a, b, hashA, hashB, uniB, d, top, bandEnd)) { found = d; break; }
        }
        if (found == 0) return 0;

        // Enough distinct content in the overlap?
        var distinct = new HashSet<ulong>();
        for (int y = top; y < bandEnd - found && distinct.Count < MinDistinctRows; y++)
            if (!uniB[y]) distinct.Add(hashB[y]);
        if (distinct.Count < MinDistinctRows) return 0;

        // Ambiguous if a neighbouring shift fits just as well (uniform or vertically invariant content).
        if (found > 1 && Verify(a, b, hashA, hashB, uniB, found - 1, top, bandEnd)) return 0;
        if (found < maxD && Verify(a, b, hashA, hashB, uniB, found + 1, top, bandEnd)) return 0;
        return found;
    }

    /// <summary>True when B[y] ≈ A[y + d] across the overlap, allowing a few bad rows on content-rich overlaps.</summary>
    private bool Verify(PixelBuffer a, PixelBuffer b, ulong[] hashA, ulong[] hashB, bool[] uniB, int d, int top, int bandEnd)
    {
        int end = bandEnd - d;
        int overlap = end - top;
        if (overlap < MinOverlapRows) return false;
        int nonUniform = 0;
        for (int y = top; y < end; y++) if (!uniB[y]) nonUniform++;
        int allowedBad = Math.Min(overlap / 100, nonUniform / 20);
        int bad = 0;
        for (int y = top; y < end; y++)
        {
            if (hashB[y] == hashA[y + d] || RowsNear(a, y + d, b, y)) continue;
            if (++bad > allowedBad) return false;
        }
        return true;
    }

    /// <summary>
    /// Fallback candidates: per-row coarse signatures (summed intensity in column bins) compared at every shift.
    /// A row pair agrees when no bin differs by more than an average of 8 levels per channel; a shift scores
    /// agreeing rows minus 10x disagreeing rows, so large consistent overlaps win over small blank ones.
    /// Returns the few best-scoring shifts.
    /// </summary>
    private List<int> SignatureCandidates(PixelBuffer a, PixelBuffer b, int top, int bandEnd, int maxD)
    {
        var sa = Signatures(a);
        var sb = Signatures(b);
        int binW = Math.Max(1, (_cmpPixels + SignatureBins - 1) / SignatureBins);
        int binTol = binW * 3 * 8;
        var scored = new List<(int Score, int D)>();
        for (int d = 1; d <= maxD; d++)
        {
            int score = 0;
            int end = bandEnd - d;
            for (int y = top; y < end; y++)
            {
                int oa = (y + d) * SignatureBins, ob = y * SignatureBins;
                bool agree = true;
                for (int k = 0; k < SignatureBins && agree; k++) agree = Math.Abs(sa[oa + k] - sb[ob + k]) <= binTol;
                score += agree ? 1 : -10;
            }
            scored.Add((score, d));
        }
        scored.Sort((x, y) => x.Score != y.Score ? y.Score.CompareTo(x.Score) : x.D.CompareTo(y.D));
        var result = new List<int>();
        for (int i = 0; i < scored.Count && result.Count < 3; i++) if (scored[i].Score > 0) result.Add(scored[i].D);
        return result;
    }

    private int[] Signatures(PixelBuffer f)
    {
        var sig = new int[_h * SignatureBins];
        int binW = Math.Max(1, (_cmpPixels + SignatureBins - 1) / SignatureBins);
        for (int y = 0; y < _h; y++)
        {
            var row = Row(f, y);
            for (int x = 0; x < _cmpPixels; x++)
            {
                int o = x * 4;
                sig[y * SignatureBins + Math.Min(SignatureBins - 1, x / binW)] += row[o] + row[o + 1] + row[o + 2];
            }
        }
        return sig;
    }

    // ---- rows --------------------------------------------------------------------------------------------------

    private ReadOnlySpan<byte> Row(PixelBuffer f, int y)
    {
        int py = _reverse ? _h - 1 - y : y;
        return f.Bgra.AsSpan(py * _w * 4, _w * 4);
    }

    private ReadOnlySpan<byte> CmpRow(PixelBuffer f, int y) => Row(f, y)[..(_cmpPixels * 4)];

    /// <summary>Hash of each logical row (scrollbar columns excluded) and whether the row is a single colour.</summary>
    private (ulong[] Hash, bool[] Uniform) HashRows(PixelBuffer f)
    {
        var hash = new ulong[_h];
        var uniform = new bool[_h];
        for (int y = 0; y < _h; y++)
        {
            var row = CmpRow(f, y);
            var words = MemoryMarshal.Cast<byte, ulong>(row);
            ulong hsh = 0x9E3779B97F4A7C15UL ^ (ulong)row.Length;
            foreach (ulong v in words) hsh = BitOperations.RotateLeft((hsh ^ v) * 0xFF51AFD7ED558CCDUL, 29);
            for (int i = words.Length * 8; i < row.Length; i++) hsh = BitOperations.RotateLeft((hsh ^ row[i]) * 0xC4CEB9FE1A85EC53UL, 29);
            hash[y] = hsh;

            var px = MemoryMarshal.Cast<byte, uint>(row);
            uint first = px[0] & Px.RgbMask;
            bool uni = true;
            for (int i = 1; i < px.Length && uni; i++) uni = (px[i] & Px.RgbMask) == first;
            uniform[y] = uni;
        }
        return (hash, uniform);
    }

    /// <summary>Rows match when at most a few channels differ by more than <see cref="Tolerance"/>.</summary>
    private bool RowsNear(PixelBuffer a, int ya, PixelBuffer b, int yb)
    {
        var ra = CmpRow(a, ya);
        var rb = CmpRow(b, yb);
        int allowed = Math.Max(3, _cmpPixels / 256);
        int bad = 0, i = 0;
        if (Vector.IsHardwareAccelerated && ra.Length >= Vector<byte>.Count)
        {
            var tol = new Vector<byte>((byte)Tolerance);
            for (; i <= ra.Length - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                var va = new Vector<byte>(ra[i..]);
                var vb = new Vector<byte>(rb[i..]);
                var diff = Vector.Max(va, vb) - Vector.Min(va, vb);
                var over = Vector.GreaterThan(diff, tol);
                if (over == Vector<byte>.Zero) continue;
                for (int k = 0; k < Vector<byte>.Count; k++) if (over[k] != 0) bad++;
                if (bad > allowed) return false;
            }
        }
        for (; i < ra.Length; i++)
            if (Math.Abs(ra[i] - rb[i]) > Tolerance && ++bad > allowed) return false;
        return true;
    }

    private void Append(PixelBuffer f, int y0, int y1)
    {
        int stride = _w * 4;
        for (int y = y0; y < y1; y++)
        {
            int chunk = _rows / ChunkRows;
            if (chunk == _chunks.Count) _chunks.Add(new byte[ChunkRows * stride]);
            Row(f, y).CopyTo(_chunks[chunk].AsSpan((_rows % ChunkRows) * stride, stride));
            _rows++;
        }
    }
}
