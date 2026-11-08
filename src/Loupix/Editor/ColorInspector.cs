using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Loupix.Imaging;
using Loupix.Services;

namespace Loupix.Editor;

/// <summary>
/// Colour inspector popover: the last two picked colours in every format (click to copy, or make a format the
/// default), plus their contrast (WCAG 2 ratio with grade, and APCA Lc).
/// </summary>
public sealed class ColorInspector : Popup
{
    private Color _fg, _bg;
    private readonly StackPanel _root = new() { Margin = new Thickness(14) };
    private readonly Action<string> _notify;

    public ColorInspector(UIElement anchor, Color foreground, Color background, Action<string> notify)
    {
        _fg = foreground;
        _bg = background;
        _notify = notify;
        PlacementTarget = anchor;
        Placement = PlacementMode.Bottom;
        StaysOpen = false;
        AllowsTransparency = true;
        Child = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x29)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x40)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = _root,
            MinWidth = 330,
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) IsOpen = false; };
        Build();
    }

    private static readonly Brush Text = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF7));
    private static readonly Brush Subtle = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA0));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private void Build()
    {
        _root.Children.Clear();

        // Swatches: picked colour and the one it is compared against.
        var swatches = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        swatches.ColumnDefinitions.Add(new ColumnDefinition());
        swatches.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        swatches.ColumnDefinitions.Add(new ColumnDefinition());
        swatches.Children.Add(Swatch(_fg, "Color", 0));
        var swap = new Button { Style = (Style)Application.Current.FindResource("RoundButton"), Content = "⇄", Width = 30, Height = 26, Margin = new Thickness(8, 0, 8, 0), ToolTip = "Swap", Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        swap.Click += (_, _) => { (_fg, _bg) = (_bg, _fg); Build(); };
        Grid.SetColumn(swap, 1);
        swatches.Children.Add(swap);
        swatches.Children.Add(Swatch(_bg, "Against", 2));
        _root.Children.Add(swatches);

        // Contrast.
        var (ratio, level, apca) = Features.ContrastOf(_fg, _bg);
        var contrast = new Border
        {
            Background = new SolidColorBrush(_bg),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock { Text = "Sample text Aa", Foreground = new SolidColorBrush(_fg), FontSize = 15, FontWeight = FontWeights.SemiBold },
        };
        _root.Children.Add(contrast);
        _root.Children.Add(new TextBlock
        {
            Foreground = Text,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 10),
            Text = $"Contrast {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1  ·  WCAG {level}  ·  APCA Lc {apca.ToString("0.#", CultureInfo.InvariantCulture)}",
        });

        // Formats.
        _root.Children.Add(new TextBlock { Text = "Click a value to copy it. The dot marks the format Tab copies.", Foreground = Subtle, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var (key, name) in Features.ColorFormats())
        {
            var value = Features.FormatColor(_fg, key);
            bool current = SettingsStore.Current.ColorFormat == key;
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new RadioButton { IsChecked = current, GroupName = "fmt", Focusable = false, ToolTip = "Make this the default format", VerticalAlignment = VerticalAlignment.Center };
            dot.Checked += (_, _) => { SettingsStore.Current.ColorFormat = key; SettingsStore.Save(); };
            var copy = new Button
            {
                Style = (Style)Application.Current.FindResource("RoundButton"),
                Content = new TextBlock { Text = value, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, Foreground = Text },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 3, 6, 3),
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = name.Split("    ")[0],
            };
            copy.Click += (_, _) => { ImageIO.CopyText(value); _notify($"{value} copied"); IsOpen = false; };
            Grid.SetColumn(copy, 1);
            row.Children.Add(dot);
            row.Children.Add(copy);
            _root.Children.Add(row);
        }
    }

    private static FrameworkElement Swatch(Color c, string label, int column)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(c), BorderBrush = Frozen(Color.FromArgb(80, 255, 255, 255)), BorderThickness = new Thickness(1) });
        var texts = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = label, Foreground = Subtle, FontSize = 11 });
        texts.Children.Add(new TextBlock { Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}", Foreground = Text, FontSize = 12.5, FontFamily = new FontFamily("Cascadia Mono, Consolas") });
        sp.Children.Add(texts);
        Grid.SetColumn(sp, column);
        return sp;
    }
}
