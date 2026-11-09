using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Loupix.Capture;
using Loupix.Core.Imaging;
using Loupix.Editor;
using Loupix.Imaging;
using Loupix.Services;
using Loupix.UI;
using Microsoft.Win32;

namespace Loupix;

/// <summary>Things to do with a capture right away (from settings or a loupix://...?then= link).</summary>
[Flags]
public enum ThenAction { None = 0, Copy = 1, Save = 2, Edit = 4, Pin = 8, Thumbnail = 16 }

/// <summary>Owns the app's lifetime: tray icon, hotkeys, captures and the editor.</summary>
public sealed class AppController
{
    public static AppController Instance { get; } = new();

    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private EditorWindow? _editor;
    private WelcomeWindow? _welcome;
    private RectI? _lastArea;
    private bool _busy;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public bool IsQuitting { get; private set; }
    private static AppSettings Settings => SettingsStore.Current;

    public Updater Updates { get; }

    private AppController()
    {
        Updates = new Updater(() => _busy || _editor is { IsVisible: true, Document: not null }, Quit);
    }

    public void Start(string[] args)
    {
        SettingsStore.Load();
        SystemIntegration.RegisterUrlScheme();
        SystemIntegration.RegisterOpenWith();

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += a => Run(a);
        _hotkeys.Apply(Settings.Hotkeys);

        _tray = new TrayIcon { MenuFactory = BuildTrayMenu };
        _tray.DoubleClicked += () => Run(CaptureAction.Area);
        if (Settings.ShowTrayIcon) _tray.Show("Loupix — screenshots");

        SettingsStore.Changed += OnSettingsChanged;

        // Warm up the editor so the first capture opens instantly.
        _editor = new EditorWindow();

        Updates.Prompt += ShowUpdateCard;
        Updates.Start();

        bool background = args.Contains("--background");
        bool firstRun = !Settings.FirstRunDone;
        if (firstRun)
        {
            Settings.FirstRunDone = true;
            Settings.FirstCaptureTipPending = true;
            SettingsStore.Save();
            ShowWelcome();
        }
        else if (!background && args.Length == 0 && Settings.ShowSplash)
        {
            // Like Shottr, a manual launch shows the (empty) editor with how-to hints.
            _editor.ShowEmpty(Monitors.AtCursor());
        }
        if (_hotkeys.Conflicts.Count > 0 && !firstRun) // the Welcome window explains these itself
            ToastWindow.Show("Some hotkeys are taken by other apps", string.Join(", ", _hotkeys.Conflicts.Select(c => $"{c.Hotkey} ({c.Action})")), buttons: [("Settings", ShowSettings)], seconds: 6);

        if (args.Contains("--updated")) ToastWindow.Show($"Updated to Loupix {Updater.CurrentVersion.ToString(3)}");

        HandleArgs(args);
    }

    private void OnSettingsChanged()
    {
        _hotkeys?.Apply(Settings.Hotkeys);
        if (Settings.ShowTrayIcon) _tray?.Show("Loupix — screenshots"); else _tray?.Hide();
    }

    private void ShowWelcome()
    {
        var w = _welcome = new WelcomeWindow(_hotkeys!.Conflicts.Select(c => c.Action).ToHashSet());
        w.Closed += (_, _) => _welcome = null;
        w.Show();
        BringToFront(w);
    }

    public void ShowToolGuide()
    {
        var existing = Application.Current.Windows.OfType<ToolGuideWindow>().FirstOrDefault();
        if (existing != null) { BringToFront(existing); return; }
        var w = new ToolGuideWindow();
        w.Show();
        BringToFront(w);
    }

    public void Quit()
    {
        IsQuitting = true;
        _tray?.Dispose();
        _hotkeys?.Dispose();
        PinWindow.CloseAll();
        Application.Current.Shutdown();
    }

    // ---------------- command line & loupix:// links ----------------

    public void HandleArgs(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--")) continue;
            if (arg.StartsWith("loupix:", StringComparison.OrdinalIgnoreCase)) HandleUrl(arg);
            else if (File.Exists(arg)) OpenFile(arg);
        }
    }

    /// <summary>
    /// loupix://grab/area?then=copy,save — same verbs as Shottr's URL scheme:
    /// show, grab/fullscreen|area|repeat|window|scrolling|scrolling/reverse|delayed[=N]|append, ocr, load/clipboard, load/file, settings.
    /// </summary>
    public void HandleUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            string path = (u.Host + u.AbsolutePath).Trim('/').ToLowerInvariant();
            // Links can come from any app or web page, so (like Shottr) they need the user's consent first.
            // "show" and "settings" are harmless and are also what a second launch of Loupix sends.
            if (path is not ("show" or "settings") && !LinksAllowed(url)) return;
            var then = ThenAction.None;
            var query = System.Web.HttpUtility.ParseQueryString(u.Query);
            foreach (var part in (query["then"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Enum.TryParse<ThenAction>(part, true, out var t)) then |= t;

            if (path.StartsWith("grab/delayed"))
            {
                int secs = Settings.DelaySeconds;
                var eq = path.IndexOf('=');
                if (eq > 0 && int.TryParse(path[(eq + 1)..], out var n)) secs = Math.Clamp(n, 1, 120);
                _ = Delayed(secs, then);
                return;
            }
            switch (path)
            {
                case "show": ShowEditor(); break;
                case "grab/fullscreen": Run(CaptureAction.Fullscreen, then); break;
                case "grab/area": Run(CaptureAction.Area, then); break;
                case "grab/repeat": Run(CaptureAction.RepeatArea, then); break;
                case "grab/window": Run(CaptureAction.Window, then); break;
                case "grab/scrolling": Run(CaptureAction.Scrolling, then); break;
                case "grab/scrolling/reverse": Run(CaptureAction.ScrollingReverse, then); break;
                case "grab/append": Run(CaptureAction.AddCapture); break;
                case "ocr": Run(CaptureAction.Ocr); break;
                case "load/clipboard": LoadClipboard(); break;
                case "load/file": OpenFileDialog(); break;
                case "settings": ShowSettings(); break;
                default: Log.Info($"Unknown link {url}"); break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Bad link {url}", ex);
        }
    }

    private bool LinksAllowed(string url)
    {
        if (Settings.AllowLinks == true) return true;
        if (Settings.AllowLinks == false)
        {
            ToastWindow.Show("loupix:// links are turned off", "Turn them on in Settings → General → Automation.", buttons: [("Settings", ShowSettings)]);
            return false;
        }
        Settings.AllowLinks = new LinkPermissionWindow(url).ShowDialog() == true;
        SettingsStore.Save();
        return Settings.AllowLinks == true;
    }

    // ---------------- tray menu ----------------

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, CaptureAction? action, Action? click = null)
        {
            var mi = new MenuItem { Header = header };
            if (action is CaptureAction a)
            {
                var hk = Hotkey.Parse(Settings.Hotkeys.GetValueOrDefault(a));
                if (!hk.IsEmpty) mi.InputGestureText = hk.ToString();
                mi.Click += (_, _) => Run(a);
            }
            else if (click != null) mi.Click += (_, _) => click();
            return mi;
        }
        if (Updates.Status == UpdateStatus.Available && Updates.Update is { } update)
        {
            var install = Item($"Update to {update.Version.ToString(3)}", null, BeginUpdate);
            install.Icon = UpdateCard.Glyph();
            menu.Items.Add(install);
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("Fullscreen capture", CaptureAction.Fullscreen));
        menu.Items.Add(Item("Area capture", CaptureAction.Area));
        menu.Items.Add(Item("Window capture", CaptureAction.Window));
        menu.Items.Add(Item("Scrolling capture", CaptureAction.Scrolling));
        menu.Items.Add(Item("Repeat area capture", CaptureAction.RepeatArea));
        menu.Items.Add(Item($"Delayed capture ({Settings.DelaySeconds}s)", CaptureAction.Delayed));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Recognize text (OCR)", CaptureAction.Ocr));
        menu.Items.Add(new Separator());
        var more = new MenuItem { Header = "More" };
        more.Items.Add(Item("Show editor", CaptureAction.ShowEditor));
        more.Items.Add(Item("Load from clipboard", CaptureAction.LoadClipboard));
        more.Items.Add(Item("Open file…", null, OpenFileDialog));
        more.Items.Add(Item("Scrolling capture (upwards)", CaptureAction.ScrollingReverse));
        more.Items.Add(Item("Open screenshots folder", null, () => OpenFolder(Settings.SaveFolder)));
        if (PinWindow.Count > 0) more.Items.Add(Item("Close all pinned screenshots", null, PinWindow.CloseAll));
        menu.Items.Add(more);
        menu.Items.Add(Item("Settings…", null, ShowSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Loupix", null, Quit));
        return menu;
    }

    // ---------------- capture flows ----------------

    public void Run(CaptureAction action, ThenAction then = ThenAction.None)
    {
        switch (action)
        {
            case CaptureAction.ShowEditor: ShowEditor(); return;
            case CaptureAction.LoadClipboard: LoadClipboard(); return;
            case CaptureAction.OpenFile: OpenFileDialog(); return;
            case CaptureAction.Settings: ShowSettings(); return;
            case CaptureAction.Delayed: _ = Delayed(Settings.DelaySeconds, then); return;
        }
        if (_busy) return;
        _ = RunCapture(action, then);
    }

    private async Task RunCapture(CaptureAction action, ThenAction then)
    {
        _busy = true;
        try
        {
            ThumbnailWindow.DismissAll();
            UpdateCard.CloseCurrent(); // it's always on top, so it would be in the capture
            bool editorWasVisible = _editor?.IsVisible == true;
            if (editorWasVisible) _editor!.Hide(); // never capture our own editor
            bool welcomeWasOpen = _welcome != null;
            _welcome?.Close(); // or the Welcome window that told you to press the hotkey
            await Task.Delay(editorWasVisible || welcomeWasOpen ? 120 : 10); // let the window disappear from the screen

            switch (action)
            {
                case CaptureAction.Fullscreen:
                    {
                        var m = Monitors.AtCursor();
                        var px = ScreenCapture.Capture(m.Bounds, Settings.CaptureCursor);
                        Deliver(px, m, CaptureAction.Fullscreen, then);
                        break;
                    }
                case CaptureAction.RepeatArea when _lastArea is RectI last:
                    {
                        var m = Monitors.FromPoint(last.X + last.Width / 2, last.Y + last.Height / 2);
                        Deliver(ScreenCapture.Capture(last, Settings.CaptureCursor), m, CaptureAction.Area, then);
                        break;
                    }
                case CaptureAction.RepeatArea:
                case CaptureAction.Area:
                case CaptureAction.AddCapture:
                    {
                        var (frozen, origin) = Freeze();
                        var windows = WindowFinder.VisibleWindows();
                        var r = await CaptureOverlay.SelectAsync(OverlayMode.Area, frozen, origin, windows);
                        if (r == null) { RestoreEditor(editorWasVisible); break; }
                        var px = r.Window != null && r.Rect == r.Window.Bounds
                            ? WindowCapture.ApplyStyle(WindowCapture.Capture(r.Window, frozen, origin, r.Monitor.Scale), Settings.WindowCaptureStyle, r.Monitor.Scale, r.Window.Bounds)
                            : frozen.Crop(r.Rect.X - origin.X, r.Rect.Y - origin.Y, r.Rect.Width, r.Rect.Height);
                        if (r.Window == null) _lastArea = r.Rect;
                        if (action == CaptureAction.AddCapture) AppendToEditor(px, r.Monitor.Scale, editorWasVisible);
                        else Deliver(px, r.Monitor, CaptureAction.Area, then);
                        break;
                    }
                case CaptureAction.Window:
                    {
                        var (frozen, origin) = Freeze();
                        var windows = WindowFinder.VisibleWindows();
                        var r = await CaptureOverlay.SelectAsync(OverlayMode.Window, frozen, origin, windows);
                        if (r?.Window == null) { RestoreEditor(editorWasVisible); break; }
                        var px = WindowCapture.ApplyStyle(WindowCapture.Capture(r.Window, frozen, origin, r.Monitor.Scale), Settings.WindowCaptureStyle, r.Monitor.Scale, r.Window.Bounds);
                        Deliver(px, r.Monitor, CaptureAction.Window, then);
                        break;
                    }
                case CaptureAction.Scrolling:
                case CaptureAction.ScrollingReverse:
                    {
                        var (frozen, origin) = Freeze();
                        var windows = WindowFinder.VisibleWindows();
                        var r = await CaptureOverlay.SelectAsync(OverlayMode.Area, frozen, origin, windows,
                            "Select the area to scroll · Loupix will scroll it for you · Esc to cancel");
                        if (r == null) { RestoreEditor(editorWasVisible); break; }
                        await Task.Delay(60);
                        var px = await ScrollingCapture.RunAsync(r.Rect, action == CaptureAction.ScrollingReverse);
                        if (px != null) Deliver(px, r.Monitor, CaptureAction.Scrolling, then == ThenAction.None ? ThenAction.Edit : then);
                        break;
                    }
                case CaptureAction.Ocr:
                    {
                        var (frozen, origin) = Freeze();
                        var r = await CaptureOverlay.SelectAsync(OverlayMode.Area, frozen, origin, [],
                            "Select the text to recognize · Esc to cancel");
                        if (r == null) { RestoreEditor(editorWasVisible); break; }
                        var px = frozen.Crop(r.Rect.X - origin.X, r.Rect.Y - origin.Y, r.Rect.Width, r.Rect.Height);
                        await RecognizeAndCopy(px, null);
                        RestoreEditor(editorWasVisible);
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Capture {action} failed", ex);
            ToastWindow.Show("Capture failed", ex.Message);
        }
        finally
        {
            _busy = false;
            if (Updates.Status is UpdateStatus.Downloading or UpdateStatus.WaitingForEditor) ShowUpdateCard();
        }
    }

    private void RestoreEditor(bool show)
    {
        if (show && _editor?.Document != null) _editor.Show();
    }

    private static (PixelBuffer Frozen, RectI Origin) Freeze()
    {
        var vs = Monitors.VirtualScreen();
        return (ScreenCapture.Capture(vs, Settings.CaptureCursor), vs);
    }

    private async Task Delayed(int seconds, ThenAction then)
    {
        for (int i = seconds; i > 0; i--)
        {
            ToastWindow.Show($"Capturing in {i}…", seconds: 0.95);
            await Task.Delay(1000);
        }
        await Task.Delay(250); // let the countdown bubble fade away
        _ = RunCapture(CaptureAction.Fullscreen, then);
    }

    /// <summary>After a capture: auto copy/save, then editor, thumbnail or nothing.</summary>
    private void Deliver(PixelBuffer px, MonitorInfo monitor, CaptureAction kind, ThenAction then)
    {
        var doc = new EditorDocument(px, monitor.Scale);
        if (then == ThenAction.None)
        {
            if (Settings.AutoCopy) then |= ThenAction.Copy;
            if (Settings.AutoSave) then |= ThenAction.Save;
            var after = kind == CaptureAction.Fullscreen ? Settings.AfterFullscreen : Settings.AfterArea;
            then |= after switch { AfterCapture.Thumbnail => ThenAction.Thumbnail, AfterCapture.Nothing => ThenAction.None, _ => ThenAction.Edit };
        }
        BitmapSource? bmp = null;
        BitmapSource Bmp() => bmp ??= ImageIO.ToBitmapSource96(px);
        if (then.HasFlag(ThenAction.Copy)) { ImageIO.CopyToClipboard(Bmp()); if (!then.HasFlag(ThenAction.Edit) && !then.HasFlag(ThenAction.Thumbnail)) Notify("Copied to clipboard", NotificationKind.Copy); }
        if (then.HasFlag(ThenAction.Save))
        {
            var path = SaveImage(Bmp(), monitor.Scale, null, doc.CreatedAt);
            if (!then.HasFlag(ThenAction.Edit)) Notify($"Saved {Path.GetFileName(path)}", NotificationKind.Save, path);
        }
        if (then.HasFlag(ThenAction.Pin)) Pin(Bmp(), monitor.Scale, monitor);
        if (then.HasFlag(ThenAction.Edit))
        {
            _editor!.Open(doc, monitor);
            if (Settings.FirstCaptureTipPending)
            {
                Settings.FirstCaptureTipPending = false;
                SettingsStore.Save();
                _editor.ShowCoach("That's your first screenshot", "Mark it up if you like, then press Esc to copy it and close.");
            }
        }
        else if (then.HasFlag(ThenAction.Thumbnail)) ShowThumbnail(doc, monitor);
    }

    private void ShowThumbnail(EditorDocument doc, MonitorInfo monitor)
    {
        var bmp = doc.Base.Bitmap;
        var t = new ThumbnailWindow(bmp,
            edit: () => _editor!.Open(doc, monitor),
            copy: () => { ImageIO.CopyToClipboard(bmp); Notify("Copied to clipboard", NotificationKind.Copy); },
            save: () => SaveAndNotify(bmp, doc.Scale),
            pin: () => Pin(bmp, doc.Scale, monitor),
            ocr: () => _ = RecognizeAndCopy(doc.Base.Pixels, null),
            saveTemp: () => TempFileFor(bmp, doc.Scale, doc.CreatedAt),
            persist: Settings.ThumbnailPersist);
        t.ShowAtCorner(Settings.ThumbnailCorner);
    }

    private void AppendToEditor(PixelBuffer px, double scale, bool editorWasVisible)
    {
        var doc = PreviousDocument(editorWasVisible);
        if (doc == null)
        {
            Deliver(px, Monitors.AtCursor(), CaptureAction.Area, ThenAction.Edit);
            return;
        }
        // Place the new capture to the right of the canvas, top-aligned, and grow the canvas.
        double gap = 16 * doc.Scale;
        double s = doc.Scale / scale;
        var r = new Rect(doc.Canvas.Right + gap, doc.Canvas.Top, px.Width * s, px.Height * s);
        doc.Checkpoint();
        doc.Annotations.Add(new ImageAnnotation { Image = ImageIO.ToBitmapSource96(px), P1 = r.TopLeft, P2 = r.BottomRight, IsCapture = true });
        doc.ExpandCanvas(r);
        doc.RaiseChanged();
        _editor!.Open(doc, Monitors.AtCursor());
    }

    public void AddCapture() => Run(CaptureAction.AddCapture);

    // ---------------- editor entry points ----------------

    public void OpenInEditor(PixelBuffer px, double scale, string? sourcePath = null)
    {
        var doc = new EditorDocument(px, scale) { SourcePath = sourcePath };
        _editor!.Open(doc, Monitors.AtCursor());
    }

    /// <summary>Reopen: the last document, or an empty editor (like clicking Shottr's Dock icon).</summary>
    public void ShowEditor()
    {
        var doc = PreviousDocument(_editor?.IsVisible == true);
        if (doc != null) _editor!.Open(doc, Monitors.AtCursor());
        else _editor?.ShowEmpty(Monitors.AtCursor());
    }

    /// <summary>The document to carry on with. One the user has closed stays away if they asked not to see it again.</summary>
    private EditorDocument? PreviousDocument(bool editorWasVisible) =>
        editorWasVisible || !Settings.HidePreviousScreenshot ? _editor?.Document : null;

    public void LoadClipboard()
    {
        var px = ImageIO.ClipboardImage();
        if (px == null) { ToastWindow.Show("No image on the clipboard"); return; }
        OpenInEditor(px, Monitors.AtCursor().Scale);
    }

    public void OpenFileDialog()
    {
        var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*" };
        if (dlg.ShowDialog() == true) OpenFile(dlg.FileName);
    }

    public void OpenFile(string path)
    {
        var px = ImageIO.LoadFile(path);
        if (px == null) { ToastWindow.Show("Couldn't open the image", Path.GetFileName(path)); return; }
        OpenInEditor(px, 1.0, path);
    }

    // ---------------- updates ----------------

    public void BeginUpdate()
    {
        _ = Updates.InstallAsync();
        if (Updates.Status == UpdateStatus.Downloading) ShowUpdateCard();
    }

    private void ShowUpdateCard()
    {
        if (_busy || Updates.Update is not { } update) return;
        bool installing = Updates.Status is UpdateStatus.Downloading or UpdateStatus.WaitingForEditor;
        bool wanted = Updates.Status == UpdateStatus.Available && Settings.CheckForUpdates && Settings.DismissedUpdateVersion != update.Version.ToString(3);
        if (installing || wanted) UpdateCard.Present(Updates, BeginUpdate);
    }

    public void ShowSettings() => ShowSettings(null);

    /// <summary>Opens Settings, on a particular page if one is named.</summary>
    public void ShowSettings(string? section)
    {
        var w = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (w == null)
        {
            w = new SettingsWindow(_hotkeys!);
            w.Show();
        }
        if (section != null) w.Sections.First(s => (string)s.Content == section).IsChecked = true;
        BringToFront(w);
    }

    // ---------------- output ----------------

    public string SaveImage(BitmapSource bmp, double scale, string? path, DateTime time, bool exactPath = false)
    {
        var format = Settings.SaveFormat;
        string ext;
        if (path != null && exactPath)
        {
            ext = Path.GetExtension(path).ToLowerInvariant();
            format = ext is ".jpg" or ".jpeg" ? SaveFormat.Jpeg : SaveFormat.Png;
            if (ext == ".gif")
            {
                File.WriteAllBytes(path, Features.EncodeGif([bmp]));
                return path;
            }
        }
        else if (path != null)
        {
            ext = Path.GetExtension(path).ToLowerInvariant();
            format = ext is ".jpg" or ".jpeg" ? SaveFormat.Jpeg : SaveFormat.Png;
        }
        else
        {
            if (format == SaveFormat.Auto) format = ImageIO.ChooseAutoFormat(ImageIO.FromBitmapSource(bmp));
            ext = format == SaveFormat.Jpeg ? ".jpeg" : ".png";
            path = FileNamer.UniquePath(Settings.SaveFolder, FileNamer.Expand(Settings.FileNameTemplate, time), ext);
        }

        // Optionally save high-DPI captures at 1× (logical size).
        var output = bmp;
        double dpi = scale;
        if (Settings.DownscaleHiDpi && scale > 1.01)
        {
            int w = (int)Math.Round(bmp.PixelWidth / scale), h = (int)Math.Round(bmp.PixelHeight / scale);
            output = ImageIO.ToBitmapSource96(RasterOps.Resize(ImageIO.FromBitmapSource(bmp), w, h));
            dpi = 1;
        }
        var bytes = format == SaveFormat.Jpeg ? ImageIO.EncodeJpeg(output, Settings.JpegQuality, dpi) : ImageIO.EncodePng(output, dpi);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        Log.Info($"Saved {path}");
        return path;
    }

    public void SaveAndNotify(BitmapSource bmp, double scale)
    {
        try
        {
            var path = SaveImage(bmp, scale, null, DateTime.Now);
            Notify($"Saved {Path.GetFileName(path)}", NotificationKind.Save, path);
        }
        catch (Exception ex)
        {
            ToastWindow.Show("Could not save", ex.Message);
        }
    }

    public string TempFileFor(BitmapSource bmp, double scale, DateTime time)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Loupix");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileNamer.Expand(Settings.FileNameTemplate, time) + ".png");
        File.WriteAllBytes(path, ImageIO.EncodePng(bmp, scale));
        return path;
    }

    public string? SaveGif(IReadOnlyList<BitmapSource> frames)
    {
        try
        {
            var bytes = Features.EncodeGif(frames);
            var path = FileNamer.UniquePath(Settings.SaveFolder, FileNamer.Expand(Settings.FileNameTemplate, DateTime.Now), ".gif");
            File.WriteAllBytes(path, bytes);
            ImageIO.CopyFile(path);
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("GIF failed", ex);
            ToastWindow.Show("Couldn't make the GIF", ex.Message);
            return null;
        }
    }

    public void Pin(BitmapSource bmp, double scale, MonitorInfo? monitor = null)
    {
        var m = monitor ?? Monitors.AtCursor();
        var pin = new PinWindow(bmp, scale);
        int w = (int)(bmp.PixelWidth * m.Scale / scale), h = (int)(bmp.PixelHeight * m.Scale / scale);
        pin.ShowAt(m.WorkArea.X + (m.WorkArea.Width - w) / 2, m.WorkArea.Y + (m.WorkArea.Height - h) / 2);
    }

    public async Task RecognizeAndCopy(PixelBuffer px, Window? owner)
    {
        try
        {
            var (text, isQr) = await Features.RecognizeAsync(px);
            if (string.IsNullOrWhiteSpace(text))
            {
                ToastWindow.Show("No text found");
                return;
            }
            if (!isQr && Settings.OcrRemoveLineBreaks) text = Features.RemoveLineBreaks(text);
            ImageIO.CopyText(text);
            var buttons = new List<(string, Action)>();
            if (!isQr && text.Contains('\n'))
                buttons.Add(("Cut breaks", () => ImageIO.CopyText(Features.RemoveLineBreaks(text))));
            if (isQr && Uri.TryCreate(text, UriKind.Absolute, out var link) && link.Scheme is "http" or "https")
                buttons.Add(("Open link", () => Process.Start(new ProcessStartInfo(link.ToString()) { UseShellExecute = true })));
            if (Settings.Notifications != NotificationStyle.None)
                ToastWindow.Show(isQr ? "QR code copied" : "Text copied", text.Length > 220 ? text[..220] + "…" : text, buttons: buttons, seconds: 2.5);
        }
        catch (Exception ex)
        {
            Log.Error("OCR failed", ex);
            ToastWindow.Show("Text recognition failed", ex.Message);
        }
    }

    public async Task<string?> Upload(BitmapSource bmp, double scale)
    {
        if (!Settings.S3.Enabled)
        {
            ToastWindow.Show("Upload isn't set up", "Add your S3 bucket in Settings → Upload.", buttons: [("Settings", ShowSettings)]);
            return null;
        }
        try
        {
            var bytes = ImageIO.EncodePng(bmp, scale);
            var name = FileNamer.Expand(Settings.FileNameTemplate, DateTime.Now) + ".png";
            var url = await Features.UploadAsync(Http, bytes, name, "image/png");
            ImageIO.CopyText(url);
            Notify("Link copied", NotificationKind.Upload, null, url);
            return url;
        }
        catch (Exception ex)
        {
            Log.Error("Upload failed", ex);
            ToastWindow.Show("Upload failed", ex.Message, seconds: 5);
            return null;
        }
    }

    public void Notify(string title, NotificationKind kind, string? path = null, string? detail = null)
    {
        if (Settings.Notifications == NotificationStyle.None) return;
        var buttons = new List<(string, Action)>();
        if (path != null && File.Exists(path)) buttons.Add(("Show in folder", () => Process.Start("explorer.exe", $"/select,\"{path}\"")));
        ToastWindow.Show(title, detail, buttons: buttons, seconds: 1.6);
    }

    /// <summary>Windows won't let a background app take focus; a brief topmost toggle brings the window forward.</summary>
    public static void BringToFront(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        bool top = w.Topmost;
        w.Topmost = true;
        w.Activate();
        Native.NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(w).Handle);
        w.Topmost = top;
    }

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
}
