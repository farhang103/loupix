using Loupix.Core.Imaging;

namespace Loupix.Services.Gif;

/// <summary>Writes animated GIF89a files.</summary>
public static class GifEncoder
{
    /// <summary>
    /// Encodes <paramref name="frames"/> as an animated GIF. Each frame gets its own local palette of up to
    /// 256 colours (octree quantization, no dithering); frames with ≤ 256 distinct colours are lossless.
    /// Alpha is ignored: frames are treated as opaque.
    /// </summary>
    /// <param name="frames">At least one frame; all must have the same size (at most 65535×65535).</param>
    /// <param name="delayMs">Per-frame delay. GIF stores centiseconds, so this is rounded to 10 ms; a positive delay
    /// is never written below 20 ms because browsers replace 0–10 ms delays with 100 ms.</param>
    /// <param name="loopCount">NETSCAPE2.0 loop count: 0 loops forever.</param>
    /// <exception cref="ArgumentException">No frames, or frames of different sizes.</exception>
    public static byte[] EncodeAnimated(IReadOnlyList<PixelBuffer> frames, int delayMs, int loopCount = 0)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0) throw new ArgumentException("At least one frame is required.", nameof(frames));
        ArgumentOutOfRangeException.ThrowIfNegative(delayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(loopCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(loopCount, ushort.MaxValue);

        int width = frames[0]?.Width ?? throw new ArgumentException("Frame 0 is null.", nameof(frames));
        int height = frames[0].Height;
        if (width > ushort.MaxValue || height > ushort.MaxValue)
            throw new ArgumentException($"GIF frames are limited to 65535×65535; got {width}×{height}.", nameof(frames));
        for (int i = 1; i < frames.Count; i++)
        {
            var f = frames[i] ?? throw new ArgumentException($"Frame {i} is null.", nameof(frames));
            if (f.Width != width || f.Height != height)
                throw new ArgumentException($"Frame {i} is {f.Width}×{f.Height} but frame 0 is {width}×{height}; all frames must be the same size.", nameof(frames));
        }

        int delayCs = (int)Math.Round(delayMs / 10.0, MidpointRounding.AwayFromZero);
        if (delayMs > 0 && delayCs < 2) delayCs = 2;
        delayCs = Math.Min(delayCs, ushort.MaxValue);

        using var ms = new MemoryStream();

        // Header + logical screen descriptor (no global colour table; 8-bit colour resolution).
        ms.Write("GIF89a"u8);
        WriteUInt16(ms, width);
        WriteUInt16(ms, height);
        ms.WriteByte(0x70);
        ms.WriteByte(0); // background colour index
        ms.WriteByte(0); // pixel aspect ratio

        // NETSCAPE2.0 application extension: loop count.
        ms.Write([0x21, 0xFF, 0x0B]);
        ms.Write("NETSCAPE2.0"u8);
        ms.Write([0x03, 0x01]);
        WriteUInt16(ms, loopCount);
        ms.WriteByte(0);

        foreach (var frame in frames)
        {
            var (palette, indices) = OctreeQuantizer.Quantize(frame);
            int colors = palette.Length / 3;
            int bits = 1;
            while (1 << bits < colors) bits++;

            // Graphic control extension: disposal 1 (leave in place), no transparency.
            ms.Write([0x21, 0xF9, 0x04, 0x04]);
            WriteUInt16(ms, delayCs);
            ms.WriteByte(0); // transparent colour index (unused)
            ms.WriteByte(0);

            // Image descriptor with a local colour table of 2^bits entries.
            ms.WriteByte(0x2C);
            WriteUInt16(ms, 0);
            WriteUInt16(ms, 0);
            WriteUInt16(ms, width);
            WriteUInt16(ms, height);
            ms.WriteByte((byte)(0x80 | (bits - 1)));

            ms.Write(palette);
            for (int pad = colors; pad < 1 << bits; pad++) ms.Write([0, 0, 0]);

            LzwEncoder.Encode(ms, indices, Math.Max(2, bits));
        }

        ms.WriteByte(0x3B); // trailer
        return ms.ToArray();
    }

    private static void WriteUInt16(Stream s, int value)
    {
        s.WriteByte((byte)value);
        s.WriteByte((byte)(value >> 8));
    }
}
