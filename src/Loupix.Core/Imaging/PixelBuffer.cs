namespace Loupix.Core.Imaging;

/// <summary>
/// A 32-bit BGRA (premultiplied is NOT assumed; screen captures are opaque) pixel buffer.
/// This is the interchange type between capture, editor and the services in Loupix.Core.
/// Row stride is always Width * 4, with no padding.
/// </summary>
public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public int Stride => Width * 4;

    public PixelBuffer(int width, int height)
        : this(width, height, new byte[checked(width * height * 4)]) { }

    public PixelBuffer(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Size must be positive.");
        if (bgra.Length < width * height * 4) throw new ArgumentException("Buffer too small.", nameof(bgra));
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public int IndexOf(int x, int y) => (y * Width + x) * 4;

    public uint GetPixel(int x, int y)
    {
        int i = IndexOf(x, y);
        return (uint)(Bgra[i] | Bgra[i + 1] << 8 | Bgra[i + 2] << 16 | Bgra[i + 3] << 24);
    }

    /// <summary>Returns (R, G, B) for the pixel at (x, y).</summary>
    public (byte R, byte G, byte B) GetRgb(int x, int y)
    {
        int i = IndexOf(x, y);
        return (Bgra[i + 2], Bgra[i + 1], Bgra[i]);
    }

    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        int i = IndexOf(x, y);
        Bgra[i] = b; Bgra[i + 1] = g; Bgra[i + 2] = r; Bgra[i + 3] = a;
    }

    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public PixelBuffer Clone() => new(Width, Height, (byte[])Bgra.Clone());

    /// <summary>Copies a rectangle out of this buffer. The rectangle is clamped to the buffer bounds.</summary>
    public PixelBuffer Crop(int x, int y, int width, int height)
    {
        int x0 = Math.Clamp(x, 0, Width), y0 = Math.Clamp(y, 0, Height);
        int x1 = Math.Clamp(x + width, 0, Width), y1 = Math.Clamp(y + height, 0, Height);
        int w = Math.Max(1, x1 - x0), h = Math.Max(1, y1 - y0);
        var result = new PixelBuffer(w, h);
        for (int row = 0; row < h && y0 + row < Height; row++)
        {
            Buffer.BlockCopy(Bgra, IndexOf(x0, y0 + row), result.Bgra, row * result.Stride, Math.Min(w, Width - x0) * 4);
        }
        return result;
    }

    /// <summary>Copies <paramref name="source"/> into this buffer with its top-left at (x, y), clipping as needed.</summary>
    public void Blit(PixelBuffer source, int x, int y)
    {
        for (int row = 0; row < source.Height; row++)
        {
            int ty = y + row;
            if (ty < 0 || ty >= Height) continue;
            int sx = Math.Max(0, -x);
            int tx = x + sx;
            int count = Math.Min(source.Width - sx, Width - tx);
            if (count <= 0) continue;
            Buffer.BlockCopy(source.Bgra, source.IndexOf(sx, row), Bgra, IndexOf(tx, ty), count * 4);
        }
    }
}
