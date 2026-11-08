using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Loupix.Imaging;
using Loupix.Services;
using Microsoft.Win32;

namespace Loupix.UI;

/// <summary>Shows a shortcut as key caps and records a new one when focused. Backspace/Delete clears, Esc cancels.</summary>
public sealed class HotkeyBox : ContentControl
{
    private readonly HotkeyManager _manager;
    private readonly Border _face = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 3, 6, 3), MinHeight = 30, BorderThickness = new Thickness(1) };
    private string _text;
    private bool _recording;
    public event Action<string>? Changed;

    public HotkeyBox(HotkeyManager manager, string value)
    {
        _manager = manager;
        _text = value;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        ToolTip = "Click, then press the new shortcut. Backspace clears it.";
        Content = _face;
        Render();
        MouseEnter += (_, _) => Render();
        MouseLeave += (_, _) => Render();
        MouseLeftButtonDown += (_, _) => Focus();
        GotKeyboardFocus += (_, _) => { _recording = true; Render(); _manager.Suspend(); };
        LostKeyboardFocus += (_, _) => { _recording = false; Render(); _manager.Apply(SettingsStore.Current.Hotkeys); };
    }

    public string Text
    {
        get => _text;
        set { _text = value; Render(); }
    }

    public static FrameworkElement KeyCaps(string hotkey)
    {
        var caps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in hotkey.Split('+'))
        {
            var cap = new Border { Margin = new Thickness(3, 0, 0, 0), Padding = new Thickness(7, 2, 7, 2), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Child = new TextBlock { Text = key, FontSize = 12 } };
            cap.SetResourceReference(Border.BackgroundProperty, "UiField");
            cap.SetResourceReference(Border.BorderBrushProperty, "UiLine");
            caps.Children.Add(cap);
        }
        return caps;
    }

    private void Render()
    {
        if (IsMouseOver && !_recording) _face.SetResourceReference(Border.BackgroundProperty, "UiHover");
        else _face.Background = Brushes.Transparent;
        if (_recording) _face.SetResourceReference(Border.BorderBrushProperty, "UiAccent");
        else _face.BorderBrush = Brushes.Transparent;

        if (_recording || _text.Length == 0)
        {
            var label = new TextBlock { Text = _recording ? "Press a shortcut…" : "Not set", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, _recording ? "UiAccent" : "UiSubtle");
            _face.Child = label;
        }
        else _face.Child = KeyCaps(_text);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var mods = Keyboard.Modifiers;
        if (key == Key.Escape && mods == ModifierKeys.None) { Keyboard.ClearFocus(); return; }
        if (key is Key.Back or Key.Delete && mods == ModifierKeys.None) { Commit(""); return; }
        bool standalone = key is Key.Snapshot or >= Key.F1 and <= Key.F24;
        if (mods == ModifierKeys.None && !standalone) return; // needs a modifier
        Commit(new Hotkey(mods, key).ToString());
    }

    private void Commit(string value)
    {
        Text = value;
        Changed?.Invoke(value);
        Keyboard.ClearFocus();
    }
}

/// <summary>Preferences: General, Hotkeys, Saving, Capture, Editor, Upload, About.</summary>
public sealed class SettingsWindow : Window
{
    private readonly HotkeyManager _hotkeys;
    private readonly List<RadioButton> _sections = [];
    private static AppSettings S => SettingsStore.Current;

    internal IReadOnlyList<RadioButton> Sections => _sections;

    public SettingsWindow(HotkeyManager hotkeys)
    {
        _hotkeys = hotkeys;
        Title = "Loupix Settings";
        Width = 920;
        Height = 720;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = IconFactory.Logo(48);
        UiTheme.Apply(this);
        FontSize = 14;

        // Always reserves the bar's width, so that pages don't shift sideways when switching.
        var content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Visible, Focusable = false };
        var nav = new StackPanel { Margin = new Thickness(12, 0, 12, 20) };
        foreach (var (name, icon, page) in new (string, string, UIElement)[]
        {
            ("General", "M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6", General()),
            ("Hotkeys", "M4 6h16a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2zM6 10h.01M10 10h.01M14 10h.01M18 10h.01M8 14h8", Hotkeys()),
            ("Saving", "M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-8l-2-3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z", Saving()),
            ("Capture", "M3 8V5a2 2 0 0 1 2-2h3M16 3h3a2 2 0 0 1 2 2v3M21 16v3a2 2 0 0 1-2 2h-3M8 21H5a2 2 0 0 1-2-2v-3", CaptureTab()),
            ("Editor", "M17 3a2.83 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5z", EditorTab()),
            ("Upload", "M12 16V4M7 9l5-5 5 5M4 20h16", Upload()),
            ("About", "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20zM12 16v-4M12 8h.01", About()),
        })
        {
            var item = new RadioButton { Content = name, Tag = Geometry.Parse(icon), GroupName = "section", Style = (Style)FindResource("UiNavItem"), Margin = new Thickness(0, 1, 0, 1) };
            item.Checked += (_, _) => { content.Content = page; content.ScrollToTop(); };
            _sections.Add(item);
            nav.Children.Add(item);
        }
        _sections[0].IsChecked = true;

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 20, 20, 16) };
        brand.Children.Add(new Image { Source = IconFactory.Logo(64), Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) });
        brand.Children.Add(new TextBlock { Text = "Loupix", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var sidebar = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = new StackPanel { Children = { brand, nav } } };
        sidebar.SetResourceReference(Border.BackgroundProperty, "UiSide");
        sidebar.SetResourceReference(Border.BorderBrushProperty, "UiLine");

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(content, 1);
        layout.Children.Add(sidebar);
        layout.Children.Add(content);
        Content = layout;
        Closed += (_, _) => SettingsStore.Save();
    }

    // ---------- small builders ----------

    private static StackPanel Page(string title)
    {
        var p = new StackPanel { MaxWidth = 680, Margin = new Thickness(36, 26, 36, 40) };
        p.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        return p;
    }

    /// <summary>Adds a titled card to the page and returns the panel for its rows.</summary>
    private static StackPanel Group(StackPanel page, string title, string? note = null)
    {
        var heading = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 22, 0, 8) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "UiSubtle");
        page.Children.Add(heading);
        if (note != null)
        {
            var text = Note(note);
            text.Margin = new Thickness(2, -4, 0, 8);
            page.Children.Add(text);
        }
        var rows = new StackPanel();
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = rows };
        card.SetResourceReference(Border.BackgroundProperty, "UiCard");
        card.SetResourceReference(Border.BorderBrushProperty, "UiLine");
        page.Children.Add(card);
        return rows;
    }

    private static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "UiSubtle");
        return t;
    }

    private static void AddRow(StackPanel rows, UIElement content)
    {
        var row = new Border { Padding = new Thickness(16, 9, 16, 9), MinHeight = 52, Child = content };
        if (rows.Children.Count > 0)
        {
            row.BorderThickness = new Thickness(0, 1, 0, 0);
            row.SetResourceReference(Border.BorderBrushProperty, "UiLine");
        }
        rows.Children.Add(row);
    }

    private static void Row(StackPanel rows, string label, FrameworkElement control, string? note = null)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (note != null) text.Children.Add(Note(note));
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, label);
        Grid.SetColumn(control, 1);
        g.Children.Add(text);
        g.Children.Add(control);
        AddRow(rows, g);
    }

    private static void Switch(StackPanel rows, string label, bool value, Action<bool> set, string? note = null)
    {
        var s = new ToggleButton { IsChecked = value };
        s.SetResourceReference(StyleProperty, "UiSwitch");
        s.Checked += (_, _) => { set(true); SettingsStore.Save(); };
        s.Unchecked += (_, _) => { set(false); SettingsStore.Save(); };
        Row(rows, label, s, note);
    }

    private static ComboBox Choice<T>(T current, IEnumerable<(T Value, string Name)> options, Action<T> set, double width = 230) where T : notnull
    {
        var cb = new ComboBox { Width = width };
        foreach (var (v, n) in options) cb.Items.Add(new ComboBoxItem { Content = n, Tag = v, IsSelected = EqualityComparer<T>.Default.Equals(v, current) });
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem { Tag: T v }) { set(v); SettingsStore.Save(); } };
        return cb;
    }

    private static TextBox Text(string value, Action<string> set, double width = 280)
    {
        var t = new TextBox { Text = value, Width = width };
        t.LostFocus += (_, _) => { set(t.Text.Trim()); SettingsStore.Save(); };
        return t;
    }

    private static TextBox Number(int value, int min, int max, Action<int> set)
    {
        var t = new TextBox { Text = value.ToString(), Width = 90 };
        t.LostFocus += (_, _) =>
        {
            if (int.TryParse(t.Text, out var v)) { v = Math.Clamp(v, min, max); set(v); t.Text = v.ToString(); SettingsStore.Save(); }
            else t.Text = value.ToString();
        };
        return t;
    }

    // ---------- pages ----------

    private UIElement General()
    {
        var p = Page("General");
        var startup = Group(p, "Startup");
        Switch(startup, "Launch Loupix when I sign in to Windows", SystemIntegration.LaunchAtStartup, v => { SystemIntegration.LaunchAtStartup = v; S.LaunchAtStartup = v; });
        Switch(startup, "Show the icon in the notification area", S.ShowTrayIcon, v => S.ShowTrayIcon = v, "With the icon hidden, use your hotkeys, or run Loupix again to open these settings.");
        Switch(startup, "Show the editor when I start Loupix myself", S.ShowSplash, v => S.ShowSplash = v, "Not when it starts at sign-in.");

        var capture = Group(p, "After capture");
        var after = new (AfterCapture, string)[] { (AfterCapture.Editor, "Open the editor"), (AfterCapture.Thumbnail, "Show a preview thumbnail"), (AfterCapture.Nothing, "Do nothing else") };
        Row(capture, "After a fullscreen capture", Choice(S.AfterFullscreen, after, v => S.AfterFullscreen = v));
        Row(capture, "After an area or window capture", Choice(S.AfterArea, after, v => S.AfterArea = v));
        Switch(capture, "Also copy every capture to the clipboard", S.AutoCopy, v => S.AutoCopy = v);
        Switch(capture, "Also save every capture to the screenshots folder", S.AutoSave, v => S.AutoSave = v);
        Switch(capture, "Keep the thumbnail until I use or close it", S.ThumbnailPersist, v => S.ThumbnailPersist = v);
        Row(capture, "Thumbnail position", Choice(S.ThumbnailCorner,
            [(ThumbnailCorner.BottomRight, "Bottom right"), (ThumbnailCorner.BottomLeft, "Bottom left"), (ThumbnailCorner.TopRight, "Top right"), (ThumbnailCorner.TopLeft, "Top left")], v => S.ThumbnailCorner = v));

        var editor = Group(p, "Editor");
        Row(editor, "Esc in the editor", Choice(S.EscapeBehavior,
            [(EscapeBehavior.Copy, "Copy the image and close"), (EscapeBehavior.Close, "Just close"), (EscapeBehavior.Save, "Save the image and close"), (EscapeBehavior.CopyAndSave, "Copy, save and close")], v => S.EscapeBehavior = v));
        Row(editor, "Confirmations", Choice(S.Notifications,
            [(NotificationStyle.Custom, "Show small confirmations"), (NotificationStyle.None, "Don't show confirmations")], v => S.Notifications = v));
        Row(editor, "Delayed capture (seconds)", Number(S.DelaySeconds, 1, 60, v => S.DelaySeconds = v));

        var automation = Group(p, "Automation");
        Switch(automation, "Allow loupix:// links from launchers, scripts and web pages", S.AllowLinks == true, v => S.AllowLinks = v, "Links can take screenshots and copy, save or pin them, so they are off until you allow them. See About for the list of links.");
        Switch(automation, "Include the mouse cursor in captures", S.CaptureCursor, v => S.CaptureCursor = v);
        return p;
    }

    private UIElement Hotkeys()
    {
        var p = Page("Hotkeys");
        var rows = Group(p, "Global hotkeys", "Click a shortcut and press the new one. Backspace clears it. Shortcuts work from any app.");
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
            Row(rows, name, box);
        }
        var reset = new Button { Content = "Restore defaults", Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            S.Hotkeys = AppSettings.DefaultHotkeys();
            SettingsStore.Save();
            foreach (var (a, b) in boxes) b.Text = S.Hotkeys.GetValueOrDefault(a, "");
        };
        p.Children.Add(reset);
        if (_hotkeys.Conflicts.Count > 0)
            p.Children.Add(PageNote("Taken by another app (not active): " + string.Join(", ", _hotkeys.Conflicts.Select(c => c.Hotkey.ToString()))));
        p.Children.Add(PageNote("Tip: to use Print Screen, first turn off \"Use the Print screen key to open screen capture\" in Windows Settings → Accessibility → Keyboard."));
        return p;
    }

    private static TextBlock PageNote(string text)
    {
        var note = Note(text);
        note.Margin = new Thickness(2, 12, 0, 0);
        return note;
    }

    private UIElement Saving()
    {
        var p = Page("Saving");
        var folderRows = Group(p, "Screenshots folder");
        var folder = new TextBox { Text = S.SaveFolder, IsReadOnly = true };
        var browse = new Button { Content = "Choose…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { InitialDirectory = Directory.Exists(S.SaveFolder) ? S.SaveFolder : null, Title = "Where should screenshots be saved?" };
            if (dlg.ShowDialog(this) == true) { S.SaveFolder = dlg.FolderName; folder.Text = dlg.FolderName; SettingsStore.Save(); }
        };
        var open = new Button { Content = "Open", Margin = new Thickness(8, 0, 0, 0) };
        open.Click += (_, _) => { Directory.CreateDirectory(S.SaveFolder); Process.Start(new ProcessStartInfo(S.SaveFolder) { UseShellExecute = true }); };
        var folderRow = new DockPanel { Children = { open, browse, folder }, LastChildFill = true };
        DockPanel.SetDock(open, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        AddRow(folderRows, folderRow);

        var nameRows = Group(p, "File name");
        var preview = Note("");
        preview.Margin = new Thickness(0, 6, 0, 0);
        void UpdatePreview(string t) => preview.Text = "Example: " + FileNamer.Expand(t, DateTime.Now) + ".png";
        var tmpl = new TextBox { Text = S.FileNameTemplate };
        AutomationProperties.SetName(tmpl, "File name");
        tmpl.TextChanged += (_, _) => UpdatePreview(tmpl.Text);
        tmpl.LostFocus += (_, _) => { S.FileNameTemplate = string.IsNullOrWhiteSpace(tmpl.Text) ? "SCR-{yyyyMMdd}-{timecode}" : tmpl.Text.Trim(); SettingsStore.Save(); };
        UpdatePreview(S.FileNameTemplate);
        AddRow(nameRows, new StackPanel { Children = { tmpl, preview, Note("{timecode} is a short code that sorts by time of day. Any other {…} is a date format, e.g. {yyyy-MM-dd HH.mm.ss}.") } });

        var format = Group(p, "Format");
        Row(format, "Image format", Choice(S.SaveFormat, [(SaveFormat.Png, "PNG"), (SaveFormat.Jpeg, "JPEG"), (SaveFormat.Auto, "Automatic (PNG for UI, JPEG for photos)")], v => S.SaveFormat = v, 320));
        Row(format, "JPEG quality (10–100)", Number(S.JpegQuality, 10, 100, v => S.JpegQuality = v));
        Switch(format, "Save high-DPI captures at 1× (logical size)", S.DownscaleHiDpi, v => S.DownscaleHiDpi = v);
        return p;
    }

    private UIElement CaptureTab()
    {
        var p = Page("Capture");
        var window = Group(p, "Window capture");
        Row(window, "Window style", Choice(S.WindowCaptureStyle,
            [(WindowCaptureStyle.Shadow, "Shadow on a transparent background"), (WindowCaptureStyle.NoShadow, "Window only, no shadow"), (WindowCaptureStyle.SolidBackground, "Shadow on a solid colour"), (WindowCaptureStyle.Wallpaper, "Shadow over the wallpaper")], v => S.WindowCaptureStyle = v, 300));
        Row(window, "Solid colour (hex)", Text(S.WindowBackgroundColor, v => { if (v.Length > 0) S.WindowBackgroundColor = v.StartsWith('#') ? v : "#" + v; }, 120));

        var scrolling = Group(p, "Scrolling capture", "Select the area to capture; Loupix scrolls the window under it and stitches the result. Press Esc or Stop to finish early.");
        Row(scrolling, "Maximum height (px)", Number(S.ScrollMaxHeight, 1000, 200000, v => S.ScrollMaxHeight = v));
        Switch(scrolling, "Reverse the scroll direction", S.ScrollInvertDirection, v => S.ScrollInvertDirection = v, "For apps that invert scrolling.");
        return p;
    }

    private UIElement EditorTab()
    {
        var p = Page("Editor");
        var editor = Group(p, "Editor");
        Switch(editor, "Open captures at 100% zoom when they fit", S.DefaultZoom100, v => S.DefaultZoom100 = v, "Otherwise they fit to the window.");
        Switch(editor, "Keep the editor window on top", S.EditorAlwaysOnTop, v => S.EditorAlwaysOnTop = v);
        Switch(editor, "Measure in physical pixels", S.MeasureInPhysicalPixels, v => S.MeasureInPhysicalPixels = v, "Instead of scaled (logical) pixels.");
        Row(editor, "Color format", Choice(S.ColorFormat, Features.ColorFormats().Select(f => (f.Key, f.Name)), v => S.ColorFormat = v, 300));

        var ocr = Group(p, "Text recognition", "More languages: Windows Settings → Time & language → Language & region → add a language with \"Optical character recognition\".");
        var langs = new List<(string, string)> { ("", "Windows display language") };
        try { langs.AddRange(Features.OcrLanguages().Select(l => (l, l))); } catch { }
        Row(ocr, "Language", Choice(S.OcrLanguage, langs, v => S.OcrLanguage = v));
        Switch(ocr, "Remove line breaks from recognized text by default", S.OcrRemoveLineBreaks, v => S.OcrRemoveLineBreaks = v);
        return p;
    }

    private UIElement Upload()
    {
        var p = Page("Upload");
        var s3 = S.S3;
        var rows = Group(p, "S3-compatible storage", "Ctrl+E (or F2) in the editor uploads the image and copies its link. Works with Amazon S3, Cloudflare R2, Backblaze B2, MinIO, DigitalOcean Spaces and other S3-compatible services. Uploaded images are only as private as your bucket's settings.");
        Switch(rows, "Enable upload", s3.Enabled, v => s3.Enabled = v);
        Row(rows, "Endpoint URL", Text(s3.Endpoint, v => s3.Endpoint = v));
        Row(rows, "Region", Text(s3.Region, v => s3.Region = v, 160));
        Row(rows, "Bucket", Text(s3.Bucket, v => s3.Bucket = v));
        Row(rows, "Access key ID", Text(s3.AccessKeyId, v => s3.AccessKeyId = v));
        var secret = new PasswordBox { Password = SecretStore.Unprotect(s3.SecretAccessKeyProtected), Width = 280 };
        secret.LostFocus += (_, _) => { s3.SecretAccessKeyProtected = SecretStore.Protect(secret.Password); SettingsStore.Save(); };
        Row(rows, "Secret access key", secret, "Encrypted with your Windows account (DPAPI) before it is stored.");
        Switch(rows, "Path-style URLs", s3.PathStyle, v => s3.PathStyle = v, "endpoint/bucket/key");
        Row(rows, "Public URL prefix (optional)", Text(s3.PublicUrlBase, v => s3.PublicUrlBase = v));
        Row(rows, "Key prefix (folder)", Text(s3.KeyPrefix, v => s3.KeyPrefix = v));
        Row(rows, "ACL (optional)", Text(s3.Acl, v => s3.Acl = v, 160), "For example public-read.");
        var test = new Button { Content = "Test upload", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0) };
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
        var p = Page("About");
        var app = Group(p, "Loupix");
        var logo = new Image { Source = IconFactory.Logo(128), Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = "Loupix", FontSize = 18, FontWeight = FontWeights.SemiBold }, Note($"Version {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)} · a fast screenshot, annotation and measurement tool for Windows.") } };
        var identity = new Grid();
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(text, 1);
        identity.Children.Add(logo);
        identity.Children.Add(text);
        AddRow(app, identity);

        var links = Group(p, "Links");
        AddRow(links, Note("Automate captures with loupix:// links, e.g. loupix://grab/area?then=copy,save — the same verbs as Shottr: grab/fullscreen, grab/area, grab/repeat, grab/window, grab/scrolling, grab/scrolling/reverse, grab/delayed=5, grab/append, ocr, load/clipboard, load/file, settings, show. then= accepts copy, save, edit, pin, thumbnail."));
        var folder = new Button { Content = "Open settings folder", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
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
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = IconFactory.Logo(48);
        UiTheme.Apply(this);
        FontSize = 13;
        var s = SettingsStore.Current;
        var p = new StackPanel { Margin = new Thickness(24) };
        p.Children.Add(new Image { Source = IconFactory.Logo(128), Width = 52, Height = 52, HorizontalAlignment = HorizontalAlignment.Left });
        p.Children.Add(new TextBlock { Text = "Loupix is ready", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        var intro = new TextBlock { Text = "It lives in the notification area. Capture with these hotkeys:", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "UiSubtle");
        p.Children.Add(intro);
        foreach (var (a, n) in new[] { (CaptureAction.Area, "Area"), (CaptureAction.Fullscreen, "Fullscreen"), (CaptureAction.Window, "Window"), (CaptureAction.Scrolling, "Scrolling"), (CaptureAction.Ocr, "Recognize text") })
        {
            var hotkey = s.Hotkeys.GetValueOrDefault(a, "");
            if (hotkey.Length == 0) continue;
            var row = new DockPanel { LastChildFill = false };
            var keys = HotkeyBox.KeyCaps(hotkey);
            DockPanel.SetDock(keys, Dock.Right);
            row.Children.Add(keys);
            row.Children.Add(new TextBlock { Text = n, VerticalAlignment = VerticalAlignment.Center });
            var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 7, 0, 7), Child = row };
            line.SetResourceReference(Border.BorderBrushProperty, "UiLine");
            p.Children.Add(line);
        }
        var startup = new ToggleButton { IsChecked = true, Style = (Style)FindResource("UiSwitch") };
        AutomationProperties.SetName(startup, "Launch Loupix when I sign in");
        DockPanel.SetDock(startup, Dock.Right);
        p.Children.Add(new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 16), Children = { startup, new TextBlock { Text = "Launch Loupix when I sign in", VerticalAlignment = VerticalAlignment.Center } } });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var settings = new Button { Content = "Customize…", Margin = new Thickness(0, 0, 8, 0) };
        settings.Click += (_, _) => { Apply(startup.IsChecked == true); AppController.Instance.ShowSettings(); Close(); };
        var ok = new Button { Content = "Get started", IsDefault = true, Style = (Style)FindResource("UiPrimary") };
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

/// <summary>Asks before a loupix:// link from another app is allowed to run.</summary>
public sealed class LinkPermissionWindow : Window
{
    public LinkPermissionWindow(string url)
    {
        Title = "Loupix";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = IconFactory.Logo(48);
        Topmost = true; // Links arrive from other apps, so this must not open behind them.
        UiTheme.Apply(this);
        FontSize = 13;

        var p = new StackPanel { Margin = new Thickness(20) };
        p.Children.Add(new TextBlock { Text = "Allow other apps and links to control Loupix?", FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var body = new TextBlock { Text = "Launchers, scripts and web pages can use loupix:// links to take screenshots and copy, save or pin them. Allow this only if you use that kind of automation.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "UiSubtle");
        p.Children.Add(body);
        var requested = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(6), Child = new TextBlock { Text = url, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap } };
        requested.SetResourceReference(Border.BackgroundProperty, "UiButton");
        p.Children.Add(requested);
        var deny = new Button { Content = "Don't allow", IsDefault = true, IsCancel = true, Style = (Style)FindResource("UiPrimary"), Margin = new Thickness(0, 0, 8, 0) };
        var allow = new Button { Content = "Allow" };
        allow.Click += (_, _) => DialogResult = true;
        p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { deny, allow } });
        Content = p;
    }
}
