using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Loupix.Core.Imaging;
using Loupix.Imaging;
using Loupix.Services;
using Loupix.UI;
using Microsoft.Win32;

namespace Loupix.Editor;

public partial class EditorWindow : Window
{
    private readonly Dictionary<ToolKind, RadioButton> _toolButtons = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };
    private EditorDocument? _doc;
    private Color? _hoverColor;
    private static AppSettings Settings => SettingsStore.Current;

    public EditorDocument? Document => _doc;
    public event Action? DocumentClosed;

    public EditorWindow()
    {
        InitializeComponent();
        AppIcon.Source = IconFactory.Logo(36);
        Icon = IconFactory.Logo(48);
        BuildToolbar();
        BuildActions();
        _hoverCards = new ToolHoverCards(this, TitleBar);
        ApplyToolTips();
        SettingsStore.Changed += ApplyToolTips;

        Canvas.StateChanged += () => { if (!_keepStyleBar) UpdateStyleBar(); UpdateInfo(); };
        Canvas.ViewChanged += UpdateZoomText;
        Canvas.ToolChanged += t => { if (_toolButtons.TryGetValue(t, out var b)) b.IsChecked = true; UpdateStyleBar(); FitToolbar(); };
        Canvas.HoverChanged += OnHover;
        Canvas.ColorPicked += OnColorPicked;
        Canvas.Notify += msg => ShowToast(msg);
        Canvas.UnhandledKey += OnCanvasKey;
        Canvas.StyleNewAnnotation = ApplyToolDefaults;
        Canvas.Drop += OnDrop;
        Canvas.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };

        _toastTimer.Tick += (_, _) => HideToast();
        SourceInitialized += (_, _) => NativeDarkFrame();
        StateChanged += (_, _) => OnWindowStateChanged();
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Closing += OnClosing;
        Activated += (_, _) => Canvas.Focus();
    }

    private void NativeDarkFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.NativeMethods.SetDarkTitleBar(hwnd);
        Native.NativeMethods.SetRoundedCorners(hwnd);
    }

    // ---------------- document lifecycle ----------------

    /// <summary>Shows a document in the editor, sizing the window to the image on the given monitor.</summary>
    public void Open(EditorDocument doc, Capture.MonitorInfo? monitor = null)
    {
        HideTip();
        _doc = doc;
        Canvas.Document = doc;
        Canvas.Tool = ToolKind.Select;
        Canvas.MeasureInPhysicalPixels = Settings.MeasureInPhysicalPixels;
        Topmost = Settings.EditorAlwaysOnTop;
        PlaceWindow(doc, monitor ?? Capture.Monitors.AtCursor());
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        ForceForeground();
        Dispatcher.BeginInvoke(() =>
        {
            Canvas.ZoomToFit();
            if (Settings.DefaultZoom100 && doc.Base.Width * 0.9 < Canvas.ActualWidth * Canvas.DpiScale) Canvas.ZoomTo100();
            UpdateInfo();
            Canvas.Focus();
        }, DispatcherPriority.Loaded);
        UpdateInfo();
        UpdateStyleBar();
    }

    /// <summary>Self-test hook: load a document without placing or activating the window.</summary>
    internal void LoadForTest(EditorDocument doc)
    {
        _doc = doc;
        Canvas.Document = doc;
        UpdateInfo();
        UpdateStyleBar();
    }

    private void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Hotkey-triggered windows can be denied foreground; a topmost toggle reliably brings it forward.
        bool wasTop = Topmost;
        Topmost = true;
        Native.NativeMethods.SetForegroundWindow(hwnd);
        Topmost = wasTop;
    }

    private const double ToolbarHeight = 45;   // title bar + toolbar (44) + divider
    private const double MinCanvasWidth = 836;

    /// <summary>
    /// Shottr's sizing rule (measured on the Mac): the image is laid out at one image pixel per screen pixel;
    /// canvas width is clamped to [836, 80% of the screen], the canvas is never less than half as tall as it
    /// is wide, and its height is capped at 80% of the screen. Centred horizontally; the top sits at 45% of the
    /// free vertical space. Re-applied on every load.
    /// </summary>
    private void PlaceWindow(EditorDocument? doc, Capture.MonitorInfo monitor)
    {
        if (WindowState == WindowState.Maximized) return;
        double s = monitor.Scale;
        var screen = monitor.Bounds;
        var wa = monitor.WorkArea;
        double maxW = 0.8 * screen.Width / s, maxH = 0.8 * screen.Height / s;
        double w, h;
        if (doc == null)
        {
            (w, h) = (600, 432);
        }
        else
        {
            var outer = DocumentRenderer.OuterBounds(doc);
            double imgW = outer.Width / s, imgH = outer.Height / s;
            double cw = Math.Clamp(imgW, Math.Min(MinCanvasWidth, maxW), maxW);
            double ch = Math.Min(Math.Max(imgH, cw / 2), maxH);
            if (Settings.DefaultZoom100) { cw = maxW; ch = maxH; }
            (w, h) = (cw, ch + ToolbarHeight);
        }
        int pw = (int)Math.Round(w * s), ph = (int)Math.Round(h * s);
        int px = screen.X + (screen.Width - pw) / 2;
        int py = wa.Y + (int)Math.Round(0.45 * Math.Max(0, wa.Height - ph));
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, px, py, pw, ph, Native.NativeMethods.SWP_NOZORDER | Native.NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Shows the editor with nothing loaded (manual launch, or reopen before any capture).</summary>
    public void ShowEmpty(Capture.MonitorInfo monitor)
    {
        _doc = null;
        Canvas.Document = null;
        var area = Hotkey.Parse(Settings.Hotkeys.GetValueOrDefault(CaptureAction.Area));
        var full = Hotkey.Parse(Settings.Hotkeys.GetValueOrDefault(CaptureAction.Fullscreen));
        Canvas.EmptyHint = "Nothing to edit yet\n"
            + (area.IsEmpty ? "" : $"{area}  area capture      ")
            + (full.IsEmpty ? "" : $"{full}  fullscreen") + "\n"
            + "Ctrl+V pastes an image · drop an image file here · Ctrl+O opens one";
        SizeText.Text = "—";
        ZoomText.Text = "";
        Title = "Loupix"; // the previous document's file name must not linger in the taskbar
        StyleBar.Visibility = Visibility.Collapsed;
        PlaceWindow(null, monitor);
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        ForceForeground();
        Canvas.InvalidateVisual();
        Canvas.Focus();
    }

    private void OnWindowStateChanged()
    {
        // Maximized borderless windows overhang the screen by the resize border.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (AppController.Instance.IsQuitting) return;
        // Keep the window around for instant reuse.
        e.Cancel = true;
        CloseDocument();
    }

    public void CloseDocument()
    {
        HideTip();
        Canvas.CommitText();
        Hide();
        DocumentClosed?.Invoke();
    }

    // ---------------- toolbar ----------------

    private void BuildToolbar()
    {
        foreach (var t in Tools.All)
        {
            var path = Tools.IconFor(t, (Brush)FindResource("TextBrush"));
            var b = new RadioButton
            {
                Style = (Style)FindResource("ToolToggle"),
                Content = path,
                GroupName = "tools",
                ToolTip = t.Tooltip,
                IsChecked = t.Kind == ToolKind.Select,
            };
            b.Checked += (_, _) => Canvas.Tool = t.Kind;
            _toolButtons[t.Kind] = b;
            ToolsPanel.Children.Add(b);
            if (t.Kind == ToolKind.Select) ToolsPanel.Children.Add(new Rectangle { Style = (Style)FindResource("Separator") });
        }
        _overflowButton = new Button { Style = (Style)FindResource("IconButton"), Content = "»", FontSize = 15, ToolTip = "More tools", Visibility = Visibility.Collapsed };
        _overflowButton.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var t in Tools.All.Where(t => _toolButtons[t.Kind].Visibility != Visibility.Visible))
            {
                var mi = new MenuItem { Header = t.Name, InputGestureText = Hotkey.KeyName(t.Shortcut), Icon = Tools.IconFor(t, (Brush)FindResource("TextBrush")) };
                var kind = t.Kind;
                mi.Click += (_, _) => Canvas.Tool = kind;
                menu.Items.Add(mi);
            }
            menu.PlacementTarget = _overflowButton;
            menu.Placement = PlacementMode.Bottom;
            UiTheme.StyleMenu(menu, light: false);
            menu.IsOpen = true;
        };
        ToolsPanel.Children.Add(_overflowButton);
        TitleBar.SizeChanged += (_, _) => FitToolbar();
    }

    private bool? _animatedTips;

    private readonly ToolHoverCards _hoverCards;
    internal ToolHoverCards HoverCards => _hoverCards;
    internal FrameworkElement ToolButton(ToolKind kind) => _toolButtons[kind];

    /// <summary>Tool buttons get an animated card, unless that's turned off in Settings.</summary>
    private void ApplyToolTips()
    {
        bool animated = Settings.AnimatedToolTips;
        if (_animatedTips == animated) return;
        bool firstTime = _animatedTips == null;
        _animatedTips = animated;
        _hoverCards.Enabled = animated;
        void Set(FrameworkElement button, ToolHelpEntry help, string plain)
        {
            if (firstTime)
            {
                _hoverCards.Attach(button, help);
                System.Windows.Automation.AutomationProperties.SetName(button, plain);
            }
            button.ToolTip = animated ? null : plain;
        }
        foreach (var t in Tools.All) Set(_toolButtons[t.Kind], ToolHelp.For(t.Kind), t.Tooltip);
        Set(_dragButton, ToolHelp.DragOut, "Drag the image into another app");
    }

    private bool _fitting;

    /// <summary>
    /// Makes the title bar fit: caption buttons always stay; lower-priority items collapse first
    /// (all of them are also in the ⋯ menu), then trailing tools move into a » overflow menu.
    /// </summary>
    private void FitToolbar()
    {
        if (_fitting || TitleBar.ActualWidth <= 0 || _overflowButton == null) return;
        _fitting = true;
        try
        {
            ColorText.Visibility = Visibility.Visible;
            foreach (var e in new UIElement[] { _dragButton, ZoomButton, _addButton, _ocrButton, _pinButton }) e.Visibility = Visibility.Visible;
            _gifButton.Visibility = _gifWanted ? Visibility.Visible : Visibility.Collapsed;
            foreach (var b in _toolButtons.Values) b.Visibility = Visibility.Visible;
            _overflowButton.Visibility = Visibility.Collapsed;

            double available = TitleBar.ActualWidth - 4;
            double Need()
            {
                double w = 0;
                foreach (UIElement c in ((DockPanel)TitleBar.Child).Children)
                {
                    c.Measure(new Size(double.PositiveInfinity, 44));
                    w += c.DesiredSize.Width;
                }
                return w;
            }
            foreach (var e in new UIElement[] { ColorText, _dragButton, _gifButton, ZoomButton, _addButton, _ocrButton, _pinButton })
            {
                if (Need() <= available) return;
                e.Visibility = Visibility.Collapsed;
            }
            if (Need() <= available) return;
            _overflowButton.Visibility = Visibility.Visible;
            for (int i = Tools.All.Count - 1; i >= 1 && Need() > available; i--)
            {
                var kind = Tools.All[i].Kind;
                if (kind != Canvas.Tool) _toolButtons[kind].Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _fitting = false;
        }
    }

    private Button ActionButton(string glyph, string tooltip, RoutedEventHandler click, bool useIconFont = true)
    {
        var b = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = useIconFont ? (FontFamily)FindResource("IconFont") : new FontFamily("Segoe UI"),
                FontSize = useIconFont ? 15 : 12,
                FontWeight = useIconFont ? FontWeights.Normal : FontWeights.SemiBold,
            },
            ToolTip = tooltip,
        };
        b.Click += click;
        ActionsPanel.Children.Add(b);
        return b;
    }

    private Button _gifButton = null!, _addButton = null!, _ocrButton = null!, _pinButton = null!, _dragButton = null!;
    private Button _overflowButton = null!;
    private bool _gifWanted;

    private void BuildActions()
    {
        _addButton = ActionButton("", "Add capture — append another screenshot to the canvas (Ctrl+N)", (_, _) => AppController.Instance.AddCapture());
        _ocrButton = ActionButton("OCR", "Recognize text (Ctrl+T) — selection or whole image", async (_, _) => await RecognizeText(), useIconFont: false);
        _gifButton = ActionButton("GIF", "Make a before/after GIF from the overlay", (_, _) => MakeGif(), useIconFont: false);
        _gifButton.Visibility = Visibility.Collapsed;
        _pinButton = ActionButton("", "Pin to screen (Ctrl+P)", (_, _) => Pin());
        var drag = _dragButton = ActionButton("", "Drag the image into another app", (_, _) => { });
        drag.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); };
        drag.PreviewMouseMove += OnDragHandleMove;
        ActionButton("", "Copy (Ctrl+C)", (_, _) => Copy(closeAfter: false));
        ActionButton("", "Save (Ctrl+S) — Shift+click or Ctrl+Shift+S to choose where", (_, _) =>
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SaveAs(); else Save(closeAfter: false);
        });
        ActionButton("", "How each tool works", (_, _) => AppController.Instance.ShowToolGuide());
        var more = ActionButton("", "More", (s, _) => ShowMainMenu((FrameworkElement)s));
    }

    // ---------------- info area ----------------

    private void UpdateInfo()
    {
        if (_doc == null) return;
        var m = Canvas.Marquee;
        if (m is RectI r) SizeText.Text = Canvas.SizeText(r.Width, r.Height);
        else
        {
            var c = DocumentRenderer.OuterBounds(_doc);
            SizeText.Text = Canvas.SizeText(c.Width, c.Height);
        }
        bool gif = _doc.Annotations.Any(a => a is ImageAnnotation { IsCapture: false });
        if (gif != _gifWanted) { _gifWanted = gif; FitToolbar(); }
        UpdateZoomText();
        Title = _doc.SourcePath != null ? $"{System.IO.Path.GetFileName(_doc.SourcePath)} — Loupix" : "Loupix";
    }

    private void UpdateZoomText() => ZoomText.Text = $"{Canvas.Zoom * 100:0}%";

    private void OnHover(Color? c, Point? p)
    {
        _hoverColor = c;
        if (c is Color col)
        {
            ColorSwatch.Background = new SolidColorBrush(col);
            ColorText.Text = Features.FormatColor(col);
        }
        else
        {
            ColorSwatch.Background = Brushes.Transparent;
            ColorText.Text = "—";
        }
    }

    private void OnColorPicked(Color c, string what)
    {
        _picked.Add(c);
        if (_picked.Count > 2) _picked.RemoveAt(0);
        var text = Features.FormatColor(c);
        ImageIO.CopyText(text);
        ShowToast($"{what} {text} copied", c);
    }

    private void OnSizeClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var menu = new ContextMenu();
        var unit = new MenuItem { Header = Settings.MeasureInPhysicalPixels ? "Show sizes in logical (scaled) pixels" : "Show sizes in physical pixels" };
        unit.Click += (_, _) =>
        {
            Settings.MeasureInPhysicalPixels = !Settings.MeasureInPhysicalPixels;
            SettingsStore.Save();
            Canvas.MeasureInPhysicalPixels = Settings.MeasureInPhysicalPixels;
            UpdateInfo();
            Canvas.InvalidateVisual();
        };
        var resize = new MenuItem { Header = "Resize image…" };
        resize.Click += (_, _) => ShowResizeDialog();
        menu.Items.Add(resize);
        menu.Items.Add(unit);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        UiTheme.StyleMenu(menu, light: false);
        menu.IsOpen = true;
    }

    private void OnZoomClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Add(string header, string gesture, Action a)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Add("Zoom to fit", "Ctrl+1", () => Canvas.ZoomToFit(true));
        Add("Actual size (100%)", "Ctrl+0", Canvas.ZoomTo100);
        Add("Zoom to selection", "Ctrl+2", () => { if (Canvas.Marquee is RectI r) Canvas.ZoomToRect(new Rect(r.X, r.Y, r.Width, r.Height)); });
        Add("Zoom in", "Ctrl+=", () => Canvas.ZoomStep(1));
        Add("Zoom out", "Ctrl+-", () => Canvas.ZoomStep(-1));
        foreach (var z in new[] { 50, 200, 400, 800 }) Add($"{z}%", "", () => Canvas.ZoomOnPoint(new Point(_doc!.Base.Width / 2.0, _doc.Base.Height / 2.0), z / 100.0));
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        UiTheme.StyleMenu(menu, light: false);
        menu.IsOpen = true;
    }

    private readonly List<Color> _picked = new();

    private void OnColorFormatClick(object sender, RoutedEventArgs e)
    {
        // Compare the last picked colour with the one before it (or white).
        Color fg = _picked.Count > 0 ? _picked[^1] : _hoverColor ?? Colors.Black;
        Color bg = _picked.Count > 1 ? _picked[^2] : Colors.White;
        new ColorInspector((UIElement)sender, fg, bg, msg => ShowToast(msg)).IsOpen = true;
    }

    // ---------------- style bar ----------------

    private ToolStyle ToolDefaults(ToolKind kind)
    {
        var info = Tools.Get(kind);
        return Settings.StyleFor(kind.ToString(), () => new ToolStyle { Color = info.DefaultColor, Size = info.DefaultSize });
    }

    private Annotation ApplyToolDefaults(ToolKind tool, Annotation a)
    {
        var s = ToolDefaults(tool);
        if (TryParseColor(Settings.AnnotationColor, out var c)) a.Color = c;
        a.Size = Math.Clamp(s.Size, 1, 5);
        a.HandDrawn = s.HandDrawn;
        if (a is ArrowAnnotation ar && tool == ToolKind.Line) ar.Variant = ArrowAnnotation.Line;
        else a.Variant = s.Variant;
        if (a is BoxAnnotation && tool is ToolKind.Rectangle or ToolKind.Oval) a.Fill = (FillMode)Math.Clamp(s.Variant, 0, 2);
        if (a is RectangleAnnotation or EllipseAnnotation) a.Variant = 0;
        if (a is SpotlightAnnotation sp) { sp.Level = Math.Clamp(s.Size * 2 - 1, 1, 9); sp.Variant = s.Variant; }
        return a;
    }

    private static bool TryParseColor(string hex, out Color c)
    {
        try { c = (Color)ColorConverter.ConvertFromString(hex); return true; }
        catch { c = Colors.Red; return false; }
    }

    private ToolKind? StyleTarget()
    {
        if (Canvas.Selected is { } a)
        {
            return a switch
            {
                ArrowAnnotation ar => ar.Variant == ArrowAnnotation.Line ? ToolKind.Line : ToolKind.Arrow,
                RectangleAnnotation => ToolKind.Rectangle,
                EllipseAnnotation => ToolKind.Oval,
                TextAnnotation => ToolKind.Text,
                FreehandAnnotation => ToolKind.Freehand,
                HighlighterAnnotation => ToolKind.Highlighter,
                SpotlightAnnotation => ToolKind.Spotlight,
                CounterAnnotation => ToolKind.Counter,
                EffectAnnotation e => e.Mode == EffectMode.Erase ? ToolKind.Erase : ToolKind.Pixelate,
                MagnifierAnnotation => ToolKind.Magnifier,
                _ => null,
            };
        }
        return Canvas.Tool;
    }

    private void UpdateStyleBar()
    {
        StylePanel.Children.Clear();
        var target = StyleTarget();
        var sel = Canvas.Selected;

        if (sel is ImageAnnotation img)
        {
            AddLabel("Opacity");
            AddSlider(10, 100, img.Opacity * 100, v => Canvas.ApplyToSelected(a => ((ImageAnnotation)a).Opacity = v / 100));
            AddHint("Keys 1–9 set opacity · 0 = opaque");
            StyleBar.Visibility = Visibility.Visible;
            return;
        }

        switch (target)
        {
            case ToolKind.Select when Canvas.Marquee != null:
                AddHint("Enter: crop · A: auto-adjust · Delete: erase · B: blur · C: average color · Ctrl+C: copy area · [ ]: grow/shrink · Ctrl+2: zoom");
                AddButton("Crop", () => { if (Canvas.Marquee is RectI r) { _doc!.Crop(r); Canvas.ClearMarquee(); Canvas.ZoomToFit(); } });
                AddButton("Auto-adjust", () => { if (Canvas.Marquee is RectI r) Canvas.SetMarquee(RasterOps.AutoAdjust(_doc!.Base.Pixels, r)); });
                AddButton("Pad +10", () => { if (Canvas.Marquee is RectI r) Canvas.SetMarquee(r.Inflate((int)(10 * _doc!.Scale), (int)(10 * _doc.Scale))); });
                StyleBar.Visibility = Visibility.Visible;
                return;
            case ToolKind.Select:
            case null:
                StyleBar.Visibility = Visibility.Collapsed;
                return;
            case ToolKind.Ruler:
                AddHint("Drag to measure · or hover and press ← → (horizontal) or ↑ ↓ (vertical) to find the edges · Shift: outer size · Click: imprint · Alt+S / Alt+D: guides");
                StyleBar.Visibility = Visibility.Visible;
                return;
            case ToolKind.ColorPicker:
                AddHint("Click or press Tab to copy the color under the cursor · Shift+Tab: text color · select an area and press C for the average color");
                StyleBar.Visibility = Visibility.Visible;
                return;
            case ToolKind.Backdrop:
                BuildBackdropBar();
                StyleBar.Visibility = Visibility.Visible;
                return;
        }

        var kind = target!.Value;
        var info = Tools.Get(kind);
        var defaults = ToolDefaults(kind);
        Color currentColor = sel?.Color ?? (TryParseColor(Settings.AnnotationColor, out var dc) ? dc : Colors.Red);
        int currentSize = sel?.Size ?? defaults.Size;
        int currentVariant = sel switch
        {
            EffectAnnotation e => e.Mode == EffectMode.Blur ? 1 : e.TextOnly ? (e.Mode == EffectMode.Erase ? 1 : 2) : 0,
            RectangleAnnotation or EllipseAnnotation => 0,
            null => defaults.Variant,
            _ => sel.Variant,
        };

        bool hasColor = kind is not (ToolKind.Spotlight or ToolKind.Pixelate or ToolKind.Erase or ToolKind.Magnifier);
        bool hasSize = kind is not (ToolKind.Erase or ToolKind.Magnifier or ToolKind.Spotlight);

        if (hasColor)
        {
            foreach (var c in Tools.Palette)
            {
                var sw = new RadioButton { Style = (Style)FindResource("Swatch"), Background = new SolidColorBrush(c), GroupName = "swatch", IsChecked = c == currentColor, ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}" };
                var color = c;
                sw.Checked += (_, _) => SetStyle(kind, a => a.Color = color, _ => Settings.AnnotationColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}");
                StylePanel.Children.Add(sw);
            }
            var custom = new Button { Style = (Style)FindResource("IconButton"), Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14 }, ToolTip = "Custom color" };
            custom.Click += (_, _) => PickCustomColor(custom, currentColor, col => SetStyle(kind, a => a.Color = col, _ => Settings.AnnotationColor = $"#{col.R:X2}{col.G:X2}{col.B:X2}"));
            StylePanel.Children.Add(custom);
            AddSeparator();
        }

        if (hasSize)
        {
            AddLabel(kind == ToolKind.Pixelate ? "Strength" : "Size");
            for (int i = 1; i <= 5; i++)
            {
                int size = i;
                var dot = new Ellipse { Width = 3 + i * 2.2, Height = 3 + i * 2.2, Fill = (Brush)FindResource("TextBrush") };
                var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = dot, GroupName = "size", IsChecked = currentSize == i, ToolTip = $"Size {i}" };
                rb.Checked += (_, _) => SetStyle(kind, a => a.Size = size, s => s.Size = size);
                StylePanel.Children.Add(rb);
            }
            AddSeparator();
        }

        if (kind is ToolKind.Rectangle or ToolKind.Oval)
        {
            AddLabel("Fill");
            var fill = sel?.Fill ?? (FillMode)Math.Clamp(defaults.Variant, 0, 2);
            foreach (var (mode, name) in new[] { (FillMode.None, "None"), (FillMode.Translucent, "Tint"), (FillMode.Solid, "Solid") })
            {
                var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = name, GroupName = "fill", IsChecked = fill == mode };
                var m = mode;
                rb.Checked += (_, _) => SetStyle(kind, a => a.Fill = m, s => s.Variant = (int)m);
                StylePanel.Children.Add(rb);
            }
            AddSeparator();
        }
        else if (info.Variants.Length > 0)
        {
            for (int i = 0; i < info.Variants.Length; i++)
            {
                int v = i;
                var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = info.Variants[i], GroupName = "variant", IsChecked = currentVariant == i };
                rb.Checked += (_, _) => SetStyle(kind, a =>
                {
                    if (a is EffectAnnotation e)
                    {
                        if (kind == ToolKind.Pixelate) { e.Mode = v == 1 ? EffectMode.Blur : EffectMode.Pixelate; e.TextOnly = v == 2; }
                        else e.TextOnly = v == 1;
                    }
                    else a.Variant = v;
                }, s => s.Variant = v);
                StylePanel.Children.Add(rb);
            }
            AddSeparator();
        }

        if (kind == ToolKind.Spotlight)
        {
            AddLabel("Darkness");
            int level = (sel as SpotlightAnnotation)?.Level ?? Math.Clamp(defaults.Size * 2 - 1, 1, 9);
            AddSlider(1, 9, level, v => SetStyle(kind, a => ((SpotlightAnnotation)a).Level = (int)Math.Round(v), s => s.Size = (int)Math.Round((v + 1) / 2)));
            AddHint("Keys 1–9");
        }

        if (kind is ToolKind.Rectangle or ToolKind.Oval or ToolKind.Arrow or ToolKind.Text or ToolKind.Line)
        {
            var hd = new ToggleButton { Style = (Style)FindResource("ToggleSegment"), Content = "Hand-drawn", IsChecked = sel?.HandDrawn ?? defaults.HandDrawn };
            hd.Click += (_, _) => SetStyle(kind, a => a.HandDrawn = hd.IsChecked == true, s => s.HandDrawn = hd.IsChecked == true);
            StylePanel.Children.Add(hd);
        }

        if (kind == ToolKind.Counter) AddHint("Type a number to renumber the selected counter");
        if (kind == ToolKind.Text) AddHint("Drag to add a pointer · Enter: done · Shift+Enter: new line · Tab: insert color");
        if (kind == ToolKind.Arrow) AddHint("Drag the middle handle to bend · Ctrl: reverse · Shift: 45°");

        StyleBar.Visibility = Visibility.Visible;
    }

    private void SetStyle(ToolKind kind, Action<Annotation> apply, Action<ToolStyle> remember)
    {
        if (Canvas.Selected != null) Canvas.ApplyToSelected(apply);
        remember(ToolDefaults(kind));
        SettingsStore.Save();
        Canvas.Focus();
    }

    private void AddLabel(string text) => StylePanel.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("BarLabel") });
    private void AddHint(string text) => StylePanel.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("BarLabel"), FontSize = 11.5, Margin = new Thickness(10, 0, 0, 0) });
    private void AddSeparator() => StylePanel.Children.Add(new Rectangle { Style = (Style)FindResource("Separator") });

    private void AddButton(string text, Action click)
    {
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = text, Height = 26, Margin = new Thickness(4, 0, 0, 0) };
        b.Click += (_, _) => { click(); Canvas.Focus(); };
        StylePanel.Children.Add(b);
    }

    private bool _keepStyleBar;

    private Slider AddSlider(double min, double max, double value, Action<double> changed)
    {
        var s = new Slider { Style = (Style)FindResource("DarkSlider"), Minimum = min, Maximum = max, Value = value };
        bool first = true;
        s.ValueChanged += (_, e) =>
        {
            if (first) return;
            // Rebuilding the bar here would destroy the very slider being dragged.
            _keepStyleBar = true;
            try { changed(e.NewValue); }
            finally { _keepStyleBar = false; }
        };
        first = false;
        StylePanel.Children.Add(s);
        return s;
    }

    private void PickCustomColor(FrameworkElement anchor, Color current, Action<Color> picked)
    {
        var box = new TextBox { Style = (Style)FindResource("RoundTextBox"), Text = $"#{current.R:X2}{current.G:X2}{current.B:X2}", Width = 90, Margin = new Thickness(8), FontFamily = new FontFamily("Consolas") };
        var grid = new UniformGrid { Columns = 8, Margin = new Thickness(8, 0, 8, 8) };
        string[] extra = ["#000000", "#3A3A3C", "#8E8E93", "#C7C7CC", "#FFFFFF", "#A2845E", "#FF6482", "#FFB340",
                          "#FFE620", "#7DDA58", "#00C7BE", "#5AC8FA", "#5E5CE6", "#DA8FFF", "#FF375F", "#AC8E68"];
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        foreach (var hex in extra)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new Button { Style = (Style)FindResource("RoundButton"), Width = 20, Height = 20, Margin = new Thickness(2), Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, Focusable = false };
            b.Click += (_, _) => { picked(c); popup.IsOpen = false; };
            grid.Children.Add(b);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return && TryParseColor(box.Text.StartsWith('#') ? box.Text : "#" + box.Text, out var c)) { picked(c); popup.IsOpen = false; }
        };
        popup.Child = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = new StackPanel { Children = { box, grid } },
        };
        popup.IsOpen = true;
        box.Focus();
        box.SelectAll();
    }

    private void BuildBackdropBar()
    {
        if (_doc == null) return;
        var bd = _doc.Backdrop;
        for (int i = 0; i < BackdropPresets.All.Count; i++)
        {
            int idx = i;
            var p = BackdropPresets.All[i];
            var sw = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                Background = p.Solid && p.A == Colors.Transparent ? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) : BackdropPresets.BrushFor(i),
                GroupName = "backdrop",
                IsChecked = bd?.Preset == i,
                ToolTip = p.Name,
            };
            sw.Checked += (_, _) => SetBackdrop(b => b with { Preset = idx });
            StylePanel.Children.Add(sw);
        }
        AddSeparator();
        var cur = bd ?? Backdrop.Default;
        AddLabel("Padding");
        AddSlider(0, 200, cur.Padding, v => SetBackdrop(b => b with { Padding = v }, checkpoint: false));
        AddLabel("Shadow");
        AddSlider(0, 1, cur.Shadow, v => SetBackdrop(b => b with { Shadow = v }, checkpoint: false));
        AddLabel("Corners");
        AddSlider(0, 40, cur.Radius, v => SetBackdrop(b => b with { Radius = v }, checkpoint: false));
        AddSeparator();
        foreach (var aspect in new[] { "Auto", "16:9", "4:3", "1:1" })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = aspect, GroupName = "aspect", IsChecked = cur.Aspect == aspect };
            rb.Checked += (_, _) => SetBackdrop(b => b with { Aspect = aspect });
            StylePanel.Children.Add(rb);
        }
        AddSeparator();
        AddButton("Remove", () => Canvas.ChangeBackdropSmoothly(() => { _doc.Checkpoint(); _doc.Backdrop = null; _doc.RaiseChanged(); }));
    }

    private DateTime _lastBackdropCheckpoint;

    private void SetBackdrop(Func<Backdrop, Backdrop> change, bool checkpoint = true)
    {
        if (_doc == null) return;
        if (checkpoint || (DateTime.Now - _lastBackdropCheckpoint).TotalSeconds > 1.5)
        {
            _doc.Checkpoint();
            _lastBackdropCheckpoint = DateTime.Now;
        }
        // The swatch or slider that was used already shows the new value, so the bar can stay as it is.
        _keepStyleBar = true;
        try
        {
            Canvas.ChangeBackdropSmoothly(() =>
            {
                _doc.Backdrop = change(_doc.Backdrop ?? Backdrop.Default);
                _doc.RaiseChanged();
            });
        }
        finally { _keepStyleBar = false; }
    }

    // ---------------- keyboard ----------------

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Canvas.IsEditingText) return;
        if (!Canvas.IsKeyboardFocusWithin && HandleCommandKey(e)) e.Handled = true;
    }

    private void OnCanvasKey(KeyEventArgs e)
    {
        if (HandleCommandKey(e)) e.Handled = true;
    }

    private bool HandleCommandKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);

        if (key == Key.Escape && !ctrl) { Escape(); return true; }
        if (key == Key.F2) { _ = Upload(); return true; }
        if (!ctrl) return false;
        switch (key)
        {
            case Key.C: Copy(closeAfter: Canvas.Marquee == null && (Canvas.Selected == null || Canvas.SelectionIsJustDrawn)); return true;
            case Key.S when shift: SaveAs(); return true;
            case Key.S: Save(closeAfter: false); return true;
            case Key.V when shift: AppController.Instance.LoadClipboard(); return true;
            case Key.V: Paste(); return true;
            case Key.Z when shift: _doc?.Redo(); return true;
            case Key.Z: _doc?.Undo(); return true;
            case Key.Y: _doc?.Redo(); return true;
            case Key.O: AppController.Instance.OpenFileDialog(); return true;
            case Key.P when shift: Print(); return true;
            case Key.P: Pin(); return true;
            case Key.T: _ = RecognizeText(); return true;
            case Key.E: _ = Upload(); return true;
            case Key.W: CloseDocument(); return true;
            case Key.N: AppController.Instance.AddCapture(); return true;
            case Key.OemComma: AppController.Instance.ShowSettings(); return true;
        }
        return false;
    }

    private void Escape()
    {
        switch (Settings.EscapeBehavior)
        {
            case EscapeBehavior.Copy: Copy(closeAfter: true); break;
            case EscapeBehavior.Save: Save(closeAfter: true); break;
            case EscapeBehavior.CopyAndSave: Copy(closeAfter: false); Save(closeAfter: true); break;
            default: CloseDocument(); break;
        }
    }

    // ---------------- commands ----------------

    private BitmapSource Render(bool selectionOnly)
    {
        if (selectionOnly && Canvas.Marquee is RectI r)
            return DocumentRenderer.Export(_doc!, new Rect(r.X, r.Y, r.Width, r.Height), includeBackdrop: false);
        return DocumentRenderer.Export(_doc!);
    }

    public void Copy(bool closeAfter)
    {
        if (_doc == null) return;
        HideTip();
        Canvas.CommitText();
        if (Canvas.Selected != null && !Canvas.SelectionIsJustDrawn && Canvas.Marquee == null && !closeAfter)
        {
            Canvas.CopySelectedAnnotation();
            _annotationClipboardSeq = Features.ClipboardSequence();
            ShowToast("Object copied");
            return;
        }
        var bmp = Render(selectionOnly: Canvas.Marquee != null);
        ImageIO.CopyToClipboard(bmp);
        if (closeAfter)
        {
            AppController.Instance.Notify("Copied to clipboard", NotificationKind.Copy);
            CloseDocument();
        }
        else ShowToast(Canvas.Marquee != null ? "Selection copied" : "Copied");
    }

    private uint _annotationClipboardSeq;
    private (EditorDocument Doc, uint Sequence)? _copiedCapture;

    /// <summary>Notes that the clipboard now holds this capture, so Ctrl+V won't paste it onto itself.</summary>
    public void RememberCopiedCapture(EditorDocument doc) => _copiedCapture = (doc, Features.ClipboardSequence());

    private void Paste()
    {
        if (_doc == null) { AppController.Instance.LoadClipboard(); return; }
        if (EditorCanvas.HasAnnotationOnClipboard && Features.ClipboardSequence() == _annotationClipboardSeq)
        {
            Canvas.PasteAnnotation();
            return;
        }
        if (_copiedCapture is { } c && c.Doc == _doc && c.Sequence == Features.ClipboardSequence())
        {
            ShowToast("Already copied. Press Ctrl+V in the app you want it in.");
            return;
        }
        var img = ImageIO.ClipboardImage();
        if (img != null) Canvas.AddImageOverlay(ImageIO.ToBitmapSource96(img));
        else if (EditorCanvas.HasAnnotationOnClipboard) Canvas.PasteAnnotation();
    }

    public string? Save(bool closeAfter)
    {
        if (_doc == null) return null;
        HideTip();
        Canvas.CommitText();
        // Like Shottr: an opened file is never overwritten silently; Save asks where to save it.
        if (_doc.SourcePath != null) { SaveAs(); return null; }
        var bmp = Render(selectionOnly: false);
        string? path = null;
        try
        {
            path = AppController.Instance.SaveImage(bmp, _doc.Scale, path, _doc.CreatedAt);
            _doc.IsDirty = false;
            if (closeAfter)
            {
                AppController.Instance.Notify($"Saved {System.IO.Path.GetFileName(path)}", NotificationKind.Save, path);
                CloseDocument();
            }
            else ShowToast($"Saved {System.IO.Path.GetFileName(path)}");
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
            ShowToast("Could not save: " + ex.Message);
            return null;
        }
    }

    public void SaveAs()
    {
        if (_doc == null) return;
        Canvas.CommitText();
        var dlg = new SaveFileDialog
        {
            FileName = FileNamer.Expand(Settings.FileNameTemplate, _doc.CreatedAt),
            Filter = "PNG image|*.png|JPEG image|*.jpg|GIF image|*.gif",
            InitialDirectory = Directory.Exists(Settings.SaveFolder) ? Settings.SaveFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var bmp = Render(selectionOnly: false);
            AppController.Instance.SaveImage(bmp, _doc.Scale, dlg.FileName, _doc.CreatedAt, exactPath: true);
            ShowToast($"Saved {System.IO.Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            ShowToast("Could not save: " + ex.Message);
        }
    }

    private void Pin()
    {
        if (_doc == null) return;
        Canvas.CommitText();
        var bmp = Render(selectionOnly: Canvas.Marquee != null);
        AppController.Instance.Pin(bmp, _doc.Scale);
        CloseDocument();
    }

    private async Task RecognizeText()
    {
        if (_doc == null) return;
        PixelBuffer px = Canvas.Marquee is RectI r ? _doc.Base.Pixels.Crop(r.X, r.Y, r.Width, r.Height) : _doc.Base.Pixels;
        ShowToast("Recognizing text…");
        await AppController.Instance.RecognizeAndCopy(px, this);
        HideToast();
    }

    private async Task Upload()
    {
        if (_doc == null) return;
        var bmp = Render(selectionOnly: false);
        ShowToast("Uploading…");
        var url = await AppController.Instance.Upload(bmp, _doc.Scale);
        if (url != null) ShowToast("Link copied: " + url);
        else HideToast();
    }

    private void MakeGif()
    {
        if (_doc == null) return;
        var overlays = _doc.Annotations.OfType<ImageAnnotation>().Where(a => !a.IsCapture).ToList();
        if (overlays.Count == 0) return;
        // Frame 1: without overlays ("before"); frame 2: with overlays at full opacity ("after").
        var saved = overlays.Select(o => o.Opacity).ToList();
        _doc.Annotations.RemoveAll(a => overlays.Contains(a));
        var before = DocumentRenderer.Export(_doc);
        _doc.Annotations.AddRange(overlays);
        foreach (var o in overlays) o.Opacity = 1;
        var after = DocumentRenderer.Export(_doc);
        for (int i = 0; i < overlays.Count; i++) overlays[i].Opacity = saved[i];
        var path = AppController.Instance.SaveGif([before, after]);
        if (path != null) ShowToast($"GIF saved and copied: {System.IO.Path.GetFileName(path)}");
    }

    private void Print()
    {
        if (_doc == null) return;
        var bmp = Render(selectionOnly: false);
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;
        double pw = dlg.PrintableAreaWidth, ph = dlg.PrintableAreaHeight;
        double s = Math.Min(1, Math.Min(pw / bmp.PixelWidth, ph / bmp.PixelHeight));
        var img = new Image { Source = bmp, Width = bmp.PixelWidth * s, Height = bmp.PixelHeight * s, Stretch = Stretch.Uniform };
        img.Measure(new Size(pw, ph));
        img.Arrange(new Rect(new Point((pw - img.Width) / 2, 0), img.DesiredSize));
        dlg.PrintVisual(img, "Loupix screenshot");
    }

    private void ShowResizeDialog()
    {
        if (_doc == null) return;
        var dlg = new ResizeDialog(_doc.Base.Width, _doc.Base.Height) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is (int w, int h) && (w != _doc.Base.Width || h != _doc.Base.Height))
        {
            _doc.Resize(RasterOps.Resize(_doc.Base.Pixels, w, h));
            Canvas.ZoomToFit();
            ShowToast($"Resized to {w} × {h}");
        }
    }

    private void ShowMainMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string gesture, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            return mi;
        }
        var file = new MenuItem { Header = "File" };
        file.Items.Add(Item("Open file…", "Ctrl+O", AppController.Instance.OpenFileDialog));
        file.Items.Add(Item("Load from clipboard", "Ctrl+Shift+V", AppController.Instance.LoadClipboard));
        file.Items.Add(new Separator());
        file.Items.Add(Item("Save", "Ctrl+S", () => Save(false)));
        file.Items.Add(Item("Save as…", "Ctrl+Shift+S", SaveAs));
        file.Items.Add(Item("Copy", "Ctrl+C", () => Copy(false)));
        file.Items.Add(Item("Upload", "Ctrl+E", () => _ = Upload(), Settings.S3.Enabled));
        file.Items.Add(Item("Pin to screen", "Ctrl+P", Pin));
        file.Items.Add(Item("Print…", "Ctrl+Shift+P", Print));
        file.Items.Add(new Separator());
        file.Items.Add(Item("Close", "Ctrl+W", CloseDocument));

        var edit = new MenuItem { Header = "Edit" };
        edit.Items.Add(Item("Undo", "Ctrl+Z", () => _doc?.Undo(), _doc?.CanUndo == true));
        edit.Items.Add(Item("Redo", "Ctrl+Shift+Z", () => _doc?.Redo(), _doc?.CanRedo == true));
        edit.Items.Add(new Separator());
        edit.Items.Add(Item("Paste", "Ctrl+V", Paste));
        edit.Items.Add(Item("Duplicate object", "Ctrl+D", () => { }, Canvas.Selected != null));
        edit.Items.Add(new Separator());
        edit.Items.Add(Item("Reset crop", "", () => { _doc?.ResetCrop(); Canvas.ZoomToFit(); }, _doc?.CanResetCrop == true));
        edit.Items.Add(Item("Rasterize image", "", Rasterize));
        edit.Items.Add(Item("Resize image…", "", ShowResizeDialog));
        edit.Items.Add(Item("Recognize text", "Ctrl+T", () => _ = RecognizeText()));

        var view = new MenuItem { Header = "View" };
        view.Items.Add(Item("Zoom to fit", "Ctrl+1", () => Canvas.ZoomToFit(true)));
        view.Items.Add(Item("Actual size", "Ctrl+0", Canvas.ZoomTo100));
        view.Items.Add(Item("Zoom in", "Ctrl+=", () => Canvas.ZoomStep(1)));
        view.Items.Add(Item("Zoom out", "Ctrl+-", () => Canvas.ZoomStep(-1)));
        var top = new MenuItem { Header = "Keep window on top", IsCheckable = true, IsChecked = Topmost };
        top.Click += (_, _) => { Topmost = top.IsChecked; Settings.EditorAlwaysOnTop = Topmost; SettingsStore.Save(); };
        view.Items.Add(new Separator());
        view.Items.Add(top);

        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(view);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Add capture", "Ctrl+N", AppController.Instance.AddCapture));
        menu.Items.Add(Item("Settings…", "Ctrl+,", AppController.Instance.ShowSettings));
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        UiTheme.StyleMenu(menu, light: false);
        menu.IsOpen = true;
    }

    private void Rasterize()
    {
        if (_doc == null) return;
        Canvas.CommitText();
        var saved = _doc.Backdrop;
        var bmp = DocumentRenderer.Export(_doc, _doc.Canvas, includeBackdrop: false);
        _doc.Checkpoint();
        _doc.Annotations.Clear();
        _doc.ReplaceBase(ImageIO.FromBitmapSource(bmp), checkpoint: false);
        _doc.Canvas = _doc.BaseBounds;
        _doc.Backdrop = saved;
        _doc.RaiseChanged();
        ShowToast("Image rasterized");
    }

    // ---------------- drag & drop ----------------

    private Point? _dragStart;

    private void OnDragHandleMove(object sender, MouseEventArgs e)
    {
        if (_doc == null || _dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        if ((e.GetPosition(this) - start).Length < 4) return;
        _dragStart = null;
        var bmp = Render(selectionOnly: false);
        var path = AppController.Instance.TempFileFor(bmp, _doc.Scale, _doc.CreatedAt);
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
        data.SetImage(bmp);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var file = files.FirstOrDefault(ImageIO.IsImageFile);
        if (file == null) return;
        var px = ImageIO.LoadFile(file);
        if (px == null) return;
        if (_doc == null || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) AppController.Instance.OpenFile(file);
        else Canvas.AddImageOverlay(ImageIO.ToBitmapSource96(px), Canvas.ViewToImage(e.GetPosition(Canvas)));
    }

    // ---------------- toast ----------------

    public void ShowToast(string text, Color? swatch = null)
    {
        ToastText.Text = text;
        ToastSwatch.Visibility = swatch.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (swatch is Color c) ToastSwatch.Background = new SolidColorBrush(c);
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Opacity = 1;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>The one-time card after the very first capture: what the screenshot can do next.</summary>
    /// <param name="copied">Whether the capture really is on the clipboard, which the card then says.</param>
    public void ShowFirstCaptureTip(bool copied)
    {
        var folder = System.IO.Path.GetFileName(Settings.SaveFolder.TrimEnd('\\', '/'));
        CoachTitle.Text = copied ? "Your screenshot is already copied" : "That's your first screenshot";
        CoachRows.Children.Clear();
        if (copied) AddTipRow("Ctrl+V", "Paste it into any app. No need to save it.");
        AddTipRow("Ctrl+C", copied ? "Drew on it? Copy your version and close." : "Copy it and close.");
        AddTipRow("Ctrl+S", folder.Length > 0 ? $"Save it as a file in {folder}." : "Save it as a file.");
        Coach.Visibility = Visibility.Visible;
    }

    private void AddTipRow(string keys, string text)
    {
        var caps = HotkeyBox.KeyCaps(keys);
        caps.Width = 96;
        DockPanel.SetDock(caps, Dock.Left);
        var label = new TextBlock { Text = text, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0xC4, 0xCA)), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        CoachRows.Children.Add(new DockPanel { Margin = new Thickness(0, 0, 0, 10), Children = { caps, label } });
    }

    private void HideTip() => Coach.Visibility = Visibility.Collapsed;

    private void OnTipDismiss(object sender, RoutedEventArgs e) => HideTip();

    private void OnTipSettings(object sender, RoutedEventArgs e)
    {
        HideTip();
        AppController.Instance.ShowSettings();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => { if (Toast.Opacity == 0) Toast.Visibility = Visibility.Collapsed; };
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    // ---------------- caption buttons ----------------

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseDocument();
}
