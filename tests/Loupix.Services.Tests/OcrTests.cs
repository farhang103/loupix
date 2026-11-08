using System.Text;
using Loupix.Core.Imaging;
using Loupix.Services.Ocr;
using ZXing;

namespace Loupix.Services.Tests;

/// <summary>A fact that is skipped (not failed) when no English OCR language is installed on this machine.</summary>
public sealed class EnglishOcrFactAttribute : FactAttribute
{
    public EnglishOcrFactAttribute()
    {
        if (OcrTests.EnglishTag() is null) Skip = "No English OCR language is installed on this machine.";
    }
}

public class OcrTests
{
    internal static string? EnglishTag()
    {
        try
        {
            return OcrService.AvailableLanguages().FirstOrDefault(t => t.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null; // WinRT OCR unavailable (e.g. Server Core)
        }
    }

    private static string Normalize(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [EnglishOcrFact]
    public async Task Recognizes_rendered_text_with_line_bounds()
    {
        var image = TestImages.RenderText(640, 140, 18,
            ("The quick brown fox jumps over the lazy dog", 20, 20),
            ("Pack my box with five dozen liquor jugs", 20, 80));

        var result = await OcrService.RecognizeAsync(image, EnglishTag());

        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("The quick brown fox jumps over the lazy dog", Normalize(result.Lines[0].Text));
        Assert.Equal("Pack my box with five dozen liquor jugs", Normalize(result.Lines[1].Text));
        Assert.Equal(result.Lines[0].Text + Environment.NewLine + result.Lines[1].Text, result.Text);
        Assert.DoesNotContain("  ", result.Text);
        Assert.Empty(result.QrCodes);

        // Bounds are in original-image pixels (the image was upscaled 2× and padded for the engine).
        var first = result.Lines[0].Bounds;
        Assert.InRange(first.X, 14, 28);
        Assert.InRange(first.Y, 20, 34);
        Assert.InRange(first.Right, 300, 420);
        Assert.InRange(first.Bottom, 30, 48);
        var second = result.Lines[1].Bounds;
        Assert.InRange(second.Y, 80, 94);
        Assert.True(second.Bottom <= image.Height);
    }

    [EnglishOcrFact]
    public async Task Recognizes_small_text_in_a_tight_crop()
    {
        // 11 px text cropped to the glyphs: needs both the upscale and the padding to be read reliably.
        var full = TestImages.RenderText(300, 40, 11, ("Settings and privacy", 4, 10));
        var tight = full.Crop(3, 10, 110, 17);

        var result = await OcrService.RecognizeAsync(tight, EnglishTag());

        Assert.Equal("Settings and privacy", Normalize(result.Text));
    }

    [EnglishOcrFact]
    public async Task Fills_qr_codes_alongside_text()
    {
        var image = TestImages.RenderText(600, 260, 20, ("Scan to open the page", 20, 20));
        image.Blit(QrTests.Barcode(BarcodeFormat.QR_CODE, "https://example.com/loupix", 180, 180), 380, 60);

        var result = await OcrService.RecognizeAsync(image, EnglishTag());

        Assert.Contains("Scan to open the page", result.Text);
        Assert.Equal(["https://example.com/loupix"], result.QrCodes);
    }

    [EnglishOcrFact]
    public async Task Default_language_works_and_cancellation_is_honoured()
    {
        var image = TestImages.RenderText(300, 60, 18, ("Hello Loupix", 10, 10));
        var result = await OcrService.RecognizeAsync(image); // user-profile languages (or first installed)
        Assert.False(string.IsNullOrWhiteSpace(result.Text));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OcrService.RecognizeAsync(image, EnglishTag(), cts.Token));
    }

    [EnglishOcrFact]
    public async Task Tiled_recognition_keeps_every_line_once_in_order()
    {
        // A 600 px engine limit forces four overlapping native-resolution tiles on this 520×1500 image,
        // so several lines straddle a tile edge or sit inside an overlap.
        string[] words = ["apple", "bridge", "candle", "dolphin", "engine", "forest", "garden", "harbor", "island", "jungle"];
        var runs = Enumerable.Range(0, 40)
            .Select(i => ($"Row {i + 1} {words[i % words.Length]} {words[(i * 3 + 1) % words.Length]}", 20.0, 12.0 + i * 36))
            .ToArray();
        var image = TestImages.RenderText(520, 1500, 18, runs);
        Assert.True(OcrService.PlanTiles(image.Width, image.Height, 600).Count >= 3);

        var result = await OcrService.RecognizeAsync(image, EnglishTag(), 600, CancellationToken.None);

        // Compared without spaces: at native scale the engine sometimes splits "17" into "1 7", which is not what this tests.
        static string NoSpaces(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
        Assert.Equal(runs.Select(r => NoSpaces(r.Item1)), result.Lines.Select(l => NoSpaces(l.Text)));
        for (int i = 0; i < result.Lines.Count; i++)
        {
            // Mapped back to original coordinates: each line's box sits on the row it was drawn at.
            Assert.InRange(result.Lines[i].Bounds.Y, (int)runs[i].Item3, (int)runs[i].Item3 + 14);
        }
    }

    [Fact]
    public async Task Unknown_language_is_reported()
    {
        var image = TestImages.Solid(10, 10, 255, 255, 255);
        await Assert.ThrowsAsync<NotSupportedException>(() => OcrService.RecognizeAsync(image, "tlh-Piqd")); // Klingon: never installed
    }

    [Fact]
    public void Joins_words_with_single_spaces_except_between_cjk()
    {
        Assert.Equal("Hello world", OcrService.JoinWords(["Hello", "world"]));
        Assert.Equal("你好世界", OcrService.JoinWords(["你", "好", "世", "界"]));
        Assert.Equal("価格は 100 円です。", OcrService.JoinWords(["価", "格", "は", "100", "円", "で", "す", "。"]));
        Assert.Equal("Loupix は便利", OcrService.JoinWords(["Loupix", "は", "便", "利"]));
        Assert.Equal("안녕 하세요", OcrService.JoinWords(["안녕", "하세요"])); // Hangul keeps its spaces
        Assert.Equal("a b c", OcrService.JoinWords([" a ", "", "  ", "b  ", "c"]));
        Assert.Equal("𠀋𠀋", OcrService.JoinWords(["𠀋", "𠀋"])); // supplementary-plane ideographs
    }

    [Fact]
    public void Remove_line_breaks_joins_lines_and_repairs_hyphenation()
    {
        Assert.Equal("The quick brown fox jumps over the lazy dog.",
            OcrService.RemoveLineBreaks("The quick brown\nfox jumps over\nthe lazy dog."));
        Assert.Equal("This sentence has a hyphenated word in it.",
            OcrService.RemoveLineBreaks("This sentence has a hyphen-\nated word in it."));
        // Not a broken word: capital letter next, or a dash after a space.
        Assert.Equal("Microsoft- Windows", OcrService.RemoveLineBreaks("Microsoft-\nWindows"));
        Assert.Equal("one - two", OcrService.RemoveLineBreaks("one -\ntwo"));
        Assert.Equal("a b", OcrService.RemoveLineBreaks("  a   \n   b  "));
    }

    [Fact]
    public void Remove_line_breaks_keeps_paragraphs_and_newline_style()
    {
        Assert.Equal("Para one line two\n\nPara two",
            OcrService.RemoveLineBreaks("Para one\nline two\n\nPara two"));
        Assert.Equal("First para\r\n\r\nSecond para\r\n",
            OcrService.RemoveLineBreaks("First\r\npara\r\n\r\nSecond\r\npara\r\n"));
        Assert.Equal("single line", OcrService.RemoveLineBreaks("single line"));
        Assert.Equal("", OcrService.RemoveLineBreaks(""));
    }

    [Fact]
    public void Small_images_are_upscaled_2x_in_one_tile()
    {
        var tiles = OcrService.PlanTiles(800, 600, 10000);
        var t = Assert.Single(tiles);
        Assert.Equal(2.0, t.Scale);
        Assert.Equal(new RectI(0, 0, 800, 600), t.Source);
    }

    [Fact]
    public void Upscale_is_reduced_to_fit_the_engine_limit()
    {
        // 2× would be 3000 px wide; a 2600 px limit (older Windows) leaves room for 1.7×.
        var t = Assert.Single(OcrService.PlanTiles(1500, 400, 2600));
        Assert.InRange(t.Scale * 1500 + 2 * OcrService.Padding, 2500, 2600);
        Assert.True(t.Scale > 1);
    }

    [Fact]
    public void Large_images_are_not_upscaled_and_mildly_downscaled()
    {
        Assert.Equal(1.0, Assert.Single(OcrService.PlanTiles(2560, 1440, 10000)).Scale);
        var t = Assert.Single(OcrService.PlanTiles(3000, 1000, 2600));
        Assert.InRange(t.Scale, OcrService.MinScale, 1.0);
    }

    [Fact]
    public void Very_tall_images_are_tiled_with_cores_that_partition_the_image()
    {
        const int w = 1200, h = 30000, max = 10000;
        var tiles = OcrService.PlanTiles(w, h, max);
        Assert.True(tiles.Count > 3);

        foreach (var tile in tiles)
        {
            Assert.Equal(1.0, tile.Scale);
            Assert.True(tile.Source.Height + 2 * OcrService.Padding <= max);
            Assert.Equal(tile.Core, tile.Core.Intersect(tile.Source)); // core inside source
        }
        // Every row belongs to exactly one core, and overlaps are wide enough to hold a text line.
        var coverage = new int[h];
        foreach (var tile in tiles)
            for (int y = tile.Core.Y; y < tile.Core.Bottom; y++) coverage[y]++;
        Assert.All(coverage, c => Assert.Equal(1, c));
        for (int i = 1; i < tiles.Count; i++)
        {
            Assert.True(tiles[i].Core.Y - tiles[i].Source.Y >= 100);
            Assert.True(tiles[i - 1].Source.Bottom - tiles[i - 1].Core.Bottom >= 100);
        }
    }

    [Fact]
    public void Wide_and_tall_images_tile_in_row_major_order()
    {
        var tiles = OcrService.PlanTiles(5000, 5000, 2600);
        Assert.True(tiles.Count >= 4);
        for (int i = 1; i < tiles.Count; i++)
        {
            var a = tiles[i - 1].Core;
            var b = tiles[i].Core;
            Assert.True(b.Y > a.Y || (b.Y == a.Y && b.X > a.X));
        }
        long area = tiles.Sum(t => (long)t.Core.Width * t.Core.Height);
        Assert.Equal(5000L * 5000, area);
    }
}
