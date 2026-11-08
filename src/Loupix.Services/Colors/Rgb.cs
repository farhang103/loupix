using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Loupix.Services.Colors;

/// <summary>An opaque 8-bit sRGB colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Parses <c>#RRGGBB</c>, <c>RRGGBB</c>, <c>#RGB</c> or <c>RGB</c> (case-insensitive, surrounding whitespace ignored).</summary>
    /// <exception cref="FormatException">The text is not a hex colour.</exception>
    public static Rgb FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        return TryParseHex(hex, out var c) ? c : throw new FormatException($"'{hex}' is not a hex colour (#RGB or #RRGGBB).");
    }

    public static bool TryParseHex([NotNullWhen(true)] string? hex, out Rgb color)
    {
        color = default;
        if (hex is null) return false;
        var s = hex.AsSpan().Trim();
        if (s.StartsWith("#")) s = s[1..];

        if (s.Length == 3)
        {
            if (!TryNibble(s[0], out int r) || !TryNibble(s[1], out int g) || !TryNibble(s[2], out int b)) return false;
            color = new Rgb((byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
            return true;
        }
        if (s.Length == 6 && uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v))
        {
            color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }
        return false;
    }

    private static bool TryNibble(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        return value >= 0;
    }

    /// <summary><c>#RRGGBB</c>, upper-case.</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>WCAG 2.x relative luminance (0 = black, 1 = white) using the exact sRGB transfer function.</summary>
    public double RelativeLuminance =>
        0.2126 * SrgbToLinear(R) + 0.7152 * SrgbToLinear(G) + 0.0722 * SrgbToLinear(B);

    public override string ToString() => ToHex();

    internal static double SrgbToLinear(byte channel) => LinearTable[channel];

    private static readonly double[] LinearTable = BuildLinearTable();

    private static double[] BuildLinearTable()
    {
        var t = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return t;
    }
}
