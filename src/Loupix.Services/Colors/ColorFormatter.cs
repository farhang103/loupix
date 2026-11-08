using System.Globalization;

namespace Loupix.Services.Colors;

public enum ColorFormat
{
    /// <summary><c>#1A2B3C</c></summary>
    Hex,
    /// <summary><c>#1a2b3c</c></summary>
    HexLowercase,
    /// <summary><c>1A2B3C</c></summary>
    HexNoHash,
    /// <summary><c>rgb(26, 43, 60)</c></summary>
    Rgb,
    /// <summary><c>0.102, 0.169, 0.235</c></summary>
    RgbFloat,
    /// <summary><c>hsl(210, 40%, 17%)</c></summary>
    Hsl,
    /// <summary><c>hsb(210, 57%, 24%)</c></summary>
    Hsb,
    /// <summary><c>oklch(28.3% 0.039 249.3)</c></summary>
    Oklch,
    /// <summary><c>rgb(26 43 60)</c></summary>
    CssRgbModern,
    /// <summary><c>#FF1A2B3C</c></summary>
    XamlArgb,
    /// <summary><c>Color.FromArgb(26, 43, 60)</c></summary>
    CSharpColor,
}

public static class ColorFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Format(Rgb c, ColorFormat f) => f switch
    {
        ColorFormat.Hex => c.ToHex(),
        ColorFormat.HexLowercase => c.ToHex().ToLowerInvariant(),
        ColorFormat.HexNoHash => c.ToHex()[1..],
        ColorFormat.Rgb => $"rgb({c.R}, {c.G}, {c.B})",
        ColorFormat.RgbFloat => string.Create(Inv, $"{c.R / 255.0:0.000}, {c.G / 255.0:0.000}, {c.B / 255.0:0.000}"),
        ColorFormat.Hsl => FormatHsl(c),
        ColorFormat.Hsb => FormatHsb(c),
        ColorFormat.Oklch => FormatOklch(c),
        ColorFormat.CssRgbModern => $"rgb({c.R} {c.G} {c.B})",
        ColorFormat.XamlArgb => $"#FF{c.R:X2}{c.G:X2}{c.B:X2}",
        ColorFormat.CSharpColor => $"Color.FromArgb({c.R}, {c.G}, {c.B})",
        _ => throw new ArgumentOutOfRangeException(nameof(f), f, null),
    };

    public static string DisplayName(ColorFormat f) => f switch
    {
        ColorFormat.Hex => "HEX",
        ColorFormat.HexLowercase => "hex (lowercase)",
        ColorFormat.HexNoHash => "HEX without #",
        ColorFormat.Rgb => "RGB",
        ColorFormat.RgbFloat => "RGB 0–1",
        ColorFormat.Hsl => "HSL",
        ColorFormat.Hsb => "HSB / HSV",
        ColorFormat.Oklch => "OKLCH",
        ColorFormat.CssRgbModern => "CSS rgb() space-separated",
        ColorFormat.XamlArgb => "XAML #AARRGGBB",
        ColorFormat.CSharpColor => "C# Color.FromArgb",
        _ => throw new ArgumentOutOfRangeException(nameof(f), f, null),
    };

    /// <summary>Hue in degrees [0, 360), chroma (max - min) and max/min, all on a 0–1 scale.</summary>
    private static (double H, double Max, double Min) HueMaxMin(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * ((g - b) / d % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
        return (h, max, min);
    }

    private static string FormatHsl(Rgb c)
    {
        var (h, max, min) = HueMaxMin(c);
        double l = (max + min) / 2;
        double d = max - min;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        return $"hsl({RoundHue(h)}, {Pct(s)}%, {Pct(l)}%)";
    }

    private static string FormatHsb(Rgb c)
    {
        var (h, max, min) = HueMaxMin(c);
        double s = max == 0 ? 0 : (max - min) / max;
        return $"hsb({RoundHue(h)}, {Pct(s)}%, {Pct(max)}%)";
    }

    private static int RoundHue(double h)
    {
        int v = (int)Math.Round(h, MidpointRounding.AwayFromZero);
        return v >= 360 ? v - 360 : v;
    }

    private static int Pct(double v) => (int)Math.Round(v * 100, MidpointRounding.AwayFromZero);

    private static string FormatOklch(Rgb c)
    {
        var (l, ch, h) = ToOklch(c);
        // Achromatic colours have no meaningful hue; CSS treats it as 0 (or "none").
        if (Math.Round(ch, 3) == 0) { ch = 0; h = 0; }
        else if (Math.Round(h, 1) >= 360) h = 0;
        return string.Create(Inv, $"oklch({l * 100:0.0}% {ch:0.000} {h:0.0})");
    }

    /// <summary>sRGB → linear sRGB → OKLab → OKLCH (Björn Ottosson's matrices). L in [0, 1], hue in degrees.</summary>
    internal static (double L, double C, double H) ToOklch(Rgb c)
    {
        double r = Rgb.SrgbToLinear(c.R), g = Rgb.SrgbToLinear(c.G), b = Rgb.SrgbToLinear(c.B);

        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);

        double okL = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        double okA = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        double okB = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;

        double chroma = Math.Sqrt(okA * okA + okB * okB);
        double hue = Math.Atan2(okB, okA) * 180 / Math.PI;
        if (hue < 0) hue += 360;
        return (okL, chroma, hue);
    }
}
