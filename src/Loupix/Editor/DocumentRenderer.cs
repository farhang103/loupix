using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Loupix.Editor;

public static class BackdropPresets
{
    public sealed record Preset(string Name, Color A, Color B, bool Solid = false);

    public static readonly IReadOnlyList<Preset> All =
    [
        new("Violet", Color.FromRgb(0x7C, 0x5C, 0xFF), Color.FromRgb(0x2E, 0xC5, 0xFF)),
        new("Sunset", Color.FromRgb(0xFF, 0x5F, 0x6D), Color.FromRgb(0xFF, 0xC3, 0x71)),
        new("Mint", Color.FromRgb(0x43, 0xE9, 0x7B), Color.FromRgb(0x38, 0xF9, 0xD7)),
        new("Ocean", Color.FromRgb(0x4F, 0xAC, 0xFE), Color.FromRgb(0x00, 0xF2, 0xFE)),
        new("Peach", Color.FromRgb(0xF6, 0xD3, 0x65), Color.FromRgb(0xFD, 0xA0, 0x85)),
        new("Dusk", Color.FromRgb(0x30, 0xCF, 0xD0), Color.FromRgb(0x33, 0x08, 0x67)),
        new("Rose", Color.FromRgb(0xF0, 0x93, 0xFB), Color.FromRgb(0xF5, 0x57, 0x6C)),
        new("Forest", Color.FromRgb(0x13, 0x4E, 0x5E), Color.FromRgb(0x71, 0xB2, 0x80)),
        new("Graphite", Color.FromRgb(0x43, 0x43, 0x43), Color.FromRgb(0x0B, 0x0B, 0x0B)),
        new("Paper", Color.FromRgb(0xEE, 0xF0, 0xF4), Color.FromRgb(0xEE, 0xF0, 0xF4), true),
        new("White", Colors.White, Colors.White, true),
        new("None", Colors.Transparent, Colors.Transparent, true),
    ];

    public static Brush BrushFor(int index)
    {
        var p = All[Math.Clamp(index, 0, All.Count - 1)];
        Brush b = p.Solid ? new SolidColorBrush(p.A) : new LinearGradientBrush(p.A, p.B, new Point(0, 0), new Point(1, 1));
        b.Freeze();
        return b;
    }
}

/// <summary>A backdrop as it appears on screen. The editor eases between two of these when a setting changes.</summary>
public readonly record struct BackdropLook(Rect Outer, Color A, Color B, double Radius, double Shadow)
{
    /// <summary>The look a document should end up with. No backdrop is an invisible one that hugs the canvas.</summary>
    public static BackdropLook Of(EditorDocument doc)
    {
        if (doc.Backdrop is not { } bd) return new BackdropLook(doc.Canvas, Colors.Transparent, Colors.Transparent, 0, 0);
        var preset = BackdropPresets.All[Math.Clamp(bd.Preset, 0, BackdropPresets.All.Count - 1)];
        return new BackdropLook(DocumentRenderer.OuterBounds(doc), preset.A, preset.B, bd.Radius * doc.Scale, bd.Shadow);
    }

    public static BackdropLook Lerp(BackdropLook from, BackdropLook to, double t)
    {
        double L(double a, double b) => a + (b - a) * t;
        var outer = new Rect(L(from.Outer.X, to.Outer.X), L(from.Outer.Y, to.Outer.Y), L(from.Outer.Width, to.Outer.Width), L(from.Outer.Height, to.Outer.Height));
        return new BackdropLook(outer, Mix(from.A, to.A, t), Mix(from.B, to.B, t), L(from.Radius, to.Radius), L(from.Shadow, to.Shadow));
    }

    private static Color Mix(Color from, Color to, double t)
    {
        // Fading in or out keeps the visible color, instead of passing through the white hidden in "transparent".
        if (from.A == 0) from = Color.FromArgb(0, to.R, to.G, to.B);
        if (to.A == 0) to = Color.FromArgb(0, from.R, from.G, from.B);
        // Rounds toward the target: on fast screens each step is tiny, and plain rounding would stall just short of it.
        byte L(byte a, byte b) => (byte)(b > a ? Math.Ceiling(a + (b - a) * t) : Math.Floor(a + (b - a) * t));
        return Color.FromArgb(L(from.A, to.A), L(from.R, to.R), L(from.G, to.G), L(from.B, to.B));
    }

    public bool IsCloseTo(BackdropLook other)
    {
        static bool Near(Color a, Color b) => (a.A <= 2 && b.A <= 2) || Math.Abs(a.A - b.A) <= 2 && Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2;
        return Math.Abs(Outer.X - other.Outer.X) < 0.5 && Math.Abs(Outer.Y - other.Outer.Y) < 0.5
            && Math.Abs(Outer.Width - other.Outer.Width) < 0.5 && Math.Abs(Outer.Height - other.Outer.Height) < 0.5
            && Math.Abs(Radius - other.Radius) < 0.25 && Math.Abs(Shadow - other.Shadow) < 0.005
            && Near(A, other.A) && Near(B, other.B);
    }

    public Brush MakeBrush()
    {
        Brush brush = A == B ? new SolidColorBrush(A) : new LinearGradientBrush(A, B, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }
}

public static class DocumentRenderer
{
    /// <summary>The full area that gets exported: the canvas, plus backdrop padding when a backdrop is on.</summary>
    public static Rect OuterBounds(EditorDocument doc)
    {
        var canvas = doc.Canvas;
        if (doc.Backdrop is not { } bd) return canvas;
        var outer = canvas;
        double pad = bd.Padding * doc.Scale;
        outer.Inflate(pad, pad);
        double? aspect = bd.Aspect switch { "16:9" => 16.0 / 9, "4:3" => 4.0 / 3, "1:1" => 1.0, "3:2" => 1.5, "9:16" => 9.0 / 16, _ => null };
        if (aspect is double a)
        {
            double w = outer.Width, h = outer.Height;
            if (w / h < a) w = h * a; else h = w / a;
            outer = new Rect(outer.X + (outer.Width - w) / 2, outer.Y + (outer.Height - h) / 2, w, h);
        }
        return new Rect(Math.Floor(outer.X), Math.Floor(outer.Y), Math.Ceiling(outer.Width), Math.Ceiling(outer.Height));
    }

    public static void Render(DrawingContext dc, RenderContext rc, bool showCanvasPattern = false)
    {
        var doc = rc.Document;
        var canvas = doc.Canvas;
        bool clipped = false;

        var look = rc.EasedBackdrop ?? (doc.Backdrop != null ? BackdropLook.Of(doc) : null);
        if (look is { } shown)
        {
            dc.DrawRectangle(shown.MakeBrush(), null, shown.Outer);
            DrawShadow(dc, canvas, shown.Radius, shown.Shadow, doc.Scale);
            dc.PushClip(new RectangleGeometry(canvas, shown.Radius, shown.Radius));
            clipped = true;
        }

        if (showCanvasPattern && canvas != doc.BaseBounds) dc.DrawRectangle(Checker(), null, canvas);

        dc.DrawImage(doc.Base.Bitmap, doc.BaseBounds);

        // Appended captures and pasted images sit directly on the canvas.
        foreach (var a in doc.Annotations) if (a is ImageAnnotation) a.Render(dc, rc);
        foreach (var a in doc.Annotations) if (a.IsRasterEffect || a is HighlighterAnnotation) a.Render(dc, rc);

        var spots = doc.Annotations.OfType<SpotlightAnnotation>().ToList();
        if (spots.Count > 0)
        {
            Geometry holes = Geometry.Empty;
            foreach (var s in spots) holes = new CombinedGeometry(GeometryCombineMode.Union, holes, s.Shape());
            var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(canvas), holes);
            byte alpha = (byte)Math.Clamp(spots[^1].Level * 25, 20, 235);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)), null, dim);
        }

        foreach (var a in doc.Annotations)
            if (a is not ImageAnnotation && !a.IsRasterEffect && a is not HighlighterAnnotation && a is not SpotlightAnnotation)
                a.Render(dc, rc);

        if (clipped) dc.Pop();
    }

    private static void DrawShadow(DrawingContext dc, Rect canvas, double radius, double intensity, double scale)
    {
        if (intensity <= 0.01) return;
        int layers = 14;
        double spread = 28 * scale * (0.5 + intensity);
        for (int i = layers; i >= 1; i--)
        {
            double t = i / (double)layers;
            var r = canvas;
            r.Inflate(spread * t * 0.6, spread * t * 0.6);
            r.Offset(0, spread * 0.25);
            byte a = (byte)Math.Clamp(intensity * 34 * (1 - t) * (1 - t) + 2, 0, 255);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(a, 0, 0, 0)), null, r, radius + spread * t * 0.6, radius + spread * t * 0.6);
        }
    }

    private static Brush? _checker;

    private static Brush Checker()
    {
        if (_checker != null) return _checker;
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xDA)), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xDA)), null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return _checker = b;
    }

    /// <summary>Renders the finished image (annotations, backdrop) at 1 bitmap pixel per image pixel.</summary>
    public static BitmapSource Export(EditorDocument doc, Rect? region = null, bool includeBackdrop = true)
    {
        var saved = doc.Backdrop;
        if (!includeBackdrop) doc.Backdrop = null;
        try
        {
            var area = region ?? OuterBounds(doc);
            int w = Math.Max(1, (int)Math.Round(area.Width)), h = Math.Max(1, (int)Math.Round(area.Height));
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(-area.X, -area.Y));
                Render(dc, new RenderContext { Document = doc, Exporting = true });
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        finally
        {
            doc.Backdrop = saved;
        }
    }
}
