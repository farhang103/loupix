using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using static Loupix.Native.NativeMethods;

namespace Loupix.UI;

/// <summary>A notification-area icon that opens a WPF context menu, like a macOS menu-bar extra.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 77;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private readonly DispatcherTimer _singleClick;
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
        _singleClick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _singleClick.Tick += (_, _) => ShowMenu();
    }

    public void Show(string tooltip)
    {
        _tooltip = tooltip;
        RefreshIcon();
        if (_added) return; // Runs on every settings save, and adding an icon twice fails.
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
            // Every modify has to repeat NIF_SHOWTIP, or Windows stops showing the tooltip.
            var data = Data(NIF_ICON | NIF_SHOWTIP);
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
                // A click also sends WM_xBUTTONUP just before these. Reacting to both would open the menu and close it again.
                case NIN_SELECT when _skipNextSelect:
                    _skipNextSelect = false; // The second release of a double-click.
                    handled = true;
                    break;
                case NIN_SELECT:
                    // Wait to see if this is a double-click. A menu opened now would sit under the cursor and swallow the second click.
                    _singleClick.Start();
                    handled = true;
                    break;
                case NIN_KEYSELECT or WM_CONTEXTMENU:
                    ShowMenu();
                    handled = true;
                    break;
                case WM_LBUTTONDOWN:
                    _skipNextSelect = false;
                    break;
                case WM_LBUTTONDBLCLK:
                    _singleClick.Stop();
                    _skipNextSelect = true;
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
    private bool _skipNextSelect;

    private void ShowMenu()
    {
        _singleClick.Stop();
        if (MenuFactory == null) return;
        if (_open is { IsOpen: true }) { _open.IsOpen = false; return; }
        var menu = MenuFactory();
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        UiTheme.StyleMenu(menu, IconFactory.TaskbarIsLight());
        menu.Closed += (_, _) => _open = null;
        _open = menu;
        // Only this hidden window goes to the foreground, so a click elsewhere still dismisses the menu.
        // Activating the menu's own popup would make WPF take keyboard focus from the menu, which closes it.
        SetForegroundWindow(_source.Handle);
        menu.IsOpen = true;
        menu.Focus();
    }

    public void Dispose()
    {
        _singleClick.Stop();
        Hide();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _source.Dispose();
    }
}
