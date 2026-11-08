using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Loupix.Core.Imaging;
using Loupix.Raster;
using Loupix.Imaging;
using Loupix.Services;
using Loupix.UI;
using static Loupix.Native.NativeMethods;

namespace Loupix.Capture;

/// <summary>
/// Scrolling capture: repeatedly grabs the selected area while sending mouse-wheel events to the
/// window under it, and stitches the frames into one tall image.
/// </summary>
public static class ScrollingCapture
{
    public static async Task<PixelBuffer?> RunAsync(RectI area, bool reverse)
    {
        var settings = SettingsStore.Current;
        var stitcher = new ScrollStitcher(reverse, settings.ScrollMaxHeight);
        int failures = 0;
        using var hud = new ScrollHud(area);
        hud.Show();

        // Scroll events go to the window under the cursor.
        GetCursorPos(out var originalCursor);
        SetCursorPos(area.X + area.Width / 2, area.Y + area.Height / 2);
        await Task.Delay(80);

        int dir = reverse ? 1 : -1;
        if (settings.ScrollInvertDirection) dir = -dir;
        int notches = Math.Clamp(area.Height / 260, 1, 6);
        int stalls = 0;
        string? stopReason = null;
        try
        {
            stitcher.Add(ScreenCapture.Capture(area));
            while (!hud.StopRequested)
            {
                SendWheel(dir * 120 * notches);
                await Task.Delay(230);
                if (hud.StopRequested) break;
                var status = stitcher.Add(ScreenCapture.Capture(area));
                hud.Update(stitcher.Height, status);
                if (status == StitchStatus.NoMovement)
                {
                    if (++stalls >= 2) { stopReason = "Reached the end"; break; }
                }
                else stalls = 0;
                if (status == StitchStatus.LimitReached) { stopReason = $"Reached the {settings.ScrollMaxHeight:N0}px limit"; break; }
                failures = status == StitchStatus.NoMatch ? failures + 1 : 0;
                if (failures >= 3) { stopReason = "Couldn't match the scrolled content"; break; }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Scrolling capture failed", ex);
            stopReason = "Scrolling capture failed";
        }
        finally
        {
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
        if (stopReason != null) ToastWindow.Show(stopReason, $"{stitcher.Height:N0} px captured");
        return stitcher.Height > 0 ? stitcher.Compose() : null;
    }

    private static void SendWheel(int delta)
    {
        var input = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { mouseData = delta, dwFlags = MOUSEEVENTF_WHEEL } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }
}

/// <summary>Small floating panel showing progress, with a Stop button. Esc also stops.</summary>
internal sealed class ScrollHud : Window, IDisposable
{
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private HwndSource? _source;
    public bool StopRequested { get; private set; }

    public ScrollHud(RectI area)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        var stop = new Button { Style = (Style)Application.Current.FindResource("RoundButton"), Content = "Stop", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(12, 3, 12, 3), Cursor = Cursors.Hand };
        stop.Click += (_, _) => StopRequested = true;
        _text.Text = "Scrolling capture…  Esc to stop";
        Content = new Border
        {
            Margin = new Thickness(10),
            Padding = new Thickness(14, 8, 10, 8),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x22, 0x22, 0x24)),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.4 },
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _text, stop } },
        };
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source.AddHook(Hook);
            RegisterHotKey(_source.Handle, 0x5C01, 0, 0x1B /* VK_ESCAPE */);
        };
        Loaded += (_, _) => Place(area);
    }

    private void Place(RectI area)
    {
        var m = Monitors.FromPoint(area.X + area.Width / 2, area.Y + area.Height / 2);
        double s = m.Scale;
        int w = (int)(ActualWidth * s), h = (int)(ActualHeight * s);
        int x = area.X + (area.Width - w) / 2;
        // Outside the captured area so it never ends up in the screenshot.
        int y = area.Bottom + 4 + h <= m.Bounds.Bottom ? area.Bottom + 4 : area.Y - h - 4 >= m.Bounds.Y ? area.Y - h - 4 : m.Bounds.Bottom - h;
        if (y > area.Y - h && y < area.Bottom) x = area.X + area.Width + 4 + w <= m.Bounds.Right ? area.Right + 4 : Math.Max(m.Bounds.X, area.X - w - 4);
        WindowHelpers.PlacePhysical(this, x, y, w, h, topmost: true);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == 0x5C01) { StopRequested = true; handled = true; }
        return IntPtr.Zero;
    }

    public void Update(int height, StitchStatus status) =>
        _text.Text = $"Scrolling capture — {height:N0} px  ·  Esc to stop";

    public void Dispose()
    {
        if (_source != null) UnregisterHotKey(_source.Handle, 0x5C01);
        Close();
    }
}
