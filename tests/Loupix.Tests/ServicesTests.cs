using System.IO;
using System.Windows.Input;
using Loupix.Imaging;
using Loupix.Services;

namespace Loupix.Tests;

public class FileNamerTests
{
    [Fact]
    public void Expands_date_tokens_and_timecode()
    {
        var t = new DateTime(2026, 11, 5, 14, 30, 15);
        var name = FileNamer.Expand("SCR-{yyyyMMdd}-{timecode}", t);
        Assert.Matches("^SCR-20261105-[a-z]{4}$", name);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "baaa")]
    [InlineData(23, 59, 59, 750, "urgh")]
    [InlineData(0, 0, 0, 250, "baab")]
    public void Timecode_matches_Shottr(int h, int m, int s, int ms, string expected) =>
        Assert.Equal(expected, FileNamer.TimeCode(new DateTime(2026, 1, 1, h, m, s, ms)));

    [Fact]
    public void Timecode_sorts_chronologically()
    {
        var day = new DateTime(2026, 1, 1);
        string? previous = null;
        for (int s = 0; s < 86400; s += 7)
        {
            var code = FileNamer.TimeCode(day.AddSeconds(s));
            if (previous != null) Assert.True(string.CompareOrdinal(previous, code) <= 0, $"{previous} > {code} at {s}s");
            previous = code;
        }
    }

    [Fact]
    public void Strips_characters_that_are_invalid_in_file_names()
    {
        var name = FileNamer.Expand("a:b/c*{yyyy}", new DateTime(2026, 1, 1));
        Assert.Equal("a-b-c-2026", name);
    }

    [Fact]
    public void Unique_path_appends_a_counter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "loupix-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = FileNamer.UniquePath(dir, "shot", ".png");
            File.WriteAllText(first, "x");
            var second = FileNamer.UniquePath(dir, "shot", ".png");
            Assert.EndsWith("shot-2.png", second);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+2", ModifierKeys.Control | ModifierKeys.Shift, Key.D2)]
    [InlineData("Alt+PrintScreen", ModifierKeys.Alt, Key.Snapshot)]
    [InlineData("Shift+Win+S", ModifierKeys.Windows | ModifierKeys.Shift, Key.S)]
    [InlineData("Ctrl+F12", ModifierKeys.Control, Key.F12)]
    public void Parses_and_formats(string text, ModifierKeys mods, Key key)
    {
        var hk = Hotkey.Parse(text);
        Assert.Equal(mods, hk.Modifiers);
        Assert.Equal(key, hk.Key);
        Assert.Equal(hk, Hotkey.Parse(hk.ToString()));
    }

    [Fact]
    public void Empty_text_is_an_empty_hotkey()
    {
        Assert.True(Hotkey.Parse("").IsEmpty);
        Assert.Equal("", default(Hotkey).ToString());
    }

    [Fact]
    public void Default_hotkeys_are_unique()
    {
        var used = AppSettings.DefaultHotkeys().Values.Where(v => v != "").ToList();
        Assert.Equal(used.Count, used.Distinct().Count());
    }
}
