using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Loupix.Core.Imaging;

namespace Loupix.Raster;

/// <summary>Low-level pixel helpers. Pixels are handled as packed BGRA uints (B in the low byte).</summary>
internal static class Px
{
    public const uint RgbMask = 0x00FFFFFF;

    public static Span<uint> Pixels(PixelBuffer img) =>
        MemoryMarshal.Cast<byte, uint>(img.Bgra.AsSpan(0, img.Width * img.Height * 4));

    /// <summary>Largest absolute difference over the B, G and R channels (alpha ignored).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Diff(uint a, uint b)
    {
        int d0 = Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));
        int d1 = Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF));
        int d2 = Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
        return Math.Max(d0, Math.Max(d1, d2));
    }

    /// <summary>True when every colour channel of a and b differs by at most tol.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Near(uint a, uint b, int tol) => ((a ^ b) & RgbMask) == 0 || Diff(a, b) <= tol;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Pack(int b, int g, int r, int a = 255) =>
        (uint)(Math.Clamp(b, 0, 255) | Math.Clamp(g, 0, 255) << 8 | Math.Clamp(r, 0, 255) << 16 | Math.Clamp(a, 0, 255) << 24);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int B(uint p) => (int)(p & 0xFF);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int G(uint p) => (int)((p >> 8) & 0xFF);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int R(uint p) => (int)((p >> 16) & 0xFF);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int A(uint p) => (int)(p >> 24);

    /// <summary>Per-channel median of a set of pixels (alpha forced opaque).</summary>
    public static uint Median(ReadOnlySpan<uint> pixels)
    {
        if (pixels.IsEmpty) return 0xFF000000;
        Span<int> hb = stackalloc int[256], hg = stackalloc int[256], hr = stackalloc int[256];
        foreach (uint p in pixels) { hb[B(p)]++; hg[G(p)]++; hr[R(p)]++; }
        int half = (pixels.Length + 1) / 2;
        return Pack(Nth(hb, half), Nth(hg, half), Nth(hr, half));

        static int Nth(Span<int> h, int n)
        {
            int acc = 0;
            for (int i = 0; i < 256; i++) { acc += h[i]; if (acc >= n) return i; }
            return 255;
        }
    }
}
