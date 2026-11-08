using static Loupix.Raster.Tests.TestImages;

namespace Loupix.Raster.Tests;

public class ResamplerTests
{
    [Fact]
    public void Downscale_ByTwo_AveragesBlocks()
    {
        var img = new PixelBuffer(4, 4);
        uint[] c = [Rgb(255, 0, 0), Rgb(0, 255, 0), Rgb(0, 0, 255), Rgb(10, 20, 30)];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                Set(img, x, y, c[(y / 2) * 2 + x / 2]);
        var r = Resampler.Resize(img, 2, 2);
        Assert.Equal(c[0], Get(r, 0, 0));
        Assert.Equal(c[1], Get(r, 1, 0));
        Assert.Equal(c[2], Get(r, 0, 1));
        Assert.Equal(c[3], Get(r, 1, 1));
    }

    [Fact]
    public void Downscale_Checkerboard_BecomesGrey()
    {
        var img = new PixelBuffer(64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                Set(img, x, y, ((x + y) & 1) == 0 ? White : Black);
        var r = Resampler.Resize(img, 16, 16);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.InRange(R(Get(r, x, y)), 126, 129);
    }

    [Fact]
    public void Downscale_NonInteger_PreservesMean()
    {
        var img = new PixelBuffer(101, 67);
        Photo(img, new RectI(0, 0, 101, 67), 20, 3);
        var r = Resampler.Resize(img, 37, 29);
        Assert.Equal(37, r.Width);
        Assert.Equal(29, r.Height);
        Assert.InRange(Mean(r) - Mean(img), -1.0, 1.0);
    }

    [Fact]
    public void Upscale_FlatStaysFlat()
    {
        var c = Rgb(12, 200, 99);
        var r = Resampler.Resize(Solid(7, 5, c), 50, 33);
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
                Assert.Equal(c, Get(r, x, y));
    }

    [Fact]
    public void Upscale_Gradient_IsSmoothAndMonotonic()
    {
        var img = new PixelBuffer(16, 4);
        Gradient(img, new RectI(0, 0, 16, 4), Rgb(0, 0, 0), Rgb(240, 240, 240));
        var r = Resampler.Resize(img, 64, 16);
        int prev = -1;
        for (int x = 0; x < 64; x++)
        {
            int v = R(Get(r, x, 8));
            Assert.True(v >= prev, $"not monotonic at {x}");
            prev = v;
        }
        Assert.InRange(R(Get(r, 0, 0)), 0, 8);
        Assert.InRange(R(Get(r, 63, 0)), 232, 243); // Catmull-Rom may overshoot slightly at the clamped edge
    }

    [Fact]
    public void Upscale_HardEdge_KeepsFarPixelsExact()
    {
        var img = Solid(10, 10, White);
        Fill(img, new RectI(5, 0, 5, 10), Black);
        var r = Resampler.Resize(img, 40, 25);
        Assert.Equal(White, Get(r, 2, 10));
        Assert.Equal(Black, Get(r, 37, 10));
        // Catmull-Rom is sharp: the transition is narrow.
        Assert.True(R(Get(r, 17, 10)) > 200);
        Assert.True(R(Get(r, 22, 10)) < 55);
    }

    [Fact]
    public void Mixed_ShrinkWidth_GrowHeight()
    {
        var img = new PixelBuffer(80, 20);
        Gradient(img, new RectI(0, 0, 80, 20), Rgb(0, 50, 100), Rgb(200, 150, 100));
        var r = Resampler.Resize(img, 40, 60);
        Assert.Equal(40, r.Width);
        Assert.Equal(60, r.Height);
        Assert.InRange(Mean(r) - Mean(img), -1.5, 1.5);
    }

    [Fact]
    public void SameSize_ReturnsCopy()
    {
        var img = new PixelBuffer(13, 9);
        Photo(img, new RectI(0, 0, 13, 9), 30, 9);
        var r = Resampler.Resize(img, 13, 9);
        Assert.NotSame(img, r);
        Assert.Equal(img.Bgra, r.Bgra);
    }

    private static double Mean(PixelBuffer img)
    {
        double s = 0;
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                uint c = Get(img, x, y);
                s += R(c) + G(c) + B(c);
            }
        return s / (3.0 * img.Width * img.Height);
    }
}
