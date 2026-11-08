#pragma warning disable WPF0001 // Fluent ThemeMode is marked experimental.
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Loupix.Imaging;
using Loupix.Services;
using Microsoft.Win32;

namespace Loupix.UI;

/// <summary>Records a key chord when focused. Backspace/Delete clears, Esc cancels.</summary>
public sealed class HotkeyBox : TextBox
{
    private readonly HotkeyManager _manager;
    private string _before = "";
    public event Action<string>? Changed;

    public HotkeyBox(HotkeyManager manager, string value)
    {
        _manager = manager;
        Text = value;
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Width = 180;
        Cursor = Cursors.Hand;
        ToolTip = "Click, then press the new shortcut. Backspace clears it.";
        GotKeyboardFocus += (_, _) => { _before = Text; Text = "Press a shortcut…"; _manager.Suspend(); };
        LostKeyboardFocus += (_, _) =>
        {
            if (Text == "Press a shortcut…") Text = _before;
            _manager.Apply(SettingsStore.Current.Hotkeys);
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var mods = Keyboard.Modifiers;
        if (key == Key.Escape && mods == ModifierKeys.None) { Text = _before; MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); return; }
        if (key is Key.Back or Key.Delete && mods == ModifierKeys.None) { Commit(""); return; }
        bool standalone = key is Key.Snapshot or >= Key.F1 and <= Key.F24;
        if (mods == ModifierKeys.None && !standalone) return; // needs a modifier
        Commit(new Hotkey(mods, key).ToString());
    }

    private void Commit(string value)
    {
        Text = value;
        _before = value;
        Changed?.Invoke(value);
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }
}

/// <summary>Preferences: General, Hotkeys, Saving, Capture, Editor, Upload, About.</summary>
public sealed class SettingsWindow : Window
{
    private readonly HotkeyManager _hotkeys;
    private static AppSettings S => SettingsStore.Current;

    public SettingsWindow(HotkeyManager hotkeys)
    {
        _hotkeys = hotkeys;
        Title = "Loupix Settings";
        Width = 680;
        Height = 640;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = IconFactory.Logo(48);
        ThemeMode = ThemeMode.System;

        var tabs = new TabControl { Margin = new Thickness(12) };
        tabs.Items.Add(Tab("General", General()));
        tabs.Items.Add(Tab("Hotkeys", Hotkeys()));
        tabs.Items.Add(Tab("Saving", Saving()));
        tabs.Items.Add(Tab("Capture", CaptureTab()));
        tabs.Items.Add(Tab("Editor", EditorTab()));
        tabs.Items.Add(Tab("Upload", Upload()));
        tabs.Items.Add(Tab("About", About()));
        Content = tabs;
        Closed += (_, _) => SettingsStore.Save();
    }

    private static TabItem Tab(string header, UIElement content) => new()
    {
        Header = header,
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4, 8, 12, 8) },
    };

    // ---------- small builders ----------

    private static StackPanel Page() => new() { Margin = new Thickness(8, 4, 8, 16) };

    private static TextBlock Header(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };

    private static TextBlock Note(string text) => new() { Text = text, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6), FontSize = 12 };

    private static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
        c.Checked += (_, _) => { set(true); SettingsStore.Save(); };
        c.Unchecked += (_, _) => { set(false); SettingsStore.Save(); };
        return c;
    }

    private static FrameworkElement Row(string label, FrameworkElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        g.Children.Add(t);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = control is TextBox or PasswordBox ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        g.Children.Add(control);
        return g;
    }

    private static ComboBox Choice<T>(T current, IEnumerable<(T Value, string Name)> options, Action<T> set) where T : notnull
    {
        var cb = new ComboBox { MinWidth = 220 };
        foreach (var (v, n) in options) cb.Items.Add(new ComboBoxItem { Content = n, Tag = v, IsSelected = EqualityComparer<T>.Default.Equals(v, current) });
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem { Tag: T v }) { set(v); SettingsStore.Save(); } };
        return cb;
    }

    private static TextBox Text(string value, Action<string> set, double width = double.NaN)
    {
        var t = new TextBox { Text = value, Width = width };
        t.LostFocus += (_, _) => { set(t.Text.Trim()); SettingsStore.Save(); };
        return t;
    }

    private static TextBox Number(int value, int min, int max, Action<int> set)
    {
        var t = new TextBox { Text = value.ToString(), Width = 100 };
        t.LostFocus += (_, _) =>
        {
            if (int.TryParse(t.Text, out var v)) { v = Math.Clamp(v, min, max); set(v); t.Text = v.ToString(); SettingsStore.Save(); }
            else t.Text = value.ToString();
        };
        return t;
    }

    // ---------- tabs ----------

    private UIElement General()
    {
        var p = Page();
        p.Children.Add(Header("Startup"));
        p.Children.Add(Check("Launch Loupix when I sign in to Windows", SystemIntegration.LaunchAtStartup, v => { SystemIntegration.LaunchAtStartup = v; S.LaunchAtStartup = v; }));
        p.Children.Add(Check("Show the icon in the notification area", S.ShowTrayIcon, v => S.ShowTrayIcon = v));
        p.Children.Add(Note("With the icon hidden, use your hotkeys, or run Loupix again to open these settings."));
        p.Children.Add(Check("Show the editor when I start Loupix myself (not at sign-in)", S.ShowSplash, v => S.ShowSplash = v));

        p.Children.Add(Header("After capture"));
        var after = new (AfterCapture, string)[] { (AfterCapture.Editor, "Open the editor"), (AfterCapture.Thumbnail, "Show a preview thumbnail"), (AfterCapture.Nothing, "Do nothing else") };
        p.Children.Add(Row("After a fullscreen capture", Choice(S.AfterFullscreen, after, v => S.AfterFullscreen = v)));
        p.Children.Add(Row("After an area or window capture", Choice(S.AfterArea, after, v => S.AfterArea = v)));
        p.Children.Add(Check("Also copy every capture to the clipboard", S.AutoCopy, v => S.AutoCopy = v));
        p.Children.Add(Check("Also save every capture to the screenshots folder", S.AutoSave, v => S.AutoSave = v));
        p.Children.Add(Check("Keep the thumbnail until I use or close it", S.ThumbnailPersist, v => S.ThumbnailPersist = v));
        p.Children.Add(Row("Thumbnail position", Choice(S.ThumbnailCorner,
            [(ThumbnailCorner.BottomRight, "Bottom right"), (ThumbnailCorner.BottomLeft, "Bottom left"), (ThumbnailCorner.TopRight, "Top right"), (ThumbnailCorner.TopLeft, "Top left")], v => S.ThumbnailCorner = v)));

        p.Children.Add(Header("Editor"));
        p.Children.Add(Row("Esc in the editor", Choice(S.EscapeBehavior,
            [(EscapeBehavior.Copy, "Copy the image and close"), (EscapeBehavior.Close, "Just close"), (EscapeBehavior.Save, "Save the image and close"), (EscapeBehavior.CopyAndSave, "Copy, save and close")], v => S.EscapeBehavior = v)));
        p.Children.Add(Row("Confirmations", Choice(S.Notifications,
            [(NotificationStyle.Custom, "Show small confirmations"), (NotificationStyle.None, "Don't show confirmations")], v => S.Notifications = v)));
        p.Children.Add(Row("Delayed capture (seconds)", Number(S.DelaySeconds, 1, 60, v => S.DelaySeconds = v)));

        p.Children.Add(Header("Automation"));
        p.Children.Add(Check("Allow loupix:// links from launchers, scripts and web pages", S.AllowLinks == true, v => S.AllowLinks = v));
        p.Children.Add(Note("Links can take screenshots and copy, save or pin them, so they are off until you allow them. See About for the list of links."));
        p.Children.Add(Check("Include the mouse cursor in captures", S.CaptureCursor, v => S.CaptureCursor = v));
        return p;
    }

    private UIElement Hotkeys()
    {
        var p = Page();
        p.Children.Add(Header("Global hotkeys"));
        p.Children.Add(Note("Click a box and press the shortcut. Backspace clears it. Shortcuts work from any app."));
        var names = new (CaptureAction, string)[]
        {
            (CaptureAction.Fullscreen, "Fullscreen capture"),
            (CaptureAction.Area, "Area capture"),
            (CaptureAction.Window, "Window capture"),
            (CaptureAction.Scrolling, "Scrolling capture"),
            (CaptureAction.ScrollingReverse, "Scrolling capture (upwards)"),
            (CaptureAction.RepeatArea, "Repeat area capture"),
            (CaptureAction.Delayed, "Delayed capture"),
            (CaptureAction.Ocr, "Recognize text (OCR)"),
            (CaptureAction.AddCapture, "Add capture to the editor"),
            (CaptureAction.ShowEditor, "Reopen the editor"),
            (CaptureAction.LoadClipboard, "Open image from the clipboard"),
        };
        var boxes = new List<(CaptureAction, HotkeyBox)>();
        foreach (var (action, name) in names)
        {
            var box = new HotkeyBox(_hotkeys, S.Hotkeys.GetValueOrDefault(action, ""));
            var a = action;
            box.Changed += v =>
            {
                // A chord can only belong to one action.
                foreach (var (other, otherBox) in boxes)
                    if (other != a && S.Hotkeys.GetValueOrDefault(other) == v && v != "") { S.Hotkeys[other] = ""; otherBox.Text = ""; }
                S.Hotkeys[a] = v;
                SettingsStore.Save();
            };
            boxes.Add((action, box));
            p.Children.Add(Row(name, box));
        }
        var reset = new Button { Content = "Restore defaults", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            S.Hotkeys = AppSettings.DefaultHotkeys();
            SettingsStore.Save();
            foreach (var (a, b) in boxes) b.Text = S.Hotkeys.GetValueOrDefault(a, "");
        };
        p.Children.Add(reset);
        if (_hotkeys.Conflicts.Count > 0)
            p.Children.Add(Note("Taken by another app (not active): " + string.Join(", ", _hotkeys.Conflicts.Select(c => c.Hotkey.ToString()))));
        p.Children.Add(Note("Tip: to use Print Screen, first turn off \"Use the Print screen key to open screen capture\" in Windows Settings → Accessibility → Keyboard."));
        return p;
    }

    private UIElement Saving()
    {
        var p = Page();
        p.Children.Add(Header("Screenshots folder"));
        var folder = new TextBox { Text = S.SaveFolder, IsReadOnly = true, MinWidth = 300 };
        var browse = new Button { Content = "Choose…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { InitialDirectory = Directory.Exists(S.SaveFolder) ? S.SaveFolder : null, Title = "Where should screenshots be saved?" };
            if (dlg.ShowDialog(this) == true) { S.SaveFolder = dlg.FolderName; folder.Text = dlg.FolderName; SettingsStore.Save(); }
        };
        var open = new Button { Content = "Open", Margin = new Thickness(8, 0, 0, 0) };
        open.Click += (_, _) => { Directory.CreateDirectory(S.SaveFolder); Process.Start(new ProcessStartInfo(S.SaveFolder) { UseShellExecute = true }); };
        p.Children.Add(new DockPanel { Children = { open, browse, folder }, LastChildFill = true });
        DockPanel.SetDock(open, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);

        p.Children.Add(Header("File name"));
        var preview = new TextBlock { Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) };
        void UpdatePreview(string t) => preview.Text = "Example: " + FileNamer.Expand(t, DateTime.Now) + ".png";
        var tmpl = new TextBox { Text = S.FileNameTemplate };
        tmpl.TextChanged += (_, _) => UpdatePreview(tmpl.Text);
        tmpl.LostFocus += (_, _) => { S.FileNameTemplate = string.IsNullOrWhiteSpace(tmpl.Text) ? "SCR-{yyyyMMdd}-{timecode}" : tmpl.Text.Trim(); SettingsStore.Save(); };
        UpdatePreview(S.FileNameTemplate);
        p.Children.Add(tmpl);
        p.Children.Add(preview);
        p.Children.Add(Note("{timecode} is a short code that sorts by time of day. Any other {…} is a date format, e.g. {yyyy-MM-dd HH.mm.ss}."));

        p.Children.Add(Header("Format"));
        p.Children.Add(Row("Image format", Choice(S.SaveFormat, [(SaveFormat.Png, "PNG"), (SaveFormat.Jpeg, "JPEG"), (SaveFormat.Auto, "Automatic (PNG for UI, JPEG for photos)")], v => S.SaveFormat = v)));
        p.Children.Add(Row("JPEG quality (10–100)", Number(S.JpegQuality, 10, 100, v => S.JpegQuality = v)));
        p.Children.Add(Check("Save high-DPI captures at 1× (logical size)", S.DownscaleHiDpi, v => S.DownscaleHiDpi = v));
        return p;
    }

    private UIElement CaptureTab()
    {
        var p = Page();
        p.Children.Add(Header("Window capture"));
        p.Children.Add(Row("Window style", Choice(S.WindowCaptureStyle,
            [(WindowCaptureStyle.Shadow, "Shadow on a transparent background"), (WindowCaptureStyle.NoShadow, "Window only, no shadow"), (WindowCaptureStyle.SolidBackground, "Shadow on a solid colour"), (WindowCaptureStyle.Wallpaper, "Shadow over the wallpaper")], v => S.WindowCaptureStyle = v)));
        p.Children.Add(Row("Solid colour (hex)", Text(S.WindowBackgroundColor, v => { if (v.Length > 0) S.WindowBackgroundColor = v.StartsWith('#') ? v : "#" + v; }, 120)));

        p.Children.Add(Header("Scrolling capture"));
        p.Children.Add(Row("Maximum height (px)", Number(S.ScrollMaxHeight, 1000, 200000, v => S.ScrollMaxHeight = v)));
        p.Children.Add(Check("Reverse the scroll direction (for apps that invert scrolling)", S.ScrollInvertDirection, v => S.ScrollInvertDirection = v));
        p.Children.Add(Note("Select the area to capture; Loupix scrolls the window under it and stitches the result. Press Esc or Stop to finish early."));
        return p;
    }

    private UIElement EditorTab()
    {
        var p = Page();
        p.Children.Add(Header("Editor"));
        p.Children.Add(Check("Open captures at 100% zoom when they fit (otherwise fit to window)", S.DefaultZoom100, v => S.DefaultZoom100 = v));
        p.Children.Add(Check("Keep the editor window on top", S.EditorAlwaysOnTop, v => S.EditorAlwaysOnTop = v));
        p.Children.Add(Check("Measure in physical pixels (instead of scaled/logical pixels)", S.MeasureInPhysicalPixels, v => S.MeasureInPhysicalPixels = v));
        p.Children.Add(Row("Color format", Choice(S.ColorFormat, Features.ColorFormats().Select(f => (f.Key, f.Name)), v => S.ColorFormat = v)));

        p.Children.Add(Header("Text recognition"));
        var langs = new List<(string, string)> { ("", "Windows display language") };
        try { langs.AddRange(Features.OcrLanguages().Select(l => (l, l))); } catch { }
        p.Children.Add(Row("Language", Choice(S.OcrLanguage, langs, v => S.OcrLanguage = v)));
        p.Children.Add(Check("Remove line breaks from recognized text by default", S.OcrRemoveLineBreaks, v => S.OcrRemoveLineBreaks = v));
        p.Children.Add(Note("More languages: Windows Settings → Time & language → Language & region → add a language with \"Optical character recognition\"."));
        return p;
    }

    private UIElement Upload()
    {
        var p = Page();
        var s3 = S.S3;
        p.Children.Add(Header("S3-compatible storage"));
        p.Children.Add(Note("Ctrl+E (or F2) in the editor uploads the image and copies its link. Works with Amazon S3, Cloudflare R2, Backblaze B2, MinIO, DigitalOcean Spaces and other S3-compatible services. Uploaded images are only as private as your bucket's settings."));
        p.Children.Add(Check("Enable upload", s3.Enabled, v => s3.Enabled = v));
        p.Children.Add(Row("Endpoint URL", Text(s3.Endpoint, v => s3.Endpoint = v)));
        p.Children.Add(Row("Region", Text(s3.Region, v => s3.Region = v, 160)));
        p.Children.Add(Row("Bucket", Text(s3.Bucket, v => s3.Bucket = v)));
        p.Children.Add(Row("Access key ID", Text(s3.AccessKeyId, v => s3.AccessKeyId = v)));
        var secret = new PasswordBox { Password = SecretStore.Unprotect(s3.SecretAccessKeyProtected) };
        secret.LostFocus += (_, _) => { s3.SecretAccessKeyProtected = SecretStore.Protect(secret.Password); SettingsStore.Save(); };
        p.Children.Add(Row("Secret access key", secret));
        p.Children.Add(Check("Path-style URLs (endpoint/bucket/key)", s3.PathStyle, v => s3.PathStyle = v));
        p.Children.Add(Row("Public URL prefix (optional)", Text(s3.PublicUrlBase, v => s3.PublicUrlBase = v)));
        p.Children.Add(Row("Key prefix (folder)", Text(s3.KeyPrefix, v => s3.KeyPrefix = v)));
        p.Children.Add(Row("ACL (e.g. public-read, optional)", Text(s3.Acl, v => s3.Acl = v, 160)));
        p.Children.Add(Note("The secret key is encrypted with your Windows account (DPAPI) before it is stored."));
        var test = new Button { Content = "Test upload", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        test.Click += async (_, _) =>
        {
            s3.SecretAccessKeyProtected = SecretStore.Protect(secret.Password);
            SettingsStore.Save();
            result.Text = "Uploading a 1×1 test image…";
            try
            {
                var bmp = IconFactory.Logo(64);
                var url = await Features.UploadAsync(new System.Net.Http.HttpClient(), ImageIO.EncodePng(bmp), "loupix-test.png", "image/png");
                result.Text = "It works: " + url;
            }
            catch (Exception ex) { result.Text = "Failed: " + ex.Message; }
        };
        p.Children.Add(test);
        p.Children.Add(result);
        return p;
    }

    private UIElement About()
    {
        var p = Page();
        p.Children.Add(new Image { Source = IconFactory.Logo(128), Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 8) });
        p.Children.Add(new TextBlock { Text = "Loupix", FontSize = 22, FontWeight = FontWeights.SemiBold });
        p.Children.Add(Note($"Version {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)} · a fast screenshot, annotation and measurement tool for Windows."));
        p.Children.Add(Header("Links"));
        p.Children.Add(Note("Automate captures with loupix:// links, e.g. loupix://grab/area?then=copy,save — the same verbs as Shottr: grab/fullscreen, grab/area, grab/repeat, grab/window, grab/scrolling, grab/scrolling/reverse, grab/delayed=5, grab/append, ocr, load/clipboard, load/file, settings, show. then= accepts copy, save, edit, pin, thumbnail."));
        var folder = new Button { Content = "Open settings folder", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        folder.Click += (_, _) => { Directory.CreateDirectory(SettingsStore.Directory); Process.Start(new ProcessStartInfo(SettingsStore.Directory) { UseShellExecute = true }); };
        p.Children.Add(folder);
        return p;
    }
}

/// <summary>First-run window: what Loupix is and its hotkeys.</summary>
public sealed class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        Title = "Welcome to Loupix";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = IconFactory.Logo(48);
        ThemeMode = ThemeMode.System;
        var s = SettingsStore.Current;
        var p = new StackPanel { Margin = new Thickness(28) };
        p.Children.Add(new Image { Source = IconFactory.Logo(128), Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left });
        p.Children.Add(new TextBlock { Text = "Loupix is ready", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
        p.Children.Add(new TextBlock { Text = "It lives in the notification area. Capture with these hotkeys:", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        foreach (var (a, n) in new[] { (CaptureAction.Area, "Area"), (CaptureAction.Fullscreen, "Fullscreen"), (CaptureAction.Window, "Window"), (CaptureAction.Scrolling, "Scrolling"), (CaptureAction.Ocr, "Recognize text") })
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var l = new TextBlock { Text = n, Margin = new Thickness(0, 3, 0, 3) };
            var k = new TextBlock { Text = s.Hotkeys.GetValueOrDefault(a, ""), FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
            Grid.SetRow(l, row); Grid.SetRow(k, row); Grid.SetColumn(k, 1);
            grid.Children.Add(l); grid.Children.Add(k);
            row++;
        }
        p.Children.Add(grid);
        var startup = new CheckBox { Content = "Launch Loupix when I sign in", IsChecked = true, Margin = new Thickness(0, 4, 0, 16) };
        p.Children.Add(startup);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var settings = new Button { Content = "Customize…", Margin = new Thickness(0, 0, 8, 0) };
        settings.Click += (_, _) => { Apply(startup.IsChecked == true); AppController.Instance.ShowSettings(); Close(); };
        var ok = new Button { Content = "Get started", IsDefault = true };
        ok.Click += (_, _) => { Apply(startup.IsChecked == true); Close(); };
        buttons.Children.Add(settings);
        buttons.Children.Add(ok);
        p.Children.Add(buttons);
        Content = p;
    }

    private static void Apply(bool startup)
    {
        try { SystemIntegration.LaunchAtStartup = startup; } catch { }
        SettingsStore.Current.LaunchAtStartup = startup;
        SettingsStore.Save();
    }
}
