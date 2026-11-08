using System.Windows.Input;
using System.Windows.Interop;
using static Loupix.Native.NativeMethods;

namespace Loupix.Services;

/// <summary>A key chord such as Ctrl+Shift+2.</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public bool IsEmpty => Key == Key.None;

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        Key.Snapshot => "PrintScreen",
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemTilde => "`",
        Key.OemPipe => "\\",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        _ => key.ToString(),
    };

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        var mods = ModifierKeys.None;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                default: key = ParseKey(raw); break;
            }
        }
        return new Hotkey(mods, key);
    }

    private static Key ParseKey(string s)
    {
        if (s.Length == 1 && char.IsDigit(s[0])) return Key.D0 + (s[0] - '0');
        if (s.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && s.Length == 4 && char.IsDigit(s[3])) return Key.NumPad0 + (s[3] - '0');
        return s switch
        {
            "PrintScreen" => Key.Snapshot,
            "=" => Key.OemPlus, "-" => Key.OemMinus, "[" => Key.OemOpenBrackets, "]" => Key.OemCloseBrackets,
            "," => Key.OemComma, "." => Key.OemPeriod, "/" => Key.OemQuestion, ";" => Key.OemSemicolon,
            "'" => Key.OemQuotes, "`" => Key.OemTilde, "\\" => Key.OemPipe, "Enter" => Key.Return, "Esc" => Key.Escape,
            "Backspace" => Key.Back,
            _ => Enum.TryParse<Key>(s, true, out var k) ? k : Key.None,
        };
    }
}

/// <summary>Registers system-wide hotkeys through RegisterHotKey on a message-only window.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, CaptureAction> _registered = new();
    private int _nextId = 1;

    public event Action<CaptureAction>? Pressed;

    /// <summary>Hotkeys that could not be registered (already taken by another app).</summary>
    public List<(CaptureAction Action, Hotkey Hotkey)> Conflicts { get; } = new();

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("LoupixHotkeys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public void Apply(Dictionary<CaptureAction, string> map)
    {
        UnregisterAll();
        Conflicts.Clear();
        foreach (var (action, text) in map)
        {
            var hk = Hotkey.Parse(text);
            if (hk.IsEmpty) continue;
            int id = _nextId++;
            int mods = MOD_NOREPEAT;
            if (hk.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
            if (hk.Modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
            if (hk.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
            if (hk.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;
            int vk = KeyInterop.VirtualKeyFromKey(hk.Key);
            if (RegisterHotKey(_source.Handle, id, mods, vk)) _registered[id] = action;
            else
            {
                Conflicts.Add((action, hk));
                Log.Info($"Hotkey {hk} for {action} is taken by another application.");
            }
        }
    }

    /// <summary>Temporarily releases all hotkeys (while the user records a new one).</summary>
    public void Suspend() => UnregisterAll();

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
