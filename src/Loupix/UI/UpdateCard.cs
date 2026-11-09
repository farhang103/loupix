using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Loupix.Capture;
using Loupix.Services;
using Loupix.Services.Updates;

namespace Loupix.UI;

/// <summary>A thin download bar. Keeps its place while the value changes, so buttons next to it stay clickable.</summary>
internal sealed class UpdateProgressBar : Grid
{
    private readonly ColumnDefinition _done = new(), _left = new();

    public UpdateProgressBar()
    {
        Height = 4;
        ColumnDefinitions.Add(_done);
        ColumnDefinitions.Add(_left);
        var track = new Border { CornerRadius = new CornerRadius(2), Background = WindowHelpers.Frozen(Color.FromArgb(0x60, 0x80, 0x80, 0x80)) };
        SetColumnSpan(track, 2);
        Children.Add(track);
        Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = (Brush)Application.Current.FindResource("AccentBrush") });
    }

    public double Value
    {
        set
        {
            _done.Width = new GridLength(value, GridUnitType.Star);
            _left.Width = new GridLength(1 - value, GridUnitType.Star);
        }
    }
}

/// <summary>Offers a new version from the corner of the screen. It stays until it's used and never takes focus.</summary>
public sealed class UpdateCard : Window
{
    private static UpdateCard? _open;
    private readonly Updater _updater;
    private readonly Action _install;
    private readonly Border _surface;
    private UpdateStatus? _shown;
    private UpdateProgressBar? _bar;
    private MonitorInfo? _monitor;

    private static AppSettings Settings => SettingsStore.Current;

    public UpdateCard(Updater updater, Action install)
    {
        _updater = updater;
        _install = install;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _surface = new Border
        {
            Width = 348,
            Margin = new Thickness(12),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(12),
            Background = WindowHelpers.Frozen(Color.FromArgb(0xF2, 0x22, 0x22, 0x24)),
            BorderBrush = WindowHelpers.Frozen(Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45 },
        };
        Content = _surface;
        Render();
        SizeChanged += (_, _) => Place();
        _updater.Changed += OnChanged;
        Closed += (_, _) => { _updater.Changed -= OnChanged; if (_open == this) _open = null; };
    }

    public static void Present(Updater updater, Action install)
    {
        if (_open != null) return;
        var card = new UpdateCard(updater, install) { _monitor = Monitors.AtCursor(), Opacity = 0 };
        _open = card;
        WindowHelpers.MakeNoActivate(card);
        card.Show();
        card.UpdateLayout();
        card.Place();
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
    }

    /// <summary>Takes the card away without counting as an answer, so that it isn't in the next screenshot.</summary>
    public static void CloseCurrent() => _open?.Close();

    public static UIElement Glyph() => new Path
    {
        Data = Geometry.Parse("M12 4v12M7 11l5 5 5-5M4 20h16"),
        Stroke = (Brush)Application.Current.FindResource("AccentBrush"),
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Stretch = Stretch.Uniform,
        Width = 14,
        Height = 14,
    };

    private void Place()
    {
        if (_monitor is not { } m) return;
        int w = (int)(ActualWidth * m.Scale), h = (int)(ActualHeight * m.Scale);
        WindowHelpers.PlacePhysical(this, m.WorkArea.Right - w, m.WorkArea.Bottom - h, w, h, topmost: true);
    }

    private void OnChanged()
    {
        if (_updater.Update == null) Close();
        else if (_updater.Status != _shown) Render();
        else if (_bar != null) _bar.Value = _updater.Progress;
    }

    private void Render()
    {
        var update = _updater.Update!;
        var version = update.Version.ToString(3);
        _shown = _updater.Status;
        bool available = _shown is not (UpdateStatus.Downloading or UpdateStatus.WaitingForEditor);
        var (title, detail) = _shown switch
        {
            UpdateStatus.Downloading => ($"Downloading Loupix {version}", "Loupix will close and reopen when it's ready."),
            UpdateStatus.WaitingForEditor => ($"Loupix {version} is ready to install", "It will install when you close the editor."),
            _ => ($"Loupix {version} is available", update.Highlight ?? "Update now, or whenever you like."),
        };

        var texts = new StackPanel { Margin = new Thickness(12, 0, 8, 0) };
        texts.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = detail, Foreground = WindowHelpers.Frozen(Color.FromRgb(0xB0, 0xB0, 0xB6)), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 50, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        _bar = _shown == UpdateStatus.Downloading ? new UpdateProgressBar { Value = _updater.Progress, Margin = new Thickness(0, 10, 0, 0) } : null;
        if (_bar != null) texts.Children.Add(_bar);

        var close = CardButton(null, available ? Later : Close);
        close.Content = new Path { Data = Geometry.Parse("M0,0 L10,10 M10,0 L0,10"), Stroke = WindowHelpers.Frozen(Color.FromRgb(0xB0, 0xB0, 0xB6)), StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        close.Width = close.Height = 24;
        close.Padding = new Thickness(0);
        close.Background = Brushes.Transparent;
        close.ToolTip = "Close";
        System.Windows.Automation.AutomationProperties.SetName(close, "Close");

        var top = new DockPanel { VerticalAlignment = VerticalAlignment.Top };
        var logo = new Image { Source = IconFactory.Logo(64), Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(logo, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        close.VerticalAlignment = VerticalAlignment.Top;
        top.Children.Add(logo);
        top.Children.Add(close);
        top.Children.Add(texts);

        var actions = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        if (available)
        {
            var stop = CardButton("Don't show again", DontShowAgain);
            stop.Background = Brushes.Transparent;
            stop.Foreground = WindowHelpers.Frozen(Color.FromRgb(0xB0, 0xB0, 0xB6));
            stop.Padding = new Thickness(6, 4, 6, 4);
            stop.Margin = new Thickness(-6, 0, 0, 0);
            stop.ToolTip = "Stops checking for updates. Turn it back on in Settings → About.";
            DockPanel.SetDock(stop, Dock.Left);
            actions.Children.Add(stop);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        if (available)
        {
            buttons.Children.Add(CardButton("Later", Later));
            var install = CardButton("Update", Install);
            install.Background = (Brush)Application.Current.FindResource("AccentBrush");
            install.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(install);
        }
        else buttons.Children.Add(CardButton("Cancel", _updater.CancelInstall));
        actions.Children.Add(buttons);

        _surface.Child = new StackPanel { Children = { top, actions } };
        SizeToContent = SizeToContent.WidthAndHeight; // moving the window turns this off
    }

    private static Button CardButton(string? label, Action click)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("RoundButton"),
            Content = label,
            Foreground = Brushes.White,
            Background = WindowHelpers.Frozen(Color.FromRgb(0x3A, 0x3A, 0x3E)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 13,
            Padding = new Thickness(14, 4, 14, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => click();
        return b;
    }

    private void Install()
    {
        _install();
        if (_updater.Status == UpdateStatus.Available) Later(); // it opened the release page instead
    }

    /// <summary>Don't bring this version up again. The tray menu and Settings still offer it.</summary>
    private void Later()
    {
        Settings.DismissedUpdateVersion = _updater.Update!.Version.ToString(3);
        SettingsStore.Save();
        Close();
    }

    private void DontShowAgain()
    {
        Settings.CheckForUpdates = false;
        SettingsStore.Save();
        Close();
        ToastWindow.Show("Update checks are off", "You can turn them back on in Settings → About.");
    }
}
