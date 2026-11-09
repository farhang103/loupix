using System.Windows;
using System.Windows.Media;
using Loupix.Core.Imaging;
using Loupix.Editor;
using Loupix.Imaging;

namespace Loupix.Tests;

public class EditorDocumentTests
{
    private static PixelBuffer Solid(int w, int h, byte r, byte g, byte b)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) p.SetPixel(x, y, r, g, b);
        return p;
    }

    [Fact]
    public void Crop_moves_annotations_and_undo_restores_everything() => Sta.Run(() =>
    {
        var doc = new EditorDocument(Solid(200, 100, 255, 255, 255), 1);
        doc.Annotations.Add(new RectangleAnnotation { P1 = new Point(50, 20), P2 = new Point(80, 60) });

        doc.Crop(new RectI(40, 10, 100, 80));
        Assert.Equal(100, doc.Base.Width);
        Assert.Equal(80, doc.Base.Height);
        Assert.Equal(new Point(10, 10), ((RectangleAnnotation)doc.Annotations[0]).P1);

        Assert.True(doc.Undo());
        Assert.Equal(200, doc.Base.Width);
        Assert.Equal(new Point(50, 20), ((RectangleAnnotation)doc.Annotations[0]).P1);

        Assert.True(doc.Redo());
        Assert.Equal(100, doc.Base.Width);
    });

    [Fact]
    public void Reset_crop_brings_back_the_original_image() => Sta.Run(() =>
    {
        var doc = new EditorDocument(Solid(200, 100, 0, 0, 0), 1);
        doc.Crop(new RectI(10, 10, 50, 50));
        doc.Crop(new RectI(5, 5, 20, 20));
        Assert.True(doc.CanResetCrop);
        doc.ResetCrop();
        Assert.Equal(200, doc.Base.Width);
    });

    [Fact]
    public void Resize_scales_annotations() => Sta.Run(() =>
    {
        var doc = new EditorDocument(Solid(200, 100, 0, 0, 0), 1);
        doc.Annotations.Add(new ArrowAnnotation { Start = new Point(10, 10), End = new Point(100, 50) });
        doc.Resize(Solid(100, 50, 0, 0, 0));
        var a = (ArrowAnnotation)doc.Annotations[0];
        Assert.Equal(new Point(5, 5), a.Start);
        Assert.Equal(new Point(50, 25), a.End);
    });

    [Fact]
    public void Counters_continue_from_the_last_one() => Sta.Run(() =>
    {
        var doc = new EditorDocument(Solid(10, 10, 0, 0, 0), 1);
        Assert.Equal(1, doc.NextCounterNumber());
        doc.Annotations.Add(new CounterAnnotation { Number = 7 });
        Assert.Equal(8, doc.NextCounterNumber());
    });

    [Fact]
    public void Export_matches_canvas_and_backdrop_adds_padding() => Sta.Run(() =>
    {
        var doc = new EditorDocument(Solid(300, 200, 255, 255, 255), 1);
        var plain = DocumentRenderer.Export(doc);
        Assert.Equal(300, plain.PixelWidth);
        Assert.Equal(200, plain.PixelHeight);

        doc.Backdrop = Backdrop.Default with { Padding = 40, Aspect = "Auto" };
        var padded = DocumentRenderer.Export(doc);
        Assert.Equal(380, padded.PixelWidth);
        Assert.Equal(280, padded.PixelHeight);

        doc.Backdrop = Backdrop.Default with { Padding = 0, Aspect = "16:9" };
        var wide = DocumentRenderer.Export(doc);
        Assert.Equal(16.0 / 9, wide.PixelWidth / (double)wide.PixelHeight, 2);
    });

    [Fact]
    public void Rendered_rectangle_has_its_colour_on_the_outline_only() => Sta.Run(() =>
    {
        var doc = new EditorDocument(Solid(200, 200, 255, 255, 255), 1);
        doc.Annotations.Add(new RectangleAnnotation { P1 = new Point(40, 40), P2 = new Point(160, 160), Color = Color.FromRgb(255, 0, 0), Size = 3 });
        var px = ImageIO.FromBitmapSource(DocumentRenderer.Export(doc));
        var edge = px.GetRgb(100, 42);
        Assert.True(edge.R > 200 && edge.G < 60 && edge.B < 60, $"edge pixel {edge}");
        Assert.Equal(((byte)255, (byte)255, (byte)255), px.GetRgb(100, 100));
    });

    [Fact]
    public void Pixelate_effect_changes_only_its_area() => Sta.Run(() =>
    {
        var src = new PixelBuffer(100, 100);
        for (int y = 0; y < 100; y++) for (int x = 0; x < 100; x++) src.SetPixel(x, y, (byte)(x * 2), (byte)(y * 2), (byte)((x + y) % 256));
        var doc = new EditorDocument(src.Clone(), 1);
        doc.Annotations.Add(new EffectAnnotation { P1 = new Point(20, 20), P2 = new Point(60, 60), Mode = EffectMode.Pixelate, Size = 3 });
        var px = ImageIO.FromBitmapSource(DocumentRenderer.Export(doc));
        Assert.Equal(src.GetRgb(5, 5), px.GetRgb(5, 5));
        Assert.Equal(src.GetRgb(80, 80), px.GetRgb(80, 80));
        Assert.Equal(px.GetRgb(21, 21), px.GetRgb(22, 22));
    });

    [Fact]
    public void Arrow_hit_test_follows_the_bend() => Sta.Run(() =>
    {
        var a = new ArrowAnnotation { Start = new Point(0, 0), End = new Point(200, 0), Bend = 40 };
        Assert.True(a.HitTest(a.MidPoint, 2));
        Assert.False(a.HitTest(new Point(100, -60), 2));
    });

    [Fact]
    public void Text_pointer_disappears_when_dragged_inside_the_bubble() => Sta.Run(() =>
    {
        var t = new TextAnnotation { Position = new Point(10, 10), Text = "Hello", Tip = new Point(200, 200) };
        var box = t.BoxFor(1);
        t.MoveHandle(2, new Point(box.X + 2, box.Y + 2), false);
        Assert.Null(t.Tip);
    });

    [Fact]
    public void Ruler_drag_measures_along_the_axis_that_moved_most() => Sta.Run(() =>
    {
        var (from, to, width) = MeasureAnnotation.FromDrag(new Point(10.4, 20.2), new Point(110.6, 25));
        Assert.Equal(new Point(10, 20.5), from);
        Assert.Equal(new Point(111, 20.5), to);
        Assert.Equal(101, width);

        (from, to, var height) = MeasureAnnotation.FromDrag(new Point(10.2, 20.4), new Point(14, 90.6));
        Assert.Equal(new Point(10.5, 20), from);
        Assert.Equal(new Point(10.5, 91), to);
        Assert.Equal(71, height);
    });

    [Fact]
    public void Every_tool_explains_itself()
    {
        foreach (var tool in Tools.All)
        {
            var help = ToolHelp.For(tool.Kind);
            Assert.False(string.IsNullOrWhiteSpace(help.How));
            Assert.False(string.IsNullOrWhiteSpace(help.Say));
        }
        var inGuide = ToolHelp.Groups.SelectMany(g => g.Tools).Where(e => e.Kind != null).Select(e => e.Kind!.Value).ToHashSet();
        Assert.Equal(Tools.All.Select(t => t.Kind).ToHashSet(), inGuide);
    }

    [Fact]
    public void Backdrop_look_blends_halfway() => Sta.Run(() =>
    {
        var from = new BackdropLook(new Rect(0, 0, 100, 100), Color.FromRgb(0, 0, 0), Color.FromRgb(0, 100, 0), 0, 0);
        var to = new BackdropLook(new Rect(-20, -40, 140, 180), Color.FromRgb(200, 0, 0), Color.FromRgb(0, 200, 0), 20, 1);
        var mid = BackdropLook.Lerp(from, to, 0.5);
        Assert.Equal(new Rect(-10, -20, 120, 140), mid.Outer);
        Assert.Equal(Color.FromRgb(100, 0, 0), mid.A);
        Assert.Equal(Color.FromRgb(0, 150, 0), mid.B);
        Assert.Equal(10, mid.Radius);
        Assert.Equal(0.5, mid.Shadow);
        Assert.False(from.IsCloseTo(to));
        Assert.True(to.IsCloseTo(BackdropLook.Lerp(from, to, 0.9999)));
    });

    [Fact]
    public void Removing_a_backdrop_fades_its_color_instead_of_going_through_white() => Sta.Run(() =>
    {
        var doc = new EditorDocument(Solid(100, 100, 255, 255, 255), 1) { Backdrop = Backdrop.Default };
        var with = BackdropLook.Of(doc);
        doc.Backdrop = null;
        var without = BackdropLook.Of(doc);
        Assert.Equal(doc.Canvas, without.Outer);

        var mid = BackdropLook.Lerp(with, without, 0.5);
        Assert.Equal(with.A.R, mid.A.R);
        Assert.Equal(with.A.G, mid.A.G);
        Assert.Equal(with.A.B, mid.A.B);
        Assert.InRange(mid.A.A, (byte)126, (byte)129);
        Assert.True(BackdropLook.Lerp(with, without, 0.9999).IsCloseTo(without)); // or the fade never finishes

        // A very fast screen takes tiny steps, and still has to arrive.
        var step = with;
        for (int i = 0; i < 2000 && !step.IsCloseTo(without); i++) step = BackdropLook.Lerp(step, without, 0.03);
        Assert.True(step.IsCloseTo(without));
    });
}
