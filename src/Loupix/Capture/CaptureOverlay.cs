using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Loupix.Core.Imaging;
using Loupix.Imaging;
using static Loupix.Native.NativeMethods;

namespace Loupix.Capture;

public enum OverlayMode { Area, Window }

/// <summary>What the user picked on the overlay. Rect is in virtual-screen physical pixels.</summary>
public sealed record OverlayResult(RectI Rect, MonitorInfo Monitor, WindowInfo? Window);

/// <summary>
/// Full-screen selection UI drawn over a frozen screenshot: one borderless window per monitor
/// (so each renders at its own DPI). Area mode = drag a rectangle; window mode = click a highlighted window.
/// </summary>
public static class CaptureOverlay
{
    public static Task<OverlayResult?> SelectAsync(OverlayMode mode, PixelBuffer frozen, RectI virtualBounds, IReadOnlyList<WindowInfo> windows, string? hint = null)
    {
        var tcs = new TaskCompletionSource<OverlayResult?>();
        var monitors = Monitors.All();
        var overlays = new List<OverlayWindow>();
        void Finish(OverlayResult? r)
        {
            foreach (var o in overlays) o.CloseQuietly();
            tcs.TrySetResult(r);
        }
        foreach (var m in monitors)
        {
            var crop = frozen.Crop(m.Bounds.X - virtualBounds.X, m.Bounds.Y - virtualBounds.Y, m.Bounds.Width, m.Bounds.Height);
            var w = new OverlayWindow(mode, m, crop, windows, hint, Finish);
            overlays.Add(w);
        }
        foreach (var o in overlays) o.ShowOnMonitor();
        // Keyboard focus goes to the overlay under the cursor.
        var (cx, cy) = Monitors.CursorPosition();
        (overlays.FirstOrDefault(o => o.Monitor.Bounds.Contains(cx, cy)) ?? overlays[0]).ActivateForInput();
        return tcs.Task;
    }
}

internal sealed class OverlayWindow : Window
{
    private readonly Action<OverlayResult?> _finish;
    private readonly OverlayView _view;
    public MonitorInfo Monitor { get; }
    private bool _closed;

    public OverlayWindow(OverlayMode mode, MonitorInfo monitor, PixelBuffer background, IReadOnlyList<WindowInfo> windows, string? hint, Action<OverlayResult?> finish)
    {
        Monitor = monitor;
        _finish = finish;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        ShowActivated = false;
        Cursor = Cursors.None;
        _view = new OverlayView(mode, monitor, background, windows, hint, Complete);
        Content = _view;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Complete(null);
            else _view.OnKey(e);
        };
        Deactivated += (_, _) => { };
    }

    public void ShowOnMonitor()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var b = Monitor.Bounds;
        SetWindowPos(hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE);
        Show();
        SetWindowPos(hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void ActivateForInput()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetForegroundWindow(hwnd);
        Activate();
        Focus();
        Keyboard.Focus(_view);
    }

    private void Complete(OverlayResult? r)
    {
        if (_closed) return;
        _finish(r);
    }

    public void CloseQuietly()
    {
        if (_closed) return;
        _closed = true;
        Close();
    }
}

internal sealed class OverlayView : FrameworkElement
{
    private readonly OverlayMode _mode;
    private readonly MonitorInfo _monitor;
    private readonly PixelBuffer _pixels;
    private readonly BitmapSource _bitmap;
    private readonly IReadOnlyList<WindowInfo> _windows;
    private readonly string? _hint;
    private readonly Action<OverlayResult?> _done;

    private Point? _mouse;             // DIP
    private (int X, int Y)? _start;    // monitor-local physical px
    private (int X, int Y) _cur;
    private bool _dragging, _moving;
    private (int X, int Y) _moveAnchor;
    private WindowInfo? _hoverWindow;

    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)));
    private static readonly Brush HighlightBrush = Freeze(new SolidColorBrush(Color.FromArgb(60, 10, 132, 255)));
    private static readonly Pen HighlightPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(10, 132, 255)), 2));
    private static readonly Pen CrossPen = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1));
    private static readonly Pen CrossShadow = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 1));
    private static readonly Pen SelPen = FreezePen(new Pen(Brushes.White, 1));
    private static readonly Brush LabelBg = Freeze(new SolidColorBrush(Color.FromArgb(225, 24, 24, 26)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    public OverlayView(OverlayMode mode, MonitorInfo monitor, PixelBuffer pixels, IReadOnlyList<WindowInfo> windows, string? hint, Action<OverlayResult?> done)
    {
        _mode = mode;
        _monitor = monitor;
        _pixels = pixels;
        _bitmap = ImageIO.ToBitmapSource96(pixels);
        _windows = windows;
        _hint = hint;
        _done = done;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        if (mode == OverlayMode.Window)
        {
            var (cx, cy) = Monitors.CursorPosition();
            _hoverWindow = WindowFinder.At(_windows, cx, cy);
        }
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    internal void SimulateForTest((int X, int Y) start, (int X, int Y) cur, Point mouse) { _start = start; _cur = cur; _mouse = mouse; _dragging = true; }
    internal void SimulateHoverForTest(WindowInfo w) => _hoverWindow = w;

    private (int X, int Y) ToPx(Point dip) =>
        (Math.Clamp((int)Math.Round(dip.X * Scale), 0, _pixels.Width), Math.Clamp((int)Math.Round(dip.Y * Scale), 0, _pixels.Height));

    private Point ToDip(int x, int y) => new(x / Scale, y / Scale);

    private RectI Selection => _start is (int sx, int sy) ? RectI.FromPoints(sx, sy, _cur.X, _cur.Y) : default;

    protected override void OnRender(DrawingContext dc)
    {
        double s = Scale;
        var full = new Rect(0, 0, _pixels.Width / s, _pixels.Height / s);
        dc.DrawImage(_bitmap, full);

        if (_mode == OverlayMode.Window)
        {
            dc.DrawRectangle(DimBrush, null, full);
            if (_hoverWindow is { } w)
            {
                var r = LocalRect(w.Bounds);
                dc.PushClip(new RectangleGeometry(full));
                dc.DrawImage(new CroppedBitmap(_bitmap, ClampInt32Rect(w.Bounds)), ClampDip(w.Bounds));
                dc.DrawRectangle(HighlightBrush, HighlightPen, r);
                dc.Pop();
                if (!string.IsNullOrWhiteSpace(w.Title)) DrawLabel(dc, w.Title.Length > 60 ? w.Title[..60] + "…" : w.Title, new Point(r.X + 8, Math.Max(4, r.Y + 8)));
            }
            DrawHint(dc, full, _hint ?? "Click a window to capture it · Esc to cancel");
            return;
        }

        var sel = Selection;
        if (_start != null && !sel.IsEmpty)
        {
            var r = new Rect(ToDip(sel.X, sel.Y), ToDip(sel.Right, sel.Bottom));
            dc.DrawGeometry(DimBrush, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(r)));
            dc.DrawRectangle(null, SelPen, new Rect(r.X - 0.5, r.Y - 0.5, r.Width + 1, r.Height + 1));
            string size = $"{Fmt(sel.Width / _monitor.Scale)} × {Fmt(sel.Height / _monitor.Scale)}";
            DrawLabel(dc, size, new Point(r.Right + 6, r.Bottom + 6));
        }
        else
        {
            DrawHint(dc, full, _hint ?? "Drag to select an area · click a window to capture it · Esc to cancel");
        }

        if (_mouse is Point m && !_moving)
        {
            var (px, py) = ToPx(m);
            var p = ToDip(px, py);
            // Crosshair across the whole screen.
            double x = Math.Round(p.X * s) / s + 0.5 / s, y = Math.Round(p.Y * s) / s + 0.5 / s;
            dc.DrawLine(CrossShadow, new Point(x + 1 / s, 0), new Point(x + 1 / s, full.Height));
            dc.DrawLine(CrossShadow, new Point(0, y + 1 / s), new Point(full.Width, y + 1 / s));
            dc.DrawLine(CrossPen, new Point(x, 0), new Point(x, full.Height));
            dc.DrawLine(CrossPen, new Point(0, y), new Point(full.Width, y));
            DrawLoupe(dc, m, px, py, full);
        }
    }

    private static string Fmt(double v) => Math.Abs(v - Math.Round(v)) < 0.01 ? Math.Round(v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);

    private Rect LocalRect(RectI screen) =>
        new(ToDip(screen.X - _monitor.Bounds.X, screen.Y - _monitor.Bounds.Y), ToDip(screen.Right - _monitor.Bounds.X, screen.Bottom - _monitor.Bounds.Y));

    private Int32Rect ClampInt32Rect(RectI screen)
    {
        var local = screen.Offset(-_monitor.Bounds.X, -_monitor.Bounds.Y).ClampTo(_pixels.Width, _pixels.Height);
        return local.IsEmpty ? new Int32Rect(0, 0, 1, 1) : new Int32Rect(local.X, local.Y, local.Width, local.Height);
    }

    private Rect ClampDip(RectI screen)
    {
        var local = screen.Offset(-_monitor.Bounds.X, -_monitor.Bounds.Y).ClampTo(_pixels.Width, _pixels.Height);
        return new Rect(ToDip(local.X, local.Y), ToDip(local.Right, local.Bottom));
    }

    private void DrawLoupe(DrawingContext dc, Point m, int px, int py, Rect full)
    {
        const int cells = 15;      // pixels shown across
        const double cell = 8;     // DIPs per pixel
        double size = cells * cell;
        double ox = m.X + 24, oy = m.Y + 24;
        if (ox + size > full.Width) ox = m.X - 24 - size;
        if (oy + size + 40 > full.Height) oy = m.Y - 24 - size - 40;
        var box = new Rect(ox, oy, size, size);

        dc.DrawRoundedRectangle(LabelBg, null, new Rect(ox - 3, oy - 3, size + 6, size + 42), 8, 8);
        dc.PushClip(new RectangleGeometry(box, 5, 5));
        int half = cells / 2;
        var src = new Int32Rect(px - half, py - half, cells, cells);
        // Clamp crop to the image; draw at the right offset.
        int cx0 = Math.Max(0, src.X), cy0 = Math.Max(0, src.Y);
        int cx1 = Math.Min(_pixels.Width, src.X + cells), cy1 = Math.Min(_pixels.Height, src.Y + cells);
        dc.DrawRectangle(Brushes.Black, null, box);
        if (cx1 > cx0 && cy1 > cy0)
        {
            var crop = new CroppedBitmap(_bitmap, new Int32Rect(cx0, cy0, cx1 - cx0, cy1 - cy0));
            dc.DrawImage(crop, new Rect(ox + (cx0 - src.X) * cell, oy + (cy0 - src.Y) * cell, (cx1 - cx0) * cell, (cy1 - cy0) * cell));
        }
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 1);
        for (int i = 1; i < cells; i++)
        {
            dc.DrawLine(grid, new Point(ox + i * cell, oy), new Point(ox + i * cell, oy + size));
            dc.DrawLine(grid, new Point(ox, oy + i * cell), new Point(ox + size, oy + i * cell));
        }
        dc.Pop();
        var center = new Rect(ox + half * cell, oy + half * cell, cell, cell);
        dc.DrawRectangle(null, new Pen(Brushes.Black, 2), center);
        dc.DrawRectangle(null, new Pen(Brushes.White, 1), center);

        string hex = "";
        if (_pixels.Contains(px, py))
        {
            var (r, g, b) = _pixels.GetRgb(Math.Min(px, _pixels.Width - 1), Math.Min(py, _pixels.Height - 1));
            hex = $"#{r:X2}{g:X2}{b:X2}";
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(r, g, b)), new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1), new Rect(ox, oy + size + 8, 14, 14), 3, 3);
        }
        int gx = px + _monitor.Bounds.X, gy = py + _monitor.Bounds.Y;
        var ft = Text($"{hex}   {Fmt(px / _monitor.Scale)}, {Fmt(py / _monitor.Scale)}", 11.5);
        dc.DrawText(ft, new Point(ox + 20, oy + size + 7));
        _ = gx; _ = gy;
    }

    private FormattedText Text(string s, double size) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawLabel(DrawingContext dc, string text, Point at)
    {
        var ft = Text(text, 12);
        double w = ft.Width + 14, h = ft.Height + 6;
        double x = Math.Min(at.X, ActualWidth - w - 4), y = Math.Min(at.Y, ActualHeight - h - 4);
        dc.DrawRoundedRectangle(LabelBg, null, new Rect(x, y, w, h), 5, 5);
        dc.DrawText(ft, new Point(x + 7, y + 3));
    }

    private void DrawHint(DrawingContext dc, Rect full, string text)
    {
        if (!_monitor.Bounds.Contains(Monitors.CursorPosition().X, Monitors.CursorPosition().Y)) return;
        var ft = Text(text, 13);
        double w = ft.Width + 24, h = ft.Height + 12;
        var r = new Rect((full.Width - w) / 2, 28, w, h);
        dc.DrawRoundedRectangle(LabelBg, null, r, h / 2, h / 2);
        dc.DrawText(ft, new Point(r.X + 12, r.Y + 6));
    }

    // ---------------- input ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        var p = ToPx(_mouse.Value);
        if (_mode == OverlayMode.Window)
        {
            var (gx, gy) = (p.X + _monitor.Bounds.X, p.Y + _monitor.Bounds.Y);
            _hoverWindow = WindowFinder.At(_windows, gx, gy);
            InvalidateVisual();
            return;
        }
        if (_dragging)
        {
            if (Keyboard.IsKeyDown(Key.Space))
            {
                // Hold Space to move the whole selection.
                if (!_moving) { _moving = true; _moveAnchor = p; }
                int dx = p.X - _moveAnchor.X, dy = p.Y - _moveAnchor.Y;
                _start = (_start!.Value.X + dx, _start.Value.Y + dy);
                _cur = (_cur.X + dx, _cur.Y + dy);
                _moveAnchor = p;
            }
            else
            {
                _moving = false;
                _cur = p;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _start is (int sx, int sy))
                {
                    int side = Math.Max(Math.Abs(p.X - sx), Math.Abs(p.Y - sy));
                    _cur = (sx + Math.Sign(p.X - sx) * side, sy + Math.Sign(p.Y - sy) * side);
                }
            }
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_dragging) { _mouse = null; InvalidateVisual(); }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton == MouseButton.Right) { _done(null); return; }
        if (e.ChangedButton != MouseButton.Left) return;
        var p = ToPx(e.GetPosition(this));
        if (_mode == OverlayMode.Window)
        {
            if (_hoverWindow is { } w) _done(new OverlayResult(w.Bounds, _monitor, w));
            return;
        }
        _start = p;
        _cur = p;
        _dragging = true;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.ChangedButton != MouseButton.Left) return;
        _dragging = false;
        ReleaseMouseCapture();
        var sel = Selection.ClampTo(_pixels.Width, _pixels.Height);
        if (sel.Width < 3 || sel.Height < 3)
        {
            // A click without dragging captures the window under the cursor.
            var (gx, gy) = (_cur.X + _monitor.Bounds.X, _cur.Y + _monitor.Bounds.Y);
            var w = WindowFinder.At(_windows, gx, gy);
            if (w != null)
            {
                var clipped = w.Bounds.Intersect(_monitor.Bounds);
                _done(new OverlayResult(clipped.IsEmpty ? w.Bounds : clipped, _monitor, w));
            }
            else _done(new OverlayResult(_monitor.Bounds, _monitor, null));
            return;
        }
        _done(new OverlayResult(sel.Offset(_monitor.Bounds.X, _monitor.Bounds.Y), _monitor, null));
    }

    public void OnKey(KeyEventArgs e)
    {
        // Arrow keys nudge the cursor by one pixel for precise selection.
        int dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
        int dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
        if (dx == 0 && dy == 0) return;
        GetCursorPos(out var pt);
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        SetCursorPos(pt.X + dx * step, pt.Y + dy * step);
        e.Handled = true;
    }
}
