using System.Windows;
using System.Windows.Media.Imaging;
using Loupix.Core.Imaging;
using Loupix.Imaging;

namespace Loupix.Editor;

/// <summary>An immutable raster layer: pixels plus a frozen WPF bitmap of them (tagged 96 DPI, so 1 px = 1 unit).</summary>
public sealed class EditorImage
{
    public PixelBuffer Pixels { get; }
    public BitmapSource Bitmap { get; }
    public int Width => Pixels.Width;
    public int Height => Pixels.Height;

    public EditorImage(PixelBuffer pixels)
    {
        Pixels = pixels;
        Bitmap = ImageIO.ToBitmapSource96(pixels);
    }
}

/// <summary>Backdrop ("beautify") settings: gradient background, padding, shadow, rounded corners.</summary>
public sealed record Backdrop(int Preset, double Padding, double Shadow, double Radius, string Aspect)
{
    public static Backdrop Default => new(0, 64, 0.5, 12, "Auto");
}

public sealed class DocumentSnapshot
{
    public required EditorImage Base { get; init; }
    public required Rect Canvas { get; init; }
    public required List<Annotation> Annotations { get; init; }
    public Backdrop? Backdrop { get; init; }
}

/// <summary>
/// The screenshot being edited: a base raster, an expandable canvas, vector annotations on top, and undo history.
/// All geometry is in base-image pixel coordinates.
/// </summary>
public sealed class EditorDocument
{
    private readonly Stack<DocumentSnapshot> _undo = new();
    private readonly Stack<DocumentSnapshot> _redo = new();
    private EditorImage? _uncropped;

    public EditorImage Base { get; private set; }
    /// <summary>DPI scale of the screen the capture came from (1.0 = 100%, 1.5 = 150%...).</summary>
    public double Scale { get; set; }
    /// <summary>Visible/exported area. Starts as the base image bounds and grows when captures are appended.</summary>
    public Rect Canvas { get; set; }
    public List<Annotation> Annotations { get; private set; } = new();
    public Backdrop? Backdrop { get; set; }
    public string? SourcePath { get; set; }
    public DateTime CreatedAt { get; } = DateTime.Now;
    public bool IsDirty { get; set; }

    public event Action? Changed;

    public EditorDocument(PixelBuffer pixels, double scale)
    {
        Base = new EditorImage(pixels);
        Scale = scale <= 0 ? 1 : scale;
        Canvas = new Rect(0, 0, pixels.Width, pixels.Height);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private DocumentSnapshot Snapshot() => new()
    {
        Base = Base,
        Canvas = Canvas,
        Annotations = Annotations.Select(a => a.Clone()).ToList(),
        Backdrop = Backdrop,
    };

    private void Restore(DocumentSnapshot s)
    {
        Base = s.Base;
        Canvas = s.Canvas;
        Annotations = s.Annotations.Select(a => a.Clone()).ToList();
        Backdrop = s.Backdrop;
    }

    /// <summary>Call before every user-visible change.</summary>
    public void Checkpoint()
    {
        _undo.Push(Snapshot());
        if (_undo.Count > 200) TrimUndo();
        _redo.Clear();
        IsDirty = true;
    }

    private void TrimUndo()
    {
        var keep = _undo.Take(150).Reverse().ToList();
        _undo.Clear();
        foreach (var s in keep) _undo.Push(s);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
        RaiseChanged();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
        RaiseChanged();
        return true;
    }

    public void RaiseChanged() => Changed?.Invoke();

    public Rect BaseBounds => new(0, 0, Base.Width, Base.Height);

    /// <summary>Crops the base image. Annotations keep their on-screen position relative to the image.</summary>
    public void Crop(RectI r)
    {
        r = r.ClampTo(Base.Width, Base.Height);
        if (r.IsEmpty) return;
        Checkpoint();
        _uncropped ??= Base;
        Base = new EditorImage(Base.Pixels.Crop(r.X, r.Y, r.Width, r.Height));
        foreach (var a in Annotations) a.Translate(new Vector(-r.X, -r.Y));
        Canvas = new Rect(0, 0, r.Width, r.Height);
        RaiseChanged();
    }

    public bool CanResetCrop => _uncropped != null;

    public void ResetCrop()
    {
        if (_uncropped == null) return;
        Checkpoint();
        Base = _uncropped;
        _uncropped = null;
        Canvas = BaseBounds;
        RaiseChanged();
    }

    /// <summary>Replaces the base raster (after a raster edit such as erase, rasterize or resize).</summary>
    public void ReplaceBase(PixelBuffer pixels, bool checkpoint = true)
    {
        if (checkpoint) Checkpoint();
        Base = new EditorImage(pixels);
        RaiseChanged();
    }

    /// <summary>Scales the base image and every annotation (the "resize image" popover).</summary>
    public void Resize(PixelBuffer resized)
    {
        Checkpoint();
        double sx = (double)resized.Width / Base.Width, sy = (double)resized.Height / Base.Height;
        Base = new EditorImage(resized);
        foreach (var a in Annotations) a.Scale(sx, sy);
        Canvas = new Rect(Canvas.X * sx, Canvas.Y * sy, Canvas.Width * sx, Canvas.Height * sy);
        Canvas = Rect.Union(Canvas, BaseBounds);
        RaiseChanged();
    }

    /// <summary>Grows the canvas so it contains <paramref name="r"/> (used by Add Capture and image overlays).</summary>
    public void ExpandCanvas(Rect r)
    {
        var c = Canvas;
        c.Union(r);
        Canvas = new Rect(Math.Floor(c.X), Math.Floor(c.Y), Math.Ceiling(c.Width), Math.Ceiling(c.Height));
    }

    public int NextCounterNumber()
    {
        var counters = Annotations.OfType<CounterAnnotation>().ToList();
        return counters.Count == 0 ? 1 : counters[^1].Number + 1;
    }
}
