using System.Runtime.InteropServices;
using System.Text;
using Loupix.Core.Imaging;
using static Loupix.Native.NativeMethods;

namespace Loupix.Capture;

/// <summary>A physical display. All rectangles are in physical (device) pixels in virtual-screen coordinates.</summary>
public sealed record MonitorInfo(IntPtr Handle, RectI Bounds, RectI WorkArea, double Scale, bool IsPrimary, string DeviceName);

public static class Monitors
{
    public static IReadOnlyList<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            list.Add(FromHandle(h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static MonitorInfo FromHandle(IntPtr h)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(h, ref mi);
        double scale = 1.0;
        if (GetDpiForMonitor(h, 0, out uint dpiX, out _) == 0) scale = dpiX / 96.0;
        return new MonitorInfo(h,
            RectI.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
            RectI.FromLTRB(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom),
            scale, (mi.dwFlags & 1) != 0, mi.szDevice ?? "");
    }

    public static MonitorInfo FromPoint(int x, int y) => FromHandle(MonitorFromPoint(new POINT(x, y), MONITOR_DEFAULTTONEAREST));

    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    public static MonitorInfo AtCursor()
    {
        var (x, y) = CursorPosition();
        return FromPoint(x, y);
    }

    public static RectI VirtualScreen()
    {
        var all = All();
        var r = all[0].Bounds;
        foreach (var m in all.Skip(1)) r = r.Union(m.Bounds);
        return r;
    }
}

public static class ScreenCapture
{
    /// <summary>Grabs a rectangle of the virtual screen (physical pixels) using GDI.</summary>
    public static PixelBuffer Capture(RectI rect, bool includeCursor = false)
    {
        int w = rect.Width, h = rect.Height;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        IntPtr dib = CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(memDc, dib);
        try
        {
            BitBlt(memDc, 0, 0, w, h, screenDc, rect.X, rect.Y, SRCCOPY | CAPTUREBLT);
            if (includeCursor) DrawCursor(memDc, rect.X, rect.Y);
            var buffer = new PixelBuffer(w, h);
            Marshal.Copy(bits, buffer.Bgra, 0, w * h * 4);
            // GDI leaves alpha undefined; screen pixels are opaque.
            var data = buffer.Bgra;
            for (int i = 3; i < data.Length; i += 4) data[i] = 255;
            return buffer;
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void DrawCursor(IntPtr hdc, int originX, int originY)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0) return;
        if (!GetIconInfo(ci.hCursor, out var ii)) return;
        DrawIconEx(hdc, ci.ptScreenPos.X - ii.xHotspot - originX, ci.ptScreenPos.Y - ii.yHotspot - originY, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
    }
}

/// <summary>A top-level window that can be captured.</summary>
public sealed record WindowInfo(IntPtr Handle, RectI Bounds, string Title, string ClassName);

public static class WindowFinder
{
    /// <summary>Visible top-level windows in z-order (front first), using DWM frame bounds (no invisible resize borders).</summary>
    public static List<WindowInfo> VisibleWindows(IEnumerable<IntPtr>? exclude = null)
    {
        var skip = new HashSet<IntPtr>(exclude ?? []);
        var result = new List<WindowInfo>();
        uint myPid = (uint)Environment.ProcessId;
        EnumWindows((hWnd, _) =>
        {
            if (skip.Contains(hWnd) || !IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            long ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_LAYERED) != 0) return true; // click-through overlays
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == myPid) return true;
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hWnd, out r);
            if (r.Width < 8 || r.Height < 8) return true;
            var cls = new StringBuilder(256);
            GetClassName(hWnd, cls, cls.Capacity);
            string className = cls.ToString();
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            var title = new StringBuilder(512);
            GetWindowText(hWnd, title, title.Capacity);
            result.Add(new WindowInfo(hWnd, RectI.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), title.ToString(), className));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static WindowInfo? At(IReadOnlyList<WindowInfo> windows, int x, int y) =>
        windows.FirstOrDefault(w => w.Bounds.Contains(x, y));

    public static WindowInfo? Foreground(IReadOnlyList<WindowInfo> windows)
    {
        var fg = GetForegroundWindow();
        var root = GetAncestor(fg, GA_ROOTOWNER);
        return windows.FirstOrDefault(w => w.Handle == fg) ?? windows.FirstOrDefault(w => w.Handle == root);
    }
}
