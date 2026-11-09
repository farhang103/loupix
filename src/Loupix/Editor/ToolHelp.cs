namespace Loupix.Editor;

/// <summary>A tool explained for someone who's never used one: what you physically do, and one plain sentence.</summary>
/// <param name="Kind">The tool, or null for the "drag into another app" button.</param>
public sealed record ToolHelpEntry(ToolKind? Kind, string Name, string Key, string How, string Say);

public static class ToolHelp
{
    private const string Drag = "Press and drag", Click = "Click";

    private static ToolHelpEntry E(ToolKind kind, string how, string say)
    {
        var t = Tools.Get(kind);
        return new ToolHelpEntry(kind, t.Name, Services.Hotkey.KeyName(t.Shortcut), how, say);
    }

    public static readonly ToolHelpEntry DragOut = new(null, "Drag into another app", "", Drag,
        "Press this button and drag it into an email, a chat or a folder. Your picture lands there.");

    public static readonly IReadOnlyList<ToolHelpEntry> All =
    [
        E(ToolKind.Select, Drag, "Press on your picture and drag a box around the part you want. Press Enter to keep only that part."),
        E(ToolKind.Arrow, Drag, "Press where the arrow starts, drag to the thing you want to point at, then let go."),
        E(ToolKind.Rectangle, Drag, "Press at one corner and drag to the opposite corner to draw a box around something."),
        E(ToolKind.Oval, Drag, "Press and drag to draw a circle around something."),
        E(ToolKind.Line, Drag, "Press where the line starts, drag to where it ends, then let go."),
        E(ToolKind.Text, "Click, then type", "Click where you want words, type them, then press Enter."),
        E(ToolKind.Freehand, Drag, "Hold the mouse button down and move to draw, like a pen."),
        E(ToolKind.Highlighter, Drag, "Hold the mouse button down and sweep across words to mark them in yellow."),
        E(ToolKind.Spotlight, Drag, "Drag a box around the important part. Everything else gets darker."),
        E(ToolKind.Counter, Click, "Click each spot in order. The numbers 1, 2, 3 appear by themselves."),
        E(ToolKind.Pixelate, Drag, "Drag over anything private, like a password or a name, to hide it."),
        E(ToolKind.Erase, Drag, "Drag over something to wipe it out of the picture."),
        E(ToolKind.Magnifier, Drag, "Press on the spot you want bigger, then drag to place the zoomed-in circle."),
        E(ToolKind.Ruler, Drag, "Press at one edge and drag to the other to see the distance. Or point at a gap and press the arrow keys."),
        E(ToolKind.ColorPicker, Click, "Click any color in your picture to copy its color code."),
        E(ToolKind.Backdrop, "Click a style", "Choose a background from the bar. Your picture gets a nice frame for sharing."),
    ];

    public static ToolHelpEntry For(ToolKind kind) => All.First(e => e.Kind == kind);

    /// <summary>The tool guide's sections, in the order someone is likely to want them.</summary>
    public static readonly IReadOnlyList<(string Title, IReadOnlyList<ToolHelpEntry> Tools)> Groups =
    [
        ("Point things out", [For(ToolKind.Arrow), For(ToolKind.Rectangle), For(ToolKind.Oval), For(ToolKind.Line), For(ToolKind.Text), For(ToolKind.Freehand), For(ToolKind.Highlighter), For(ToolKind.Spotlight), For(ToolKind.Counter), For(ToolKind.Magnifier)]),
        ("Hide things", [For(ToolKind.Pixelate), For(ToolKind.Erase)]),
        ("Measure and pick colors", [For(ToolKind.Ruler), For(ToolKind.ColorPicker)]),
        ("Crop, frame and share", [For(ToolKind.Select), For(ToolKind.Backdrop), DragOut]),
    ];

    public static readonly IReadOnlyList<(string Name, string Say)> Buttons =
    [
        ("Copy", "Click to copy the picture, then paste it anywhere with Ctrl+V."),
        ("Save", "Click to save the picture to your screenshots folder."),
        ("Pin", "Click to keep the picture floating on top of your other windows."),
        ("OCR", "Click to copy the words in the picture as text you can paste."),
        ("Add capture", "Click to add another screenshot to the same picture."),
    ];
}
