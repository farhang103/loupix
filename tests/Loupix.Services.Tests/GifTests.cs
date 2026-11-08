using Loupix.Core.Imaging;
using Loupix.Services.Gif;

namespace Loupix.Services.Tests;

public class GifTests
{
    private static PixelBuffer Pattern(int w, int h, Func<int, int, (byte R, byte G, byte B)> f)
    {
        var img = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = f(x, y);
                img.SetPixel(x, y, r, g, b);
            }
        return img;
    }

    private static void AssertSamePixels(PixelBuffer expected, PixelBuffer actual, int tolerance)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
            {
                var e = expected.GetRgb(x, y);
                var a = actual.GetRgb(x, y);
                if (Math.Abs(e.R - a.R) > tolerance || Math.Abs(e.G - a.G) > tolerance || Math.Abs(e.B - a.B) > tolerance)
                    Assert.Fail($"Pixel ({x},{y}): expected {e}, got {a} (tolerance {tolerance}).");
            }
    }

    [Fact]
    public void Frames_with_few_colours_round_trip_exactly()
    {
        var frames = new[]
        {
            Pattern(64, 48, (x, y) => x < 32 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255)),
            Pattern(64, 48, (x, y) => ((byte)(x * 4), (byte)(y * 5), (byte)77)), // 3072 colours: quantized
            Pattern(64, 48, (x, y) => (x + y) % 2 == 0 ? ((byte)10, (byte)20, (byte)30) : ((byte)240, (byte)230, (byte)220)),
        };
        byte[] gif = GifEncoder.EncodeAnimated(frames, delayMs: 100);

        var (decoded, w, h) = TestImages.DecodeGif(gif);
        Assert.Equal(3, decoded.Count);
        Assert.Equal(64, w);
        Assert.Equal(48, h);
        AssertSamePixels(frames[0], decoded[0], 0);
        AssertSamePixels(frames[2], decoded[2], 0);
        AssertSamePixels(frames[1], decoded[1], 24);
    }

    [Fact]
    public void Large_noisy_frame_round_trips_through_lzw_table_resets()
    {
        // 200 distinct colours in random order: lossless palette, and incompressible enough
        // to fill the 4096-entry LZW table many times (exercises code-width growth and Clear codes).
        var rng = new Random(1234);
        var palette = Enumerable.Range(0, 200).Select(_ => ((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256))).ToArray();
        var frame = Pattern(320, 240, (_, _) => palette[rng.Next(palette.Length)]);
        var second = Pattern(320, 240, (x, y) => palette[(x / 7 + y / 5) % palette.Length]);

        byte[] gif = GifEncoder.EncodeAnimated([frame, second], delayMs: 40);

        var (decoded, _, _) = TestImages.DecodeGif(gif);
        Assert.Equal(2, decoded.Count);
        AssertSamePixels(frame, decoded[0], 0);
        AssertSamePixels(second, decoded[1], 0);
    }

    [Fact]
    public void Smooth_gradient_is_quantized_within_tolerance()
    {
        var frame = Pattern(256, 128, (x, y) => ((byte)x, (byte)(y * 2), (byte)(255 - x / 2)));
        byte[] gif = GifEncoder.EncodeAnimated([frame], delayMs: 0);

        var decoded = TestImages.DecodeGif(gif).Frames.Single();

        long totalError = 0;
        int maxError = 0;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                var e = frame.GetRgb(x, y);
                var a = decoded.GetRgb(x, y);
                int err = Math.Max(Math.Abs(e.R - a.R), Math.Max(Math.Abs(e.G - a.G), Math.Abs(e.B - a.B)));
                totalError += err;
                maxError = Math.Max(maxError, err);
            }
        double meanError = (double)totalError / (frame.Width * frame.Height);
        Assert.True(maxError <= 24, $"max channel error {maxError}");
        Assert.True(meanError <= 8, $"mean channel error {meanError:F2}");
    }

    [Fact]
    public void Writes_header_loop_extension_and_delay()
    {
        var frame = TestImages.Solid(3, 2, 1, 2, 3);
        byte[] gif = GifEncoder.EncodeAnimated([frame, frame], delayMs: 250, loopCount: 3);

        Assert.Equal("GIF89a"u8.ToArray(), gif[..6]);
        Assert.Equal(3, gif[6] | gif[7] << 8);
        Assert.Equal(2, gif[8] | gif[9] << 8);
        Assert.Equal(0x3B, gif[^1]);

        int netscape = IndexOf(gif, "NETSCAPE2.0"u8.ToArray());
        Assert.True(netscape > 0);
        Assert.Equal(new byte[] { 0x03, 0x01, 0x03, 0x00, 0x00 }, gif[(netscape + 11)..(netscape + 16)]);

        int gce = IndexOf(gif, [0x21, 0xF9, 0x04]);
        Assert.Equal(25, gif[gce + 4] | gif[gce + 5] << 8); // 250 ms = 25 cs

        Assert.Equal(2, TestImages.DecodeGif(gif).Frames.Count);
    }

    [Fact]
    public void Single_pixel_and_single_colour_frames_decode()
    {
        var one = TestImages.Solid(1, 1, 200, 100, 50);
        var decoded = TestImages.DecodeGif(GifEncoder.EncodeAnimated([one], 10)).Frames.Single();
        Assert.Equal(one.GetRgb(0, 0), decoded.GetRgb(0, 0));

        var flat = TestImages.Solid(500, 300, 9, 99, 199);
        AssertSamePixels(flat, TestImages.DecodeGif(GifEncoder.EncodeAnimated([flat], 10)).Frames.Single(), 0);
    }

    [Fact]
    public void Rejects_mismatched_frame_sizes_and_empty_input()
    {
        Assert.Throws<ArgumentException>(() => GifEncoder.EncodeAnimated([new PixelBuffer(4, 4), new PixelBuffer(4, 5)], 100));
        Assert.Throws<ArgumentException>(() => GifEncoder.EncodeAnimated([], 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => GifEncoder.EncodeAnimated([new PixelBuffer(4, 4)], -1));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }
}
