using static Loupix.Raster.Tests.TestImages;

namespace Loupix.Raster.Tests;

public class TextTests
{
    private static readonly uint Page = Rgb(246, 247, 249);
    private static readonly uint Ink = Rgb(34, 34, 40);
    private static readonly uint ButtonC = Rgb(43, 58, 85);

    /// <summary>A UI-like scene with text in several places plus things that must never be masked.</summary>
    private sealed class Scene
    {
        public PixelBuffer Img = Solid(800, 420, Page);
        public PixelBuffer Clean = null!;      // the same scene without any text
        public List<RectI> TextRects = [];
        public RectI Button = new(400, 20, 260, 56);
        public RectI ButtonText;
        public RectI Gradient = new(460, 250, 320, 110);
        public RectI GradientText;
        public RectI Shape = new(20, 120, 150, 100);
        public RectI BorderBox = new(200, 120, 160, 100);
        public RectI Rules = new(400, 100, 220, 12);
        public RectI Icon = new(660, 110, 64, 64);
        public RectI NoisyPhoto = new(20, 250, 200, 120);
        public RectI SmoothPhoto = new(240, 250, 200, 120);

        public Scene()
        {
            Fill(Img, Button, ButtonC);
            Fill(Img, Shape, Rgb(60, 141, 255));
            Fill(Img, BorderBox, White);
            Border(Img, BorderBox, 1, Rgb(150, 150, 150));
            Fill(Img, new RectI(Rules.X, Rules.Y, 200, 1), Rgb(200, 200, 200));
            Fill(Img, new RectI(Rules.X, Rules.Y + 8, 220, 2), Rgb(90, 90, 90));
            // Outline icon: 3px frame with a cross, all connected (larger than a glyph).
            Border(Img, Icon, 3, Rgb(80, 80, 200));
            Fill(Img, new RectI(Icon.X + 30, Icon.Y, 4, 64), Rgb(80, 80, 200));
            Fill(Img, new RectI(Icon.X, Icon.Y + 30, 64, 4), Rgb(80, 80, 200));
            Photo(Img, NoisyPhoto, 30, 11);
            Photo(Img, SmoothPhoto, 2, 12);
            TestImages.Gradient(Img, Gradient, Rgb(200, 225, 255), Rgb(255, 210, 230));
            Clean = Img.Clone();

            TextRects.Add(Text(Img, 20, 20, 18, Ink, 1));
            TextRects.Add(Text(Img, 20, 42, 22, Ink, 2));
            TextRects.Add(Text(Img, 20, 64, 9, Rgb(120, 120, 130), 3, scale: 1)); // small grey caption
            ButtonText = Text(Img, 420, 34, 12, White, 4);
            TextRects.Add(ButtonText);
            GradientText = Text(Img, 475, 290, 14, Ink, 5);
            TextRects.Add(GradientText);
            TextRects.Add(Text(Img, 215, 150, 8, Ink, 6)); // text inside the bordered box
        }

        public bool NearText(int x, int y, int margin) => TextRects.Any(r => r.Inflate(margin, margin).Contains(x, y));
    }

    [Fact]
    public void DetectText_MasksGlyphs_AndNothingElse()
    {
        var s = new Scene();
        var full = new RectI(0, 0, 800, 420);
        var mask = Effects.DetectText(s.Img, full);
        Assert.Equal(full, mask.Bounds);

        // Every glyph core pixel is masked.
        foreach (var r in s.TextRects)
        {
            var cores = new List<(int X, int Y)>();
            for (int y = r.Y; y < r.Bottom; y++)
                for (int x = r.X; x < r.Right; x++)
                    if (Diff(Get(s.Img, x, y), Get(s.Clean, x, y)) > 60) cores.Add((x, y));
            Assert.NotEmpty(cores);
            Assert.All(cores, p => Assert.True(mask[p.X, p.Y], $"glyph pixel {p} in {r} not masked"));
        }

        // Nothing away from text is masked.
        for (int y = 0; y < 420; y++)
            for (int x = 0; x < 800; x++)
                if (mask[x, y]) Assert.True(s.NearText(x, y, 2), $"non-text pixel ({x},{y}) masked");

        Assert.Equal(0, MaskCount(mask, s.Shape.Inflate(2, 2)));
        Assert.Equal(0, MaskCount(mask, RectI.FromLTRB(s.BorderBox.X - 2, s.BorderBox.Y - 2, s.BorderBox.Right + 2, s.BorderBox.Y + 20)));
        Assert.Equal(0, MaskCount(mask, s.Rules.Inflate(2, 2)));
        Assert.Equal(0, MaskCount(mask, s.Icon.Inflate(2, 2)));
        Assert.Equal(0, MaskCount(mask, s.NoisyPhoto));
        Assert.Equal(0, MaskCount(mask, s.SmoothPhoto));
    }

    [Fact]
    public void DetectText_SmallSelection_UsesSurroundingContext()
    {
        var img = Solid(300, 100, Page);
        var t = Text(img, 100, 40, 3, Ink, 7);
        var mask = Effects.DetectText(img, t.Inflate(1, 1));
        Assert.Equal(t.Inflate(1, 1), mask.Bounds);
        var cores = PixelsNear(img, t, Ink, 10);
        Assert.All(cores, p => Assert.True(mask[p.X, p.Y]));
        Assert.False(mask[0, 0]); // outside the mask bounds reads false
    }

    [Fact]
    public void DetectText_TextStraddlingBandBoundaries_IsMaskedOnce()
    {
        // The detector works in bands of 256 rows; these lines cross rows 256, 512 and 768.
        var img = Solid(420, 900, Page);
        var rects = new[] { 120, 250, 505, 760 }.Select((y, i) => Text(img, 10 + i * 7, y, 16, Ink, 30 + i)).ToList();
        Fill(img, new RectI(340, 200, 70, 400), Rgb(60, 140, 255)); // a tall shape crossing two boundaries
        var full = new RectI(0, 0, 420, 900);
        var det = TextDetector.Detect(img, full);
        foreach (var r in rects)
            Assert.All(PixelsNear(img, r, Ink, 10), p => Assert.True(det.Mask[p.X, p.Y], $"glyph pixel {p} not masked"));
        for (int y = 0; y < 900; y++)
            for (int x = 0; x < 420; x++)
                if (det.Mask[x, y]) Assert.Contains(rects, r => r.Inflate(2, 2).Contains(x, y));
        Assert.Equal(det.Blobs.Count, det.Blobs.Select(b => b.Bounds).Distinct().Count());
        Assert.Equal(rects.Count, Effects.TextLines(det.Blobs).Count(l => l.Width > 100));
    }

    [Fact]
    public void DetectText_LargeTextAndBigShapes()
    {
        var img = Solid(400, 200, Page);
        var big = Text(img, 10, 10, 3, Ink, 8, scale: 6);       // 42px tall glyphs: still text
        var huge = Text(img, 10, 100, 2, Ink, 9, scale: 10);    // 70px tall: too big for a glyph
        var mask = Effects.DetectText(img, new RectI(0, 0, 400, 200));
        Assert.All(PixelsNear(img, big, Ink, 10), p => Assert.True(mask[p.X, p.Y]));
        Assert.Equal(0, MaskCount(mask, huge));
    }

    [Fact]
    public void EraseText_ButtonBecomesPlain_PageUntouched()
    {
        var s = new Scene();
        var before = s.Img.Clone();
        Effects.EraseText(s.Img, new RectI(0, 0, 800, 420));

        // The button is a plain dark button again.
        var inner = s.Button.Inflate(-1, -1);
        Assert.Equal(0, CountDiff(s.Img, s.Clean, inner, tol: 2));
        // Paragraph and boxed text are gone.
        foreach (var r in s.TextRects.Where(r => r != s.GradientText))
            Assert.Equal(0, CountDiff(s.Img, s.Clean, r, tol: 3));
        // Text on the gradient: the gradient continues underneath.
        Assert.Equal(0, CountDiff(s.Img, s.Clean, s.GradientText, tol: 6));
        // Everything away from text is untouched.
        for (int y = 0; y < 420; y++)
            for (int x = 0; x < 800; x++)
                if (!s.NearText(x, y, 2)) Assert.Equal(Get(before, x, y), Get(s.Img, x, y));
    }

    [Fact]
    public void EraseText_OnlyInsideSelection()
    {
        var s = new Scene();
        var before = s.Img.Clone();
        var sel = s.Button; // only the button
        Effects.EraseText(s.Img, sel);
        Assert.Equal(0, CountDiff(s.Img, s.Clean, sel.Inflate(-1, -1), tol: 2));
        Assert.Equal(0, CountDiff(s.Img, before, new RectI(0, 0, 400, 420)));
    }

    [Fact]
    public void PixelateText_ChangesOnlyTextLines()
    {
        var s = new Scene();
        var before = s.Img.Clone();
        Effects.PixelateText(s.Img, new RectI(0, 0, 800, 420), 6);
        foreach (var r in s.TextRects)
            Assert.True(CountDiff(s.Img, before, r) > r.Width * r.Height / 10, $"text at {r} barely changed");
        for (int y = 0; y < 420; y++)
            for (int x = 0; x < 800; x++)
                if (!s.NearText(x, y, 3)) Assert.Equal(Get(before, x, y), Get(s.Img, x, y));
    }

    [Fact]
    public void TextLines_GroupsGlyphsIntoLines()
    {
        var s = new Scene();
        var det = TextDetector.Detect(s.Img, new RectI(0, 0, 800, 420));
        var lines = Effects.TextLines(det.Blobs);
        // Each drawn line becomes one (or very few, at word gaps) line rectangles, never merged across lines.
        foreach (var line in lines)
            Assert.Single(s.TextRects, r => r.Inflate(2, 2).Contains(line.X, line.Y) && r.Inflate(2, 2).Contains(line.Right - 1, line.Bottom - 1));
        Assert.InRange(lines.Count, s.TextRects.Count, s.TextRects.Count * 2);
    }
}
