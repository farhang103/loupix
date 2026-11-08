using Loupix.Core.Imaging;
using Loupix.Services.Colors;

namespace Loupix.Services.Tests;

public class ColorTests
{
    private static readonly Rgb Sample = new(26, 43, 60);

    [Theory]
    [InlineData(ColorFormat.Hex, "#1A2B3C")]
    [InlineData(ColorFormat.HexLowercase, "#1a2b3c")]
    [InlineData(ColorFormat.HexNoHash, "1A2B3C")]
    [InlineData(ColorFormat.Rgb, "rgb(26, 43, 60)")]
    [InlineData(ColorFormat.RgbFloat, "0.102, 0.169, 0.235")]
    [InlineData(ColorFormat.Hsl, "hsl(210, 40%, 17%)")]
    [InlineData(ColorFormat.Hsb, "hsb(210, 57%, 24%)")]
    // linear sRGB → OKLab → OKLCH: L = 0.28261, C = 0.038826, h = 249.345° (checked independently in Python).
    [InlineData(ColorFormat.Oklch, "oklch(28.3% 0.039 249.3)")]
    [InlineData(ColorFormat.CssRgbModern, "rgb(26 43 60)")]
    [InlineData(ColorFormat.XamlArgb, "#FF1A2B3C")]
    [InlineData(ColorFormat.CSharpColor, "Color.FromArgb(26, 43, 60)")]
    public void Formats_sample_colour(ColorFormat format, string expected)
    {
        Assert.Equal(expected, ColorFormatter.Format(Sample, format));
    }

    [Fact]
    public void Formats_are_culture_invariant()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("0.102, 0.169, 0.235", ColorFormatter.Format(Sample, ColorFormat.RgbFloat));
            Assert.Equal("oklch(28.3% 0.039 249.3)", ColorFormatter.Format(Sample, ColorFormat.Oklch));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Every_format_has_a_display_name()
    {
        foreach (var f in Enum.GetValues<ColorFormat>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ColorFormatter.DisplayName(f)));
            Assert.False(string.IsNullOrWhiteSpace(ColorFormatter.Format(Sample, f)));
        }
    }

    [Theory]
    [InlineData(255, 255, 255, "oklch(100.0% 0.000 0.0)")]
    [InlineData(0, 0, 0, "oklch(0.0% 0.000 0.0)")]
    [InlineData(255, 0, 0, "oklch(62.8% 0.258 29.2)")]   // CSS Color 4 reference for sRGB red
    [InlineData(128, 128, 128, "oklch(60.0% 0.000 0.0)")] // grey: no chroma, hue reported as 0
    public void Oklch_reference_values(byte r, byte g, byte b, string expected)
    {
        Assert.Equal(expected, ColorFormatter.Format(new Rgb(r, g, b), ColorFormat.Oklch));
    }

    [Theory]
    [InlineData(255, 0, 0, "hsl(0, 100%, 50%)", "hsb(0, 100%, 100%)")]
    [InlineData(0, 0, 0, "hsl(0, 0%, 0%)", "hsb(0, 0%, 0%)")]
    [InlineData(255, 255, 255, "hsl(0, 0%, 100%)", "hsb(0, 0%, 100%)")]
    [InlineData(255, 0, 128, "hsl(330, 100%, 50%)", "hsb(330, 100%, 100%)")]
    public void Hsl_and_hsb_edge_cases(byte r, byte g, byte b, string hsl, string hsb)
    {
        var c = new Rgb(r, g, b);
        Assert.Equal(hsl, ColorFormatter.Format(c, ColorFormat.Hsl));
        Assert.Equal(hsb, ColorFormatter.Format(c, ColorFormat.Hsb));
    }

    [Theory]
    [InlineData("#1A2B3C")]
    [InlineData("1a2b3c")]
    [InlineData("  #1a2B3c ")]
    public void Parses_six_digit_hex(string text)
    {
        Assert.Equal(Sample, Rgb.FromHex(text));
        Assert.True(Rgb.TryParseHex(text, out var c));
        Assert.Equal(Sample, c);
    }

    [Fact]
    public void Parses_three_digit_hex_and_round_trips()
    {
        Assert.Equal(new Rgb(0xAA, 0xBB, 0xCC), Rgb.FromHex("#abc"));
        Assert.Equal("#1A2B3C", Rgb.FromHex(Sample.ToHex()).ToHex());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GGGGGG")]
    [InlineData("+12345")]
    public void Rejects_bad_hex(string? text)
    {
        Assert.False(Rgb.TryParseHex(text, out _));
        if (text is not null) Assert.Throws<FormatException>(() => Rgb.FromHex(text));
    }

    [Fact]
    public void Relative_luminance_endpoints()
    {
        Assert.Equal(0.0, new Rgb(0, 0, 0).RelativeLuminance, 10);
        Assert.Equal(1.0, new Rgb(255, 255, 255).RelativeLuminance, 10);
        Assert.Equal(0.2126, new Rgb(255, 0, 0).RelativeLuminance, 10);
        Assert.Equal(0.21586, new Rgb(128, 128, 128).RelativeLuminance, 4);
    }

    [Fact]
    public void Wcag_ratio_known_values()
    {
        var black = new Rgb(0, 0, 0);
        var white = new Rgb(255, 255, 255);
        Assert.Equal(21.0, Contrast.WcagRatio(black, white), 6);
        Assert.Equal(21.0, Contrast.WcagRatio(white, black), 6);
        Assert.Equal(1.0, Contrast.WcagRatio(Sample, Sample), 6);
        // #767676 on white is the classic "just passes AA" grey: 4.54:1.
        Assert.Equal(4.54, Contrast.WcagRatio(Rgb.FromHex("#767676"), white), 2);
    }

    [Theory]
    [InlineData(21.0, "AAA")]
    [InlineData(7.0, "AAA")]
    [InlineData(6.99, "AA")]
    [InlineData(4.5, "AA")]
    [InlineData(4.49, "AA Large")]
    [InlineData(3.0, "AA Large")]
    [InlineData(2.99, "Fail")]
    [InlineData(1.0, "Fail")]
    public void Wcag_levels(double ratio, string level)
    {
        Assert.Equal(level, Contrast.WcagLevel(ratio));
    }

    [Fact]
    public void Apca_published_reference_values()
    {
        var black = new Rgb(0, 0, 0);
        var white = new Rgb(255, 255, 255);
        Assert.Equal(106.04, Contrast.ApcaLc(black, white), 2);
        Assert.Equal(-107.88, Contrast.ApcaLc(white, black), 2);
        Assert.Equal(0.0, Contrast.ApcaLc(Sample, Sample), 6);
        // apca-w3 README examples: #888 text on #fff = Lc 63.06; #fff text on #888 = Lc -68.54.
        Assert.Equal(63.06, Contrast.ApcaLc(Rgb.FromHex("#888"), white), 2);
        Assert.Equal(-68.54, Contrast.ApcaLc(white, Rgb.FromHex("#888")), 2);
    }

    [Fact]
    public void Sampler_reads_pixel_and_clamps()
    {
        var img = TestImages.Solid(4, 3, 10, 20, 30);
        img.SetPixel(3, 2, 200, 100, 50);
        Assert.Equal(new Rgb(200, 100, 50), ColorSampler.At(img, 3, 2));
        Assert.Equal(new Rgb(200, 100, 50), ColorSampler.At(img, 99, 99));
        Assert.Equal(new Rgb(10, 20, 30), ColorSampler.At(img, -5, 0));
    }

    [Fact]
    public void Sampler_average_rounds_and_clamps()
    {
        var img = new PixelBuffer(2, 2);
        img.SetPixel(0, 0, 0, 0, 0);
        img.SetPixel(1, 0, 255, 255, 255);
        img.SetPixel(0, 1, 0, 0, 0);
        img.SetPixel(1, 1, 255, 255, 255);
        Assert.Equal(new Rgb(128, 128, 128), ColorSampler.Average(img, new RectI(0, 0, 2, 2)));
        Assert.Equal(new Rgb(255, 255, 255), ColorSampler.Average(img, new RectI(1, -10, 50, 50)));
        Assert.Throws<ArgumentException>(() => ColorSampler.Average(img, new RectI(5, 5, 2, 2)));
    }

    [Fact]
    public void Darkest_finds_text_ink_in_square_clamped_to_image()
    {
        var img = TestImages.Solid(50, 50, 250, 250, 250);
        img.SetPixel(12, 11, 120, 120, 120); // anti-aliased edge
        img.SetPixel(13, 12, 30, 40, 90);    // ink
        img.SetPixel(40, 40, 0, 0, 0);       // outside the square
        Assert.Equal(new Rgb(30, 40, 90), ColorSampler.Darkest(img, 10, 10));

        // Square around a corner point is clipped to the image rather than failing.
        img.SetPixel(0, 0, 5, 5, 5);
        Assert.Equal(new Rgb(5, 5, 5), ColorSampler.Darkest(img, 0, 0, 6));
        Assert.Throws<ArgumentException>(() => ColorSampler.Darkest(img, 500, 500));
    }

    [Fact]
    public void Darkest_uses_luminance_not_channel_sum()
    {
        var img = TestImages.Solid(10, 10, 255, 255, 255);
        img.SetPixel(2, 2, 0, 0, 255);   // pure blue: sum 255, luminance 0.0722
        img.SetPixel(3, 3, 100, 100, 100); // grey: sum 300, luminance 0.127
        Assert.Equal(new Rgb(0, 0, 255), ColorSampler.Darkest(img, 5, 5, 10));
    }
}
