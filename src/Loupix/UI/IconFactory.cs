using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using static Loupix.Native.NativeMethods;

namespace Loupix.UI;

/// <summary>
/// Draws the Loupix logo in code, so it is crisp at every size: a loupe revealing chunky pixels, with the picked one in the middle.
/// Same numbers as assets/logo/generate.py, which builds the .ico and the README logo; change the two together.
/// </summary>
public static class IconFactory
{
    private const double LensCentre = 0.445, RingRadius = 0.255, HandleEnd = 0.795, Pitch = 0.135, Cell = 0.115, SmallPixel = 0.17;

    private static readonly Color TileTop = Color.FromRgb(0x22, 0x34, 0x5C), TileBottom = Color.FromRgb(0x0D, 0x16, 0x30);
    private static readonly Color Ring = Color.FromRgb(0xF6, 0xF2, 0xE7), Amber = Color.FromRgb(0xFF, 0xB6, 0x27), Lens = Color.FromRgb(0x0B, 0x12, 0x26);

    private static readonly uint[,] Pixels =
    {
        { 0x2DD4BF, 0x38BDF8, 0x60A5FA },
        { 0x34D399, 0xFFB627, 0x818CF8 },
        { 0xA3E635, 0xF472B6, 0xC084FC },
    };

    /// <summary>The full-colour app logo. At 48 px and below the 3x3 grid would turn to mush, so only the picked pixel stays.</summary>
    public static BitmapSource Logo(int size)
    {
        bool small = size <= 48;
        double s = size, ringWidth = s * (small ? 0.10 : 0.085), handleWidth = s * (small ? 0.14 : 0.125);
        double c = s * LensCentre, r = s * RingRadius, inner = r - ringWidth / 2, outer = r + ringWidth / 2;
        var centre = new Point(c, c);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var tile = new LinearGradientBrush(TileTop, TileBottom, new Point(0, 0), new Point(1, 1));
            dc.DrawRoundedRectangle(tile, null, new Rect(0, 0, s, s), s * 0.23, s * 0.23);

            double start = c + (outer - s * 0.015) * Math.Cos(Math.PI / 4);
            var handle = new Pen(new SolidColorBrush(Amber), handleWidth) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Round };
            dc.DrawLine(handle, new Point(start, start), new Point(s * HandleEnd, s * HandleEnd));

            dc.DrawEllipse(new SolidColorBrush(Lens), null, centre, inner, inner);
            dc.PushClip(new EllipseGeometry(centre, inner, inner));
            if (small)
            {
                double p = s * SmallPixel;
                dc.DrawRectangle(new SolidColorBrush(Amber), null, new Rect(c - p / 2, c - p / 2, p, p));
            }
            else
            {
                double cell = s * Cell, pitch = s * Pitch;
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 3; col++)
                    {
                        uint v = Pixels[row, col];
                        var brush = new SolidColorBrush(Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v));
                        dc.DrawRectangle(brush, null, new Rect(c + (col - 1) * pitch - cell / 2, c + (row - 1) * pitch - cell / 2, cell, cell));
                    }
            }
            dc.Pop();

            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Ring), ringWidth), centre, r, r);
        }
        return Render(dv, size);
    }

    /// <summary>A one-colour loupe for the notification area, matched to the taskbar theme.</summary>
    public static BitmapSource TrayGlyph(int size)
    {
        var brush = TaskbarIsLight() ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)) : Brushes.White;
        double s = size, c = s * 0.41, r = s * 0.30, ringWidth = Math.Max(1.5, s * 0.125), pixel = s * 0.17;
        double start = c + (r + ringWidth / 2 - s * 0.02) * Math.Cos(Math.PI / 4), end = s * 0.86;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawEllipse(null, new Pen(brush, ringWidth), new Point(c, c), r, r);
            dc.DrawLine(new Pen(brush, Math.Max(2, s * 0.17)) { EndLineCap = PenLineCap.Round }, new Point(start, start), new Point(end, end));
            dc.DrawRectangle(brush, null, new Rect(c - pixel / 2, c - pixel / 2, pixel, pixel));
        }
        return Render(dv, size);
    }

    private static BitmapSource Render(Visual v, int size)
    {
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        rtb.Freeze();
        return rtb;
    }

    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Creates an HICON from a 32-bit bitmap. The caller owns the handle (DestroyIcon).</summary>
    public static IntPtr ToHIcon(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var pixels = new byte[w * h * 4];
        new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, w * 4, 0);

        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr color = CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, screen);
        Marshal.Copy(pixels, 0, bits, pixels.Length);

        IntPtr mask = CreateBitmap(w, h, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { fIcon = 1, hbmColor = color, hbmMask = mask };
        IntPtr icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    /// <summary>Writes a multi-resolution .ico file (PNG-compressed entries).</summary>
    public static void WriteIco(string path, params int[] sizes)
    {
        var pngs = sizes.Select(s =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Logo(s)));
            using var ms = new System.IO.MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }).ToList();

        using var fs = System.IO.File.Create(path);
        using var bw = new System.IO.BinaryWriter(fs);
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)1); bw.Write((short)32);
            bw.Write(pngs[i].Length);
            bw.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) bw.Write(p);
    }
}
