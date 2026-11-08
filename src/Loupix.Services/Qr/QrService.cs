using Loupix.Core.Imaging;
using Loupix.Services.Imaging;
using ZXing;
using ZXing.Common;

namespace Loupix.Services.Qr;

/// <summary>Finds and decodes QR codes and other common 2-D / 1-D barcodes in an image.</summary>
public static class QrService
{
    /// <summary>
    /// Upscaling the whole image is only worth its cost while the 2× copy stays reasonable;
    /// above this many source pixels (a 4K screen) the upscaled passes are skipped.
    /// </summary>
    private const long MaxPixelsForUpscale = 3840L * 2160;

    /// <summary>
    /// Everything except QR. QR codes get their own pass with ZXing's QR multi-reader, which is both better at
    /// finding several QR codes and more robust than the single QR reader the generic multi-reader would use.
    /// </summary>
    private static readonly List<BarcodeFormat> OtherFormats =
    [
        BarcodeFormat.DATA_MATRIX,
        BarcodeFormat.AZTEC,
        BarcodeFormat.PDF_417,
        BarcodeFormat.CODE_128,
        BarcodeFormat.CODE_39,
        BarcodeFormat.CODE_93,
        BarcodeFormat.EAN_13,
        BarcodeFormat.EAN_8,
        BarcodeFormat.UPC_A,
        BarcodeFormat.UPC_E,
        BarcodeFormat.ITF,
    ];

    private static readonly List<BarcodeFormat> QrOnly = [BarcodeFormat.QR_CODE];

    /// <summary>
    /// Returns the decoded text of every code found, de-duplicated, ordered top-to-bottom then left-to-right.
    /// Tries the image as is, then inverted (light-on-dark codes), then 2× upscaled and 2× upscaled inverted
    /// (tiny codes), stopping at the first pass that finds anything. Never throws for "nothing found".
    /// Formats: QR, Data Matrix, Aztec, PDF417, Code 128/39/93, EAN-13/8, UPC-A/E and ITF.
    /// </summary>
    /// <remarks>
    /// QR, PDF417 and 1-D codes are found anywhere in the image. ZXing's Data Matrix and Aztec detectors grow a
    /// search rectangle from the image centre, so those two are reliably found only in a crop around the code.
    /// </remarks>
    public static IReadOnlyList<string> Decode(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);

        byte[] luma = ToLuminance(image);
        int w = image.Width, h = image.Height;
        var source = new RGBLuminanceSource(luma, w, h, RGBLuminanceSource.BitmapFormat.Gray8);

        var found = Run(source);
        if (found.Count == 0) found = Run(source.invert());
        if (found.Count == 0 && (long)w * h <= MaxPixelsForUpscale)
        {
            byte[] big = Resampler.ResizeGray(luma, w, h, w * 2, h * 2);
            var bigSource = new RGBLuminanceSource(big, w * 2, h * 2, RGBLuminanceSource.BitmapFormat.Gray8);
            found = Run(bigSource);
            if (found.Count == 0) found = Run(bigSource.invert());
        }
        return found;
    }

    private static List<string> Run(LuminanceSource source)
    {
        var results = new List<Result>();
        results.AddRange(DecodeMultiple(source, QrOnly));
        results.AddRange(DecodeMultiple(source, OtherFormats));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var r in results.Where(r => !string.IsNullOrEmpty(r.Text)).OrderBy(r => TopLeft(r).Y).ThenBy(r => TopLeft(r).X))
        {
            if (seen.Add(r.Text)) list.Add(r.Text);
        }
        return list;
    }

    private static Result[] DecodeMultiple(LuminanceSource source, List<BarcodeFormat> formats)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false, // 1-D readers already try a 90° rotation under TryHarder.
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = formats,
                // No CharacterSet: ZXing honours ECI and otherwise guesses UTF-8 / Shift_JIS / ISO-8859-1 per code,
                // which handles both UTF-8 generators that omit ECI and spec-default Latin-1 codes.
            },
        };
        try
        {
            return reader.DecodeMultiple(source) ?? [];
        }
        catch (Exception ex) when (ex is ReaderException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            // ZXing occasionally throws on pathological input instead of returning null; treat as "nothing found".
            return [];
        }
    }

    private static (float X, float Y) TopLeft(Result r)
    {
        var pts = r.ResultPoints;
        if (pts is null || pts.Length == 0) return (0, 0);
        float x = float.MaxValue, y = float.MaxValue;
        foreach (var p in pts)
        {
            if (p is null) continue;
            x = Math.Min(x, p.X);
            y = Math.Min(y, p.Y);
        }
        return (x, y);
    }

    /// <summary>BT.601 luma, with translucent pixels composited over white so transparent backgrounds read as paper.</summary>
    internal static byte[] ToLuminance(PixelBuffer image)
    {
        var src = image.Bgra;
        var luma = new byte[image.Width * image.Height];
        bool useAlpha = AlphaHelper.HasMeaningfulAlpha(image);
        for (int i = 0, p = 0; i < luma.Length; i++, p += 4)
        {
            int y = (src[p + 2] * 306 + src[p + 1] * 601 + src[p] * 117 + 512) >> 10;
            int a = src[p + 3];
            if (useAlpha && a != 255) y = (y * a + 255 * (255 - a) + 127) / 255;
            luma[i] = (byte)y;
        }
        return luma;
    }
}
