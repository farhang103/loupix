using Loupix.Core.Imaging;

namespace Loupix.Services.Gif;

/// <summary>
/// Octree colour quantizer. Every pixel is inserted at full 8-bit depth, so a frame with at most
/// <c>maxColors</c> distinct colours is reproduced exactly. Otherwise the least-populated nodes at the
/// deepest level are folded into their parents until the leaf count fits. Nodes live in flat arrays.
/// </summary>
internal sealed class OctreeQuantizer
{
    private const int Depth = 8;

    private int[] _children;   // 8 per node, 0 = none (node 0 is the root, so it is never a child)
    private long[] _count, _r, _g, _b;
    private bool[] _leaf;
    private int[] _paletteIndex;
    private int _nodes;
    private readonly List<int>[] _levels = new List<int>[Depth]; // interior nodes by level (root is level 0)

    private OctreeQuantizer(int capacity)
    {
        _children = new int[capacity * 8];
        _count = new long[capacity];
        _r = new long[capacity];
        _g = new long[capacity];
        _b = new long[capacity];
        _leaf = new bool[capacity];
        _paletteIndex = new int[capacity];
        for (int i = 0; i < Depth; i++) _levels[i] = [];
        NewNode(0); // root
    }

    /// <summary>Returns the palette as packed RGB triplets (length = 3 × colour count) and one palette index per pixel.</summary>
    public static (byte[] Palette, byte[] Indices) Quantize(PixelBuffer frame, int maxColors = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColors, 8); // level 1 has up to 8 nodes and is never folded
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxColors, 256);

        var q = new OctreeQuantizer(1024);
        byte[] px = frame.Bgra;
        int n = frame.Width * frame.Height;

        int lastColor = -1, lastLeaf = 0;
        for (int i = 0, p = 0; i < n; i++, p += 4)
        {
            int color = px[p + 2] << 16 | px[p + 1] << 8 | px[p];
            if (color == lastColor)
            {
                q._count[lastLeaf]++;
                continue;
            }
            lastColor = color;
            lastLeaf = q.Insert(px[p + 2], px[p + 1], px[p]);
        }

        q.Reduce(maxColors);
        byte[] palette = q.BuildPalette();

        var indices = new byte[n];
        lastColor = -1;
        byte lastIndex = 0;
        for (int i = 0, p = 0; i < n; i++, p += 4)
        {
            int color = px[p + 2] << 16 | px[p + 1] << 8 | px[p];
            if (color != lastColor)
            {
                lastColor = color;
                lastIndex = (byte)q.Lookup(px[p + 2], px[p + 1], px[p]);
            }
            indices[i] = lastIndex;
        }
        return (palette, indices);
    }

    private static int ChildSlot(byte r, byte g, byte b, int level)
    {
        int shift = 7 - level;
        return ((r >> shift) & 1) << 2 | ((g >> shift) & 1) << 1 | ((b >> shift) & 1);
    }

    /// <summary>Adds one pixel; returns the leaf it landed in. Colour sums are only kept at leaves until <see cref="Reduce"/>.</summary>
    private int Insert(byte r, byte g, byte b)
    {
        int node = 0;
        for (int level = 0; level < Depth; level++)
        {
            int slot = node * 8 + ChildSlot(r, g, b, level);
            int child = _children[slot];
            if (child == 0)
            {
                child = NewNode(level + 1);
                _children[slot] = child; // NewNode may have reallocated _children; index again
            }
            node = child;
        }
        if (_count[node] == 0)
        {
            _r[node] = r;
            _g[node] = g;
            _b[node] = b;
        }
        _count[node]++;
        return node;
    }

    private int NewNode(int level)
    {
        if (_nodes == _count.Length) Grow();
        int id = _nodes++;
        _leaf[id] = level == Depth;
        if (level < Depth) _levels[level].Add(id);
        return id;
    }

    private void Grow()
    {
        int cap = _count.Length * 2;
        Array.Resize(ref _children, cap * 8);
        Array.Resize(ref _count, cap);
        Array.Resize(ref _r, cap);
        Array.Resize(ref _g, cap);
        Array.Resize(ref _b, cap);
        Array.Resize(ref _leaf, cap);
        Array.Resize(ref _paletteIndex, cap);
    }

    private void Reduce(int maxColors)
    {
        // Leaves hold one exact colour each; turn their (r, g, b) into running sums weighted by count.
        int leaves = 0;
        for (int id = 0; id < _nodes; id++)
        {
            if (!_leaf[id]) continue;
            leaves++;
            _r[id] *= _count[id];
            _g[id] *= _count[id];
            _b[id] *= _count[id];
        }

        // Subtree totals for every interior node, deepest level first.
        for (int level = Depth - 1; level >= 0; level--)
        {
            foreach (int id in _levels[level])
            {
                long c = 0, r = 0, g = 0, b = 0;
                for (int k = 0; k < 8; k++)
                {
                    int ch = _children[id * 8 + k];
                    if (ch == 0) continue;
                    c += _count[ch]; r += _r[ch]; g += _g[ch]; b += _b[ch];
                }
                _count[id] = c; _r[id] = r; _g[id] = g; _b[id] = b;
            }
        }

        // Fold the least-populated nodes of the deepest level into leaves until the palette fits.
        // Each level is exhausted before moving up, so a folded node's children are always leaves.
        for (int level = Depth - 1; level >= 1 && leaves > maxColors; level--)
        {
            var candidates = _levels[level];
            candidates.Sort((a, b) => _count[a].CompareTo(_count[b]));
            foreach (int id in candidates)
            {
                if (leaves <= maxColors) break;
                int childCount = 0;
                for (int k = 0; k < 8; k++)
                {
                    if (_children[id * 8 + k] != 0) childCount++;
                    _children[id * 8 + k] = 0;
                }
                _leaf[id] = true;
                leaves -= childCount - 1;
            }
        }
    }

    private byte[] BuildPalette()
    {
        var palette = new List<byte>(256 * 3);
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            int id = stack.Pop();
            if (_leaf[id])
            {
                long c = _count[id];
                _paletteIndex[id] = palette.Count / 3;
                palette.Add((byte)((_r[id] + c / 2) / c));
                palette.Add((byte)((_g[id] + c / 2) / c));
                palette.Add((byte)((_b[id] + c / 2) / c));
                continue;
            }
            for (int k = 7; k >= 0; k--)
            {
                int ch = _children[id * 8 + k];
                if (ch != 0) stack.Push(ch);
            }
        }
        return [.. palette];
    }

    private int Lookup(byte r, byte g, byte b)
    {
        int node = 0;
        for (int level = 0; !_leaf[node]; level++)
        {
            node = _children[node * 8 + ChildSlot(r, g, b, level)];
        }
        return _paletteIndex[node];
    }
}
