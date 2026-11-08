using Loupix.Core.Imaging;

namespace Loupix.Services.Imaging;

internal static class AlphaHelper
{
    /// <summary>
    /// True when the alpha channel carries transparency that should be honoured. A buffer whose alpha is
    /// 0 everywhere is treated as opaque: GDI captures (BitBlt into a 32-bpp DIB) leave alpha at 0.
    /// </summary>
    public static bool HasMeaningfulAlpha(PixelBuffer image)
    {
        var px = image.Bgra;
        int n = image.Width * image.Height * 4;
        bool anyNonZero = false, anyTranslucent = false;
        for (int i = 3; i < n; i += 4)
        {
            byte a = px[i];
            if (a != 0) anyNonZero = true;
            if (a != 255) anyTranslucent = true;
            if (anyNonZero && anyTranslucent) return true;
        }
        return false;
    }

    /// <summary>Returns an opaque copy, compositing translucent pixels over the given background colour when alpha is meaningful.</summary>
    public static PixelBuffer Flatten(PixelBuffer image, byte bgR = 255, byte bgG = 255, byte bgB = 255)
    {
        var copy = image.Clone();
        var px = copy.Bgra;
        int n = copy.Width * copy.Height * 4;
        if (!HasMeaningfulAlpha(image))
        {
            for (int i = 3; i < n; i += 4) px[i] = 255;
            return copy;
        }
        for (int i = 0; i < n; i += 4)
        {
            int a = px[i + 3];
            if (a == 255) continue;
            int ia = 255 - a;
            px[i] = (byte)((px[i] * a + bgB * ia + 127) / 255);
            px[i + 1] = (byte)((px[i + 1] * a + bgG * ia + 127) / 255);
            px[i + 2] = (byte)((px[i + 2] * a + bgR * ia + 127) / 255);
            px[i + 3] = 255;
        }
        return copy;
    }
}
