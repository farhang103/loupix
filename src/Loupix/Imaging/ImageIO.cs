using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Loupix.Core.Imaging;
using Loupix.Services;

namespace Loupix.Imaging;

public static class ImageIO
{
    public static BitmapSource ToBitmapSource(PixelBuffer pb, double dpiScale = 1.0)
    {
        var bmp = BitmapSource.Create(pb.Width, pb.Height, 96 * dpiScale, 96 * dpiScale, PixelFormats.Bgra32, null, pb.Bgra, pb.Stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Same pixels, but tagged 96 DPI so WPF draws 1 bitmap pixel per DIP unit.</summary>
    public static BitmapSource ToBitmapSource96(PixelBuffer pb) => ToBitmapSource(pb, 1.0);

    public static PixelBuffer FromBitmapSource(BitmapSource src)
    {
        BitmapSource s = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var pb = new PixelBuffer(s.PixelWidth, s.PixelHeight);
        s.CopyPixels(pb.Bgra, pb.Stride, 0);
        return pb;
    }

    public static PixelBuffer? LoadFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            return FromBitmapSource(decoder.Frames[0]);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
            return null;
        }
    }

    public static byte[] EncodePng(BitmapSource bmp, double dpiScale = 1.0)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(WithDpi(bmp, dpiScale)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] EncodeJpeg(BitmapSource bmp, int quality, double dpiScale = 1.0)
    {
        // JPEG has no alpha: flatten onto white.
        var flat = Flatten(bmp, Colors.White);
        var enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 10, 100) };
        enc.Frames.Add(BitmapFrame.Create(WithDpi(flat, dpiScale)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static BitmapSource Flatten(BitmapSource bmp, Color background)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
            dc.DrawImage(bmp, new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
        }
        var rtb = new RenderTargetBitmap(bmp.PixelWidth, bmp.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static BitmapSource WithDpi(BitmapSource bmp, double dpiScale)
    {
        double dpi = 96 * dpiScale;
        if (Math.Abs(bmp.DpiX - dpi) < 0.01) return bmp;
        var pb = FromBitmapSource(bmp);
        return ToBitmapSource(pb, dpiScale);
    }

    /// <summary>Picks PNG for UI/text-heavy images and JPEG for photographic ones, by counting distinct colours in a sample.</summary>
    public static SaveFormat ChooseAutoFormat(PixelBuffer pb)
    {
        var colors = new HashSet<uint>();
        int step = Math.Max(1, (int)Math.Sqrt((double)pb.Width * pb.Height / 40000));
        int samples = 0;
        for (int y = 0; y < pb.Height; y += step)
            for (int x = 0; x < pb.Width; x += step)
            {
                colors.Add(pb.GetPixel(x, y) & 0xF8F8F8);
                samples++;
            }
        return colors.Count > samples * 0.25 ? SaveFormat.Jpeg : SaveFormat.Png;
    }

    /// <summary>Puts the image on the clipboard as PNG (keeps transparency) and as a DIB for older apps.</summary>
    public static bool CopyToClipboard(BitmapSource bmp)
    {
        var data = new DataObject();
        data.SetImage(Flatten(bmp, Colors.White));
        var png = new MemoryStream(EncodePng(bmp));
        data.SetData("PNG", png, false);
        return SetClipboard(data);
    }

    public static bool CopyText(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        return SetClipboard(data);
    }

    public static bool CopyFile(string path)
    {
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
        return SetClipboard(data);
    }

    private static bool SetClipboard(IDataObject data)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(30); // Another app holds the clipboard.
            }
        }
        Log.Info("Clipboard busy; copy failed.");
        return false;
    }

    public static PixelBuffer? ClipboardImage()
    {
        try
        {
            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream ms)
            {
                var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return FromBitmapSource(dec.Frames[0]);
            }
            if (Clipboard.ContainsFileDropList())
            {
                foreach (string? f in Clipboard.GetFileDropList())
                    if (f != null && IsImageFile(f)) return LoadFile(f);
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource bs)
            {
                var pb = FromBitmapSource(bs);
                // DIBs on the clipboard often carry a zero alpha channel.
                bool allZero = true;
                for (int i = 3; i < pb.Bgra.Length; i += 4) if (pb.Bgra[i] != 0) { allZero = false; break; }
                if (allZero) for (int i = 3; i < pb.Bgra.Length; i += 4) pb.Bgra[i] = 255;
                return pb;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard read failed", ex);
        }
        return null;
    }

    public static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" or ".ico" or ".heic";
}

public static class FileNamer
{
    /// <summary>
    /// Expands a template such as "SCR-{yyyyMMdd}-{timecode}". {timecode} is the 4-letter code Shottr uses:
    /// 26³ + ⌊4 × seconds since local midnight⌋ written in base 26 with a–z ("baaa" at midnight … "urgh"),
    /// so names sort chronologically. Any other {...} is a .NET date format string.
    /// </summary>
    public static string Expand(string template, DateTime time)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(template, @"\{([^}]+)\}", m =>
        {
            var token = m.Groups[1].Value;
            if (token.Equals("timecode", StringComparison.OrdinalIgnoreCase)) return TimeCode(time);
            try { return time.ToString(token); } catch { return m.Value; }
        });
        foreach (var c in Path.GetInvalidFileNameChars()) result = result.Replace(c, '-');
        return string.IsNullOrWhiteSpace(result) ? "Loupix" : result;
    }

    public static string TimeCode(DateTime time)
    {
        int value = 17576 + (int)Math.Floor(time.TimeOfDay.TotalSeconds * 4);
        var chars = new char[4];
        for (int i = 3; i >= 0; i--) { chars[i] = (char)('a' + value % 26); value /= 26; }
        return new string(chars);
    }

    public static string UniquePath(string folder, string baseName, string ext)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, baseName + ext);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{baseName}-{i}{ext}");
        return path;
    }
}
