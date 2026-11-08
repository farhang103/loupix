using Loupix.Core.Imaging;

namespace Loupix.Raster;

/// <summary>
/// A packed bit mask covering <see cref="Bounds"/> of an image. The indexer takes image coordinates;
/// anything outside the bounds reads as false.
/// </summary>
public sealed class BitMask
{
    private readonly ulong[] _bits;
    private readonly int _wordsPerRow;

    public BitMask(int x, int y, int width, int height)
    {
        X = x; Y = y;
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        _wordsPerRow = (Width + 63) >> 6;
        _bits = new ulong[_wordsPerRow * Height];
    }

    public BitMask(RectI bounds) : this(bounds.X, bounds.Y, bounds.Width, bounds.Height) { }

    /// <summary>Offset of the mask in image coordinates.</summary>
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public RectI Bounds => new(X, Y, Width, Height);

    public bool this[int x, int y]
    {
        get
        {
            x -= X; y -= Y;
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return false;
            return (_bits[y * _wordsPerRow + (x >> 6)] & (1UL << (x & 63))) != 0;
        }
        set
        {
            x -= X; y -= Y;
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
            ref ulong w = ref _bits[y * _wordsPerRow + (x >> 6)];
            if (value) w |= 1UL << (x & 63); else w &= ~(1UL << (x & 63));
        }
    }

    /// <summary>Number of set pixels.</summary>
    public int Count()
    {
        int n = 0;
        foreach (ulong w in _bits) n += System.Numerics.BitOperations.PopCount(w);
        return n;
    }

    public bool IsEmpty => Array.TrueForAll(_bits, w => w == 0);

    /// <summary>Raw words for row <paramref name="row"/> (mask-relative), bit i = column i.</summary>
    internal Span<ulong> Row(int row) => _bits.AsSpan(row * _wordsPerRow, _wordsPerRow);

    internal int WordsPerRow => _wordsPerRow;

    /// <summary>Sets mask-relative columns [x0, x1) of mask-relative row <paramref name="row"/>.</summary>
    internal void SetRun(int row, int x0, int x1)
    {
        var r = Row(row);
        while (x0 < x1)
        {
            int word = x0 >> 6, bit = x0 & 63;
            int n = Math.Min(64 - bit, x1 - x0);
            ulong m = n == 64 ? ulong.MaxValue : ((1UL << n) - 1) << bit;
            r[word] |= m;
            x0 += n;
        }
    }

    /// <summary>
    /// Copies <see cref="Width"/> bits of <paramref name="src"/>'s row <paramref name="srcRow"/>, starting at its
    /// column <paramref name="srcX"/>, into row <paramref name="dstRow"/> of this mask (all mask-relative).
    /// </summary>
    internal void CopyRow(BitMask src, int srcRow, int srcX, int dstRow)
    {
        var from = src.Row(srcRow);
        var to = Row(dstRow);
        for (int i = 0; i < to.Length; i++)
        {
            int bit = srcX + i * 64;
            int word = bit >> 6, shift = bit & 63;
            ulong v = word < from.Length ? from[word] >> shift : 0;
            if (shift != 0 && word + 1 < from.Length) v |= from[word + 1] << (64 - shift);
            to[i] = v;
        }
        if ((Width & 63) != 0 && to.Length > 0) to[^1] &= (1UL << (Width & 63)) - 1;
    }

    /// <summary>Returns a new mask grown by one pixel in all 8 directions (clipped to the bounds).</summary>
    internal BitMask Dilate8()
    {
        var result = new BitMask(X, Y, Width, Height);
        int wpr = _wordsPerRow;
        if (wpr == 0) return result;
        ulong lastMask = (Width & 63) == 0 ? ulong.MaxValue : (1UL << (Width & 63)) - 1;
        for (int y = 0; y < Height; y++)
        {
            var dst = result.Row(y);
            for (int dy = -1; dy <= 1; dy++)
            {
                int sy = y + dy;
                if ((uint)sy >= (uint)Height) continue;
                var src = Row(sy);
                for (int i = 0; i < wpr; i++)
                {
                    ulong w = src[i];
                    ulong left = (w << 1) | (i > 0 ? src[i - 1] >> 63 : 0);
                    ulong right = (w >> 1) | (i + 1 < wpr ? src[i + 1] << 63 : 0);
                    dst[i] |= w | left | right;
                }
            }
            dst[wpr - 1] &= lastMask;
        }
        return result;
    }
}
