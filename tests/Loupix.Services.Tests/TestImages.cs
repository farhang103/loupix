using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Loupix.Core.Imaging;

namespace Loupix.Services.Tests;

/// <summary>Builds test images. WPF work runs on a dedicated STA thread.</summary>
internal static class TestImages
{
    public static T OnSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new AggregateException(error);
        return result;
    }

    /// <summary>Renders lines of text (black on white, Segoe UI) at the given origins. Returns the image.</summary>
    public static PixelBuffer RenderText(int width, int height, double fontSize, params (string Text, double X, double Y)[] runs) =>
        OnSta(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                foreach (var (text, x, y) in runs)
                {
                    var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), fontSize, Brushes.Black, 1.0);
                    dc.DrawText(ft, new Point(x, y));
                }
            }
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var bytes = new byte[width * height * 4];
            rtb.CopyPixels(bytes, width * 4, 0);
            return new PixelBuffer(width, height, bytes);
        });

    public static PixelBuffer Solid(int width, int height, byte r, byte g, byte b)
    {
        var img = new PixelBuffer(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                img.SetPixel(x, y, r, g, b);
        return img;
    }

    /// <summary>Decodes a GIF with WPF's decoder (independent of our encoder) into BGRA frames.</summary>
    public static (List<PixelBuffer> Frames, int Width, int Height) DecodeGif(byte[] gif) =>
        OnSta(() =>
        {
            using var ms = new MemoryStream(gif);
            var decoder = new GifBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frames = new List<PixelBuffer>();
            foreach (var frame in decoder.Frames)
            {
                var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                int w = bgra.PixelWidth, h = bgra.PixelHeight;
                var bytes = new byte[w * h * 4];
                bgra.CopyPixels(bytes, w * 4, 0);
                frames.Add(new PixelBuffer(w, h, bytes));
            }
            var meta = decoder.Frames[0];
            return (frames, meta.PixelWidth, meta.PixelHeight);
        });
}
