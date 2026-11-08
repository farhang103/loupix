using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Loupix.Core.Imaging;
using Loupix.Imaging;
using Loupix.Services;
using static Loupix.Native.NativeMethods;

namespace Loupix.Capture;

/// <summary>Captures a single window (even when partly covered) and applies the window-capture style.</summary>
public static class WindowCapture
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(int action, int param, StringBuilder value, int winIni);

    private const uint PW_RENDERFULLCONTENT = 2;

    public static PixelBuffer Capture(WindowInfo w, PixelBuffer? frozen, RectI frozenOrigin, double scale)
    {
        PixelBuffer? img = null;
        try { img = PrintWindowCapture(w); } catch (Exception ex) { Log.Error("PrintWindow failed", ex); }
        if (img == null || LooksBlank(img))
        {
            if (frozen == null) return ScreenCapture.Capture(w.Bounds);
            var local = w.Bounds.Offset(-frozenOrigin.X, -frozenOrigin.Y);
            img = frozen.Crop(local.X, local.Y, local.Width, local.Height);
        }
        if (Environment.OSVersion.Version.Build >= 22000 && !IsMaximized(w)) RoundCorners(img, (int)Math.Round(8 * scale));
        return img;
    }

    private static bool IsMaximized(WindowInfo w)
    {
        var m = Monitors.FromPoint(w.Bounds.X + w.Bounds.Width / 2, w.Bounds.Y + w.Bounds.Height / 2);
        return w.Bounds.Width >= m.WorkArea.Width && w.Bounds.Height >= m.WorkArea.Height;
    }

    private static PixelBuffer? PrintWindowCapture(WindowInfo w)
    {
        if (!GetWindowRect(w.Handle, out var wr) || wr.Width <= 0 || wr.Height <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = wr.Width, biHeight = -wr.Height, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, dib);
        try
        {
            if (!PrintWindow(w.Handle, mem, PW_RENDERFULLCONTENT)) return null;
            var full = new PixelBuffer(wr.Width, wr.Height);
            Marshal.Copy(bits, full.Bgra, 0, full.Bgra.Length);
            for (int i = 3; i < full.Bgra.Length; i += 4) full.Bgra[i] = 255;
            // Trim the invisible resize borders: crop to the DWM frame bounds.
            var frame = w.Bounds.Offset(-wr.Left, -wr.Top).ClampTo(wr.Width, wr.Height);
            return frame.IsEmpty ? full : full.Crop(frame.X, frame.Y, frame.Width, frame.Height);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static bool LooksBlank(PixelBuffer p)
    {
        int step = Math.Max(1, p.Width * p.Height / 5000);
        var d = p.Bgra;
        for (int i = 0; i < d.Length; i += step * 4)
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 0) return false;
        return true;
    }

    private static void RoundCorners(PixelBuffer p, int r)
    {
        if (r <= 0 || p.Width < r * 2 || p.Height < r * 2) return;
        for (int y = 0; y < r; y++)
            for (int x = 0; x < r; x++)
            {
                double dx = r - x - 0.5, dy = r - y - 0.5;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double cover = Math.Clamp(r - dist + 0.5, 0, 1);
                if (cover >= 1) continue;
                byte a = (byte)(cover * 255);
                foreach (var (cx, cy) in new[] { (x, y), (p.Width - 1 - x, y), (x, p.Height - 1 - y), (p.Width - 1 - x, p.Height - 1 - y) })
                    p.Bgra[p.IndexOf(cx, cy) + 3] = Math.Min(p.Bgra[p.IndexOf(cx, cy) + 3], a);
            }
    }

    /// <summary>Adds the configured surroundings: soft shadow on transparency, solid colour or wallpaper.</summary>
    public static PixelBuffer ApplyStyle(PixelBuffer window, WindowCaptureStyle style, double scale, RectI screenBounds)
    {
        if (style == WindowCaptureStyle.NoShadow) return window;
        int pad = (int)Math.Round(48 * scale);
        int w = window.Width + pad * 2, h = window.Height + pad * 2;
        var src = ImageIO.ToBitmapSource96(window);

        var grid = new Grid { Width = w, Height = h };
        if (style == WindowCaptureStyle.SolidBackground)
        {
            var c = (Color)ColorConverter.ConvertFromString(SettingsStore.Current.WindowBackgroundColor);
            grid.Background = new SolidColorBrush(c);
        }
        else if (style == WindowCaptureStyle.Wallpaper && WallpaperBrush(screenBounds, pad) is Brush wb)
        {
            grid.Background = wb;
        }
        var img = new Image
        {
            Source = src,
            Width = window.Width,
            Height = window.Height,
            Effect = new DropShadowEffect { BlurRadius = 34 * scale, ShadowDepth = 10 * scale, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        grid.Children.Add(img);
        grid.Measure(new Size(w, h));
        grid.Arrange(new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(grid);
        return ImageIO.FromBitmapSource(rtb);
    }

    private static Brush? WallpaperBrush(RectI windowBounds, int pad)
    {
        try
        {
            var sb = new StringBuilder(520);
            if (!SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, sb.Capacity, sb, 0)) return null;
            var path = sb.ToString();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            // Map the area around the window from the monitor onto the wallpaper (fill mode).
            var m = Monitors.FromPoint(windowBounds.X + windowBounds.Width / 2, windowBounds.Y + windowBounds.Height / 2);
            double fill = Math.Max((double)m.Bounds.Width / bmp.PixelWidth, (double)m.Bounds.Height / bmp.PixelHeight);
            double offX = (m.Bounds.Width - bmp.PixelWidth * fill) / 2, offY = (m.Bounds.Height - bmp.PixelHeight * fill) / 2;
            double x = (windowBounds.X - pad - m.Bounds.X - offX) / fill, y = (windowBounds.Y - pad - m.Bounds.Y - offY) / fill;
            double vw = (windowBounds.Width + pad * 2) / fill, vh = (windowBounds.Height + pad * 2) / fill;
            var brush = new ImageBrush(bmp) { Viewbox = new Rect(x, y, vw, vh), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }
        catch (Exception ex)
        {
            Log.Error("Wallpaper lookup failed", ex);
            return null;
        }
    }
}
