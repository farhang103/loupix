namespace Loupix.Core.Imaging;

/// <summary>An integer pixel rectangle. Right and Bottom are exclusive.</summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectI FromLTRB(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    /// <summary>Normalizes two corner points (in any order) into a rectangle.</summary>
    public static RectI FromPoints(int x1, int y1, int x2, int y2) =>
        FromLTRB(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public RectI Intersect(RectI other)
    {
        int l = Math.Max(X, other.X), t = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right), b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? default : FromLTRB(l, t, r, b);
    }

    public RectI Union(RectI other) =>
        IsEmpty ? other : other.IsEmpty ? this :
        FromLTRB(Math.Min(X, other.X), Math.Min(Y, other.Y), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public RectI Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

    public RectI Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public RectI ClampTo(int width, int height) => Intersect(new RectI(0, 0, width, height));

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
