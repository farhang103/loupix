using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using static Loupix.Native.NativeMethods;

namespace Loupix.UI;

/// <summary>The look of Settings, the dialogs and the menus, in the Windows light or dark mode.</summary>
internal static class UiTheme
{
    private static readonly (string Key, string Dark, string Light)[] Palette =
    [
        ("UiBg", "#1C1C1E", "#F5F5F7"),
        ("UiSide", "#232326", "#ECECEF"),
        ("UiCard", "#2A2A2D", "#FFFFFF"),
        ("UiField", "#1C1C1E", "#FBFBFB"),
        ("UiHover", "#3A3A3E", "#E2E2E6"),
        ("UiNavHover", "#2E2E31", "#E4E4E8"),
        ("UiLine", "#3C3C40", "#DADADF"),
        ("UiText", "#F2F2F7", "#1A1A1A"),
        ("UiSubtle", "#A3A3A9", "#62626A"),
        ("UiKnobOff", "#A3A3A9", "#6E6E76"),
        ("UiButton", "#3A3A3E", "#E2E2E6"),
        ("UiButtonHover", "#46464B", "#D6D6DB"),
        ("UiAccent", "#0A84FF", "#0A84FF"),
        ("MenuBackground", "#202020", "#FBFBFB"),
        ("MenuBorder", "#3A3A3A", "#D9D9D9"),
        ("MenuHover", "#333333", "#EDEDED"),
        ("MenuLine", "#383838", "#E5E5E5"),
        ("MenuText", "#F2F2F2", "#1A1A1A"),
        ("MenuSubtle", "#A3A3A3", "#6E6E6E"),
    ];

    /// <summary>The "app mode" in Windows settings, which can differ from the taskbar's mode.</summary>
    public static bool AppsAreLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch { return true; }
    }

    public static void SetPalette(ResourceDictionary resources, bool light)
    {
        foreach (var (key, dark, lightColor) in Palette)
            resources[key] = new BrushConverter().ConvertFromString(light ? lightColor : dark);
    }

    /// <summary>Gives a window the shared controls and keeps it in step with the Windows mode.</summary>
    public static void Apply(Window window)
    {
        window.Resources.MergedDictionaries.Add(Styles("Controls"));
        window.SetResourceReference(Control.BackgroundProperty, "UiBg");
        window.SetResourceReference(Control.ForegroundProperty, "UiText");
        window.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        void Refresh()
        {
            bool light = AppsAreLight();
            SetPalette(window.Resources, light);
            PaintTitleBar(window, light);
        }
        UserPreferenceChangedEventHandler changed = (_, e) => { if (e.Category == UserPreferenceCategory.General) window.Dispatcher.BeginInvoke(Refresh); };
        Refresh();
        window.SourceInitialized += (_, _) => Refresh();
        SystemEvents.UserPreferenceChanged += changed;
        window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= changed;
    }

    /// <summary>Call after setting the menu's placement.</summary>
    public static void StyleMenu(ContextMenu menu, bool light)
    {
        SetPalette(menu.Resources, light);
        menu.Resources.MergedDictionaries.Add(Styles("Menu"));
        menu.Style = (Style)menu.Resources["PopupMenu"];
        if (menu.Placement == PlacementMode.Bottom)
        {
            // Lines the visible menu up with its button despite the room left around it for the shadow.
            menu.HorizontalOffset = -14;
            menu.VerticalOffset = -8;
        }
    }

    private static ResourceDictionary Styles(string name) => new() { Source = new Uri($"/Loupix;component/Themes/{name}.xaml", UriKind.Relative) };

    private static void PaintTitleBar(Window window, bool light)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = light ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var bg = ((SolidColorBrush)window.Resources["UiBg"]).Color;
        int caption = bg.R | bg.G << 8 | bg.B << 16;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int)); // Windows 11 only; ignored elsewhere.
    }
}
