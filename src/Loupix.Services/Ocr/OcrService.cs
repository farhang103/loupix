using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Loupix.Core.Imaging;
using Loupix.Services.Imaging;
using Loupix.Services.Qr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace Loupix.Services.Ocr;

/// <summary>One recognised line. <paramref name="Bounds"/> is in the coordinates of the image passed to OCR.</summary>
public sealed record OcrLine(string Text, RectI Bounds);

/// <summary>OCR output: the full text (lines joined with <see cref="Environment.NewLine"/>), the lines, and any QR/barcode payloads.</summary>
public sealed record OcrResult(string Text, IReadOnlyList<OcrLine> Lines, IReadOnlyList<string> QrCodes);

/// <summary>Text recognition with the built-in Windows OCR engine (<c>Windows.Media.Ocr</c>).</summary>
public static class OcrService
{
    /// <summary>Images whose sides are both under this are upscaled 2× first: screenshot text is often too small for the engine.</summary>
    internal const int UpscaleBelow = 1600;

    /// <summary>Uniform border added around each image sent to the engine; it misses text that touches the edge of a tight crop.</summary>
    internal const int Padding = 24;

    /// <summary>Downscaling further than this to fit the engine's size limit would make small text unreadable, so tile instead.</summary>
    internal const double MinScale = 0.75;

    private const int MaxTileOverlap = 256;

    /// <summary>BCP-47 tags of the OCR languages installed on this machine, e.g. "en-US".</summary>
    public static IReadOnlyList<string> AvailableLanguages() =>
        WinOcr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// <summary>
    /// Recognises the text in <paramref name="image"/> and, in parallel, decodes any QR codes / barcodes.
    /// </summary>
    /// <param name="languageTag">An installed OCR language (see <see cref="AvailableLanguages"/>), or null for the
    /// user-profile languages (falling back to the first installed OCR language).</param>
    /// <exception cref="NotSupportedException">The requested language has no OCR support installed.</exception>
    /// <exception cref="InvalidOperationException">No OCR language is installed at all.</exception>
    public static Task<OcrResult> RecognizeAsync(PixelBuffer image, string? languageTag = null, CancellationToken ct = default) =>
        RecognizeAsync(image, languageTag, (int)WinOcr.OcrEngine.MaxImageDimension, ct);

    /// <summary>As the public overload, with the engine's size limit overridable so tests can exercise tiling on small images.</summary>
    internal static async Task<OcrResult> RecognizeAsync(PixelBuffer image, string? languageTag, int maxDimension, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();

        var engine = CreateEngine(languageTag);
        var qrTask = Task.Run(() => QrService.Decode(image), ct);
        IReadOnlyList<OcrLine> lines;
        try
        {
            lines = await RecognizeLinesAsync(engine, image, maxDimension, ct).ConfigureAwait(false);
        }
        catch
        {
            _ = qrTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // don't leave a faulted task unobserved
            throw;
        }
        var qrCodes = await qrTask.ConfigureAwait(false);
        return new OcrResult(string.Join(Environment.NewLine, lines.Select(l => l.Text)), lines, qrCodes);
    }

    /// <summary>
    /// Joins wrapped lines into paragraphs: lines are trimmed and joined with single spaces, except that a line
    /// ending in a hyphenated word fragment ("infor-") followed by a line starting with a lower-case letter is
    /// re-joined without the hyphen ("information"). Blank lines (paragraph breaks) are kept as they are.
    /// The output uses the input's first line-break sequence.
    /// </summary>
    public static string RemoveLineBreaks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string newline = DetectNewline(text);
        var output = new List<string>();
        StringBuilder? paragraph = null;

        foreach (var raw in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                if (paragraph is not null) output.Add(paragraph.ToString());
                paragraph = null;
                output.Add("");
                continue;
            }
            if (paragraph is null)
            {
                paragraph = new StringBuilder(line);
            }
            else if (EndsWithWordFragment(paragraph) && char.IsLower(line[0]))
            {
                paragraph.Length--; // drop the hyphen
                paragraph.Append(line);
            }
            else
            {
                paragraph.Append(' ').Append(line);
            }
        }
        if (paragraph is not null) output.Add(paragraph.ToString());
        return string.Join(newline, output);
    }

    private static string DetectNewline(string text)
    {
        int i = text.IndexOfAny(['\r', '\n']);
        if (i < 0) return Environment.NewLine;
        if (text[i] == '\n') return "\n";
        return i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
    }

    /// <summary>"…infor-": a hyphen (ASCII, U+2010 or soft hyphen) directly after a letter.</summary>
    private static bool EndsWithWordFragment(StringBuilder sb) =>
        sb.Length >= 2 && sb[^1] is '-' or '‐' or '­' && char.IsLetter(sb[^2]);

    // ---------------------------------------------------------------- engine

    private static WinOcr.OcrEngine CreateEngine(string? languageTag)
    {
        if (languageTag is not null)
        {
            Language language;
            try
            {
                language = new Language(languageTag);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"'{languageTag}' is not a valid language tag.", nameof(languageTag), ex);
            }
            if (!WinOcr.OcrEngine.IsLanguageSupported(language))
                throw new NotSupportedException($"OCR for '{languageTag}' is not installed. Installed: {string.Join(", ", AvailableLanguages())}.");
            return WinOcr.OcrEngine.TryCreateFromLanguage(language)
                ?? throw new NotSupportedException($"Could not create an OCR engine for '{languageTag}'.");
        }

        var engine = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is not null) return engine;
        foreach (var language in WinOcr.OcrEngine.AvailableRecognizerLanguages)
        {
            engine = WinOcr.OcrEngine.TryCreateFromLanguage(language);
            if (engine is not null) return engine;
        }
        throw new InvalidOperationException(
            "No OCR language is installed. Add a language with the \"Optical character recognition\" feature in Settings › Time & language › Language & region.");
    }

    private static async Task<IReadOnlyList<OcrLine>> RecognizeLinesAsync(WinOcr.OcrEngine engine, PixelBuffer image, int maxDimension, CancellationToken ct)
    {
        var tiles = PlanTiles(image.Width, image.Height, maxDimension);
        var lines = new List<OcrLine>();
        foreach (var tile in tiles)
        {
            ct.ThrowIfCancellationRequested();
            var (pixels, scaleX, scaleY) = await Task.Run(() => PrepareTile(image, tile), ct).ConfigureAwait(false);

            WinOcr.OcrResult result;
            using (var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                pixels.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, pixels.Width, pixels.Height, BitmapAlphaMode.Premultiplied))
            {
                result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
            }

            foreach (var line in result.Lines)
            {
                var mapped = MapLine(line, tile, scaleX, scaleY, image.Width, image.Height);
                if (mapped is not null) lines.Add(mapped);
            }
        }
        // The engine reports lines in reading order within an image, and tiles are visited top-to-bottom,
        // left-to-right, so the concatenation is already in reading order.
        return lines;
    }

    private static OcrLine? MapLine(WinOcr.OcrLine line, OcrTile tile, double scaleX, double scaleY, int imageWidth, int imageHeight)
    {
        var words = new List<string>();
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var word in line.Words)
        {
            var r = word.BoundingRect;
            double x0 = tile.Source.X + (r.X - Padding) / scaleX;
            double y0 = tile.Source.Y + (r.Y - Padding) / scaleY;
            double x1 = tile.Source.X + (r.X + r.Width - Padding) / scaleX;
            double y1 = tile.Source.Y + (r.Y + r.Height - Padding) / scaleY;

            // With several tiles, a word seen in two overlapping tiles belongs to the one whose core holds its centre.
            int cx = Math.Clamp((int)Math.Floor((x0 + x1) / 2), 0, imageWidth - 1);
            int cy = Math.Clamp((int)Math.Floor((y0 + y1) / 2), 0, imageHeight - 1);
            if (!tile.Core.Contains(cx, cy)) continue;

            words.Add(word.Text);
            left = Math.Min(left, x0);
            top = Math.Min(top, y0);
            right = Math.Max(right, x1);
            bottom = Math.Max(bottom, y1);
        }

        string text = JoinWords(words);
        if (text.Length == 0) return null;
        var bounds = RectI.FromLTRB((int)Math.Floor(left), (int)Math.Floor(top), (int)Math.Ceiling(right), (int)Math.Ceiling(bottom))
            .ClampTo(imageWidth, imageHeight);
        return new OcrLine(text, bounds);
    }

    // ---------------------------------------------------------------- image preparation

    /// <summary>A region of the source image to recognise at <paramref name="Scale"/>; only words centred in <paramref name="Core"/> are kept.</summary>
    internal readonly record struct OcrTile(RectI Source, RectI Core, double Scale);

    /// <summary>
    /// One tile covering the whole image when it fits within <paramref name="maxDimension"/> (after the 2× upscale for
    /// small images, shrunk as needed but not below <see cref="MinScale"/>); otherwise overlapping native-resolution
    /// tiles in row-major order whose cores partition the image.
    /// </summary>
    internal static IReadOnlyList<OcrTile> PlanTiles(int width, int height, int maxDimension)
    {
        double desired = width < UpscaleBelow && height < UpscaleBelow ? 2.0 : 1.0;
        int budget = Math.Max(64, maxDimension - 2 * Padding);
        double fit = Math.Min(desired, Math.Min((double)budget / width, (double)budget / height));
        var whole = new RectI(0, 0, width, height);
        if (fit >= MinScale) return [new OcrTile(whole, whole, fit)];

        int overlap = Math.Min(MaxTileOverlap, budget / 4);
        var xs = Spans(width, budget, overlap);
        var ys = Spans(height, budget, overlap);
        var tiles = new List<OcrTile>(xs.Count * ys.Count);
        foreach (var y in ys)
        {
            foreach (var x in xs)
            {
                tiles.Add(new OcrTile(
                    RectI.FromLTRB(x.Start, y.Start, x.End, y.End),
                    RectI.FromLTRB(x.CoreStart, y.CoreStart, x.CoreEnd, y.CoreEnd),
                    1.0));
            }
        }
        return tiles;
    }

    private static List<(int Start, int End, int CoreStart, int CoreEnd)> Spans(int length, int tile, int overlap)
    {
        if (length <= tile) return [(0, length, 0, length)];
        var starts = new List<int>();
        for (int s = 0; ; s += tile - overlap)
        {
            if (s + tile >= length)
            {
                starts.Add(length - tile);
                break;
            }
            starts.Add(s);
        }
        var spans = new List<(int, int, int, int)>(starts.Count);
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i], end = start + tile;
            // Core boundaries sit in the middle of each overlap, so neighbouring cores meet exactly.
            int coreStart = i == 0 ? 0 : (start + starts[i - 1] + tile) / 2;
            int coreEnd = i == starts.Count - 1 ? length : (starts[i + 1] + end) / 2;
            spans.Add((start, end, coreStart, coreEnd));
        }
        return spans;
    }

    /// <summary>Crops, flattens alpha onto white, resamples and pads one tile. Returns the buffer and the exact scale factors used.</summary>
    private static (PixelBuffer Pixels, double ScaleX, double ScaleY) PrepareTile(PixelBuffer image, OcrTile tile)
    {
        var source = tile.Source == new RectI(0, 0, image.Width, image.Height)
            ? image
            : image.Crop(tile.Source.X, tile.Source.Y, tile.Source.Width, tile.Source.Height);
        var flat = AlphaHelper.Flatten(source);

        int w = Math.Max(1, (int)Math.Round(flat.Width * tile.Scale));
        int h = Math.Max(1, (int)Math.Round(flat.Height * tile.Scale));
        var scaled = w == flat.Width && h == flat.Height ? flat : Resampler.Resize(flat, w, h);

        var (r, g, b) = BorderColor(flat);
        var padded = new PixelBuffer(w + 2 * Padding, h + 2 * Padding);
        var px = padded.Bgra;
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = 255;
        }
        padded.Blit(scaled, Padding, Padding);
        return (padded, (double)w / flat.Width, (double)h / flat.Height);
    }

    /// <summary>Mean colour of the outermost ring of pixels: the background a tight crop sits on.</summary>
    private static (byte R, byte G, byte B) BorderColor(PixelBuffer img)
    {
        long r = 0, g = 0, b = 0, n = 0;
        void Add(int x, int y)
        {
            var (pr, pg, pb) = img.GetRgb(x, y);
            r += pr; g += pg; b += pb; n++;
        }
        for (int x = 0; x < img.Width; x++)
        {
            Add(x, 0);
            if (img.Height > 1) Add(x, img.Height - 1);
        }
        for (int y = 1; y < img.Height - 1; y++)
        {
            Add(0, y);
            if (img.Width > 1) Add(img.Width - 1, y);
        }
        return ((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    // ---------------------------------------------------------------- text assembly

    /// <summary>
    /// Joins recognised words with single spaces, except between two CJK characters (Chinese and Japanese are
    /// written without spaces, but the engine reports each character as a word). Never produces double spaces.
    /// </summary>
    internal static string JoinWords(IEnumerable<string> words)
    {
        var sb = new StringBuilder();
        Rune last = default;
        foreach (var raw in words)
        {
            string word = CollapseWhitespace(raw);
            if (word.Length == 0) continue;
            Rune.DecodeFromUtf16(word, out Rune first, out _);
            if (sb.Length > 0 && !(IsCjk(last) && IsCjk(first))) sb.Append(' ');
            sb.Append(word);
            Rune.DecodeLastFromUtf16(word, out last, out _);
        }
        return sb.ToString();
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool pendingSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Han ideographs, kana, CJK punctuation and full-width forms (not Hangul: Korean separates words with spaces).</summary>
    internal static bool IsCjk(Rune r) => r.Value switch
    {
        >= 0x3000 and <= 0x303F => true,   // CJK symbols and punctuation
        >= 0x3040 and <= 0x30FF => true,   // Hiragana, Katakana
        >= 0x31F0 and <= 0x31FF => true,   // Katakana phonetic extensions
        >= 0x3400 and <= 0x4DBF => true,   // CJK Extension A
        >= 0x4E00 and <= 0x9FFF => true,   // CJK Unified Ideographs
        >= 0xF900 and <= 0xFAFF => true,   // CJK Compatibility Ideographs
        >= 0xFF00 and <= 0xFFEF => true,   // Half-width and full-width forms
        >= 0x20000 and <= 0x3FFFF => true, // CJK Extensions B and later
        _ => false,
    };
}
