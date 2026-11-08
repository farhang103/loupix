using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Loupix.Editor;

/// <summary>Resize-image dialog: width/height in pixels or percent, with locked aspect ratio.</summary>
public sealed class ResizeDialog : Window
{
    private readonly int _w0, _h0;
    private readonly TextBox _w = new() { Width = 80 }, _h = new() { Width = 80 }, _pct = new() { Width = 60 };
    private readonly CheckBox _lock = new() { Content = "Keep aspect ratio", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private bool _updating;

    public (int W, int H)? Result { get; private set; }

    public ResizeDialog(int width, int height)
    {
        _w0 = width; _h0 = height;
        Title = "Resize image";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _w.Text = width.ToString(CultureInfo.InvariantCulture);
        _h.Text = height.ToString(CultureInfo.InvariantCulture);
        _pct.Text = "100";
        _w.TextChanged += (_, _) => Sync(fromWidth: true);
        _h.TextChanged += (_, _) => Sync(fromWidth: false);
        _pct.TextChanged += (_, _) => SyncPercent();

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Row(int r, string label, UIElement e)
        {
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            Grid.SetRow(t, r); grid.Children.Add(t);
            Grid.SetRow(e, r); Grid.SetColumn(e, 1); grid.Children.Add(e);
            if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
        }
        Row(0, "Width (px)", _w);
        Row(1, "Height (px)", _h);
        Row(2, "Scale (%)", _pct);
        Grid.SetRow(_lock, 3); Grid.SetColumnSpan(_lock, 2); grid.Children.Add(_lock);
        var ok = new Button { Content = "Resize", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { ok, cancel } };
        Grid.SetRow(buttons, 4); Grid.SetColumnSpan(buttons, 2); grid.Children.Add(buttons);
        Content = grid;
        Loaded += (_, _) => { _w.Focus(); _w.SelectAll(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private static int? Parse(TextBox t) => int.TryParse(t.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    private void Sync(bool fromWidth)
    {
        if (_updating || _lock.IsChecked != true) return;
        _updating = true;
        if (fromWidth && Parse(_w) is int w)
        {
            _h.Text = Math.Max(1, (int)Math.Round(w * (double)_h0 / _w0)).ToString(CultureInfo.InvariantCulture);
            _pct.Text = Math.Round(w * 100.0 / _w0, 1).ToString(CultureInfo.InvariantCulture);
        }
        else if (!fromWidth && Parse(_h) is int h)
        {
            _w.Text = Math.Max(1, (int)Math.Round(h * (double)_w0 / _h0)).ToString(CultureInfo.InvariantCulture);
            _pct.Text = Math.Round(h * 100.0 / _h0, 1).ToString(CultureInfo.InvariantCulture);
        }
        _updating = false;
    }

    private void SyncPercent()
    {
        if (_updating || !double.TryParse(_pct.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || p <= 0) return;
        _updating = true;
        _w.Text = Math.Max(1, (int)Math.Round(_w0 * p / 100)).ToString(CultureInfo.InvariantCulture);
        _h.Text = Math.Max(1, (int)Math.Round(_h0 * p / 100)).ToString(CultureInfo.InvariantCulture);
        _updating = false;
    }

    private void Accept()
    {
        if (Parse(_w) is int w && Parse(_h) is int h && w <= 30000 && h <= 60000)
        {
            Result = (w, h);
            DialogResult = true;
        }
    }
}
