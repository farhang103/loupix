using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Loupix.Capture;
using Loupix.Imaging;
using Loupix.Services;
using static Loupix.Native.NativeMethods;

namespace Loupix.UI;

internal static class WindowHelpers
{
    /// <summary>Tool window that never takes focus (for notifications).</summary>
    public static void MakeNoActivate(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    /// <summary>Moves a window to a physical-pixel position (handles mixed-DPI monitors).</summary>
    public static void PlacePhysical(Window w, int x, int y, int width, int height, bool topmost)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : IntPtr.Zero, x, y, width, height, SWP_NOACTIVATE | (topmost ? 0 : SWP_NOZORDER));
    }

    public static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

public enum NotificationKind { Info, Copy, Save, Ocr, Upload, Error }

/// <summary>A small, non-activating confirmation bubble (Shottr-style custom notification).</summary>
public sealed class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _timer = new();

    private ToastWindow(string title, string? detail, Color? swatch, IReadOnlyList<(string Label, Action Action)> buttons, TimeSpan duration)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;

        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new Image { Source = IconFactory.Logo(32), Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        if (swatch is Color c)
            stack.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(c), BorderBrush = WindowHelpers.Frozen(Color.FromArgb(80, 255, 255, 255)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420 };
        texts.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(detail))
            texts.Children.Add(new TextBlock { Text = detail, Foreground = WindowHelpers.Frozen(Color.FromRgb(0xB0, 0xB0, 0xB6)), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 70, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        stack.Children.Add(texts);
        foreach (var (label, action) in buttons)
        {
            var b = new Button
            {
                Content = label,
                Margin = new Thickness(12, 0, 0, 0),
                Padding = new Thickness(10, 3, 10, 3),
                Foreground = Brushes.White,
                Background = WindowHelpers.Frozen(Color.FromRgb(0x3A, 0x3A, 0x3E)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            b.Click += (_, _) => { action(); FadeOut(); };
            stack.Children.Add(b);
        }

        Content = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(12),
            Background = WindowHelpers.Frozen(Color.FromArgb(0xF2, 0x22, 0x22, 0x24)),
            BorderBrush = WindowHelpers.Frozen(Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45 },
            Child = stack,
        };
        _timer.Interval = duration;
        _timer.Tick += (_, _) => FadeOut();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => _timer.Start();
        MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Button) FadeOut(); };
    }

    public static void Show(string title, string? detail = null, Color? swatch = null, IReadOnlyList<(string, Action)>? buttons = null, double seconds = 1.8)
    {
        _current?.Close();
        var t = new ToastWindow(title, detail, swatch, buttons ?? [], TimeSpan.FromSeconds(buttons?.Count > 0 ? Math.Max(seconds, 4) : seconds));
        _current = t;
        WindowHelpers.MakeNoActivate(t);
        t.Opacity = 0;
        t.Show();
        t.UpdateLayout();
        // Top-centre of the monitor with the cursor.
        var m = Monitors.AtCursor();
        double s = m.Scale;
        int w = (int)(t.ActualWidth * s), h = (int)(t.ActualHeight * s);
        WindowHelpers.PlacePhysical(t, m.WorkArea.X + (m.WorkArea.Width - w) / 2, m.WorkArea.Y + (int)(24 * s), w, h, topmost: true);
        t.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        t._timer.Start();
    }

    private void FadeOut()
    {
        _timer.Stop();
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        a.Completed += (_, _) => { Close(); if (_current == this) _current = null; };
        BeginAnimation(OpacityProperty, a);
    }
}

/// <summary>Floating preview thumbnail shown after a capture (when configured), like a quick-actions card.</summary>
public sealed class ThumbnailWindow : Window
{
    private static readonly List<ThumbnailWindow> Open = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly BitmapSource _image;
    private Point? _press;
    private readonly Action _edit;
    private readonly Func<string> _saveTemp;

    public ThumbnailWindow(BitmapSource image, Action edit, Action copy, Action save, Action pin, Action ocr, Func<string> saveTemp, bool persist)
    {
        _image = image;
        _edit = edit;
        _saveTemp = saveTemp;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        double maxW = 240, maxH = 170;
        double scale = Math.Min(maxW / image.PixelWidth, maxH / image.PixelHeight);
        scale = Math.Min(scale, 1);
        var img = new Image { Source = image, Width = Math.Max(60, image.PixelWidth * scale), Height = Math.Max(40, image.PixelHeight * scale), Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
        Button B(string glyph, string tip, Action a)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, Foreground = Brushes.White },
                Width = 34,
                Height = 30,
                Margin = new Thickness(3),
                ToolTip = tip,
                Background = WindowHelpers.Frozen(Color.FromArgb(0xD0, 0x20, 0x20, 0x22)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
            };
            b.Click += (_, _) => { a(); Dismiss(); };
            buttons.Children.Add(b);
            return b;
        }
        B("", "Copy", copy);
        B("", "Save", save);
        B("", "Edit", edit);
        B("", "Copy text / QR", ocr);
        B("", "Pin", pin);
        B("", "Close", () => { });

        var grid = new Grid();
        grid.Children.Add(img);
        grid.Children.Add(new Border { Background = WindowHelpers.Frozen(Color.FromArgb(70, 0, 0, 0)), Visibility = Visibility.Hidden, Name = "Shade" });
        grid.Children.Add(buttons);
        var shade = (Border)grid.Children[1];

        Content = new Border
        {
            Margin = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            BorderBrush = WindowHelpers.Frozen(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(2),
            Background = Brushes.White,
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.5 },
            Child = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = grid },
            Cursor = Cursors.Hand,
        };

        MouseEnter += (_, _) => { _timer.Stop(); buttons.Visibility = Visibility.Visible; shade.Visibility = Visibility.Visible; };
        MouseLeave += (_, _) => { if (!persist) _timer.Start(); buttons.Visibility = Visibility.Hidden; shade.Visibility = Visibility.Hidden; };
        PreviewMouseLeftButtonDown += (_, e) => { if (!InsideButton(e.OriginalSource as DependencyObject)) _press = e.GetPosition(this); };
        PreviewMouseMove += OnMove;
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_press != null && !InsideButton(e.OriginalSource as DependencyObject))
            {
                _press = null;
                _edit();
                Dismiss();
            }
            _press = null;
        };
        _timer.Tick += (_, _) => Dismiss();
        if (!persist) _timer.Start();
    }

    private static bool InsideButton(DependencyObject? d)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Button) return true;
        return false;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_press is not Point p || e.LeftButton != MouseButtonState.Pressed) return;
        if ((e.GetPosition(this) - p).Length < 5) return;
        _press = null;
        // Drag the screenshot out as a file.
        var path = _saveTemp();
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
        data.SetImage(_image);
        Opacity = 0.5;
        var result = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move);
        Opacity = 1;
        if (result != DragDropEffects.None) Dismiss();
    }

    public void ShowAtCorner(ThumbnailCorner corner)
    {
        foreach (var o in Open.ToList()) o.Dismiss();
        Open.Add(this);
        WindowHelpers.MakeNoActivate(this);
        Opacity = 0;
        Show();
        UpdateLayout();
        var m = Monitors.AtCursor();
        double s = m.Scale;
        int w = (int)(ActualWidth * s), h = (int)(ActualHeight * s);
        int margin = (int)(12 * s);
        var wa = m.WorkArea;
        int x = corner is ThumbnailCorner.BottomLeft or ThumbnailCorner.TopLeft ? wa.X + margin : wa.Right - w - margin;
        int y = corner is ThumbnailCorner.TopLeft or ThumbnailCorner.TopRight ? wa.Y + margin : wa.Bottom - h - margin;
        WindowHelpers.PlacePhysical(this, x, y, w, h, topmost: true);
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
    }

    public void Dismiss()
    {
        _timer.Stop();
        Open.Remove(this);
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        a.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, a);
    }

    public static void DismissAll()
    {
        foreach (var o in Open.ToList()) o.Dismiss();
    }
}

/// <summary>An always-on-top borderless window showing a pinned screenshot.</summary>
public sealed class PinWindow : Window
{
    private static readonly List<PinWindow> Pins = new();
    private readonly BitmapSource _image;
    private readonly double _dpiScale;
    private double _zoom = 1;
    private readonly Image _img;
    private readonly Border _frame;

    public PinWindow(BitmapSource image, double captureScale)
    {
        _image = image;
        _dpiScale = captureScale <= 0 ? 1 : captureScale;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Pinned screenshot";
        Background = Brushes.Black;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(0, 0, 0, 1), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });

        _img = new Image { Source = image, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
        _frame = new Border { Child = _img, BorderBrush = WindowHelpers.Frozen(Color.FromArgb(90, 128, 128, 128)), BorderThickness = new Thickness(0) };
        Content = _frame;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Close(); return; }
            DragMove();
        };
        MouseWheel += (_, e) => Step(e.Delta > 0 ? 1 : -1);
        KeyDown += OnKey;
        MouseRightButtonUp += (_, _) => ShowMenu();
        MouseEnter += (_, _) => _frame.BorderThickness = new Thickness(1);
        MouseLeave += (_, _) => _frame.BorderThickness = new Thickness(0);
        Closed += (_, _) => Pins.Remove(this);
        ToolTip = "Drag or arrow keys to move · scroll or −/= to zoom · 5 semi-transparent, 0 opaque · double-click or Esc to close";
    }

    public void ShowAt(int physicalX, int physicalY)
    {
        Pins.Add(this);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var m = Monitors.FromPoint(physicalX, physicalY);
        double s = m.Scale;
        // One image pixel per screen pixel when the screen scale matches the capture.
        int w = (int)Math.Round(_image.PixelWidth * s / _dpiScale), h = (int)Math.Round(_image.PixelHeight * s / _dpiScale);
        int maxW = (int)(m.WorkArea.Width * 0.9), maxH = (int)(m.WorkArea.Height * 0.9);
        double fit = Math.Min(1, Math.Min((double)maxW / w, (double)maxH / h));
        w = (int)(w * fit); h = (int)(h * fit);
        _zoom = fit;
        int x = Math.Clamp(physicalX, m.WorkArea.X, m.WorkArea.Right - w), y = Math.Clamp(physicalY, m.WorkArea.Y, m.WorkArea.Bottom - h);
        Show();
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_SHOWWINDOW);
        Activate();
    }

    private static readonly double[] ZoomSteps = [0.15, 0.2, 0.25, 0.3, 0.4, 0.5, 0.65, 0.8, 1, 1.25, 1.5, 2];

    private Point Center => new(ActualWidth / 2, ActualHeight / 2);

    /// <summary>Zoom to the next or previous of Shottr's pin zoom steps, keeping the window centred.</summary>
    private void Step(int dir)
    {
        double next = dir > 0 ? ZoomSteps.FirstOrDefault(z => z > _zoom + 0.001, ZoomSteps[^1]) : ZoomSteps.LastOrDefault(z => z < _zoom - 0.001, ZoomSteps[0]);
        SetZoom(next, Center);
    }

    private void Nudge(Key key, int step)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out var r);
        double s = Monitors.FromPoint(r.Left, r.Top).Scale;
        int d = (int)Math.Round(step * s);
        int dx = key == Key.Left ? -d : key == Key.Right ? d : 0, dy = key == Key.Up ? -d : key == Key.Down ? d : 0;
        SetWindowPos(hwnd, HWND_TOPMOST, r.Left + dx, r.Top + dy, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE);
    }

    private void SetZoom(double zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, 0.1, 8);
        var hwnd = new WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out var r);
        var m = Monitors.FromPoint(r.Left, r.Top);
        double s = m.Scale;
        int w = Math.Max(24, (int)Math.Round(_image.PixelWidth * s / _dpiScale * zoom));
        int h = Math.Max(24, (int)Math.Round(_image.PixelHeight * s / _dpiScale * zoom));
        // Keep the point under the mouse fixed.
        double fx = anchor.X * s / Math.Max(1, r.Width), fy = anchor.Y * s / Math.Max(1, r.Height);
        int x = r.Left + (int)((r.Width - w) * fx), y = r.Top + (int)((r.Height - h) * fy);
        _zoom = zoom;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.C when ctrl: ImageIO.CopyToClipboard(_image); ToastWindow.Show("Copied"); break;
            case Key.S when ctrl: AppController.Instance.SaveAndNotify(_image, _dpiScale); break;
            case Key.E when ctrl: AppController.Instance.OpenInEditor(ImageIO.FromBitmapSource(_image), _dpiScale); Close(); break;
            case Key.D0 when ctrl: SetZoom(1, Center); break;
            case Key.D0 or Key.NumPad0: Opacity = 1; break;
            case Key.D5 or Key.NumPad5: Opacity = Opacity < 1 ? 1 : 0.5; break; // semi-transparent toggle
            case Key.OemPlus or Key.Add: Step(1); break;
            case Key.OemMinus or Key.Subtract: Step(-1); break;
            case Key.Left or Key.Right or Key.Up or Key.Down: Nudge(e.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1); e.Handled = true; break;
        }
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu();
        MenuItem I(string h, string g, Action a) { var mi = new MenuItem { Header = h, InputGestureText = g }; mi.Click += (_, _) => a(); return mi; }
        menu.Items.Add(I("Copy", "Ctrl+C", () => ImageIO.CopyToClipboard(_image)));
        menu.Items.Add(I("Save", "Ctrl+S", () => AppController.Instance.SaveAndNotify(_image, _dpiScale)));
        menu.Items.Add(I("Edit", "Ctrl+E", () => { AppController.Instance.OpenInEditor(ImageIO.FromBitmapSource(_image), _dpiScale); Close(); }));
        menu.Items.Add(new Separator());
        var opacity = new MenuItem { Header = "Opacity" };
        opacity.Items.Add(I("Opaque", "0", () => Opacity = 1));
        opacity.Items.Add(I("Semi-transparent", "5", () => Opacity = 0.5));
        menu.Items.Add(opacity);
        menu.Items.Add(I("Actual size", "Ctrl+0", () => SetZoom(1, new Point(0, 0))));
        menu.Items.Add(new Separator());
        menu.Items.Add(I("Close", "Esc", Close));
        menu.Items.Add(I("Close all pins", "", CloseAll));
        menu.IsOpen = true;
    }

    public static void CloseAll()
    {
        foreach (var p in Pins.ToList()) p.Close();
    }

    public static int Count => Pins.Count;
}
