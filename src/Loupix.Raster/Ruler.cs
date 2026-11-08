using Loupix.Core.Imaging;

namespace Loupix.Raster;

/// <summary>Screen-ruler measurements: distances between and sizes of flat-coloured UI elements.</summary>
public static class Ruler
{
    /// <summary>Most pixels <see cref="MeasureOuter"/> will walk through looking for an element's border.</summary>
    public const int MaxBorder = 12;

    /// <summary>Consecutive similar pixels that count as "the colour has settled" (outside background).</summary>
    private const int SettleRun = 4;

    /// <summary>
    /// From (x, y), scans both ways along <paramref name="axis"/> while pixels stay within
    /// <paramref name="tolerance"/> (per channel) of the start pixel. End is exclusive.
    /// Between two objects this is the gap; inside a box it is the inner size.
    /// Returns an empty span at the point when it is outside the image.
    /// </summary>
    public static Span Measure(PixelBuffer img, int x, int y, Axis axis, int tolerance = 10)
    {
        ArgumentNullException.ThrowIfNull(img);
        if (!img.Contains(x, y)) return axis == Axis.Horizontal ? new Span(x, x) : new Span(y, y);
        var line = new Line(img, x, y, axis);
        int pos = line.Pos;
        uint start = line[pos];
        int lo = pos, hi = pos + 1;
        while (lo > 0 && Px.Near(line[lo - 1], start, tolerance)) lo--;
        while (hi < line.Length && Px.Near(line[hi], start, tolerance)) hi++;
        return new Span(lo, hi);
    }

    /// <summary>
    /// The outer size of the element under the cursor: like <see cref="Measure"/>, then on each side it
    /// continues through the element's border (up to <see cref="MaxBorder"/> pixels that differ from the
    /// inside colour) until the colour settles onto the outside background, and includes the border.
    /// A side with no recognisable border (the colour never settles) is left at the inner edge.
    /// </summary>
    public static Span MeasureOuter(PixelBuffer img, int x, int y, Axis axis, int tolerance = 10)
    {
        var inner = Measure(img, x, y, axis, tolerance);
        if (inner.Length == 0) return inner;
        var line = new Line(img, x, y, axis);
        int right = BorderWidth(line, inner.End, +1, tolerance);
        int left = BorderWidth(line, inner.Start - 1, -1, tolerance);
        return new Span(inner.Start - left, inner.End + right);
    }

    /// <summary>Number of border pixels starting at <paramref name="from"/> going in <paramref name="dir"/>.</summary>
    private static int BorderWidth(Line line, int from, int dir, int tol)
    {
        for (int k = 0; k <= MaxBorder; k++)
        {
            int p = from + k * dir;
            if (p < 0 || p >= line.Length) return k; // the border runs into the image edge
            if (Settled(line, p, dir, tol)) return k;
        }
        return 0;
    }

    private static bool Settled(Line line, int p, int dir, int tol)
    {
        uint c = line[p];
        for (int i = 1; i < SettleRun; i++)
        {
            int q = p + i * dir;
            if (q < 0 || q >= line.Length) return false; // can't confirm before the image edge: still border
            if (!Px.Near(line[q], c, tol)) return false;
        }
        return true;
    }

    /// <summary>A row or column of the image, addressed by a single coordinate.</summary>
    private readonly ref struct Line
    {
        private readonly ReadOnlySpan<uint> _px;
        private readonly int _origin, _step;

        public Line(PixelBuffer img, int x, int y, Axis axis)
        {
            _px = Px.Pixels(img);
            if (axis == Axis.Horizontal) { _origin = y * img.Width; _step = 1; Length = img.Width; Pos = x; }
            else { _origin = x; _step = img.Width; Length = img.Height; Pos = y; }
        }

        public int Length { get; }
        public int Pos { get; }
        public uint this[int i] => _px[_origin + i * _step];
    }
}
