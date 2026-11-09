#if DEBUG
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Loupix.Capture;
using Loupix.Core.Imaging;
using Loupix.Editor;
using Loupix.Imaging;
using Loupix.Services;
using Loupix.Services.Updates;
using Loupix.UI;

namespace Loupix.Dev;

/// <summary>
/// Debug-only visual self test: renders every UI surface off-screen (never shown, never focused) to PNG files.
/// Run: Loupix.exe --selftest &lt;folder&gt;
/// </summary>
public static class SelfTest
{
    public static async Task RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        SettingsStore.Load(); // the settings window saves on close; keep the real values
        var log = new List<string>();
        async Task Step(string name, Func<Task> body)
        {
            try { await body(); log.Add("ok   " + name); }
            catch (Exception ex) { log.Add("FAIL " + name + ": " + ex); }
        }

        var sample = SampleScreenshot(1440, 900);
        File.WriteAllBytes(Path.Combine(outDir, "00_sample.png"), ImageIO.EncodePng(ImageIO.ToBitmapSource96(sample)));

        await Step("editor_annotations", async () =>
        {
            var doc = new EditorDocument(sample.Clone(), 1.0);
            AddEveryAnnotation(doc);
            var win = await OffscreenEditor(doc, 1360, 900);
            win.Canvas.Select(doc.Annotations.OfType<ArrowAnnotation>().First());
            win.Canvas.Tool = ToolKind.Arrow;
            win.Canvas.Select(doc.Annotations.OfType<ArrowAnnotation>().First());
            await Idle();
            Save(win.Content as FrameworkElement, Path.Combine(outDir, "01_editor_annotations.png"));
            File.WriteAllBytes(Path.Combine(outDir, "02_export.png"), ImageIO.EncodePng(DocumentRenderer.Export(doc)));
            win.Close();
        });

        await Step("editor_marquee", async () =>
        {
            var doc = new EditorDocument(sample.Clone(), 1.0);
            var win = await OffscreenEditor(doc, 1360, 900);
            win.Canvas.SetMarquee(new RectI(420, 180, 520, 300));
            await Idle();
            Save(win.Content as FrameworkElement, Path.Combine(outDir, "03_editor_marquee.png"));
            win.Close();
        });

        await Step("editor_backdrop", async () =>
        {
            var doc = new EditorDocument(sample.Crop(300, 120, 760, 480), 1.0) { Backdrop = Backdrop.Default with { Preset = 0, Padding = 56 } };
            var win = await OffscreenEditor(doc, 1360, 900);
            win.Canvas.Tool = ToolKind.Backdrop;
            await Idle();
            win.Canvas.ZoomToFit();
            await Idle();
            Save(win.Content as FrameworkElement, Path.Combine(outDir, "04_editor_backdrop.png"));
            File.WriteAllBytes(Path.Combine(outDir, "05_backdrop_export.png"), ImageIO.EncodePng(DocumentRenderer.Export(doc)));
            win.Close();
        });

        await Step("editor_zoomed_ruler", async () =>
        {
            var doc = new EditorDocument(sample.Clone(), 1.0);
            var win = await OffscreenEditor(doc, 1100, 760);
            win.Canvas.Tool = ToolKind.Ruler;
            win.Canvas.ZoomOnPoint(new Point(520, 300), 4);
            await Idle();
            Save(win.Content as FrameworkElement, Path.Combine(outDir, "06_editor_zoom400_ruler.png"));
            win.Close();
        });

        await Step("overlay_area", async () =>
        {
            var view = new OverlayView(OverlayMode.Area, new MonitorInfo(IntPtr.Zero, new RectI(0, 0, 1440, 900), new RectI(0, 0, 1440, 860), 1.0, true, "test"), sample, [], null, _ => { });
            view.SimulateForTest((400, 200), (900, 520), new Point(900, 520));
            await RenderElement(view, 1440, 900, Path.Combine(outDir, "07_overlay_area.png"));
        });

        await Step("overlay_window", async () =>
        {
            var windows = new List<WindowInfo> { new(IntPtr.Zero, new RectI(260, 70, 900, 620), "Sample App — Dashboard", "x") };
            var view = new OverlayView(OverlayMode.Window, new MonitorInfo(IntPtr.Zero, new RectI(0, 0, 1440, 900), new RectI(0, 0, 1440, 860), 1.0, true, "test"), sample, windows, null, _ => { });
            view.SimulateHoverForTest(windows[0]);
            await RenderElement(view, 1440, 900, Path.Combine(outDir, "08_overlay_window.png"));
        });

        await Step("settings", async () =>
        {
            using var hk = new HotkeyManager();
            var w = new SettingsWindow(hk) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            w.Show();
            foreach (var (light, mode) in new[] { (false, "dark"), (true, "light") })
            {
                UiTheme.SetPalette(w.Resources, light);
                for (int i = 0; i < w.Sections.Count; i++)
                {
                    w.Sections[i].IsChecked = true;
                    await Idle();
                    Save((FrameworkElement)w.Content, Path.Combine(outDir, $"09_settings_{i}_{w.Sections[i].Content}_{mode}.png"), light ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1C, 0x1C, 0x1E));
                }
            }
            w.Close();
        });

        await Step("update_card", async () =>
        {
            var updates = AppController.Instance.Updates;
            var release = new AvailableUpdate(new Version(1, 3, 0), "https://example.test", "https://example.test/setup.exe", null, "New: hide your previous screenshot when you reopen Loupix.");
            using var hk = new HotkeyManager();
            var settings = new SettingsWindow(hk) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            settings.Show();
            settings.Sections[^1].IsChecked = true;
            updates.SetForTest(UpdateStatus.Available, release, 0);
            var card = new UpdateCard(updates, () => { }) { ShowActivated = false, Left = -32000, Top = -32000 };
            card.Show();
            foreach (var (status, progress, name) in new[] { (UpdateStatus.Available, 0.0, "available"), (UpdateStatus.Downloading, 0.64, "downloading"), (UpdateStatus.WaitingForEditor, 1.0, "waiting") })
            {
                updates.SetForTest(status, release, progress);
                await Idle();
                Save((FrameworkElement)card.Content, Path.Combine(outDir, $"16_update_card_{name}.png"), Color.FromRgb(0x1F, 0x3A, 0x52));
                Save((FrameworkElement)settings.Content, Path.Combine(outDir, $"16_update_about_{name}.png"), Color.FromRgb(0x1C, 0x1C, 0x1E));
            }
            card.Close();
            settings.Close();
            updates.SetForTest(UpdateStatus.Idle, null, 0);
        });

        await Step("dialogs", async () =>
        {
            foreach (var (window, name) in new (Window, string)[] { (new WelcomeWindow { IsPreview = true }, "welcome"), (new WelcomeWindow([CaptureAction.Area, CaptureAction.Ocr]) { IsPreview = true }, "welcome_taken"),(new ToolGuideWindow(), "tool_guide"), (new ResizeDialog(1440, 900), "resize"), (new LinkPermissionWindow("loupix://grab/area?then=copy,save"), "link") })
            {
                window.ShowActivated = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = window.Top = -32000;
                window.Show();
                if (window is ToolGuideWindow) await Task.Delay(1500); // let the animations get going
                foreach (var (light, mode) in new[] { (false, "dark"), (true, "light") })
                {
                    UiTheme.SetPalette(window.Resources, light);
                    await Idle();
                    Save((FrameworkElement)window.Content, Path.Combine(outDir, $"15_{name}_{mode}.png"), light ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1C, 0x1C, 0x1E));
                }
                window.Close();
            }
        });

        await Step("tool_demos", async () =>
        {
            var sheet = new WrapPanel { Width = 1000, Background = Brushes.White };
            foreach (var help in ToolHelp.All.Append(ToolHelp.DragOut))
                foreach (var moment in new[] { .5, .85 })
                {
                    var demo = new ToolDemo(help.Kind) { Margin = new Thickness(4) };
                    demo.ShowFrame(moment);
                    sheet.Children.Add(demo);
                }
            await RenderElement(sheet, 1000, double.NaN, Path.Combine(outDir, "16_tool_demos.png"));

            var card = (StackPanel)ToolHelpViews.HoverCard(ToolHelp.For(ToolKind.Arrow));
            TextElement.SetForeground(card, Brushes.White);
            ((ToolDemo)card.Children[0]).ShowFrame(.5);
            await RenderElement(new Border { Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)), Padding = new Thickness(12), Child = card }, 320, double.NaN, Path.Combine(outDir, "17_tool_card.png"));
        });

        await Step("hover_card_follows_the_mouse", async () =>
        {
            var win = await OffscreenEditor(new EditorDocument(sample.Clone(), 1.0), 1360, 900);
            void Hover(ToolKind kind, System.Windows.RoutedEvent e) => win.ToolButton(kind).RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = e });
            void Expect(ToolKind? kind, string when) { if (win.HoverCards.Shown?.Kind != kind) throw new Exception($"{when}: showing {win.HoverCards.Shown?.Name ?? "nothing"}"); }
            var enter = System.Windows.Input.Mouse.MouseEnterEvent;
            var leave = System.Windows.Input.Mouse.MouseLeaveEvent;
            win.Activate();

            Hover(ToolKind.Arrow, enter);
            await Task.Delay(120);
            Expect(null, "before the first card's delay");
            await Task.Delay(500);
            Expect(ToolKind.Arrow, "after the delay");

            // Straight across the toolbar, the way a real mouse does it, with no pause between buttons.
            foreach (var next in new[] { ToolKind.Rectangle, ToolKind.Oval, ToolKind.Line, ToolKind.Text, ToolKind.Ruler, ToolKind.Backdrop })
            {
                var current = win.HoverCards.Shown!.Kind!.Value;
                Hover(current, leave);
                Hover(next, enter);
                Expect(next, "moving straight across");
                await Task.Delay(40);
            }

            Hover(ToolKind.Backdrop, leave);
            await Task.Delay(300);
            Expect(null, "after leaving the toolbar");
            win.Close();
        });

        await Step("backdrop_slider_and_easing", async () =>
        {
            var doc = new EditorDocument(sample.Crop(300, 120, 760, 480), 1.0) { Backdrop = Backdrop.Default with { Preset = 0, Padding = 20 } };
            var win = await OffscreenEditor(doc, 1360, 900);
            win.Canvas.Tool = ToolKind.Backdrop;
            await Idle();
            win.Canvas.ZoomToFit();
            await Idle();

            var slider = win.StylePanel.Children.OfType<Slider>().First();
            slider.Value = 40;
            slider.Value = 90;
            slider.Value = 160;
            if (!ReferenceEquals(slider, win.StylePanel.Children.OfType<Slider>().First())) throw new Exception("the slider was replaced while it was being dragged");
            if (doc.Backdrop!.Padding != 160) throw new Exception("the document didn't get the new value right away");

            var widths = new List<double>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool saved = false;
            while (win.Canvas.ShownBackdrop is { } shown && sw.ElapsedMilliseconds < 3000)
            {
                widths.Add(shown.Outer.Width);
                if (!saved && widths.Count > 3)
                {
                    saved = true;
                    Save(win.Content as FrameworkElement, Path.Combine(outDir, "18_backdrop_easing_mid.png"));
                }
                await Task.Delay(8);
            }
            log.Add($"info backdrop eased through {widths.Distinct().Count()} sizes in {sw.ElapsedMilliseconds} ms: {string.Join(", ", widths.Distinct().Take(14).Select(w => w.ToString("0")))}");
            if (win.Canvas.ShownBackdrop != null) throw new Exception("never settled");
            if (widths.Distinct().Count() < 5) throw new Exception($"only {widths.Distinct().Count()} steps");
            for (int i = 1; i < widths.Count; i++) if (widths[i] < widths[i - 1]) throw new Exception("went backwards");
            await Idle();
            Save(win.Content as FrameworkElement, Path.Combine(outDir, "19_backdrop_easing_end.png"));

            win.Canvas.ChangeBackdropSmoothly(() => { doc.Backdrop = null; doc.RaiseChanged(); });
            sw.Restart();
            while (win.Canvas.ShownBackdrop != null && sw.ElapsedMilliseconds < 3000) await Task.Delay(8);
            if (win.Canvas.ShownBackdrop != null) throw new Exception("removing never settled");
            win.Close();
        });

        await Step("microsoft_store_package", async () =>
        {
            if (!SystemIntegration.IsPackaged) { log.Add("info not running from a package"); return; }
            SystemIntegration.LaunchAtStartup = true;
            if (!SystemIntegration.LaunchAtStartup) throw new Exception("start at sign-in didn't turn on");
            SystemIntegration.LaunchAtStartup = false;
            if (SystemIntegration.LaunchAtStartup) throw new Exception("start at sign-in didn't turn off");

            SettingsStore.Save();
            var settings = Path.Combine(SystemIntegration.SettingsFolderOnDisk, "settings.json");
            if (!File.Exists(settings)) throw new Exception("settings aren't where Explorer would be sent: " + settings);
            log.Add("info settings on disk: " + settings);

            // Written fresh, so the script running this can see where Windows really put them.
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loupix-PackageCheck", "new.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(appData)!);
            File.WriteAllText(appData, "x");
            log.Add("info new appdata file: " + appData);
            var temp = Path.Combine(Path.GetTempPath(), "Loupix-PackageCheck", "new.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            File.WriteAllText(temp, "x");
            log.Add("info new temp file: " + temp);
            await Task.CompletedTask;
        });

        await Step("effects_on_text", async () =>
        {
            var doc = new EditorDocument(sample.Clone(), 1.0);
            // Left to right over the invoice table and the banner: pixelate, blur, erase, text-only erase, text-only pixelate.
            doc.Annotations.Add(new EffectAnnotation { P1 = new Point(270, 560), P2 = new Point(420, 640), Mode = EffectMode.Pixelate });
            doc.Annotations.Add(new EffectAnnotation { P1 = new Point(430, 560), P2 = new Point(600, 640), Mode = EffectMode.Blur });
            doc.Annotations.Add(new EffectAnnotation { P1 = new Point(610, 560), P2 = new Point(760, 640), Mode = EffectMode.Erase });
            doc.Annotations.Add(new EffectAnnotation { P1 = new Point(250, 80), P2 = new Point(800, 150), Mode = EffectMode.Erase, TextOnly = true });
            doc.Annotations.Add(new EffectAnnotation { P1 = new Point(900, 560), P2 = new Point(1060, 820), Mode = EffectMode.Pixelate, TextOnly = true });
            doc.Annotations.Add(new EffectAnnotation { P1 = new Point(1130, 10), P2 = new Point(1440, 50), Mode = EffectMode.Erase, TextOnly = true });
            var bmp = DocumentRenderer.Export(doc, new Rect(240, 0, 1200, 860));
            File.WriteAllBytes(Path.Combine(outDir, "13_effects.png"), ImageIO.EncodePng(bmp));
            await Task.CompletedTask;
        });

        await Step("color_inspector", async () =>
        {
            var popup = new ColorInspector(new Border(), Color.FromRgb(0x25, 0x63, 0xEB), Colors.White, _ => { });
            var child = (FrameworkElement)popup.Child;
            popup.Child = null;
            await RenderElement(child, 360, double.NaN, Path.Combine(outDir, "11_color_inspector.png"));
        });

        await Step("empty_editor", async () =>
        {
            var win = new EditorWindow { ShowActivated = false, Width = 600, Height = 432, Left = -32000, Top = -32000 };
            win.Show();
            win.Canvas.EmptyHint = "Nothing to edit yet\nCtrl+Shift+D  area capture      Ctrl+Shift+A  fullscreen\nCtrl+V pastes an image · drop an image file here · Ctrl+O opens one";
            win.Canvas.Document = null;
            await Idle();
            Save(win.Content as FrameworkElement, Path.Combine(outDir, "12_empty_editor.png"));
            win.Close();
        });

        await Step("first_capture_tip", async () =>
        {
            foreach (var (w, h, name) in new[] { (1000.0, 640.0, "normal"), (560.0, 340.0, "smallest") })
            {
                var win = await OffscreenEditor(new EditorDocument(sample.Clone(), 1.0), w, h);
                win.ShowFirstCaptureTip(copied: true);
                await Idle();
                Save(win.Content as FrameworkElement, Path.Combine(outDir, $"12_first_capture_tip_{name}.png"));
                win.Close();
            }
        });

        await Step("toolbar_icons", async () =>
        {
            var panel = new WrapPanel { Background = (Brush)Application.Current.FindResource("BarBrush"), Width = 760 };
            foreach (var t in Tools.All)
            {
                var sp = new StackPanel { Margin = new Thickness(10), Width = 70 };
                var icon = Tools.IconFor(t, Brushes.White); icon.LayoutTransform = new ScaleTransform(2, 2); sp.Children.Add(icon);
                sp.Children.Add(new TextBlock { Text = t.Name, Foreground = Brushes.White, FontSize = 10, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
                panel.Children.Add(sp);
            }
            await RenderElement(panel, 760, double.NaN, Path.Combine(outDir, "10_tool_icons.png"));
        });

        await Step("resize_dialog", async () =>
        {
            var w = new ResizeDialog(1440, 900) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            w.Show();
            await Idle();
            Save(w.Content as FrameworkElement, Path.Combine(outDir, "15_resize_dialog.png"), IconFactory.TaskbarIsLight() ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20));
            w.Close();
        });

        await Step("tray_glyph", async () =>
        {
            foreach (int size in new[] { 16, 20, 24, 32 })
                File.WriteAllBytes(Path.Combine(outDir, $"14_tray_{size}.png"), ImageIO.EncodePng(IconFactory.TrayGlyph(size)));
            await Task.CompletedTask;
        });

        File.WriteAllLines(Path.Combine(outDir, "selftest.log"), log);
    }

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static async Task<EditorWindow> OffscreenEditor(EditorDocument doc, double w, double h)
    {
        var win = new EditorWindow { ShowActivated = false, Width = w, Height = h, Left = -32000, Top = -32000 };
        win.Show();
        win.LoadForTest(doc);
        await Idle();
        win.Canvas.ZoomToFit();
        await Idle();
        return win;
    }

    private static void Save(FrameworkElement? element, string path, Color? background = null)
    {
        if (element == null) return;
        // The element is drawn inside its margin, so the bitmap has to include it.
        var m = element.Margin;
        int w = (int)Math.Ceiling(element.ActualWidth + m.Left + m.Right), h = (int)Math.Ceiling(element.ActualHeight + m.Top + m.Bottom);
        var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        if (background is Color bg)
        {
            // The window paints this colour, but only its content is rendered here.
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, w, h));
            rtb.Render(dv);
        }
        rtb.Render(element);
        File.WriteAllBytes(path, ImageIO.EncodePng(rtb));
    }

    private static async Task RenderElement(FrameworkElement e, double w, double h, string path)
    {
        var host = new Window { ShowActivated = false, WindowStyle = WindowStyle.None, Left = -32000, Top = -32000, SizeToContent = double.IsNaN(h) ? SizeToContent.Height : SizeToContent.Manual, Width = w, Height = double.IsNaN(h) ? 100 : h, Content = e, ShowInTaskbar = false };
        host.Show();
        await Idle();
        Save(e, path);
        host.Close();
    }

    private static void AddEveryAnnotation(EditorDocument doc)
    {
        var red = Color.FromRgb(0xFF, 0x3B, 0x30);
        doc.Annotations.Add(new ArrowAnnotation { Start = new Point(150, 640), End = new Point(380, 470), Color = red });
        doc.Annotations.Add(new ArrowAnnotation { Start = new Point(150, 720), End = new Point(420, 700), Bend = -40, Color = Color.FromRgb(0x0A, 0x84, 0xFF), Variant = ArrowAnnotation.Classic });
        doc.Annotations.Add(new ArrowAnnotation { Start = new Point(160, 790), End = new Point(400, 800), Variant = ArrowAnnotation.Double, Color = Color.FromRgb(0x30, 0xD1, 0x58), Size = 2 });
        doc.Annotations.Add(new RectangleAnnotation { P1 = new Point(450, 175), P2 = new Point(820, 320), Color = red });
        doc.Annotations.Add(new RectangleAnnotation { P1 = new Point(860, 175), P2 = new Point(1080, 320), Color = Color.FromRgb(0xBF, 0x5A, 0xF2), HandDrawn = true });
        doc.Annotations.Add(new EllipseAnnotation { P1 = new Point(1120, 160), P2 = new Point(1380, 330), Color = Color.FromRgb(0xFF, 0x95, 0x00), Fill = FillMode.Translucent });
        doc.Annotations.Add(new TextAnnotation { Position = new Point(560, 380), Tip = new Point(500, 470), Text = "Label with a pointer", Color = red });
        doc.Annotations.Add(new TextAnnotation { Position = new Point(900, 380), Text = "Plain text", Variant = TextAnnotation.Plain, Color = Color.FromRgb(0x0A, 0x84, 0xFF), Size = 4 });
        doc.Annotations.Add(new TextAnnotation { Position = new Point(900, 440), Text = "Outlined", Variant = TextAnnotation.Outlined, Color = Color.FromRgb(0xFF, 0xD6, 0x0A), Size = 4 });
        doc.Annotations.Add(new CounterAnnotation { Center = new Point(470, 560), Number = 1, Color = red });
        doc.Annotations.Add(new CounterAnnotation { Center = new Point(520, 560), Number = 2, Color = red });
        doc.Annotations.Add(new CounterAnnotation { Center = new Point(570, 560), Number = 12, Color = Color.FromRgb(0x0A, 0x84, 0xFF) });
        var free = new FreehandAnnotation { Color = Color.FromRgb(0x30, 0xD1, 0x58), Size = 3 };
        for (int i = 0; i <= 40; i++) free.AddPoint(new Point(640 + i * 6, 560 + Math.Sin(i / 4.0) * 22));
        doc.Annotations.Add(free);
        doc.Annotations.Add(new HighlighterAnnotation { Start = new Point(320, 236), End = new Point(410, 236), Color = Color.FromRgb(0xFF, 0xD6, 0x0A) });
        doc.Annotations.Add(new EffectAnnotation { P1 = new Point(1110, 600), P2 = new Point(1390, 660), Mode = EffectMode.Pixelate });
        doc.Annotations.Add(new EffectAnnotation { P1 = new Point(1110, 680), P2 = new Point(1390, 740), Mode = EffectMode.Blur });
        doc.Annotations.Add(new EffectAnnotation { P1 = new Point(1110, 760), P2 = new Point(1390, 820), Mode = EffectMode.Erase });
        doc.Annotations.Add(new MagnifierAnnotation { Source = new Point(990, 610), SourceRadius = 22, Target = new Point(960, 760), Variant = 1 });
        doc.Annotations.Add(new MeasureAnnotation { Start = new Point(450, 340), End = new Point(820, 340), Label = "370", Color = Color.FromRgb(0xFF, 0x2D, 0x78) });
        doc.Annotations.Add(new SpotlightAnnotation { P1 = new Point(30, 20), P2 = new Point(1420, 880), Level = 1 });
    }

    /// <summary>A synthetic app screenshot: title bar, sidebar, cards with text, buttons, a gradient banner and a photo-ish area.</summary>
    public static PixelBuffer SampleScreenshot(int w, int h)
    {
        var dv = new DrawingVisual();
        var face = new Typeface("Segoe UI");
        FormattedText T(string s, double size, Brush b, FontWeight? weight = null) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, b, 1);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xF8)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xE3, 0xE5, 0xE8)), 1), new Rect(0, 0, w, 56));
            dc.DrawText(T("Acme Analytics", 18, Brushes.Black, FontWeights.SemiBold), new Point(24, 16));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), null, new Rect(w - 160, 12, 136, 32), 8, 8);
            dc.DrawText(T("New report", 13, Brushes.White, FontWeights.SemiBold), new Point(w - 128, 19));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)), null, new Rect(0, 56, 220, h - 56));
            string[] nav = ["Overview", "Reports", "Customers", "Billing", "Settings"];
            for (int i = 0; i < nav.Length; i++)
            {
                if (i == 1) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)), null, new Rect(12, 80 + i * 44, 196, 36), 6, 6);
                dc.DrawText(T(nav[i], 14, new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB))), new Point(28, 88 + i * 44));
            }
            var banner = new LinearGradientBrush(Color.FromRgb(0x63, 0x66, 0xF1), Color.FromRgb(0xEC, 0x48, 0x99), 0);
            dc.DrawRoundedRectangle(banner, null, new Rect(250, 80, w - 280, 70), 12, 12);
            dc.DrawText(T("Quarterly revenue is up 24% — see what changed", 20, Brushes.White, FontWeights.SemiBold), new Point(274, 102));
            for (int c = 0; c < 3; c++)
            {
                var r = new Rect(250 + c * 390, 170, 370, 150);
                dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xE3, 0xE5, 0xE8)), 1), r, 10, 10);
                dc.DrawText(T(new[] { "Active users", "Conversion", "Churn" }[c], 13, Brushes.Gray), new Point(r.X + 20, r.Y + 18));
                dc.DrawText(T(new[] { "12,480", "4.7%", "1.2%" }[c], 34, Brushes.Black, FontWeights.SemiBold), new Point(r.X + 20, r.Y + 44));
                dc.DrawText(T("Compared with the previous 30 days", 12, Brushes.Gray), new Point(r.X + 20, r.Y + 104));
            }
            var body = new Rect(250, 340, 820, 520);
            dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xE3, 0xE5, 0xE8)), 1), body, 10, 10);
            string text = "The new onboarding flow shipped on Monday. Since then, sign-ups that finish the setup wizard rose from 61% to 78%, and the median time to first report fell to under four minutes. Support tickets about workspace invitations dropped by half. Next we will test a shorter pricing page and a guided tour for the reports builder.";
            var para = T(text, 15, Brushes.Black);
            para.MaxTextWidth = 760;
            para.LineHeight = 24;
            dc.DrawText(para, new Point(280, 370));
            for (int i = 0; i < 6; i++)
            {
                double y = 560 + i * 44;
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xEE, 0xEF, 0xF1)), 1), new Point(270, y), new Point(1050, y));
                dc.DrawText(T($"Invoice #{1042 + i}", 14, Brushes.Black), new Point(280, y + 12));
                dc.DrawText(T(new[] { "Paid", "Pending", "Paid", "Overdue", "Paid", "Paid" }[i], 13, new SolidColorBrush(i == 3 ? Color.FromRgb(0xDC, 0x26, 0x26) : Color.FromRgb(0x05, 0x96, 0x69))), new Point(620, y + 13));
                dc.DrawText(T($"${(i + 3) * 137}.00", 14, Brushes.Black), new Point(960, y + 12));
            }
            // Photo-like area.
            var photo = new RadialGradientBrush(Color.FromRgb(0xFD, 0xBA, 0x74), Color.FromRgb(0x7C, 0x2D, 0x12)) { Center = new Point(0.3, 0.3), GradientOrigin = new Point(0.3, 0.3), RadiusX = 0.9, RadiusY = 0.9 };
            dc.DrawRoundedRectangle(photo, null, new Rect(1090, 340, 320, 520), 10, 10);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), null, new Point(1250, 520), 60, 60);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return ImageIO.FromBitmapSource(rtb);
    }
}
#endif
