using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Loupix.Core.Imaging;
using Loupix.Imaging;
using Loupix.Services.Gif;
using Loupix.Services.Ocr;
using Loupix.Services.Upload;
using MediaColor = System.Windows.Media.Color;
using Rgb = Loupix.Services.Colors.Rgb;
using ColorFormat = Loupix.Services.Colors.ColorFormat;
using ColorFormatter = Loupix.Services.Colors.ColorFormatter;
using Contrast = Loupix.Services.Colors.Contrast;

namespace Loupix.Services;

/// <summary>The app's entry point to the Loupix.Services library (OCR, QR, colours, S3 upload, GIF).</summary>
public static partial class Features
{
    // ---------------- colours ----------------

    public static IEnumerable<(string Key, string Name)> ColorFormats() =>
        Enum.GetValues<ColorFormat>().Select(f => (f.ToString(), $"{ColorFormatter.DisplayName(f)}    {ColorFormatter.Format(new Rgb(26, 43, 60), f)}"));

    private static ColorFormat CurrentFormat =>
        Enum.TryParse<ColorFormat>(SettingsStore.Current.ColorFormat, out var f) ? f : ColorFormat.Hex;

    public static string FormatColor(MediaColor c) => ColorFormatter.Format(new Rgb(c.R, c.G, c.B), CurrentFormat);

    public static string FormatColor(MediaColor c, string formatKey) =>
        ColorFormatter.Format(new Rgb(c.R, c.G, c.B), Enum.TryParse<ColorFormat>(formatKey, out var f) ? f : ColorFormat.Hex);

    /// <summary>WCAG 2 contrast ratio, its level, and the APCA Lc value (text on background).</summary>
    public static (double Ratio, string Level, double Apca) ContrastOf(MediaColor text, MediaColor background)
    {
        var t = new Rgb(text.R, text.G, text.B);
        var b = new Rgb(background.R, background.G, background.B);
        double ratio = Contrast.WcagRatio(t, b);
        return (ratio, Contrast.WcagLevel(ratio), Contrast.ApcaLc(t, b));
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    public static uint ClipboardSequence() => GetClipboardSequenceNumber();

    // ---------------- OCR / QR ----------------

    public static IReadOnlyList<string> OcrLanguages() => OcrService.AvailableLanguages();

    /// <summary>Recognizes text; if the image contains QR (or other) codes, their content wins.</summary>
    public static async Task<(string Text, bool IsQr)> RecognizeAsync(PixelBuffer px)
    {
        var lang = SettingsStore.Current.OcrLanguage;
        try
        {
            var result = await Task.Run(() => OcrService.RecognizeAsync(px, string.IsNullOrEmpty(lang) ? null : lang));
            if (result.QrCodes.Count > 0) return (string.Join(Environment.NewLine, result.QrCodes), true);
            return (result.Text, false);
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("No text-recognition language is installed. Add one in Windows Settings → Time & language → Language & region.");
        }
    }

    public static string RemoveLineBreaks(string text) => OcrService.RemoveLineBreaks(text);

    // ---------------- upload ----------------

    public static async Task<string> UploadAsync(HttpClient http, byte[] content, string name, string contentType)
    {
        var s = SettingsStore.Current.S3;
        if (string.IsNullOrWhiteSpace(s.Bucket) || string.IsNullOrWhiteSpace(s.AccessKeyId))
            throw new InvalidOperationException("Fill in the bucket and access key in Settings → Upload.");
        var config = new S3Config(
            s.Endpoint.Trim(), s.Region.Trim(), s.Bucket.Trim(), s.AccessKeyId.Trim(), SecretStore.Unprotect(s.SecretAccessKeyProtected),
            s.PathStyle,
            string.IsNullOrWhiteSpace(s.PublicUrlBase) ? null : s.PublicUrlBase.Trim(),
            s.KeyPrefix ?? "",
            string.IsNullOrWhiteSpace(s.Acl) ? null : s.Acl.Trim());
        try
        {
            var uri = await new S3Uploader(http).UploadAsync(config, name, content, contentType);
            return uri.ToString();
        }
        catch (S3UploadException ex)
        {
            throw new InvalidOperationException($"{(int)ex.StatusCode} {ex.ErrorCode}: {ex.ErrorMessage}".Trim(), ex);
        }
    }

    // ---------------- GIF ----------------

    /// <summary>Encodes frames as a looping animated GIF (before/after: 1 s per frame).</summary>
    public static byte[] EncodeGif(IReadOnlyList<BitmapSource> frames, int delayMs = 1000)
    {
        var buffers = frames.Select(f => ImageIO.FromBitmapSource(ImageIO.Flatten(f, System.Windows.Media.Colors.White))).ToList();
        int w = buffers.Max(b => b.Width), h = buffers.Max(b => b.Height);
        // All frames must share a size: pad smaller ones onto white.
        for (int i = 0; i < buffers.Count; i++)
        {
            if (buffers[i].Width == w && buffers[i].Height == h) continue;
            var padded = new PixelBuffer(w, h);
            Array.Fill(padded.Bgra, (byte)255);
            padded.Blit(buffers[i], 0, 0);
            buffers[i] = padded;
        }
        return GifEncoder.EncodeAnimated(buffers, delayMs);
    }
}
