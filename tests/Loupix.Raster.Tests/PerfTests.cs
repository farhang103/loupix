using System.Diagnostics;
using Xunit.Abstractions;
using static Loupix.Raster.Tests.TestImages;

namespace Loupix.Raster.Tests;

/// <summary>
/// Timings on a 3840x2160 UI-like screenshot (dense text, boxes, a photo). Bounds are generous so the suite
/// stays stable on slow machines and in Debug; the printed numbers are what matters.
/// </summary>
public class PerfTests(ITestOutputHelper output)
{
    private static readonly Lazy<PixelBuffer> Screen = new(Make4K);

    private static PixelBuffer Make4K()
    {
        var tile = Solid(960, 540, Rgb(246, 247, 249));
        for (int y = 12; y < 300; y += 22) Text(tile, 16, y, 60, Rgb(30, 30, 36), y, scale: 1 + (y / 22) % 2);
        Fill(tile, new RectI(20, 320, 300, 60), Rgb(43, 58, 85));
        Text(tile, 36, 340, 14, White, 1);
        Fill(tile, new RectI(360, 320, 200, 200), White);
        Border(tile, new RectI(360, 320, 200, 200), 1, Rgb(160, 160, 160));
        Photo(tile, new RectI(600, 320, 340, 200), 12, 3);
        var img = new PixelBuffer(3840, 2160);
        for (int ty = 0; ty < 4; ty++)
            for (int tx = 0; tx < 4; tx++)
                img.Blit(tile, tx * 960, ty * 540);
        return img;
    }

    private double Time(string name, Action action, int runs = 5)
    {
        action(); // warm-up (JIT)
        var times = new List<double>();
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        double median = times[times.Count / 2];
        output.WriteLine($"{name}: median {median:F2} ms (min {times[0]:F2}, max {times[^1]:F2})");
        return median;
    }

    [Fact]
    public void Measure_And_FloodBounds_4K()
    {
        var img = Screen.Value;
        var uniform = Solid(3840, 2160, Rgb(250, 250, 250));
        Assert.True(Time("Ruler.Measure (horizontal, page row)", () => Ruler.Measure(img, 1000, 310, Axis.Horizontal), 50) < 20);
        Assert.True(Time("Ruler.MeasureOuter (vertical)", () => Ruler.MeasureOuter(img, 400, 330, Axis.Vertical), 50) < 20);
        Assert.True(Time("SmartSelect.FloodBounds (UI page, seed on background)", () => SmartSelect.FloodBounds(img, 5, 5)) < 500);
        Assert.True(Time("SmartSelect.FloodBounds (all-uniform 4K, worst case)", () => SmartSelect.FloodBounds(uniform, 1920, 1080)) < 500);
        Assert.True(Time("SmartSelect.AutoAdjust", () => SmartSelect.AutoAdjust(img, new RectI(350, 310, 230, 220)), 20) < 100);
    }

    [Fact]
    public void DetectText_Resize_Blur_4K()
    {
        var img = Screen.Value;
        int count = 0;
        Assert.True(Time("Effects.DetectText (full 4K)", () => count = Effects.DetectText(img, new RectI(0, 0, 3840, 2160)).Count(), 3) < 3000);
        output.WriteLine($"  masked pixels: {count}");
        Time("Effects.DetectText (600x200 selection)", () => Effects.DetectText(img, new RectI(100, 100, 600, 200)), 10);
        Time("Resampler.Resize 4K -> 1920x1080", () => Resampler.Resize(img, 1920, 1080), 3);
        Time("Resampler.Resize 4K -> 5760x3240 (1.5x up)", () => Resampler.Resize(img, 5760, 3240), 3);
        var copy = img.Clone();
        Time("Effects.Blur (1000x600, radius 12)", () => Effects.Blur(copy, new RectI(500, 500, 1000, 600), 12), 3);
        Time("Effects.Pixelate (full 4K, block 12)", () => Effects.Pixelate(copy, new RectI(0, 0, 3840, 2160), 12), 3);
    }

    [Fact]
    public void DetectText_FullScreenPhoto_WorstCase()
    {
        var photo = new PixelBuffer(3840, 2160);
        var rng = new Random(1);
        rng.NextBytes(photo.Bgra); // pure noise: almost every pixel is its own flat run
        long before = GC.GetTotalAllocatedBytes(true);
        int count = -1;
        Time("Effects.DetectText (4K pure noise, worst case)", () => count = Effects.DetectText(photo, new RectI(0, 0, 3840, 2160)).Count(), 1);
        output.WriteLine($"  allocated per call ~{(GC.GetTotalAllocatedBytes(true) - before) / 2 / (1024 * 1024)} MB, masked {count}");
        Assert.Equal(0, count);
    }

    [Fact]
    public void ScrollStitcher_1080pFrames()
    {
        var page = Solid(1920, 6000, White);
        var rng = new Random(5);
        for (int y = 10; y < 5980; y += rng.Next(18, 40)) Text(page, 20 + rng.Next(40), y, rng.Next(40, 120), Rgb(30, 30, 36), y, 1);
        var st = new ScrollStitcher();
        st.Add(page.Crop(0, 0, 1920, 1080));
        int s = 0;
        Time("ScrollStitcher.Add (1920x1080 frame, hashed path)", () =>
        {
            s += 300;
            Assert.Equal(StitchStatus.Added, st.Add(page.Crop(0, s, 1920, 1080)));
        }, 8);
        Time("ScrollStitcher.Compose", () => st.Compose(), 3);
    }
}
