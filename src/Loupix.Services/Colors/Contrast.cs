namespace Loupix.Services.Colors;

public static class Contrast
{
    /// <summary>WCAG 2.x contrast ratio, 1 to 21. Order of the arguments does not matter.</summary>
    public static double WcagRatio(Rgb a, Rgb b)
    {
        double la = a.RelativeLuminance, lb = b.RelativeLuminance;
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>"AAA" (≥ 7), "AA" (≥ 4.5), "AA Large" (≥ 3, large text only) or "Fail".</summary>
    public static string WcagLevel(double ratio) => ratio switch
    {
        >= 7 => "AAA",
        >= 4.5 => "AA",
        >= 3 => "AA Large",
        _ => "Fail",
    };

    // APCA-W3 0.0.98G-4g constants (Myndex, https://github.com/Myndex/apca-w3).
    private const double MainTrc = 2.4;
    private const double SRco = 0.2126729, SGco = 0.7151522, SBco = 0.0721750;
    private const double NormBg = 0.56, NormTxt = 0.57, RevTxt = 0.62, RevBg = 0.65;
    private const double BlkThrs = 0.022, BlkClmp = 1.414;
    private const double ScaleBoW = 1.14, ScaleWoB = 1.14;
    private const double LoBoWOffset = 0.027, LoWoBOffset = 0.027;
    private const double DeltaYMin = 0.0005, LoClip = 0.1;

    /// <summary>
    /// APCA lightness contrast (Lc), roughly -108 to 106. Positive for dark text on a light background,
    /// negative for light text on a dark background. Argument order matters.
    /// </summary>
    public static double ApcaLc(Rgb text, Rgb background)
    {
        double txtY = ApcaY(text), bgY = ApcaY(background);

        // Soft-clamp near black.
        txtY = txtY > BlkThrs ? txtY : txtY + Math.Pow(BlkThrs - txtY, BlkClmp);
        bgY = bgY > BlkThrs ? bgY : bgY + Math.Pow(BlkThrs - bgY, BlkClmp);

        if (Math.Abs(bgY - txtY) < DeltaYMin) return 0;

        double output;
        if (bgY > txtY)
        {
            // Normal polarity: dark text on a light background.
            double sapc = (Math.Pow(bgY, NormBg) - Math.Pow(txtY, NormTxt)) * ScaleBoW;
            output = sapc < LoClip ? 0 : sapc - LoBoWOffset;
        }
        else
        {
            // Reverse polarity: light text on a dark background.
            double sapc = (Math.Pow(bgY, RevBg) - Math.Pow(txtY, RevTxt)) * ScaleWoB;
            output = sapc > -LoClip ? 0 : sapc + LoWoBOffset;
        }
        return output * 100;
    }

    /// <summary>APCA's estimated screen luminance: a simple 2.4 power curve, not the piecewise sRGB function.</summary>
    private static double ApcaY(Rgb c) =>
        SRco * Math.Pow(c.R / 255.0, MainTrc) +
        SGco * Math.Pow(c.G / 255.0, MainTrc) +
        SBco * Math.Pow(c.B / 255.0, MainTrc);
}
