using System.Windows.Input;
using System.Windows.Media;

namespace Loupix.Editor;

public enum ToolKind
{
    Select, Arrow, Rectangle, Oval, Text, Freehand, Highlighter, Spotlight, Counter, Pixelate, Erase, Magnifier, Ruler, ColorPicker, Backdrop, Line,
}

/// <summary>A toolbar tool: name, keyboard shortcut, icon and default style.</summary>
public sealed record ToolInfo(ToolKind Kind, string Name, Key Shortcut, string Icon, string DefaultColor, int DefaultSize, string[] Variants)
{
    public string Tooltip => Shortcut == Key.None ? Name : $"{Name} ({Services.Hotkey.KeyName(Shortcut)})";
}

public static class Tools
{
    // Icons are 20x20 path geometries, stroked; an "F|" prefix means filled.
    public static readonly IReadOnlyList<ToolInfo> All =
    [
        new(ToolKind.Select, "Select / Crop", Key.V, "M4,4 L4,15 L7,12 L9.5,17 L11.5,16 L9,11 L13,11 Z", "#FF3B30", 3, []),
        new(ToolKind.Arrow, "Arrow", Key.A, "M4,16 L15,5 M8,5 L15,5 L15,12", "#FF3B30", 3, ["Tapered", "Classic", "Double", "Line", "Slim"]),
        new(ToolKind.Rectangle, "Rectangle", Key.R, "M3.5,5.5 L16.5,5.5 L16.5,14.5 L3.5,14.5 Z", "#FF3B30", 3, []),
        new(ToolKind.Oval, "Oval", Key.O, "M10,4.5 C14,4.5 16.5,7 16.5,10 C16.5,13 14,15.5 10,15.5 C6,15.5 3.5,13 3.5,10 C3.5,7 6,4.5 10,4.5 Z", "#FF3B30", 3, []),
        new(ToolKind.Line, "Line", Key.L, "M4,16 L16,4", "#FF3B30", 3, []),
        new(ToolKind.Text, "Text", Key.T, "M4,5 L16,5 M10,5 L10,16 M7.5,16 L12.5,16", "#FF3B30", 3, ["Label", "Plain", "Outlined"]),
        new(ToolKind.Freehand, "Freehand", Key.D, "M3,14 C5,8 7,8 8,11 C9,14 11,14 12,10 C13,6 15,5 17,6", "#FF3B30", 3, ["Even", "Variable"]),
        new(ToolKind.Highlighter, "Highlighter", Key.H, "M4,16 L16,16 M6,13 L12,4 L15,6 L9,15 Z", "#FFD60A", 3, ["Flat", "Round"]),
        new(ToolKind.Spotlight, "Spotlight", Key.S, "M10,3 L10,5 M3,10 L5,10 M15,10 L17,10 M5,5 L6.5,6.5 M15,5 L13.5,6.5 M7,10 C7,8 8.3,7 10,7 C11.7,7 13,8 13,10 C13,11.5 12,12 12,13.5 L8,13.5 C8,12 7,11.5 7,10 Z M8.5,16 L11.5,16", "#000000", 3, ["Rectangle", "Ellipse"]),
        new(ToolKind.Counter, "Step Counter", Key.N, "M10,3.5 C13.6,3.5 16.5,6.4 16.5,10 C16.5,13.6 13.6,16.5 10,16.5 C6.4,16.5 3.5,13.6 3.5,10 C3.5,6.4 6.4,3.5 10,3.5 Z M9,8 L10.5,7 L10.5,13", "#FF3B30", 3, []),
        new(ToolKind.Pixelate, "Pixelate / Blur", Key.P, "F|M3,3 h4.67 v4.67 h-4.67 Z M12.33,3 h4.67 v4.67 h-4.67 Z M7.67,7.67 h4.67 v4.67 h-4.67 Z M3,12.33 h4.67 v4.67 h-4.67 Z M12.33,12.33 h4.67 v4.67 h-4.67 Z", "#FF3B30", 3, ["Pixelate", "Blur", "Pixelate text"]),
        new(ToolKind.Erase, "Erase", Key.E, "M7,16 L16,16 M3.5,12 L10,5.5 L15,10.5 L9.5,16 L7.5,16 Z M7,9 L12,14", "#FF3B30", 3, ["Object", "Text only"]),
        new(ToolKind.Magnifier, "Magnifier", Key.M, "M8.5,3.5 C11.3,3.5 13.5,5.7 13.5,8.5 C13.5,11.3 11.3,13.5 8.5,13.5 C5.7,13.5 3.5,11.3 3.5,8.5 C3.5,5.7 5.7,3.5 8.5,3.5 Z M12.2,12.2 L16.5,16.5 M6.5,8.5 L10.5,8.5 M8.5,6.5 L8.5,10.5", "#FFFFFF", 3, ["2×", "3×", "4×"]),
        new(ToolKind.Ruler, "Ruler", Key.U, "M3,13 L13,3 L17,7 L7,17 Z M6,10 L8,12 M8.5,7.5 L10,9 M11,5 L13,7", "#FF2D78", 2, []),
        new(ToolKind.ColorPicker, "Color Picker", Key.I, "M12,4 L16,8 M14,6 L6,14 L4,16 M5,13 L7,15 M13,3 L17,7", "#FF3B30", 3, []),
        new(ToolKind.Backdrop, "Backdrop", Key.G, "M3,5 L17,5 L17,15 L3,15 Z M6,8 L14,8 L14,12 L6,12 Z", "#FF3B30", 3, []),
    ];

    public static ToolInfo Get(ToolKind kind) => All.First(t => t.Kind == kind);

    /// <summary>Builds the toolbar icon shape for a tool.</summary>
    public static System.Windows.Shapes.Path IconFor(ToolInfo t, Brush brush)
    {
        bool filled = t.Icon.StartsWith("F|");
        return new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(filled ? t.Icon[2..] : t.Icon),
            Stroke = filled ? null : brush,
            Fill = filled ? brush : null,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 20,
            Height = 20,
            Stretch = Stretch.None,
        };
    }

    public static readonly IReadOnlyList<Color> Palette =
    [
        Color.FromRgb(0xFF, 0x3B, 0x30), // red
        Color.FromRgb(0xFF, 0x95, 0x00), // orange
        Color.FromRgb(0xFF, 0xD6, 0x0A), // yellow
        Color.FromRgb(0x30, 0xD1, 0x58), // green
        Color.FromRgb(0x0A, 0x84, 0xFF), // blue
        Color.FromRgb(0xBF, 0x5A, 0xF2), // purple
        Color.FromRgb(0xFF, 0x2D, 0x78), // pink
        Color.FromRgb(0x1C, 0x1C, 0x1E), // black
        Color.FromRgb(0xFF, 0xFF, 0xFF), // white
    ];
}
