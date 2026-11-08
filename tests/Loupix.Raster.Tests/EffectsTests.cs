using static Loupix.Raster.Tests.TestImages;

namespace Loupix.Raster.Tests;

public class EffectsTests
{
    // ---- Pixelate ----------------------------------------------------------------------------------------------

    [Fact]
    public void Pixelate_LargeArea_AveragesBlocksInPlace()
    {
        var img = new PixelBuffer(100, 80);
        Photo(img, new RectI(0, 0, 100, 80), 25, 4);
        var orig = img.Clone();
        var r = new RectI(10, 8, 64, 48);
        Effects.Pixelate(img, r, 8);

        // Block (0,0) equals the mean of the original block.
        double sr = 0;
        for (int y = 8; y < 16; y++) for (int x = 10; x < 18; x++) sr += R(Get(orig, x, y));
        Assert.InRange(R(Get(img, 10, 8)) - sr / 64, -0.51, 0.51);
        for (int y = 8; y < 16; y++) for (int x = 10; x < 18; x++) Assert.Equal(Get(img, 10, 8), Get(img, x, y));
        // Outside untouched.
        Assert.Equal(0, CountDiff(img, orig, new RectI(0, 0, 100, 8)));
        Assert.Equal(0, CountDiff(img, orig, new RectI(0, 56, 100, 24)));
        Assert.Equal(0, CountDiff(img, orig, new RectI(74, 0, 26, 80)));
    }

    [Fact]
    public void Pixelate_SmallArea_ShufflesBlocksDeterministically()
    {
        var img = Solid(60, 20, White);
        var r = new RectI(4, 4, 32, 8); // 4 x 1 blocks -> shuffled
        // Make every block a distinct colour.
        for (int i = 0; i < 4; i++) Fill(img, new RectI(4 + i * 8, 4, 8, 8), Rgb(i * 60, 255 - i * 60, 30));
        var a = img.Clone();
        var b = img.Clone();
        Effects.Pixelate(a, r, 8);
        Effects.Pixelate(b, r, 8);
        Assert.Equal(a.Bgra, b.Bgra); // deterministic

        var before = Enumerable.Range(0, 4).Select(i => Get(img, 4 + i * 8, 4)).ToArray();
        var after = Enumerable.Range(0, 4).Select(i => Get(a, 4 + i * 8, 4)).ToArray();
        Assert.Equal(before.OrderBy(c => c), after.OrderBy(c => c)); // same colours...
        Assert.NotEqual(before, after);                               // ...in a different order
    }

    [Fact]
    public void Pixelate_ClipsToImage()
    {
        var img = Solid(20, 20, White);
        Effects.Pixelate(img, new RectI(-10, -10, 100, 100), 7);
        Effects.Pixelate(img, new RectI(50, 50, 10, 10), 7);
        Assert.Equal(White, Get(img, 19, 19));
    }

    // ---- Blur --------------------------------------------------------------------------------------------------

    [Fact]
    public void Blur_FlatStaysFlat()
    {
        var c = Rgb(10, 100, 200);
        var img = Solid(80, 60, c);
        Effects.Blur(img, new RectI(-5, -5, 100, 100), 6);
        for (int y = 0; y < 60; y++) for (int x = 0; x < 80; x++) Assert.Equal(c, Get(img, x, y));
    }

    [Fact]
    public void Blur_SmoothsEdgeAndBlendsWithOutside()
    {
        var img = Solid(200, 100, Black);
        Fill(img, new RectI(100, 0, 100, 100), White);
        var orig = img.Clone();
        var r = new RectI(60, 20, 80, 60);
        Effects.Blur(img, r, 5);

        // Monotonic ramp across the edge inside r.
        int prev = -1;
        for (int x = r.X; x < r.Right; x++)
        {
            int v = R(Get(img, x, 50));
            Assert.True(v >= prev);
            prev = v;
        }
        Assert.InRange(R(Get(img, 100, 50)), 100, 200);
        Assert.InRange(R(Get(img, 99, 50)), 60, 160);
        // Only r changed.
        Assert.Equal(0, CountDiff(img, orig, new RectI(0, 0, 200, 20)));
        Assert.Equal(0, CountDiff(img, orig, new RectI(0, 80, 200, 20)));
        Assert.Equal(0, CountDiff(img, orig, new RectI(0, 0, 60, 100)));
        Assert.Equal(0, CountDiff(img, orig, new RectI(140, 0, 60, 100)));
    }

    [Fact]
    public void Blur_SamplesOutsideTheRectangle()
    {
        var img = Solid(100, 100, Black);
        var r = new RectI(30, 30, 40, 40);
        Fill(img, r, White);
        Effects.Blur(img, r, 4);
        Assert.True(R(Get(img, 30, 50)) < 200, "edge should darken from the black outside");
        Assert.True(R(Get(img, 50, 50)) > 250, "centre stays white");
        Assert.Equal(Black, Get(img, 29, 50));
    }

    [Fact]
    public void Blur_RemovesTextDetail()
    {
        var img = Solid(200, 60, White);
        var text = Text(img, 10, 10, 12, Black, 5);
        Effects.Blur(img, text.Inflate(4, 4), 6);
        int min = 255, max = 0;
        for (int x = text.X + 8; x < text.Right - 8; x++)
        {
            int v = R(Get(img, x, text.Y + text.Height / 2));
            min = Math.Min(min, v); max = Math.Max(max, v);
        }
        Assert.True(max - min < 80, $"text still has contrast {max - min}");
    }

    // ---- Erase -------------------------------------------------------------------------------------------------

    [Fact]
    public void Erase_UniformRing_FillsWithBackground()
    {
        var bg = Rgb(240, 242, 245);
        var img = Solid(200, 120, bg);
        var obj = new RectI(60, 30, 50, 40);
        Fill(img, obj, Rgb(200, 30, 30));
        Text(img, 120, 40, 4, Black, 1);
        Effects.Erase(img, obj.Inflate(2, 2));
        Assert.Equal(0, CountDiff(img, Solid(200, 120, bg), obj.Inflate(2, 2)));
    }

    [Fact]
    public void Erase_Gradient_ContinuesAcross()
    {
        var img = new PixelBuffer(300, 100);
        var full = new RectI(0, 0, 300, 100);
        Gradient(img, full, Rgb(20, 40, 200), Rgb(240, 180, 30));
        var clean = img.Clone();
        var obj = new RectI(100, 30, 80, 40);
        Fill(img, obj, White);
        Text(img, 105, 40, 5, Black, 2);
        Effects.Erase(img, obj);
        Assert.Equal(0, CountDiff(img, clean, obj, tol: 2));
    }

    [Fact]
    public void Erase_HorizontalStripes_RunAcross()
    {
        var img = new PixelBuffer(200, 120);
        for (int y = 0; y < 120; y++) Fill(img, new RectI(0, y, 200, 1), (y / 3) % 2 == 0 ? Rgb(250, 250, 250) : Rgb(30, 60, 90));
        var clean = img.Clone();
        var obj = new RectI(50, 31, 70, 50);
        Fill(img, obj, Rgb(255, 0, 0));
        Effects.Erase(img, obj);
        Assert.Equal(0, CountDiff(img, clean, obj, tol: 1));
    }

    [Fact]
    public void Erase_VerticalStripes_RunAcross()
    {
        var img = new PixelBuffer(200, 120);
        for (int x = 0; x < 200; x++) Fill(img, new RectI(x, 0, 1, 120), (x / 4) % 2 == 0 ? Rgb(200, 220, 240) : Rgb(90, 20, 20));
        var clean = img.Clone();
        var obj = new RectI(41, 20, 60, 70);
        Fill(img, obj, Rgb(0, 255, 0));
        Effects.Erase(img, obj);
        Assert.Equal(0, CountDiff(img, clean, obj, tol: 1));
    }

    [Fact]
    public void Erase_AtImageCorner_UsesRemainingSides()
    {
        var img = new PixelBuffer(100, 80);
        Gradient(img, new RectI(0, 0, 100, 80), Rgb(0, 0, 0), Rgb(200, 200, 200), vertical: true);
        var clean = img.Clone();
        var obj = new RectI(0, 0, 30, 25);
        Fill(img, obj, Rgb(255, 0, 255));
        Effects.Erase(img, obj);
        // Rows are uniform in a vertical gradient: the left/right boundaries carry it.
        Assert.Equal(0, CountDiff(img, clean, new RectI(0, 0, 30, 25), tol: 3));
        // Whole image: nothing to sample, no-op without throwing.
        Effects.Erase(img, new RectI(0, 0, 100, 80));
    }
}
