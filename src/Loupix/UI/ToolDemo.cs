using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Loupix.Editor;

namespace Loupix.UI;

/// <summary>
/// A small looping picture of a tool being used, drawn by hand so it stays sharp at any display scale and adds nothing to the download.
/// A yellow ring on the cursor means the mouse button is held down. Most tools share one timeline: press at 22%, let go at 68%.
/// </summary>
internal sealed class ToolDemo : FrameworkElement
{
    private const double W = 240, H = 135;
    private const double LoopSeconds = 4;
    private const double Press = .22, Release = .68;

    private static readonly Brush Page = Solid(0xF4, 0xF5, 0xF7), Head = Solid(0x1A, 0x2A, 0x4D), Bar = Solid(0xC9, 0xCD, 0xD6), Blue = Solid(0x0A, 0x84, 0xFF), Picture = Solid(0xE8, 0x87, 0x3A);
    private static readonly Brush Red = Solid(0xFF, 0x3B, 0x30), Pink = Solid(0xFF, 0x2D, 0x78), Amber = Solid(0xFF, 0xB6, 0x27), Navy = Solid(0x0D, 0x16, 0x30), White = Solid(0xFF, 0xFF, 0xFF);
    private static readonly Brush Ink = Solid(0x1A, 0x1A, 0x1A), DarkChip = Solid(0x1C, 0x1C, 0x1E), Shade = Frozen(new SolidColorBrush(Color.FromArgb(148, 0, 0, 0))), Marker = Frozen(new SolidColorBrush(Color.FromArgb(178, 0xFF, 0xD6, 0x0A)));
    private static readonly Brush MosaicA = Solid(0x7E, 0x85, 0x94), MosaicB = Solid(0xB4, 0xBA, 0xC5), Bubble = Solid(0x9A, 0xA0, 0xAC);
    private static readonly Pen RedPen = MakePen(Red, 2.5), RedLine = MakePen(Red, 3.5), PinkPen = MakePen(Pink, 2), RingPen = MakePen(Amber, 3), CursorPen = MakePen(Brushes.Black, 1.1), WhitePen = MakePen(White, 3), BluePen = MakePen(Blue, 2);
    private static readonly Pen DashedPen = MakeDashed();
    private static readonly Geometry CursorShape = Frozen(Geometry.Parse("M1 1v15l4-3.6 2.6 6 2.7-1.2-2.6-5.8H13z"));
    private static readonly Typeface Regular = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly (double T, double X, double Y)[] DiagonalPath = [(0, 218, 128), (.18, 180, 100), (Press, 180, 100), (Release, 70, 45), (1, 70, 45)];
    private static readonly (double T, double X, double Y)[] BoxPath = [(0, 218, 128), (.18, 60, 32), (Press, 60, 32), (Release, 190, 92), (1, 190, 92)];
    private static readonly Point[] Scribble = [new(40, 100), new(70, 60), new(100, 95), new(130, 55), new(165, 90)];

    private readonly ToolKind? _kind;
    private readonly Stopwatch _clock = new();
    private long _lastFrame;
    private double _progress = .8; // the finished picture, for anyone with Windows animations turned off
    private bool _frozen, _running;
    private double _dip = 1;

    public ToolDemo(ToolKind? kind)
    {
        _kind = kind;
        Width = W;
        Height = H;
        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else Stop(); };
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Holds one moment of the animation, for rendering a still.</summary>
    public void ShowFrame(double progress)
    {
        _frozen = true;
        Stop();
        _progress = progress;
        InvalidateVisual();
    }

    private void Start()
    {
        if (_running || _frozen || !SystemParameters.ClientAreaAnimation) return;
        _running = true;
        _lastFrame = 0;
        _clock.Restart();
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        long now = _clock.ElapsedMilliseconds;
        if (now - _lastFrame < 33) return; // 30 frames a second is plenty, and the guide has 17 of these
        _lastFrame = now;
        _progress = (now / 1000.0 % LoopSeconds) / LoopSeconds;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        _dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H), 8, 8));
        Draw(dc, _progress);
        dc.Pop();
    }

    private void Draw(DrawingContext dc, double p)
    {
        double d = Ease(Seg(p, Press, Release));
        double fade = 1 - Seg(p, .93, .97);
        bool held = p >= Press && p < Release;
        bool drawing = p >= Press;
        var box = new Rect(60, 32, 130 * d, 60 * d);
        string? caption = DragCaption(p);
        Point cursor;
        bool ring = held;
        double clickRing = p >= .20 && p < .25 ? 1 : 0;

        bool backdrop = _kind == ToolKind.Backdrop;
        dc.DrawRectangle(backdrop && p >= .26 && p < .96 ? Amber : Page, null, new Rect(0, 0, W, H));
        DrawScreenshot(dc, backdrop ? Ease(Seg(p, .25, .32)) * fade : 0);

        dc.PushOpacity(fade);
        switch (_kind)
        {
            case ToolKind.Select:
                cursor = At(p, BoxPath, true);
                if (p >= .78 && p < .95) dc.DrawGeometry(DarkChip, null, Outside(new Rect(60, 32, 130, 60)));
                else if (drawing) dc.DrawRectangle(null, DashedPen, box);
                if (p >= .70 && p < .78) Chip(dc, "Enter", new Point(96, 54), White, Ink);
                caption = p < .18 ? null : p < .30 ? "Press and hold" : p < .67 ? "Drag" : p < .92 ? "Let go, press Enter" : null;
                break;
            case ToolKind.Arrow:
                cursor = At(p, DiagonalPath, true);
                if (drawing) Arrow(dc, new Point(180, 100), cursor);
                break;
            case ToolKind.Line:
                cursor = At(p, DiagonalPath, true);
                if (drawing) dc.DrawLine(RedLine, new Point(180, 100), cursor);
                break;
            case ToolKind.Rectangle:
                cursor = At(p, BoxPath, true);
                if (drawing) dc.DrawRectangle(null, RedPen, box);
                break;
            case ToolKind.Oval:
                cursor = At(p, BoxPath, true);
                if (drawing) dc.DrawEllipse(null, RedPen, new Point(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2, box.Height / 2);
                break;
            case ToolKind.Spotlight:
                cursor = At(p, BoxPath, true);
                if (drawing) dc.DrawGeometry(Shade, null, Outside(box));
                break;
            case ToolKind.Pixelate:
                cursor = At(p, BoxPath, true);
                if (drawing) Mosaic(dc, box);
                break;
            case ToolKind.Erase:
                cursor = At(p, BoxPath, true);
                if (drawing) dc.DrawRectangle(Page, null, box);
                break;
            case ToolKind.Highlighter:
                cursor = At(p, [(0, 218, 128), (.18, 16, 51), (Press, 16, 51), (Release, 132, 51), (1, 132, 51)], true);
                if (drawing) dc.DrawRectangle(Marker, null, new Rect(14, 44, 120 * d, 14));
                break;
            case ToolKind.Freehand:
                cursor = Freehand(dc, p);
                break;
            case ToolKind.Text:
                cursor = At(p, [(0, 218, 128), (.16, 40, 58), (.25, 40, 58), (.36, 112, 96), (1, 112, 96)], true);
                ring = clickRing > 0;
                caption = p < .14 ? null : p < .34 ? "Click" : p < .90 ? "Type" : null;
                if (p >= .26) TypedLabel(dc, (int)Math.Floor(Seg(p, .32, .72) * 9));
                break;
            case ToolKind.Counter:
                cursor = At(p, [(0, 218, 128), (.16, 50, 45), (.25, 50, 45), (.38, 120, 70), (.47, 120, 70), (.60, 190, 100), (1, 190, 100)], true);
                ring = (p >= .20 && p < .25) || (p >= .42 && p < .47) || (p >= .64 && p < .69);
                caption = ClickCaption(p);
                Number(dc, "1", new Point(50, 45), Seg(p, .21, .24));
                Number(dc, "2", new Point(120, 70), Seg(p, .43, .46));
                Number(dc, "3", new Point(190, 100), Seg(p, .65, .68));
                break;
            case ToolKind.Magnifier:
                cursor = At(p, [(0, 218, 128), (.18, 60, 50), (Press, 60, 50), (Release, 170, 82), (1, 170, 82)], true);
                if (drawing)
                {
                    dc.DrawEllipse(null, BluePen, new Point(60, 50), 14, 14);
                    Zoomed(dc, cursor);
                }
                break;
            case ToolKind.Ruler:
                cursor = At(p, [(0, 218, 128), (.18, 60, 70), (Press, 60, 70), (Release, 190, 70), (1, 190, 70)], true);
                if (drawing) Measure(dc, cursor.X);
                break;
            case ToolKind.ColorPicker:
                cursor = At(p, [(0, 218, 128), (.16, 187, 64), (1, 187, 64)], true);
                ring = clickRing > 0;
                caption = ClickCaption(p);
                if (p >= .28) ColorChip(dc);
                break;
            case ToolKind.Backdrop:
                cursor = At(p, [(0, 218, 60), (.16, 159, 118), (1, 159, 118)], true);
                ring = clickRing > 0;
                caption = ClickCaption(p);
                Swatches(dc);
                break;
            default: // Drag into another app
                cursor = At(p, [(0, 218, 128), (.18, 34, 42), (Press, 34, 42), (Release, 176, 64), (1, 176, 64)], true);
                ChatWindow(dc, p);
                if (held) dc.DrawRoundedRectangle(Blue, null, new Rect(cursor.X + 4, cursor.Y + 4, 44, 30), 4, 4);
                break;
        }
        dc.Pop();

        Cursor(dc, cursor, ring, Seg(p, 0, .06) * (1 - Seg(p, .92, .97)));
        if (caption != null) Caption(dc, caption);
    }

    private static string? DragCaption(double p) => p < .18 ? null : p < .30 ? "Press and hold" : p < .67 ? "Drag" : p < .92 ? "Let go" : null;
    private static string? ClickCaption(double p) => p >= .14 && p < .84 ? "Click" : null;

    private static void DrawScreenshot(DrawingContext dc, double shrink)
    {
        if (shrink > 0)
        {
            double s = 1 - .32 * shrink;
            dc.PushTransform(new MatrixTransform(s, 0, 0, s, W / 2 * (1 - s), H / 2 * (1 - s) - 13 * shrink));
            dc.DrawRectangle(Page, null, new Rect(0, 0, W, H));
        }
        dc.DrawRectangle(Head, null, new Rect(0, 0, W, 20));
        foreach (var (y, width) in new (double, double)[] { (34, 112), (48, 104), (62, 118), (76, 80) })
            dc.DrawRoundedRectangle(Bar, null, new Rect(16, y, width, 6), 3, 3);
        dc.DrawRoundedRectangle(Blue, null, new Rect(16, 92, 62, 18), 5, 5);
        dc.DrawRoundedRectangle(Picture, null, new Rect(150, 36, 74, 58), 5, 5);
        if (shrink > 0) dc.Pop();
    }

    private static void Arrow(DrawingContext dc, Point from, Point tip)
    {
        var v = tip - from;
        if (v.Length < 6) return;
        v.Normalize();
        var n = new Vector(-v.Y, v.X);
        var back = tip - v * 12;
        dc.DrawLine(RedLine, from, back);
        var head = new StreamGeometry();
        using (var c = head.Open())
        {
            c.BeginFigure(tip, true, true);
            c.LineTo(back + n * 7, true, false);
            c.LineTo(back - n * 7, true, false);
        }
        head.Freeze();
        dc.DrawGeometry(Red, null, head);
    }

    private static Point Freehand(DrawingContext dc, double p)
    {
        if (p < Press) return At(p, [(0, 218, 128), (.18, 40, 100), (Press, 40, 100)], true);
        double along = Seg(p, Press, Release) * (Scribble.Length - 1);
        int whole = Math.Min((int)along, Scribble.Length - 2);
        var now = Scribble[whole] + (Scribble[whole + 1] - Scribble[whole]) * (along - whole);
        for (int i = 0; i < whole; i++) dc.DrawLine(RedLine, Scribble[i], Scribble[i + 1]);
        dc.DrawLine(RedLine, Scribble[whole], now);
        return now;
    }

    private static void Mosaic(DrawingContext dc, Rect area)
    {
        if (area.Width < 1 || area.Height < 1) return;
        dc.PushClip(new RectangleGeometry(area));
        for (int y = 0; y * 7 < H; y++)
            for (int x = 0; x * 7 < W; x++)
                dc.DrawRectangle((x + y) % 2 == 0 ? MosaicA : MosaicB, null, new Rect(x * 7, y * 7, 7, 7));
        dc.Pop();
    }

    private static void Zoomed(DrawingContext dc, Point at)
    {
        var clip = new EllipseGeometry(at, 29, 29);
        dc.PushClip(clip);
        dc.DrawEllipse(Page, null, at, 29, 29);
        dc.DrawRoundedRectangle(Bubble, null, new Rect(at.X - 21, at.Y - 16, 36, 11), 5, 5);
        dc.DrawRoundedRectangle(Bubble, null, new Rect(at.X - 21, at.Y + 1, 26, 11), 5, 5);
        dc.Pop();
        dc.DrawGeometry(null, WhitePen, clip);
    }

    private void TypedLabel(DrawingContext dc, int letters)
    {
        var text = Label("Look here"[..letters] + "|", 11, White);
        dc.DrawRoundedRectangle(Red, null, new Rect(40, 48, text.Width + 14, text.Height + 6), 4, 4);
        dc.DrawText(text, new Point(47, 51));
    }

    private void Number(DrawingContext dc, string n, Point at, double pop)
    {
        if (pop <= 0) return;
        double r = 10 * (.4 + .6 * pop);
        dc.DrawEllipse(Red, null, at, r, r);
        if (pop < 1) return;
        var text = Label(n, 11, White);
        dc.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2));
    }

    private void Measure(DrawingContext dc, double x)
    {
        dc.DrawLine(PinkPen, new Point(60, 70), new Point(x, 70));
        dc.DrawLine(PinkPen, new Point(60, 65), new Point(60, 75));
        dc.DrawLine(PinkPen, new Point(x, 65), new Point(x, 75));
        if (x > 96) Chip(dc, $"{Math.Round(x - 60)} px", new Point((60 + x) / 2 - 20, 46), Pink, White);
    }

    private void ColorChip(DrawingContext dc)
    {
        var text = Label("Copied #E8873A", 11, White);
        dc.DrawRoundedRectangle(DarkChip, null, new Rect(100, 104, text.Width + 31, text.Height + 8), 6, 6);
        dc.DrawRoundedRectangle(Picture, null, new Rect(108, 108.5, 10, 10), 2, 2);
        dc.DrawText(text, new Point(123, 108));
    }

    private static void Swatches(DrawingContext dc)
    {
        Brush[] colors = [White, Blue, Amber, Solid(0xBF, 0x5A, 0xF2), Solid(0x34, 0xC7, 0x59)];
        for (int i = 0; i < colors.Length; i++)
            dc.DrawRoundedRectangle(colors[i], MakePen(White, 1), new Rect(108 + 22 * i, 109, 18, 18), 4, 4);
    }

    private void ChatWindow(DrawingContext dc, double p)
    {
        dc.DrawRoundedRectangle(White, null, new Rect(128, 12, 100, 96), 6, 6);
        dc.DrawText(Label("Chat", 11, Ink), new Point(136, 18));
        dc.DrawRoundedRectangle(Navy, null, new Rect(20, 28, 28, 28), 6, 6);
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 2; col++)
                dc.DrawEllipse(White, null, new Point(30.5 + 7 * col, 35 + 7 * row), 1.5, 1.5);
        if (p >= Release) dc.DrawRoundedRectangle(Blue, null, new Rect(140, 36, 76, 50), 4, 4);
    }

    private void Chip(DrawingContext dc, string s, Point at, Brush background, Brush foreground)
    {
        var text = Label(s, 11, foreground);
        dc.DrawRoundedRectangle(background, null, new Rect(at.X, at.Y, text.Width + 16, text.Height + 6), 5, 5);
        dc.DrawText(text, new Point(at.X + 8, at.Y + 3));
    }

    private static void Cursor(DrawingContext dc, Point at, bool ring, double opacity)
    {
        dc.PushOpacity(opacity);
        if (ring) dc.DrawEllipse(null, RingPen, at, 9.5, 9.5);
        dc.PushTransform(new TranslateTransform(at.X - 2, at.Y - 1));
        dc.DrawGeometry(White, CursorPen, CursorShape);
        dc.Pop();
        dc.Pop();
    }

    private void Caption(DrawingContext dc, string s)
    {
        var text = Label(s, 11, White);
        double height = text.Height + 6;
        dc.DrawRoundedRectangle(Navy, null, new Rect(8, H - 8 - height, text.Width + 16, height), height / 2, height / 2);
        dc.DrawText(text, new Point(16, H - 8 - height + 3));
    }

    private FormattedText Label(string s, double size, Brush brush) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, brush == Ink ? Regular : Bold, size, brush, _dip);

    private static Geometry Outside(Rect hole) =>
        new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, W, H)), new RectangleGeometry(hole));

    private static double Seg(double p, double from, double to) => p <= from ? 0 : p >= to ? 1 : (p - from) / (to - from);
    private static double Ease(double x) => x < .5 ? 2 * x * x : 1 - Math.Pow(-2 * x + 2, 2) / 2;

    private static Point At(double p, (double T, double X, double Y)[] stops, bool ease)
    {
        for (int i = 0; i < stops.Length - 1; i++)
        {
            if (p > stops[i + 1].T) continue;
            double k = Seg(p, stops[i].T, stops[i + 1].T);
            if (ease) k = Ease(k);
            return new Point(stops[i].X + (stops[i + 1].X - stops[i].X) * k, stops[i].Y + (stops[i + 1].Y - stops[i].Y) * k);
        }
        return new Point(stops[^1].X, stops[^1].Y);
    }

    private static Brush Solid(byte r, byte g, byte b) => Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    private static Pen MakePen(Brush b, double width) => Frozen(new Pen(b, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    private static Pen MakeDashed() => Frozen(new Pen(Blue, 1.5) { DashStyle = new DashStyle([3, 2], 0) });
}

/// <summary>The hover card on a tool button, and the matching tile in the tool guide.</summary>
internal static class ToolHelpViews
{
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xFF, 0xB6, 0x27));
    private static readonly Brush Navy = new SolidColorBrush(Color.FromRgb(0x0D, 0x16, 0x30));
    private const string MouseIcon = "M4.5 .7h2a3.8 3.8 0 0 1 3.8 3.8v5a3.8 3.8 0 0 1-3.8 3.8h-2A3.8 3.8 0 0 1 .7 9.5v-5A3.8 3.8 0 0 1 4.5 .7zM5.5 .7v5";

    public static FrameworkElement Badge(string how)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(MouseIcon), Stroke = Navy, StrokeThickness = 1.4, Width = 11, Height = 14, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = how, Foreground = Navy, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return new Border { Background = Amber, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 2), HorizontalAlignment = HorizontalAlignment.Left, Child = row };
    }

    public const double CardWidth = 270;

    public static FrameworkElement HoverCard(ToolHelpEntry e)
    {
        var card = new StackPanel { Width = CardWidth };
        card.Children.Add(new ToolDemo(e.Kind) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) });

        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (e.Key.Length > 0)
        {
            var key = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x46, 0x46, 0x4B)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 1, 7, 1), Child = new TextBlock { Text = e.Key, FontSize = 12 } };
            DockPanel.SetDock(key, Dock.Right);
            title.Children.Add(key);
        }
        title.Children.Add(new TextBlock { Text = e.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        card.Children.Add(title);
        card.Children.Add(Badge(e.How));
        card.Children.Add(new TextBlock { Text = e.Say, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xDB)), Margin = new Thickness(0, 6, 0, 0) });
        return card;
    }

    public static FrameworkElement GuideTile(ToolHelpEntry e)
    {
        var tile = new StackPanel { Width = 240, Margin = new Thickness(0, 0, 20, 22) };
        tile.Children.Add(new ToolDemo(e.Kind));
        tile.Children.Add(new TextBlock { Text = e.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        tile.Children.Add(Badge(e.How));
        var say = new TextBlock { Text = e.Say, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        say.SetResourceReference(TextBlock.ForegroundProperty, "UiSubtle");
        tile.Children.Add(say);
        return tile;
    }
}

/// <summary>
/// The card that follows the mouse along the toolbar. A plain WPF tooltip won't do: it stays on the old button
/// while the mouse moves across to the next one, so the card only changed after leaving the toolbar and coming back.
/// </summary>
internal sealed class ToolHoverCards
{
    private const double BorderSize = 1, PaddingSize = 12;
    private static readonly double FullWidth = ToolHelpViews.CardWidth + 2 * (BorderSize + PaddingSize);

    private readonly FrameworkElement _anchor;
    private readonly System.Windows.Controls.Primitives.Popup _popup;
    private readonly System.Windows.Threading.DispatcherTimer _opening = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly System.Windows.Threading.DispatcherTimer _closing = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Dictionary<ToolHelpEntry, FrameworkElement> _cards = [];
    private FrameworkElement? _button;
    private ToolHelpEntry? _help;

    public bool Enabled { get; set; }
    public ToolHelpEntry? Shown { get; private set; }

    public ToolHoverCards(Window window, FrameworkElement anchor)
    {
        _anchor = anchor;
        _popup = new System.Windows.Controls.Primitives.Popup { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Relative, AllowsTransparency = true, StaysOpen = true, IsHitTestVisible = false, Focusable = false };
        _opening.Tick += (_, _) => Show();
        _closing.Tick += (_, _) => Hide();
        window.Deactivated += (_, _) => Hide();
        window.LocationChanged += (_, _) => Hide();
        window.IsVisibleChanged += (_, _) => { if (!window.IsVisible) Hide(); };
    }

    public void Attach(FrameworkElement button, ToolHelpEntry help)
    {
        button.MouseEnter += (_, _) => Enter(button, help);
        button.MouseLeave += (_, _) => Leave();
    }

    private void Enter(FrameworkElement button, ToolHelpEntry help)
    {
        if (!Enabled) return;
        _closing.Stop();
        _button = button;
        _help = help;
        if (_popup.IsOpen) Show(); // already reading these: switch straight away
        else { _opening.Stop(); _opening.Start(); }
    }

    private void Leave()
    {
        _opening.Stop();
        if (_popup.IsOpen) { _closing.Stop(); _closing.Start(); }
    }

    private void Show()
    {
        _opening.Stop();
        if (_button == null || _help == null || !_button.IsVisible) return;
        if (Shown != _help)
        {
            if (!_cards.TryGetValue(_help, out var card))
            {
                card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x40)),
                    BorderThickness = new Thickness(BorderSize),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(PaddingSize),
                    Child = ToolHelpViews.HoverCard(_help),
                };
                System.Windows.Documents.TextElement.SetForeground(card, new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)));
                _cards[_help] = card;
            }
            _popup.Child = card;
            Shown = _help;
        }
        var at = _button.TranslatePoint(new Point(0, 0), _anchor);
        _popup.HorizontalOffset = Math.Max(4, Math.Min(at.X + (_button.ActualWidth - FullWidth) / 2, _anchor.ActualWidth - FullWidth - 4));
        _popup.VerticalOffset = _anchor.ActualHeight + 2;
        _popup.IsOpen = true;
    }

    public void Hide()
    {
        _opening.Stop();
        _closing.Stop();
        _popup.IsOpen = false;
        _popup.Child = null;
        Shown = null;
    }
}
