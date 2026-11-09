using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Loupix.Editor;

namespace Loupix.UI;

/// <summary>Every editor tool on one page, each with its animation and a plain explanation.</summary>
public sealed class ToolGuideWindow : Window
{
    public ToolGuideWindow()
    {
        Title = "Loupix tool guide";
        Width = 1130; // four tiles across, plus the scroll bar
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 40);
        MinWidth = 600;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = IconFactory.Logo(48);
        UiTheme.Apply(this);
        FontSize = 13;

        var layout = new DockPanel();

        var header = new DockPanel { Margin = new Thickness(30, 8, 30, 16) };
        var legend = new StackPanel { Orientation = Orientation.Horizontal };
        legend.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB6, 0x27)), BorderThickness = new Thickness(3), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        legend.Children.Add(new TextBlock { Text = "This ring means the mouse button is held down", VerticalAlignment = VerticalAlignment.Center });
        var legendBox = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6, 12, 6), VerticalAlignment = VerticalAlignment.Bottom, Child = legend };
        legendBox.SetResourceReference(Border.BackgroundProperty, "UiCard");
        DockPanel.SetDock(legendBox, Dock.Right);
        header.Children.Add(legendBox);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "How each tool works", FontSize = 24, FontWeight = FontWeights.SemiBold });
        var intro = new TextBlock { Text = "Pick a tool at the top of the editor, then do what the picture shows.", FontSize = 14, TextWrapping = TextWrapping.Wrap };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "UiSubtle");
        titles.Children.Add(intro);
        header.Children.Add(titles);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var line = new Border { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "UiLine");
        DockPanel.SetDock(line, Dock.Top);
        layout.Children.Add(line);

        var page = new StackPanel { Margin = new Thickness(30, 18, 10, 30) };
        foreach (var (title, tools) in ToolHelp.Groups)
        {
            page.Children.Add(Heading(title));
            var tiles = new WrapPanel();
            foreach (var tool in tools) tiles.Children.Add(ToolHelpViews.GuideTile(tool));
            page.Children.Add(tiles);
        }

        page.Children.Add(Heading("Buttons at the top right"));
        var buttons = new WrapPanel();
        foreach (var (name, say) in ToolHelp.Buttons)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
            var text = new TextBlock { Text = say, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, "UiSubtle");
            body.Children.Add(text);
            var card = new Border { Width = 240, MinHeight = 88, CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 0, 20, 16), Child = body };
            card.SetResourceReference(Border.BackgroundProperty, "UiCard");
            buttons.Children.Add(card);
        }
        page.Children.Add(buttons);

        layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = page });
        Content = layout;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
}
