using static Loupix.Raster.Tests.TestImages;

namespace Loupix.Raster.Tests;

public class ScrollStitcherTests
{
    private const int W = 320, FrameH = 400, HeaderH = 40, FooterH = 30, ViewH = FrameH - HeaderH - FooterH;
    private const int ScrollbarW = 12;
    private static readonly uint Ink = Rgb(30, 30, 36);

    /// <summary>A tall page: text-like lines, boxes (some with text), rules and uniform gaps.</summary>
    private static PixelBuffer MakePage(int height, int seed)
    {
        var page = Solid(W, height, White);
        var rng = new Random(seed);
        int y = 8;
        while (y < height - 40)
        {
            switch (rng.Next(6))
            {
                case 0:
                case 1:
                    {
                        int scale = rng.Next(1, 3);
                        int chars = scale == 1 ? rng.Next(10, 38) : rng.Next(6, 19);
                        Text(page, 6 + rng.Next(20), y, chars, Ink, rng.Next(), scale);
                        y += 7 * scale + rng.Next(6, 14);
                        break;
                    }
                case 2:
                    {
                        int h = rng.Next(20, 70), x = rng.Next(4, 60), w = rng.Next(60, W - 40 - x);
                        var c = Rgb(rng.Next(40, 230), rng.Next(40, 230), rng.Next(40, 230));
                        Fill(page, new RectI(x, y, w, h), c);
                        if (h > 24) Text(page, x + 6, y + 6, Math.Max(1, (w - 12) / 14), White, rng.Next());
                        y += h + rng.Next(6, 16);
                        break;
                    }
                case 3:
                    y += rng.Next(20, 80); // uniform gap
                    break;
                case 4:
                    Fill(page, new RectI(10, y, W - 40, 1), Rgb(210, 210, 210));
                    y += rng.Next(8, 20);
                    break;
                default:
                    Gradient(page, new RectI(0, y, W, 30), Rgb(230, 240, 255), Rgb(255, 235, 220));
                    Text(page, 12, y + 8, rng.Next(8, 30), Ink, rng.Next(), 1);
                    y += 30 + rng.Next(4, 12);
                    break;
            }
        }
        return page;
    }

    private static PixelBuffer Header()
    {
        var h = Solid(W, HeaderH, Rgb(32, 36, 48));
        Text(h, 12, 12, 10, White, 100);
        return h;
    }

    private static PixelBuffer Footer()
    {
        var f = Solid(W, FooterH, Rgb(236, 238, 242));
        Text(f, 12, 8, 6, Rgb(90, 90, 100), 200);
        Fill(f, new RectI(0, 0, W, 1), Rgb(200, 200, 205));
        return f;
    }

    /// <summary>The screen while the page is scrolled to <paramref name="s"/>: header, viewport, footer, scrollbar.</summary>
    private static PixelBuffer Frame(PixelBuffer page, int s, int noise = 0, int seed = 0)
    {
        var f = new PixelBuffer(W, FrameH);
        f.Blit(Header(), 0, 0);
        f.Blit(page.Crop(0, s, W, ViewH), 0, HeaderH);
        f.Blit(Footer(), 0, HeaderH + ViewH);
        Fill(f, new RectI(W - ScrollbarW, HeaderH, ScrollbarW, ViewH), Rgb(235, 235, 235));
        int maxS = page.Height - ViewH;
        int thumbTop = HeaderH + (int)((long)s * (ViewH - 60) / Math.Max(1, maxS));
        Fill(f, new RectI(W - ScrollbarW + 2, thumbTop, ScrollbarW - 4, 60), Rgb(150, 150, 150));
        if (noise > 0)
        {
            var rng = new Random(seed);
            for (int i = 0; i < f.Bgra.Length; i++)
                if ((i & 3) != 3) f.Bgra[i] = (byte)Math.Clamp(f.Bgra[i] + rng.Next(-noise, noise + 1), 0, 255);
        }
        return f;
    }

    /// <summary>Header + page rows [0, contentRows) + footer, which a perfect stitch must reproduce.</summary>
    private static PixelBuffer Expected(PixelBuffer page, int contentRows)
    {
        var e = new PixelBuffer(W, HeaderH + contentRows + FooterH);
        e.Blit(Header(), 0, 0);
        e.Blit(page.Crop(0, 0, W, contentRows), 0, HeaderH);
        e.Blit(Footer(), 0, HeaderH + contentRows);
        return e;
    }

    private static void AssertSameIgnoringScrollbar(PixelBuffer expected, PixelBuffer actual, int tol = 0)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        int bad = CountDiff(expected, actual, new RectI(0, 0, W - ScrollbarW, expected.Height), tol);
        Assert.Equal(0, bad);
    }

    private static readonly int[] Shifts = [37, 120, 200, 15, 90, 160, 64, 3, 128, 250, 47, 181, 99];

    [Fact]
    public void ScrollDown_StitchesThePageExactly()
    {
        var page = MakePage(2400, 1);
        int maxS = page.Height - ViewH;
        var st = new ScrollStitcher();
        Assert.Equal(StitchStatus.First, st.Add(Frame(page, 0)));
        Assert.Equal(FrameH, st.Height);

        int s = 0, i = 0;
        while (s < maxS)
        {
            int d = Math.Min(Shifts[i++ % Shifts.Length], maxS - s);
            s += d;
            Assert.Equal(StitchStatus.Added, st.Add(Frame(page, s)));
            Assert.Equal(d, st.LastOffset);
            Assert.Equal(FrameH + s, st.Height);
        }
        Assert.Equal(StitchStatus.NoMovement, st.Add(Frame(page, s)));
        Assert.Equal(0, st.LastOffset);

        AssertSameIgnoringScrollbar(Expected(page, page.Height), st.Compose());
    }

    [Fact]
    public void ScrollUp_Reverse_PrependsContent()
    {
        var page = MakePage(1800, 2);
        int s = page.Height - ViewH;
        var st = new ScrollStitcher(reverse: true);
        Assert.Equal(StitchStatus.First, st.Add(Frame(page, s)));
        int i = 0;
        while (s > 0)
        {
            int d = Math.Min(Shifts[i++ % Shifts.Length], s);
            s -= d;
            Assert.Equal(StitchStatus.Added, st.Add(Frame(page, s)));
            Assert.Equal(d, st.LastOffset);
        }
        Assert.Equal(StitchStatus.NoMovement, st.Add(Frame(page, 0)));
        AssertSameIgnoringScrollbar(Expected(page, page.Height), st.Compose());
    }

    [Fact]
    public void PartialScroll_EndsWhereCaptureStopped()
    {
        var page = MakePage(2000, 3);
        var st = new ScrollStitcher();
        st.Add(Frame(page, 0));
        st.Add(Frame(page, 140));
        st.Add(Frame(page, 300));
        AssertSameIgnoringScrollbar(Expected(page, 300 + ViewH), st.Compose());
    }

    [Fact]
    public void JumpWithoutOverlap_IsNoMatch_AndStitchingContinues()
    {
        var page = MakePage(2400, 4);
        var st = new ScrollStitcher();
        st.Add(Frame(page, 0));
        Assert.Equal(StitchStatus.Added, st.Add(Frame(page, 150)));
        Assert.Equal(StitchStatus.NoMatch, st.Add(Frame(page, 1500)));
        Assert.Equal(0, st.LastOffset);
        Assert.Equal(StitchStatus.Added, st.Add(Frame(page, 260)));
        AssertSameIgnoringScrollbar(Expected(page, 260 + ViewH), st.Compose());
    }

    [Fact]
    public void UniformContent_IsNoMatchRatherThanAGuess()
    {
        // A blank page with only a vertical rule: every row looks the same, so no shift can be trusted.
        var page = Solid(W, 1200, White);
        Fill(page, new RectI(40, 0, 2, 1200), Rgb(200, 200, 200));
        Text(page, 60, 700, 12, Ink, 5); // content that only appears after the jump
        var st = new ScrollStitcher();
        st.Add(Frame(page, 300));
        Assert.Equal(StitchStatus.NoMatch, st.Add(Frame(page, 420)));
        Assert.Equal(FrameH, st.Height);
    }

    [Fact]
    public void SmoothVerticalGradient_IsNoMatch()
    {
        // Steep enough that the frames differ, but every row is uniform and a shift of one row still fits.
        var page = new PixelBuffer(W, 600);
        Gradient(page, new RectI(0, 0, W, 600), Rgb(255, 255, 255), Rgb(0, 20, 40), vertical: true);
        var st = new ScrollStitcher();
        st.Add(Frame(page, 100));
        Assert.Equal(StitchStatus.NoMatch, st.Add(Frame(page, 180)));
    }

    [Fact]
    public void ReRenderNoise_StillStitches()
    {
        var page = MakePage(1600, 6);
        var st = new ScrollStitcher();
        st.Add(Frame(page, 0, noise: 4, seed: 1));
        int s = 0, i = 0;
        while (s < 900)
        {
            s += Shifts[i++];
            Assert.Equal(StitchStatus.Added, st.Add(Frame(page, s, noise: 4, seed: 10 + i)));
            Assert.Equal(Shifts[i - 1], st.LastOffset);
        }
        AssertSameIgnoringScrollbar(Expected(page, s + ViewH), st.Compose(), tol: 8);
    }

    [Fact]
    public void MaxHeight_IsEnforced()
    {
        var page = MakePage(2400, 7);
        var st = new ScrollStitcher(maxHeight: 700);
        st.Add(Frame(page, 0));
        Assert.Equal(StitchStatus.Added, st.Add(Frame(page, 200)));
        Assert.Equal(StitchStatus.LimitReached, st.Add(Frame(page, 400)));
        Assert.Equal(StitchStatus.LimitReached, st.Add(Frame(page, 500)));
        Assert.Equal(700, st.Height);
        var img = st.Compose();
        Assert.Equal(700, img.Height);
        AssertSameIgnoringScrollbar(Expected(page, 700 - HeaderH - FooterH), img);
    }

    [Fact]
    public void DifferentFrameSize_IsNoMatch_AndComposeNeedsAFrame()
    {
        var st = new ScrollStitcher();
        Assert.Throws<InvalidOperationException>(() => st.Compose());
        st.Add(Solid(100, 100, White));
        Assert.Equal(StitchStatus.NoMatch, st.Add(Solid(100, 90, White)));
        Assert.Equal(StitchStatus.NoMovement, st.Add(Solid(100, 100, White)));
    }
}
