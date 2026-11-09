using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Loupix.Services;

public enum CaptureAction
{
    Fullscreen, Area, Window, Scrolling, ScrollingReverse, RepeatArea, Delayed, Ocr, AddCapture, ShowEditor, LoadClipboard, OpenFile, Settings,
}

public enum AfterCapture { Editor, Thumbnail, Nothing }
public enum EscapeBehavior { Close, Copy, Save, CopyAndSave }
public enum SaveFormat { Png, Jpeg, Auto }
public enum WindowCaptureStyle { Shadow, NoShadow, SolidBackground, Wallpaper }
public enum NotificationStyle { Custom, System, None }
public enum ThumbnailCorner { BottomRight, BottomLeft, TopRight, TopLeft }

public sealed class S3Settings
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "https://s3.us-east-1.amazonaws.com";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    /// <summary>Stored encrypted with DPAPI (current user). Use <see cref="SecretStore"/>.</summary>
    public string SecretAccessKeyProtected { get; set; } = "";
    public bool PathStyle { get; set; } = true;
    public string PublicUrlBase { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public string Acl { get; set; } = "";
}

/// <summary>Last-used annotation style per tool, so the editor reopens with the user's choices.</summary>
public sealed class ToolStyle
{
    public string Color { get; set; } = "#FF3B30";
    public int Size { get; set; } = 2;
    public int Variant { get; set; }
    public bool Fill { get; set; }
    public bool HandDrawn { get; set; }
}

public sealed class AppSettings
{
    // Hotkeys, as display strings like "Ctrl+Shift+2". Empty = unassigned.
    public Dictionary<CaptureAction, string> Hotkeys { get; set; } = DefaultHotkeys();

    // After capture
    public AfterCapture AfterFullscreen { get; set; } = AfterCapture.Editor;
    public AfterCapture AfterArea { get; set; } = AfterCapture.Editor;
    public bool AutoCopy { get; set; }
    public bool AutoSave { get; set; }
    public bool ThumbnailPersist { get; set; }
    public ThumbnailCorner ThumbnailCorner { get; set; } = ThumbnailCorner.BottomRight;
    public int DelaySeconds { get; set; } = 3;
    public bool CaptureCursor { get; set; }

    // Editor
    public EscapeBehavior EscapeBehavior { get; set; } = EscapeBehavior.Copy;
    public bool DefaultZoom100 { get; set; }
    public bool EditorAlwaysOnTop { get; set; }
    /// <summary>Reopening the editor starts empty instead of bringing back the screenshot that was closed.</summary>
    public bool HidePreviousScreenshot { get; set; }
    public string ColorFormat { get; set; } = "Hex";
    public bool MeasureInPhysicalPixels { get; set; }
    public NotificationStyle Notifications { get; set; } = NotificationStyle.Custom;
    public bool SnapObjects { get; set; } = true;
    /// <summary>One annotation colour shared by every tool (Shottr keeps only the size per tool).</summary>
    public string AnnotationColor { get; set; } = "#FF3B30";
    /// <summary>loupix:// links from other apps: null = ask the first time, like Shottr's consent dialog.</summary>
    public bool? AllowLinks { get; set; }
    public Dictionary<string, ToolStyle> ToolStyles { get; set; } = new();

    // Saving
    public string SaveFolder { get; set; } = DefaultSaveFolder();
    public string FileNameTemplate { get; set; } = "SCR-{yyyyMMdd}-{timecode}";
    public SaveFormat SaveFormat { get; set; } = SaveFormat.Png;
    public int JpegQuality { get; set; } = 90;
    public bool DownscaleHiDpi { get; set; }

    // Window capture
    public WindowCaptureStyle WindowCaptureStyle { get; set; } = WindowCaptureStyle.Shadow;
    public string WindowBackgroundColor { get; set; } = "#FFFFFF";

    // OCR
    public string OcrLanguage { get; set; } = "";
    public bool OcrRemoveLineBreaks { get; set; }

    // Scrolling capture
    public int ScrollMaxHeight { get; set; } = 20000;
    public bool ScrollInvertDirection { get; set; }

    // Upload
    public S3Settings S3 { get; set; } = new();

    // Advanced
    public bool LaunchAtStartup { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    public bool ShowSplash { get; set; } = true;
    public bool FirstRunDone { get; set; }
    /// <summary>Set on a fresh install so the first capture gets its one-time tip. Existing users never see it.</summary>
    public bool FirstCaptureTipPending { get; set; }
    public bool AnimatedToolTips { get; set; } = true;

    // Updates
    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>The version the user answered "Later" to, so that the card doesn't keep coming back for it.</summary>
    public string DismissedUpdateVersion { get; set; } = "";

    public static Dictionary<CaptureAction, string> DefaultHotkeys() => new()
    {
        // Left Alt sits where Cmd does on a Mac, so these keep the hand shape of Shottr's ⇧⌘1 and ⇧⌘2; everything else starts unassigned.
        [CaptureAction.Fullscreen] = "Alt+Shift+1",
        [CaptureAction.Area] = "Alt+Shift+2",
        [CaptureAction.Ocr] = "Alt+Shift+3",
        [CaptureAction.Scrolling] = "",
        [CaptureAction.Window] = "",
        [CaptureAction.RepeatArea] = "",
        [CaptureAction.Delayed] = "",
        [CaptureAction.ScrollingReverse] = "",
        [CaptureAction.AddCapture] = "",
        [CaptureAction.ShowEditor] = "",
        [CaptureAction.LoadClipboard] = "",
    };

    /// <summary>Like Shottr, screenshots go to the Downloads folder by default.</summary>
    public static string DefaultSaveFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var path) == 0 && !string.IsNullOrEmpty(path))
                return path;
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);

    public ToolStyle StyleFor(string tool, Func<ToolStyle> fallback)
    {
        if (!ToolStyles.TryGetValue(tool, out var s)) ToolStyles[tool] = s = fallback();
        return s;
    }
}

public static class SettingsStore
{
    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loupix");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; private set; } = new();

    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (loaded != null)
                {
                    // Add any hotkey actions introduced after the file was written.
                    foreach (var (k, v) in AppSettings.DefaultHotkeys())
                        loaded.Hotkeys.TryAdd(k, v);
                    Current = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load settings", ex);
        }
    }

    public static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
        Changed?.Invoke();
    }
}

public static class Log
{
    private static readonly object Gate = new();
    private static string PathName => System.IO.Path.Combine(SettingsStore.Directory, "loupix.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(SettingsStore.Directory);
                var fi = new FileInfo(PathName);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
