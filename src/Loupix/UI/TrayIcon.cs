using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using static Loupix.Native.NativeMethods;

namespace Loupix.UI;

/// <summary>A notification-area icon that opens a WPF context menu, like a macOS menu-bar extra.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 77;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private IntPtr _hIcon;
    private bool _added;
    private string _tooltip = "Loupix";

    public Func<ContextMenu>? MenuFactory { get; set; }
    public event Action? DoubleClicked;

    public TrayIcon()
    {
        _source = new HwndSource(new HwndSourceParameters("LoupixTray") { WindowStyle = 0, Width = 0, Height = 0 });
        _source.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    public void Show(string tooltip)
    {
        _tooltip = tooltip;
        RefreshIcon();
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        data.uVersion = 4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    public void Hide()
    {
        if (!_added) return;
        var data = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>Re-renders the glyph (e.g. after the taskbar theme changes).</summary>
    public void RefreshIcon()
    {
        var old = _hIcon;
        double scale = GetDpiForWindow(_source.Handle) / 96.0;
        if (scale <= 0) scale = 1;
        _hIcon = IconFactory.ToHIcon(IconFactory.TrayGlyph((int)Math.Round(16 * scale)));
        if (_added)
        {
            var data = Data(NIF_ICON);
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            int ev = lParam.ToInt32() & 0xFFFF;
            switch (ev)
            {
                case WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT or WM_CONTEXTMENU or WM_RBUTTONUP:
                    ShowMenu();
                    handled = true;
                    break;
                case WM_LBUTTONDBLCLK:
                    DoubleClicked?.Invoke();
                    handled = true;
                    break;
            }
        }
        else if (msg == (int)_taskbarCreated && _taskbarCreated != 0)
        {
            _added = false;
            Show(_tooltip); // Explorer restarted.
        }
        else if (msg == 0x001A /* WM_SETTINGCHANGE */)
        {
            RefreshIcon();
        }
        return IntPtr.Zero;
    }

    private ContextMenu? _open;

    private void ShowMenu()
    {
        if (MenuFactory == null) return;
        if (_open is { IsOpen: true }) { _open.IsOpen = false; return; }
        var menu = MenuFactory();
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Closed += (_, _) => _open = null;
        _open = menu;
        SetForegroundWindow(_source.Handle);
        menu.IsOpen = true;
        if (PresentationSource.FromVisual(menu) is HwndSource popup) SetForegroundWindow(popup.Handle);
        menu.Focus();
    }

    public void Dispose()
    {
        Hide();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _source.Dispose();
    }
}
