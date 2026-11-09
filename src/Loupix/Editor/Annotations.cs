using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Loupix.Editor;

public enum FillMode { None, Translucent, Solid }

/// <summary>What a renderer needs to know besides the annotation itself.</summary>
public sealed class RenderContext
{
    public required EditorDocument Document { get; init; }
    /// <summary>Screen DIPs per image pixel (only used for on-screen hints; 1 when exporting).</summary>
    public double ViewScale { get; init; } = 1;
    public bool Exporting { get; init; }
    /// <summary>The annotation whose text is being edited in a TextBox (its text is not drawn).</summary>
    public Annotation? Editing { get; init; }
    /// <summary>While a backdrop setting is changing, the in-between look to draw instead of the document's own.</summary>
    public BackdropLook? EasedBackdrop { get; init; }
    public EditorImage Base => Document.Base;
    public double Scale => Document.Scale;
}

/// <summary>Base class of every vector object drawn on top of the screenshot. Coordinates are base-image pixels.</summary>
public abstract class Annotation
{
    public Color Color { get; set; } = Color.FromRgb(0xFF, 0x3B, 0x30);
    /// <summary>Size step 1..5.</summary>
    public int Size { get; set; } = 3;
    /// <summary>Tool-specific style variant (arrow style, text style, spotlight shape...).</summary>
    public int Variant { get; set; }
    public FillMode Fill { get; set; }
    public bool HandDrawn { get; set; }
    public int Seed { get; set; } = Random.Shared.Next();

    public abstract string Kind { get; }
    public abstract Rect Bounds { get; }
    public abstract void Render(DrawingContext dc, RenderContext rc);
    public abstract bool HitTest(Point p, double tolerance);
    public abstract IReadOnlyList<Point> Handles { get; }
    public abstract void MoveHandle(int index, Point p, bool constrain);
    public abstract void Translate(Vector v);
    public virtual void Scale(double sx, double sy) { }
    /// <summary>True while the object is too small to keep (e.g. a click without drag).</summary>
    public virtual bool IsDegenerate => Bounds.Width < 2 && Bounds.Height < 2;
    /// <summary>Raster effects sample the base image and are drawn below vector annotations.</summary>
    public virtual bool IsRasterEffect => false;

    public Annotation Clone()
    {
        var c = (Annotation)MemberwiseClone();
        c.CloneDeep();
        return c;
    }

    protected virtual void CloneDeep() { }

    // ----- shared helpers -----
    protected static readonly double[] StrokeSteps = [1.5, 2.5, 4, 6, 9];

    protected double Stroke(RenderContext rc) => StrokeSteps[Math.Clamp(Size, 1, 5) - 1] * rc.Scale;

    protected static Brush Frozen(Brush b) { b.Freeze(); return b; }

    protected static Pen MakePen(Color c, double width, bool round = true)
    {
        var p = new Pen(new SolidColorBrush(c), width)
        {
            StartLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
            EndLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
            LineJoin = PenLineJoin.Round,
        };
        p.Freeze();
        return p;
    }

    public static Color ContrastText(Color bg)
    {
        double l = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B;
        return l > 165 ? Color.FromRgb(0x14, 0x14, 0x14) : Colors.White;
    }

    protected static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-9) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / len2, 0, 1);
        return (p - (a + t * ab)).Length;
    }

    protected static Point Constrain45(Point origin, Point p)
    {
        var v = p - origin;
        double angle = Math.Atan2(v.Y, v.X);
        double snapped = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
        double len = v.Length;
        return origin + new Vector(Math.Cos(snapped) * len, Math.Sin(snapped) * len);
    }

    protected static Typeface LabelFace { get; } = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    protected static Typeface BoldFace { get; } = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    protected static Typeface HandFace { get; } = new(new FontFamily("Segoe Print, Ink Free, Comic Sans MS"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    protected static FormattedText MakeText(string s, Typeface face, double size, Brush brush) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, 1.0);
}

/// <summary>Annotations defined by two corner points (rectangle, oval, spotlight, effects, images).</summary>
public abstract class BoxAnnotation : Annotation
{
    public Point P1 { get; set; }
    public Point P2 { get; set; }

    public Rect Rect => new(P1, P2);
    public override Rect Bounds => Rect;

    public override IReadOnlyList<Point> Handles
    {
        get
        {
            var r = Rect;
            double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            return [r.TopLeft, new(cx, r.Top), r.TopRight, new(r.Right, cy), r.BottomRight, new(cx, r.Bottom), r.BottomLeft, new(r.Left, cy)];
        }
    }

    protected virtual bool KeepAspect => false;

    public override void MoveHandle(int index, Point p, bool constrain)
    {
        var r = Rect;
        double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
        double aspect = r.Height > 0 ? r.Width / r.Height : 1;
        switch (index)
        {
            case 0: l = p.X; t = p.Y; break;
            case 1: t = p.Y; break;
            case 2: rr = p.X; t = p.Y; break;
            case 3: rr = p.X; break;
            case 4: rr = p.X; b = p.Y; break;
            case 5: b = p.Y; break;
            case 6: l = p.X; b = p.Y; break;
            case 7: l = p.X; break;
        }
        if ((constrain || KeepAspect) && index % 2 == 0)
        {
            double w = rr - l, h = b - t;
            if (constrain && !KeepAspect) { double s = Math.Max(Math.Abs(w), Math.Abs(h)); w = Math.Sign(w) * s; h = Math.Sign(h) * s; }
            else h = Math.Sign(h == 0 ? 1 : h) * Math.Abs(w) / aspect;
            if (index == 0) { l = rr - w; t = b - h; }
            else if (index == 2) { rr = l + w; t = b - h; }
            else if (index == 4) { rr = l + w; b = t + h; }
            else { l = rr - w; b = t + h; }
        }
        P1 = new Point(l, t);
        P2 = new Point(rr, b);
    }

    /// <summary>While drawing: P1 is the anchor, P2 follows the mouse.</summary>
    public virtual void DragTo(Point p, bool constrain)
    {
        if (constrain)
        {
            var v = p - P1;
            double s = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
            p = P1 + new Vector(Math.Sign(v.X == 0 ? 1 : v.X) * s, Math.Sign(v.Y == 0 ? 1 : v.Y) * s);
        }
        P2 = p;
    }

    public override void Translate(Vector v) { P1 += v; P2 += v; }
    public override void Scale(double sx, double sy) { P1 = new(P1.X * sx, P1.Y * sy); P2 = new(P2.X * sx, P2.Y * sy); }

    /// <summary>Normalizes P1 to top-left and P2 to bottom-right.</summary>
    public void Normalize() { var r = Rect; P1 = r.TopLeft; P2 = r.BottomRight; }

    public override bool HitTest(Point p, double tol)
    {
        var r = Rect;
        var outer = r; outer.Inflate(tol, tol);
        return outer.Contains(p);
    }
}

public sealed class RectangleAnnotation : BoxAnnotation
{
    public override string Kind => "rect";

    public override void Render(DrawingContext dc, RenderContext rc)
    {
        double w = Stroke(rc);
        var r = Rect;
        double radius = Math.Min(w * 1.2, Math.Min(r.Width, r.Height) / 2);
        Brush? fill = Fill switch
        {
            FillMode.Solid => Frozen(new SolidColorBrush(Color)),
            FillMode.Translucent => Frozen(new SolidColorBrush(Color.FromArgb(70, Color.R, Color.G, Color.B))),
            _ => null,
        };
        if (HandDrawn)
        {
            if (fill != null) dc.DrawRectangle(fill, null, r);
            Rough.Rectangle(dc, r, MakePen(Color, w), Seed);
            return;
        }
        var inset = r;
        if (Fill != FillMode.Solid && r.Width > w && r.Height > w) inset.Inflate(-w / 2, -w / 2);
        dc.DrawRoundedRectangle(fill, Fill == FillMode.Solid ? null : MakePen(Color, w), inset, radius, radius);
    }

    public override bool HitTest(Point p, double tol)
    {
        var r = Rect;
        if (Fill != FillMode.None) { var o = r; o.Inflate(tol, tol); return o.Contains(p); }
        var outer = r; outer.Inflate(tol + 3, tol + 3);
        var inner = r; inner.Inflate(-(tol + 3), -(tol + 3));
        return outer.Contains(p) && (inner.IsEmpty || inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p));
    }
}

public sealed class EllipseAnnotation : BoxAnnotation
{
    public override string Kind => "oval";

    public override void Render(DrawingContext dc, RenderContext rc)
    {
        double w = Stroke(rc);
        var r = Rect;
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        Brush? fill = Fill switch
        {
            FillMode.Solid => Frozen(new SolidColorBrush(Color)),
            FillMode.Translucent => Frozen(new SolidColorBrush(Color.FromArgb(70, Color.R, Color.G, Color.B))),
            _ => null,
        };
        if (HandDrawn)
        {
            if (fill != null) dc.DrawEllipse(fill, null, c, r.Width / 2, r.Height / 2);
            Rough.Ellipse(dc, r, MakePen(Color, w), Seed);
            return;
        }
        double inset = Fill == FillMode.Solid ? 0 : w / 2;
        dc.DrawEllipse(fill, Fill == FillMode.Solid ? null : MakePen(Color, w), c, Math.Max(0, r.Width / 2 - inset), Math.Max(0, r.Height / 2 - inset));
    }

    public override bool HitTest(Point p, double tol)
    {
        var r = Rect;
        double rx = r.Width / 2, ry = r.Height / 2;
        if (rx < 1 || ry < 1) return base.HitTest(p, tol);
        double dx = (p.X - (r.X + rx)) / rx, dy = (p.Y - (r.Y + ry)) / ry;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (Fill != FillMode.None) return d <= 1 + tol / Math.Min(rx, ry);
        return Math.Abs(d - 1) * Math.Min(rx, ry) <= tol + 4;
    }
}

/// <summary>Dims everything outside one or more highlighted areas. All spotlights are drawn together by the renderer.</summary>
public sealed class SpotlightAnnotation : BoxAnnotation
{
    public override string Kind => "spotlight";
    /// <summary>Darkness 1..9.</summary>
    public int Level { get; set; } = 5;
    public bool IsEllipse => Variant == 1;

    public Geometry Shape()
    {
        var r = Rect;
        if (IsEllipse) return new EllipseGeometry(r);
        double radius = Math.Min(8, Math.Min(r.Width, r.Height) / 4);
        return new RectangleGeometry(r, radius, radius);
    }

    // Drawn by DocumentRenderer as a group.
    public override void Render(DrawingContext dc, RenderContext rc) { }
}

public enum EffectMode { Pixelate, Blur, Erase }

/// <summary>Pixelate / blur / erase an area of the base image. Live: it re-samples whatever is under it.</summary>
public sealed class EffectAnnotation : BoxAnnotation
{
    public override string Kind => "effect";
    public EffectMode Mode { get; set; } = EffectMode.Pixelate;
    public bool TextOnly { get; set; }
    public override bool IsRasterEffect => true;

    private (Rect Rect, EffectMode Mode, bool TextOnly, int Size, EditorImage Base)? _cacheKey;
    private BitmapSource? _cache;
    private Rect _cacheRect;

    public override void Render(DrawingContext dc, RenderContext rc)
    {
        var r = Rect;
        r.Intersect(rc.Document.BaseBounds);
        if (r.IsEmpty || r.Width < 1 || r.Height < 1) return;
        var key = (Rect, Mode, TextOnly, Size, rc.Base);
        if (_cacheKey != key || _cache == null)
        {
            (_cache, _cacheRect) = EffectRenderer.Render(rc.Base, r, Mode, TextOnly, Size, rc.Scale);
            _cacheKey = key;
        }
        if (_cache != null) dc.DrawImage(_cache, _cacheRect);
    }

    protected override void CloneDeep() { _cacheKey = null; _cache = null; }
}

/// <summary>A pasted or appended image placed on the canvas.</summary>
public sealed class ImageAnnotation : BoxAnnotation
{
    public override string Kind => "image";
    public required BitmapSource Image { get; set; }
    public double Opacity { get; set; } = 1.0;
    public bool IsCapture { get; set; }
    protected override bool KeepAspect => true;

    public override void Render(DrawingContext dc, RenderContext rc)
    {
        if (Opacity < 1) dc.PushOpacity(Opacity);
        dc.DrawImage(Image, Rect);
        if (Opacity < 1) dc.Pop();
    }
}

public sealed class ArrowAnnotation : Annotation
{
    public override string Kind => "arrow";
    public Point Start { get; set; }
    public Point End { get; set; }
    /// <summary>Perpendicular offset of the curve's midpoint from the chord (0 = straight).</summary>
    public double Bend { get; set; }

    // Variants: 0 tapered (default), 1 classic, 2 double-headed, 3 plain line, 4 super slim.
    public const int Tapered = 0, Classic = 1, Double = 2, Line = 3, Slim = 4;

    public override Rect Bounds { get { var r = new Rect(Start, End); r.Union(MidPoint); return r; } }

    private Vector Normal
    {
        get
        {
            var d = End - Start;
            if (d.Length < 1e-6) return new Vector(0, -1);
            d.Normalize();
            return new Vector(-d.Y, d.X);
        }
    }

    public Point MidPoint => Start + (End - Start) / 2 + Normal * Bend;
    private Point Control => Start + (End - Start) / 2 + Normal * (Bend * 2);

    public override IReadOnlyList<Point> Handles => [Start, MidPoint, End];

    public override void MoveHandle(int index, Point p, bool constrain)
    {
        if (index == 0) Start = constrain ? Constrain45(End, p) : p;
        else if (index == 2) End = constrain ? Constrain45(Start, p) : p;
        else
        {
            var mid = Start + (End - Start) / 2;
            Bend = Vector.Multiply(p - mid, Normal);
            if (Math.Abs(Bend) < 3) Bend = 0;
        }
    }

    public override void Translate(Vector v) { Start += v; End += v; }
    public override void Scale(double sx, double sy) { Start = new(Start.X * sx, Start.Y * sy); End = new(End.X * sx, End.Y * sy); Bend *= (sx + sy) / 2; }
    public override bool IsDegenerate => (End - Start).Length < 4;

    private Point At(double t)
    {
        var c = Control;
        double u = 1 - t;
        return new Point(u * u * Start.X + 2 * u * t * c.X + t * t * End.X, u * u * Start.Y + 2 * u * t * c.Y + t * t * End.Y);
    }

    private List<Point> Sample(int n = 48)
    {
        var pts = new List<Point>(n + 1);
        for (int i = 0; i <= n; i++) pts.Add(At(i / (double)n));
        return pts;
    }

    public override bool HitTest(Point p, double tol)
    {
        var pts = Sample(24);
        for (int i = 1; i < pts.Count; i++)
            if (DistanceToSegment(p, pts[i - 1], pts[i]) <= tol + 6) return true;
        return false;
    }

    public override void Render(DrawingContext dc, RenderContext rc)
    {
        double w = Stroke(rc);
        double len = (End - Start).Length;
        if (len < 1) return;
        var brush = Frozen(new SolidColorBrush(Color));
        var pts = Sample();

        if (HandDrawn)
        {
            var pen = MakePen(Color, w);
            Rough.Polyline(dc, pts, pen, Seed);
            if (Variant != Line) Rough.ArrowHead(dc, pts, pen, w * 3.2 + 6, Seed);
            if (Variant == Double) { var rev = new List<Point>(pts); rev.Reverse(); Rough.ArrowHead(dc, rev, pen, w * 3.2 + 6, Seed + 1); }
            return;
        }

        switch (Variant)
        {
            case Tapered:
                {
                    // Longer arrows get relatively slimmer.
                    double thick = w * 2.3 * Math.Clamp(Math.Sqrt(len / (w * 45)), 0.75, 1.1);
                    double headLen = Math.Min(len * 0.45, thick * 2.9 + 6);
                    double headWidth = headLen * 1.05;
                    var g = TaperedShaft(pts, thick * 0.3, thick, headLen * 0.7);
                    dc.DrawGeometry(brush, null, g);
                    dc.DrawGeometry(brush, null, Head(pts, headLen, headWidth, true));
                    break;
                }
            case Classic:
            case Double:
            case Slim:
                {
                    double sw = Variant == Slim ? Math.Max(1, w * 0.55) : w;
                    double headLen = Math.Min(len * 0.4, Variant == Slim ? sw * 6 + 6 : sw * 3.6 + 5);
                    var shaft = new List<Point>(pts);
                    TrimEnd(shaft, headLen * 0.8);
                    if (Variant == Double) { shaft.Reverse(); TrimEnd(shaft, headLen * 0.8); shaft.Reverse(); }
                    dc.DrawGeometry(null, MakePen(Color, sw), Polyline(shaft));
                    dc.DrawGeometry(brush, null, Head(pts, headLen, headLen * 0.9, false));
                    if (Variant == Double)
                    {
                        var rev = new List<Point>(pts); rev.Reverse();
                        dc.DrawGeometry(brush, null, Head(rev, headLen, headLen * 0.9, false));
                    }
                    break;
                }
            default:
                dc.DrawGeometry(null, MakePen(Color, w), Polyline(pts));
                break;
        }
    }

    private static Geometry Polyline(IReadOnlyList<Point> pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], false, false);
            c.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    private static void TrimEnd(List<Point> pts, double amount)
    {
        while (pts.Count > 2 && amount > 0)
        {
            double seg = (pts[^1] - pts[^2]).Length;
            if (seg > amount)
            {
                var dir = pts[^2] - pts[^1]; dir.Normalize();
                pts[^1] = pts[^1] + dir * amount;
                return;
            }
            amount -= seg;
            pts.RemoveAt(pts.Count - 1);
        }
    }

    private static Geometry TaperedShaft(List<Point> pts, double w0, double w1, double trim)
    {
        var shaft = new List<Point>(pts);
        TrimEnd(shaft, trim);
        int n = shaft.Count;
        var left = new List<Point>(n);
        var right = new List<Point>(n);
        double total = 0;
        var cum = new double[n];
        for (int i = 1; i < n; i++) { total += (shaft[i] - shaft[i - 1]).Length; cum[i] = total; }
        for (int i = 0; i < n; i++)
        {
            Vector d = i == 0 ? shaft[1] - shaft[0] : i == n - 1 ? shaft[n - 1] - shaft[n - 2] : shaft[i + 1] - shaft[i - 1];
            if (d.Length < 1e-9) d = new Vector(1, 0);
            d.Normalize();
            var nrm = new Vector(-d.Y, d.X);
            double t = total > 0 ? cum[i] / total : 1;
            double hw = (w0 + (w1 - w0) * t) / 2;
            left.Add(shaft[i] + nrm * hw);
            right.Add(shaft[i] - nrm * hw);
        }
        right.Reverse();
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(left[0], true, true);
            c.PolyLineTo(left.Skip(1).Concat(right).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    private static Geometry Head(List<Point> pts, double headLen, double headWidth, bool swept)
    {
        var tip = pts[^1];
        // Direction from the point headLen back along the path to the tip.
        Point back = pts[0];
        double acc = 0;
        for (int i = pts.Count - 1; i > 0; i--)
        {
            acc += (pts[i] - pts[i - 1]).Length;
            if (acc >= headLen) { back = pts[i - 1]; break; }
        }
        var d = tip - back;
        if (d.Length < 1e-6) d = new Vector(1, 0);
        d.Normalize();
        var n = new Vector(-d.Y, d.X);
        var baseCenter = tip - d * headLen;
        var a = baseCenter + n * headWidth / 2;
        var b = baseCenter - n * headWidth / 2;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(tip, true, true);
            if (swept)
            {
                var notch = tip - d * headLen * 0.78;
                c.PolyLineTo([a, notch, b], true, true);
            }
            else c.PolyLineTo([a, b], true, true);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Text label: a coloured bubble with an optional pointer, or plain/outlined text.</summary>
public sealed class TextAnnotation : Annotation
{
    public override string Kind => "text";
    public Point Position { get; set; }
    /// <summary>Where the bubble's pointer points. Null = no pointer.</summary>
    public Point? Tip { get; set; }
    public string Text { get; set; } = "";

    // Variants: 0 bubble (+ pointer), 1 plain text with halo, 2 outlined text.
    public const int Bubble = 0, Plain = 1, Outlined = 2;
    private static readonly double[] FontSteps = [11, 14, 18, 24, 34];

    public double FontSize(double scale) => FontSteps[Math.Clamp(Size, 1, 5) - 1] * scale;
    public double Padding(double scale) => Variant == Bubble ? FontSize(scale) * 0.45 : 0;

    public FormattedText Layout(double scale, Brush brush)
    {
        var t = MakeText(string.IsNullOrEmpty(Text) ? " " : this.Text, HandDrawn ? HandFace : LabelFace, FontSize(scale), brush);
        return t;
    }

    private double _lastScale = 1;

    public Rect BoxFor(double scale)
    {
        var ft = Layout(scale, Brushes.Black);
        double pad = Padding(scale);
        double w = Math.Max(ft.WidthIncludingTrailingWhitespace, FontSize(scale) * 0.6);
        return new Rect(Position.X, Position.Y, w + pad * 2, ft.Height + pad * 2);
    }

    public override Rect Bounds
    {
        get
        {
            var r = BoxFor(_lastScale);
            if (Tip is Point t && Variant == Bubble) r.Union(t);
            return r;
        }
    }

    public override IReadOnlyList<Point> Handles
    {
        get
        {
            var r = BoxFor(_lastScale);
            var list = new List<Point> { r.TopLeft, r.BottomRight };
            if (Variant == Bubble) list.Add(Tip ?? new Point(r.Left + r.Width / 2, r.Bottom + FontSize(_lastScale) * 1.2));
            return list;
        }
    }

    public override void MoveHandle(int index, Point p, bool constrain)
    {
        if (index == 0) Position = p;
        else if (index == 1)
        {
            // Bottom-right handle resizes the font.
            var r = BoxFor(_lastScale);
            double factor = Math.Max(0.2, (p.Y - r.Top) / Math.Max(1, r.Height));
            double target = FontSize(_lastScale) * factor;
            int best = 1;
            for (int i = 0; i < FontSteps.Length; i++) if (Math.Abs(FontSteps[i] * _lastScale - target) < Math.Abs(FontSteps[best - 1] * _lastScale - target)) best = i + 1;
            Size = best;
        }
        else Tip = BoxFor(_lastScale).Contains(p) ? null : p;
    }

    public override void Translate(Vector v) { Position += v; if (Tip is Point t) Tip = t + v; }
    public override void Scale(double sx, double sy) { Position = new(Position.X * sx, Position.Y * sy); if (Tip is Point t) Tip = new(t.X * sx, t.Y * sy); }
    public override bool IsDegenerate => false;

    public override bool HitTest(Point p, double tol)
    {
        var r = BoxFor(_lastScale);
        r.Inflate(tol, tol);
        if (r.Contains(p)) return true;
        return Tip is Point t && Variant == Bubble && DistanceToSegment(p, Center(BoxFor(_lastScale)), t) < tol + 6;
    }

    private static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    public Geometry BubbleGeometry(double scale)
    {
        var r = BoxFor(scale);
        double radius = FontSize(scale) * 0.32;
        Geometry box = new RectangleGeometry(r, radius, radius);
        if (Tip is not Point tip || r.Contains(tip)) return box;

        // Triangle from the nearest bubble side towards the tip.
        var c = Center(r);
        var d = tip - c;
        double baseHalf = Math.Min(FontSize(scale) * 0.45, Math.Min(r.Width, r.Height) * 0.3);
        Point b1, b2;
        if (Math.Abs(d.X) * r.Height > Math.Abs(d.Y) * r.Width)
        {
            double x = d.X > 0 ? r.Right - radius : r.Left + radius;
            double y = Math.Clamp(c.Y + d.Y * 0.25, r.Top + radius + baseHalf, r.Bottom - radius - baseHalf);
            if (r.Height < (radius + baseHalf) * 2) y = c.Y;
            b1 = new Point(x, y - baseHalf); b2 = new Point(x, y + baseHalf);
        }
        else
        {
            double y = d.Y > 0 ? r.Bottom - radius : r.Top + radius;
            double x = Math.Clamp(c.X + d.X * 0.25, r.Left + radius + baseHalf, r.Right - radius - baseHalf);
            if (r.Width < (radius + baseHalf) * 2) x = c.X;
            b1 = new Point(x - baseHalf, y); b2 = new Point(x + baseHalf, y);
        }
        var tri = new StreamGeometry();
        using (var ctx = tri.Open())
        {
            ctx.BeginFigure(b1, true, true);
            ctx.PolyLineTo([tip, b2], true, true);
        }
        return new CombinedGeometry(GeometryCombineMode.Union, box, tri);
    }

    public override void Render(DrawingContext dc, RenderContext rc)
    {
        _lastScale = rc.Scale;
        bool editing = ReferenceEquals(rc.Editing, this);
        double fs = FontSize(rc.Scale);
        double pad = Padding(rc.Scale);
        switch (Variant)
        {
            case Bubble:
                {
                    var geo = BubbleGeometry(rc.Scale);
                    var bg = Frozen(new SolidColorBrush(Color));
                    if (HandDrawn) Rough.Rectangle(dc, BoxFor(rc.Scale), MakePen(Color, fs * 0.12), Seed, bg);
                    else dc.DrawGeometry(bg, null, geo);
                    if (!editing)
                        dc.DrawText(Layout(rc.Scale, Frozen(new SolidColorBrush(ContrastText(Color)))), new Point(Position.X + pad, Position.Y + pad));
                    break;
                }
            case Outlined:
                {
                    if (editing) break;
                    var ft = Layout(rc.Scale, Brushes.Black);
                    var geo = ft.BuildGeometry(Position);
                    var halo = new Pen(new SolidColorBrush(ContrastText(Color) == Colors.White ? Colors.White : Color.FromRgb(20, 20, 20)), fs * 0.18) { LineJoin = PenLineJoin.Round };
                    halo.Freeze();
                    dc.DrawGeometry(null, halo, geo);
                    dc.DrawGeometry(Frozen(new SolidColorBrush(Color)), null, geo);
                    break;
                }
            default:
                {
                    if (editing) break;
                    var ft = Layout(rc.Scale, Frozen(new SolidColorBrush(Color)));
                    // Subtle shadow keeps plain text readable on busy backgrounds.
                    var shadow = Layout(rc.Scale, Frozen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0))));
                    dc.DrawText(shadow, new Point(Position.X + fs * 0.04, Position.Y + fs * 0.06));
                    dc.DrawText(ft, Position);
                    break;
                }
        }
    }
}

public sealed class FreehandAnnotation : Annotation
{
    public override string Kind => "freehand";
    public List<Point> Points { get; private set; } = new();
    /// <summary>Variant 0 = even stroke, 1 = variable (tapered ends).</summary>
    public int Smoothness { get; set; } = 2;

    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0) return Rect.Empty;
            var r = new Rect(Points[0], Points[0]);
            foreach (var p in Points) r.Union(p);
            return r;
        }
    }

    public override IReadOnlyList<Point> Handles => [];
    public override void MoveHandle(int index, Point p, bool constrain) { }
    public override void Translate(Vector v) { for (int i = 0; i < Points.Count; i++) Points[i] += v; }
    public override void Scale(double sx, double sy) { for (int i = 0; i < Points.Count; i++) Points[i] = new(Points[i].X * sx, Points[i].Y * sy); }
    public override bool IsDegenerate => Points.Count == 0;
    protected override void CloneDeep() => Points = new List<Point>(Points);

    public void AddPoint(Point p)
    {
        if (Points.Count > 0 && (p - Points[^1]).Length < 0.75) return;
        Points.Add(p);
    }

    public override bool HitTest(Point p, double tol)
    {
        if (Points.Count == 1) return (p - Points[0]).Length < tol + 6;
        for (int i = 1; i < Points.Count; i++)
            if (DistanceToSegment(p, Points[i - 1], Points[i]) <= tol + 5) return true;
        return false;
    }

    private List<Point> Smoothed()
    {
        var pts = Points;
        for (int pass = 0; pass < Smoothness && pts.Count > 2; pass++)
        {
            var next = new List<Point>(pts.Count) { pts[0] };
            for (int i = 1; i < pts.Count - 1; i++)
                next.Add(new Point((pts[i - 1].X + 2 * pts[i].X + pts[i + 1].X) / 4, (pts[i - 1].Y + 2 * pts[i].Y + pts[i + 1].Y) / 4));
            next.Add(pts[^1]);
            pts = next;
        }
        return pts;
    }

    public override void Render(DrawingContext dc, RenderContext rc)
    {
        double w = Stroke(rc);
        if (Points.Count == 1)
        {
            dc.DrawEllipse(Frozen(new SolidColorBrush(Color)), null, Points[0], w / 2, w / 2);
            return;
        }
        var pts = Smoothed();
        if (Variant == 1 && pts.Count > 3)
        {
            // Variable width: taper the first and last 20% of the stroke.
            var brush = Frozen(new SolidColorBrush(Color));
            int n = pts.Count;
            for (int i = 1; i < n; i++)
            {
                double t = i / (double)(n - 1);
                double f = Math.Min(1, Math.Min(t, 1 - t) / 0.2) * 0.75 + 0.25;
                dc.DrawLine(MakePen(Color, w * f), pts[i - 1], pts[i]);
            }
            return;
        }
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], false, false);
            if (pts.Count > 2)
            {
                var beziers = new List<Point>();
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    var p0 = pts[Math.Max(0, i - 1)];
                    var p1 = pts[i];
                    var p2 = pts[i + 1];
                    var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
                    beziers.Add(p1 + (p2 - p0) / 6);
                    beziers.Add(p2 - (p3 - p1) / 6);
                    beziers.Add(p2);
                }
                c.PolyBezierTo(beziers, true, true);
            }
            else c.LineTo(pts[1], true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, MakePen(Color, w), g);
    }
}

/// <summary>Text highlighter: a straight marker band blended with multiply so the text underneath stays crisp.</summary>
public sealed class HighlighterAnnotation : Annotation
{
    public override string Kind => "highlighter";
    public Point Start { get; set; }
    public Point End { get; set; }
    private static readonly double[] Heights = [8, 12, 16, 22, 30];
    /// <summary>Variant 0 = flat caps, 1 = round caps.</summary>
    public double Height(double scale) => Heights[Math.Clamp(Size, 1, 5) - 1] * scale;

    private double _lastScale = 1;

    public override Rect Bounds { get { var r = new Rect(Start, End); r.Inflate(Height(_lastScale) / 2, Height(_lastScale) / 2); return r; } }
    public override IReadOnlyList<Point> Handles => [Start, End];
    public override void MoveHandle(int index, Point p, bool constrain)
    {
        if (index == 0) Start = constrain ? new Point(p.X, End.Y) : p;
        else End = constrain ? new Point(p.X, Start.Y) : p;
    }
    public override void Translate(Vector v) { Start += v; End += v; }
    public override void Scale(double sx, double sy) { Start = new(Start.X * sx, Start.Y * sy); End = new(End.X * sx, End.Y * sy); }
    public override bool IsDegenerate => (End - Start).Length < 3;
    public override bool HitTest(Point p, double tol) => DistanceToSegment(p, Start, End) <= Height(_lastScale) / 2 + tol;

    public Geometry Band(double scale)
    {
        double h = Height(scale);
        var d = End - Start;
        if (d.Length < 1e-6) d = new Vector(1, 0);
        d.Normalize();
        var n = new Vector(-d.Y, d.X) * (h / 2);
        if (Variant == 1)
        {
            var pen = new Pen(Brushes.Black, h) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            return new LineGeometry(Start, End).GetWidenedPathGeometry(pen);
        }
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(Start + n, true, true);
            c.PolyLineTo([End + n, End - n, Start - n], true, true);
        }
        return g;
    }

    private (Rect, Color, EditorImage)? _key;
    private BitmapSource? _multiplied;
    private Rect _multipliedRect;

    public override void Render(DrawingContext dc, RenderContext rc)
    {
        _lastScale = rc.Scale;
        var band = Band(rc.Scale);
        var bounds = band.Bounds;
        bounds.Intersect(rc.Document.BaseBounds);
        // Multiply blend over the base image; plain translucent colour elsewhere (expanded canvas).
        var tint = Frozen(new SolidColorBrush(Color.FromArgb(110, Color.R, Color.G, Color.B)));
        if (bounds.IsEmpty) { dc.DrawGeometry(tint, null, band); return; }
        var key = (bounds, Color, rc.Base);
        if (_key != key || _multiplied == null)
        {
            (_multiplied, _multipliedRect) = EffectRenderer.Multiply(rc.Base, bounds, Color);
            _key = key;
        }
        dc.PushClip(band);
        dc.DrawImage(_multiplied, _multipliedRect);
        dc.Pop();
    }

    protected override void CloneDeep() { _key = null; _multiplied = null; }
}

/// <summary>Numbered step marker.</summary>
public sealed class CounterAnnotation : Annotation
{
    public override string Kind => "counter";
    public Point Center { get; set; }
    public int Number { get; set; } = 1;
    private static readonly double[] Radii = [9, 12, 15, 19, 25];
    public double Radius(double scale) => Radii[Math.Clamp(Size, 1, 5) - 1] * scale;
    private double _lastScale = 1;

    public override Rect Bounds { get { double r = Radius(_lastScale); return new Rect(Center.X - r, Center.Y - r, r * 2, r * 2); } }
    public override IReadOnlyList<Point> Handles => [];
    public override void MoveHandle(int index, Point p, bool constrain) { }
    public override void Translate(Vector v) => Center += v;
    public override void Scale(double sx, double sy) => Center = new(Center.X * sx, Center.Y * sy);
    public override bool IsDegenerate => false;
    public override bool HitTest(Point p, double tol) => (p - Center).Length <= Radius(_lastScale) + tol;

    public override void Render(DrawingContext dc, RenderContext rc)
    {
        _lastScale = rc.Scale;
        double r = Radius(rc.Scale);
        var shadow = Frozen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)));
        dc.DrawEllipse(shadow, null, Center + new Vector(0, r * 0.08), r * 1.02, r * 1.02);
        dc.DrawEllipse(Frozen(new SolidColorBrush(Color)), MakePen(Colors.White, Math.Max(1, r * 0.12)), Center, r, r);
        var ft = MakeText(Number.ToString(CultureInfo.InvariantCulture), BoldFace, r * (Number >= 10 ? 0.95 : 1.15), Frozen(new SolidColorBrush(ContrastText(Color))));
        dc.DrawText(ft, new Point(Center.X - ft.Width / 2, Center.Y - ft.Height / 2));
    }
}

/// <summary>Magnifier callout: a circle of the image shown enlarged elsewhere.</summary>
public sealed class MagnifierAnnotation : Annotation
{
    public override string Kind => "magnifier";
    public Point Source { get; set; }
    public double SourceRadius { get; set; } = 20;
    public Point Target { get; set; }
    /// <summary>Variant 0 = 2×, 1 = 3×, 2 = 4×.</summary>
    public double Zoom => Variant switch { 1 => 3, 2 => 4, _ => 2 };
    public double TargetRadius => SourceRadius * Zoom;

    public override Rect Bounds
    {
        get
        {
            var a = new Rect(Source.X - SourceRadius, Source.Y - SourceRadius, SourceRadius * 2, SourceRadius * 2);
            a.Union(new Rect(Target.X - TargetRadius, Target.Y - TargetRadius, TargetRadius * 2, TargetRadius * 2));
            return a;
        }
    }

    public override IReadOnlyList<Point> Handles => [Source, new Point(Source.X + SourceRadius, Source.Y), Target];

    public override void MoveHandle(int index, Point p, bool constrain)
    {
        if (index == 0) Source = p;
        else if (index == 1) SourceRadius = Math.Max(4, (p - Source).Length);
        else Target = p;
    }

    public override void Translate(Vector v) { Source += v; Target += v; }
    public override void Scale(double sx, double sy) { Source = new(Source.X * sx, Source.Y * sy); Target = new(Target.X * sx, Target.Y * sy); SourceRadius *= sx; }
    public override bool IsDegenerate => false;
    public override bool HitTest(Point p, double tol) => (p - Target).Length <= TargetRadius + tol || (p - Source).Length <= SourceRadius + tol;

    public override void Render(DrawingContext dc, RenderContext rc)
    {
        double border = Math.Max(2, 3 * rc.Scale);
        var ringPen = MakePen(Colors.White, border);
        var linePen = MakePen(Color.FromArgb(160, 255, 255, 255), Math.Max(1, rc.Scale));
        // Connector tangents.
        var d = Target - Source;
        if (d.Length > SourceRadius + TargetRadius)
        {
            d.Normalize();
            var n = new Vector(-d.Y, d.X);
            dc.DrawLine(linePen, Source + n * SourceRadius, Target + n * TargetRadius);
            dc.DrawLine(linePen, Source - n * SourceRadius, Target - n * TargetRadius);
        }
        dc.DrawEllipse(null, MakePen(Color.FromArgb(80, 0, 0, 0), border + 2), Source, SourceRadius, SourceRadius);
        dc.DrawEllipse(null, ringPen, Source, SourceRadius, SourceRadius);

        var clip = new EllipseGeometry(Target, TargetRadius, TargetRadius);
        dc.DrawEllipse(Frozen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0))), null, Target + new Vector(0, TargetRadius * 0.04), TargetRadius + border, TargetRadius + border);
        dc.PushClip(clip);
        dc.DrawRectangle(Brushes.White, null, clip.Bounds);
        // Crisp pixels: draw through a DrawingGroup that forces nearest-neighbour scaling.
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        using (var gc = group.Open())
        {
            gc.PushTransform(new MatrixTransform(Zoom, 0, 0, Zoom, Target.X - Source.X * Zoom, Target.Y - Source.Y * Zoom));
            gc.DrawImage(rc.Base.Bitmap, rc.Document.BaseBounds);
            gc.Pop();
        }
        dc.DrawDrawing(group);
        dc.Pop();
        dc.DrawEllipse(null, ringPen, Target, TargetRadius, TargetRadius);
    }
}

/// <summary>An imprinted measurement (ruler) or guide line.</summary>
public sealed class MeasureAnnotation : Annotation
{
    public override string Kind => "measure";
    public Point Start { get; set; }
    public Point End { get; set; }
    public string Label { get; set; } = "";
    public bool IsGuide { get; set; }

    /// <summary>The ruler line for a drag: straight along whichever axis moved further, snapped to whole pixels.</summary>
    public static (Point From, Point To, double Length) FromDrag(Point from, Point to)
    {
        if (Math.Abs(to.X - from.X) >= Math.Abs(to.Y - from.Y))
        {
            double y = Math.Floor(from.Y) + 0.5, x1 = Math.Round(from.X), x2 = Math.Round(to.X);
            return (new Point(x1, y), new Point(x2, y), Math.Abs(x2 - x1));
        }
        double x = Math.Floor(from.X) + 0.5, y1 = Math.Round(from.Y), y2 = Math.Round(to.Y);
        return (new Point(x, y1), new Point(x, y2), Math.Abs(y2 - y1));
    }

    public override Rect Bounds => new(Start, End);
    public override IReadOnlyList<Point> Handles => [];
    public override void MoveHandle(int index, Point p, bool constrain) { }
    public override void Translate(Vector v) { Start += v; End += v; }
    public override void Scale(double sx, double sy) { Start = new(Start.X * sx, Start.Y * sy); End = new(End.X * sx, End.Y * sy); }
    public override bool IsDegenerate => false;
    public override bool HitTest(Point p, double tol) => DistanceToSegment(p, Start, End) <= tol + 4;

    public override void Render(DrawingContext dc, RenderContext rc)
    {
        double s = rc.Scale;
        var pen = MakePen(Color, Math.Max(1, s), round: false);
        if (IsGuide)
        {
            var dashed = new Pen(new SolidColorBrush(Color), Math.Max(1, s)) { DashStyle = new DashStyle([4, 3], 0) };
            dashed.Freeze();
            dc.DrawLine(dashed, Start, End);
            return;
        }
        dc.DrawLine(pen, Start, End);
        var d = End - Start;
        if (d.Length < 1e-6) return;
        d.Normalize();
        var n = new Vector(-d.Y, d.X) * (4 * s);
        dc.DrawLine(pen, Start - n, Start + n);
        dc.DrawLine(pen, End - n, End + n);
        if (string.IsNullOrEmpty(Label)) return;
        var ft = MakeText(Label, LabelFace, 11 * s, Brushes.White);
        var mid = Start + (End - Start) / 2;
        var box = new Rect(mid.X - ft.Width / 2 - 4 * s, mid.Y - ft.Height / 2 - 1 * s, ft.Width + 8 * s, ft.Height + 2 * s);
        bool horizontal = Math.Abs(d.X) > Math.Abs(d.Y);
        box.Offset(horizontal ? 0 : box.Width / 2 + 6 * s, horizontal ? -(box.Height / 2 + 6 * s) : 0);
        dc.DrawRoundedRectangle(Frozen(new SolidColorBrush(Color)), null, box, 3 * s, 3 * s);
        dc.DrawText(ft, new Point(box.X + 4 * s, box.Y + 1 * s));
    }
}

/// <summary>Deterministic "hand-drawn" strokes (slightly wobbly double lines).</summary>
public static class Rough
{
    private static Point J(Random r, Point p, double amount) => new(p.X + (r.NextDouble() - 0.5) * amount, p.Y + (r.NextDouble() - 0.5) * amount);

    private static void WobblyLine(DrawingContext dc, Point a, Point b, Pen pen, Random r)
    {
        double len = (b - a).Length;
        double amount = Math.Min(6, len * 0.02 + 1.5);
        for (int pass = 0; pass < 2; pass++)
        {
            var mid1 = a + (b - a) * (0.25 + r.NextDouble() * 0.1);
            var mid2 = a + (b - a) * (0.65 + r.NextDouble() * 0.1);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(J(r, a, amount), false, false);
                c.BezierTo(J(r, mid1, amount), J(r, mid2, amount), J(r, b, amount), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, pass == 0 ? pen : new Pen(pen.Brush, pen.Thickness * 0.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        }
    }

    public static void Rectangle(DrawingContext dc, Rect rect, Pen pen, int seed, Brush? fill = null)
    {
        var r = new Random(seed);
        if (fill != null) dc.DrawRoundedRectangle(fill, null, rect, 4, 4);
        WobblyLine(dc, rect.TopLeft, rect.TopRight, pen, r);
        WobblyLine(dc, rect.TopRight, rect.BottomRight, pen, r);
        WobblyLine(dc, rect.BottomRight, rect.BottomLeft, pen, r);
        WobblyLine(dc, rect.BottomLeft, rect.TopLeft, pen, r);
    }

    public static void Ellipse(DrawingContext dc, Rect rect, Pen pen, int seed)
    {
        var r = new Random(seed);
        var c = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        for (int pass = 0; pass < 2; pass++)
        {
            var g = new StreamGeometry();
            double start = r.NextDouble() * Math.PI * 2;
            double overshoot = 0.2 + r.NextDouble() * 0.2;
            using (var ctx = g.Open())
            {
                int n = 36;
                for (int i = 0; i <= n; i++)
                {
                    double a = start + (Math.PI * 2 + overshoot) * i / n;
                    double wob = 1 + (r.NextDouble() - 0.5) * 0.03;
                    var p = new Point(c.X + Math.Cos(a) * rect.Width / 2 * wob, c.Y + Math.Sin(a) * rect.Height / 2 * wob);
                    if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, true);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pass == 0 ? pen : new Pen(pen.Brush, pen.Thickness * 0.6) { LineJoin = PenLineJoin.Round }, g);
        }
    }

    public static void Polyline(DrawingContext dc, IReadOnlyList<Point> pts, Pen pen, int seed)
    {
        var r = new Random(seed);
        int step = Math.Max(1, pts.Count / 4);
        for (int i = step; i < pts.Count + step - 1; i += step)
            WobblyLine(dc, pts[i - step], pts[Math.Min(i, pts.Count - 1)], pen, r);
    }

    public static void ArrowHead(DrawingContext dc, IReadOnlyList<Point> pts, Pen pen, double size, int seed)
    {
        var r = new Random(seed);
        var tip = pts[^1];
        var back = pts[Math.Max(0, pts.Count - 6)];
        var d = tip - back;
        if (d.Length < 1e-6) return;
        d.Normalize();
        var n = new Vector(-d.Y, d.X);
        WobblyLine(dc, tip, tip - d * size + n * size * 0.55, pen, r);
        WobblyLine(dc, tip, tip - d * size - n * size * 0.55, pen, r);
    }
}
