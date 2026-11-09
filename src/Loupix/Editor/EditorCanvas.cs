using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Loupix.Core.Imaging;
using Loupix.Imaging;

namespace Loupix.Editor;

/// <summary>
/// The editing surface: draws the document with zoom/pan and handles every mouse/keyboard interaction
/// (crop marquee, drawing tools, object handles, ruler, guides, colour picking, text editing).
/// </summary>
public sealed class EditorCanvas : Canvas
{
    private EditorDocument _doc = null!;
    private ToolKind _tool = ToolKind.Select;

    // View
    private double _zoom = 1;            // device pixels per image pixel
    private Vector _pan;                  // view position (DIPs) of image origin
    private bool _userZoomed;

    // Interaction state
    private RectI? _marquee;
    private Annotation? _selected;
    private Annotation? _creating;
    private enum Drag { None, Pan, Marquee, MoveMarquee, MarqueeHandle, Create, MoveObject, ObjectHandle, ZoomRect, Measure }
    private Drag _drag;
    private Point _dragStartImg, _dragStartView, _lastImg;
    private Vector _panAtDragStart;
    private RectI _marqueeAtDragStart;
    private int _handleIndex = -1;
    private bool _dragMoved, _spaceDown, _zDown, _checkpointed;
    private Point? _mouseImg;
    private Point _mouseView;

    // Measurement / guides
    private MeasureAxis? _measureAxis;
    private bool _measureOuter;
    private (Point A, Point B, string Label)? _measurement;
    private MeasureAxis? _guide;

    // Text editing
    private TextBox? _textBox;
    private TextAnnotation? _editingText;

    public event Action? ViewChanged;
    public event Action? StateChanged;
    public event Action<Color?, Point?>? HoverChanged;
    public event Action<Color, string>? ColorPicked;
    public event Action<string>? Notify;
    public event Action<ToolKind>? ToolChanged;
    public event Action<KeyEventArgs>? UnhandledKey;

    public Func<ToolKind, Annotation, Annotation>? StyleNewAnnotation { get; set; }
    public bool MeasureInPhysicalPixels { get; set; }

    public EditorCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20));
        Cursor = Cursors.Cross;
        AllowDrop = true;
        SnapsToDevicePixels = true;
    }

    // ---------------- public API ----------------

    /// <summary>The document being edited; null shows the empty-state hints.</summary>
    public EditorDocument? Document
    {
        get => _doc;
        set
        {
            if (_doc != null) _doc.Changed -= OnDocChanged;
            StopEasingBackdrop();
            CommitText();
            _doc = value!;
            if (_doc != null) _doc.Changed += OnDocChanged;
            _selected = null;
            _marquee = null;
            _measurement = null;
            _measureAxis = null;
            _userZoomed = false;
            InvalidateVisual();
            StateChanged?.Invoke();
        }
    }

    private void OnDocChanged()
    {
        if (_selected != null && !_doc.Annotations.Contains(_selected)) _selected = null;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    public ToolKind Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            CommitText();
            _tool = value;
            if (value != ToolKind.Select) _marquee = null;
            _measureAxis = value == ToolKind.Ruler ? MeasureAxis.Horizontal : null;
            UpdateMeasurement();
            Cursor = value switch { ToolKind.Select => Cursors.Cross, ToolKind.Text => Cursors.IBeam, _ => Cursors.Cross };
            InvalidateVisual();
            ToolChanged?.Invoke(value);
            StateChanged?.Invoke();
        }
    }

    public Annotation? Selected => _selected;
    public RectI? Marquee => _marquee;
    public double Zoom => _zoom;
    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;
    private double ViewScale => _zoom / DpiScale;

    /// <summary>The selection is only the object just drawn, not one the user picked, so copying still means the whole image.</summary>
    public bool SelectionIsJustDrawn { get; private set; }

    public void Select(Annotation? a)
    {
        if (!ReferenceEquals(a, _selected)) SelectionIsJustDrawn = false;
        _selected = a;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    private void SelectDrawn(Annotation a)
    {
        Select(a);
        SelectionIsJustDrawn = true;
    }

    public void ClearMarquee()
    {
        _marquee = null;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    public void SetMarquee(RectI r)
    {
        _marquee = r.ClampTo(_doc.Base.Width, _doc.Base.Height);
        if (_marquee.Value.IsEmpty) _marquee = null;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    /// <summary>Applies a style change to the selected annotation (with undo) — used by the style bar.</summary>
    public void ApplyToSelected(Action<Annotation> change)
    {
        if (_selected == null) return;
        _doc.Checkpoint();
        change(_selected);
        _doc.RaiseChanged();
    }

    public Point ImageToView(Point p) => new(_pan.X + p.X * ViewScale, _pan.Y + p.Y * ViewScale);
    public Point ViewToImage(Point v) => new((v.X - _pan.X) / ViewScale, (v.Y - _pan.Y) / ViewScale);

    // ---------------- zoom ----------------

    public void ZoomToFit(bool allowUpscale = false)
    {
        if (_doc == null || ActualWidth < 10) return;
        var (zoom, center) = FitFor(DocumentRenderer.OuterBounds(_doc), allowUpscale);
        SetZoom(zoom, null);
        CenterOn(center);
        _userZoomed = false;
    }

    private (double Zoom, Point Center) FitFor(Rect b, bool allowUpscale)
    {
        var center = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        // Shottr opens captures that fit at exactly 100%, with no margin.
        if (!allowUpscale && b.Width / DpiScale <= ActualWidth + 0.5 && b.Height / DpiScale <= ActualHeight + 0.5) return (1, center);
        double margin = 24;
        double sx = (ActualWidth - margin * 2) / b.Width, sy = (ActualHeight - margin * 2) / b.Height;
        double z = Math.Min(sx, sy) * DpiScale;
        if (!allowUpscale) z = Math.Min(z, 1);
        return (z, center);
    }

    // ---------------- smooth backdrop changes ----------------

    private const double EaseTimeConstantMs = 55; // about a quarter of a second to settle, and it keeps up with a dragged slider
    private readonly Stopwatch _easeClock = new();
    private BackdropLook? _shownBackdrop;
    private Point _shownCenter;

    internal BackdropLook? ShownBackdrop => _shownBackdrop;

    /// <summary>Applies a backdrop change at once, while the picture and the view glide to it from wherever they are now.</summary>
    public void ChangeBackdropSmoothly(Action apply)
    {
        if (_doc == null) return;
        if (!SystemParameters.ClientAreaAnimation || !IsVisible || ActualWidth < 10)
        {
            apply();
            ZoomToFit();
            return;
        }
        bool alreadyEasing = _shownBackdrop != null;
        if (!alreadyEasing)
        {
            _shownBackdrop = BackdropLook.Of(_doc);
            _shownCenter = ViewToImage(new Point(ActualWidth / 2, ActualHeight / 2));
        }
        apply();
        _userZoomed = false;
        if (alreadyEasing) return;
        _easeClock.Restart();
        CompositionTarget.Rendering += OnEaseFrame;
    }

    private void OnEaseFrame(object? sender, EventArgs e)
    {
        if (_doc == null || _shownBackdrop is not { } shown) { StopEasingBackdrop(); return; }
        double k = 1 - Math.Exp(-_easeClock.Elapsed.TotalMilliseconds / EaseTimeConstantMs);
        _easeClock.Restart();

        var target = BackdropLook.Of(_doc);
        var (targetZoom, targetCenter) = FitFor(target.Outer, false);
        var next = BackdropLook.Lerp(shown, target, k);
        double zoom = _zoom + (targetZoom - _zoom) * k;
        var center = _shownCenter + (targetCenter - _shownCenter) * k;
        if (next.IsCloseTo(target) && Math.Abs(zoom - targetZoom) < 0.002 && (center - targetCenter).Length < 0.5)
        {
            StopEasingBackdrop();
            ZoomToFit();
            return;
        }

        _shownBackdrop = next;
        _shownCenter = center;
        SetZoom(zoom, null);
        _pan = new Vector(ActualWidth / 2 - center.X * ViewScale, ActualHeight / 2 - center.Y * ViewScale); // not snapped to pixels, so it glides
    }

    private void StopEasingBackdrop()
    {
        if (_shownBackdrop == null) return;
        _shownBackdrop = null;
        CompositionTarget.Rendering -= OnEaseFrame;
        InvalidateVisual();
    }

    public void ZoomTo100() { SetZoom(1, new Point(ActualWidth / 2, ActualHeight / 2)); _userZoomed = true; }

    public void ZoomBy(double factor, Point? viewAnchor = null)
    {
        SetZoom(_zoom * factor, viewAnchor ?? new Point(ActualWidth / 2, ActualHeight / 2));
        _userZoomed = true;
    }

    public void ZoomToRect(Rect imageRect, double maxZoom = 16)
    {
        if (imageRect.Width < 1 || imageRect.Height < 1) return;
        double margin = 30;
        double vs = Math.Min((ActualWidth - margin * 2) / imageRect.Width, (ActualHeight - margin * 2) / imageRect.Height);
        SetZoom(Math.Min(vs * DpiScale, maxZoom), null);
        CenterOn(new Point(imageRect.X + imageRect.Width / 2, imageRect.Y + imageRect.Height / 2));
        _userZoomed = true;
    }

    public void ZoomOnPoint(Point imagePoint, double zoom)
    {
        SetZoom(zoom, null);
        CenterOn(imagePoint);
        _userZoomed = true;
    }

    private static readonly double[] ZoomStops = [0.05, 0.1, 0.125, 0.25, 0.333, 0.5, 0.667, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64];

    public void ZoomStep(int dir, Point? anchor = null)
    {
        double target = dir > 0 ? ZoomStops.FirstOrDefault(z => z > _zoom * 1.01, 64) : ZoomStops.LastOrDefault(z => z < _zoom * 0.99, 0.05);
        SetZoom(target, anchor ?? new Point(ActualWidth / 2, ActualHeight / 2));
        _userZoomed = true;
    }

    private void SetZoom(double zoom, Point? viewAnchor)
    {
        zoom = Math.Clamp(zoom, 0.02, 64);
        if (viewAnchor is Point a)
        {
            var img = ViewToImage(a);
            _zoom = zoom;
            _pan = new Vector(a.X - img.X * ViewScale, a.Y - img.Y * ViewScale);
        }
        else _zoom = zoom;
        RenderOptions.SetBitmapScalingMode(this, _zoom >= 1.5 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        SnapPan();
        RepositionTextBox();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    private void CenterOn(Point imagePoint)
    {
        _pan = new Vector(ActualWidth / 2 - imagePoint.X * ViewScale, ActualHeight / 2 - imagePoint.Y * ViewScale);
        SnapPan();
        RepositionTextBox();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    private void SnapPan()
    {
        double d = DpiScale;
        _pan = new Vector(Math.Round(_pan.X * d) / d, Math.Round(_pan.Y * d) / d);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_doc != null && !_userZoomed) ZoomToFit();
    }

    // ---------------- rendering ----------------

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_doc == null) { DrawEmptyState(dc); return; }
        double vs = ViewScale;

        dc.PushTransform(new MatrixTransform(vs, 0, 0, vs, _pan.X, _pan.Y));
        var rc = new RenderContext { Document = _doc, ViewScale = vs, Editing = _editingText, EasedBackdrop = _shownBackdrop };
        DocumentRenderer.Render(dc, rc, showCanvasPattern: true);
        if (_creating != null && !_doc.Annotations.Contains(_creating))
        {
            if (_creating is SpotlightAnnotation sp)
            {
                var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(_doc.Canvas), sp.Shape());
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(sp.Level * 25), 0, 0, 0)), null, dim);
            }
            else _creating.Render(dc, rc);
        }
        dc.Pop();

        DrawOverlays(dc);
    }

    public string EmptyHint { get; set; } = "";

    private void DrawEmptyState(DrawingContext dc)
    {
        var lines = EmptyHint.Split('\n');
        double y = ActualHeight / 2 - lines.Length * 13;
        for (int i = 0; i < lines.Length; i++)
        {
            var ft = new FormattedText(lines[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"),
                i == 0 ? 16 : 13, new SolidColorBrush(i == 0 ? Color.FromRgb(0xE6, 0xE6, 0xEA) : Color.FromRgb(0x9A, 0x9A, 0xA0)), DpiScale);
            dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, y));
            y += ft.Height + (i == 0 ? 10 : 4);
        }
    }

    private static readonly Pen HandlePen = MakeFrozenPen(Color.FromRgb(0x0A, 0x84, 0xFF), 1.5);
    private static readonly Pen MarqueePen = MakeFrozenPen(Colors.White, 1);
    private static readonly Pen MarqueeDash = MakeDash();
    private static readonly Brush Dim = MakeFrozenBrush(Color.FromArgb(110, 0, 0, 0));
    private static readonly Brush Accent = MakeFrozenBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
    private static readonly Brush LabelBg = MakeFrozenBrush(Color.FromArgb(220, 20, 20, 22));

    private static Pen MakeFrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }
    private static Brush MakeFrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen MakeDash() { var p = new Pen(Brushes.Black, 1) { DashStyle = new DashStyle([4, 4], 0) }; p.Freeze(); return p; }

    private void DrawOverlays(DrawingContext dc)
    {
        // Crop marquee: dim outside, dashed border, handles, size label.
        if (_marquee is RectI m)
        {
            var r = ViewRect(m);
            var all = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            dc.DrawGeometry(Dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(r)));
            var snapped = new Rect(Math.Round(r.X) + 0.5, Math.Round(r.Y) + 0.5, Math.Round(r.Width), Math.Round(r.Height));
            dc.DrawRectangle(null, MarqueePen, snapped);
            dc.DrawRectangle(null, MarqueeDash, snapped);
            foreach (var h in MarqueeHandles(r)) DrawHandle(dc, h, square: true);
            DrawLabel(dc, SizeText(m.Width, m.Height), new Point(r.X + r.Width / 2, r.Bottom + 14), center: true);

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && _drag == Drag.None)
            {
                var adj = RasterOps.AutoAdjust(_doc.Base.Pixels, m);
                var ar = ViewRect(adj);
                var preview = new Pen(Accent, 1) { DashStyle = DashStyles.Dash };
                dc.DrawRectangle(null, preview, ar);
            }
        }

        // Selected annotation handles.
        if (_selected != null && _editingText == null)
        {
            foreach (var h in _selected.Handles) DrawHandle(dc, ImageToView(h), square: false);
            if (_selected.Handles.Count == 0)
            {
                var b = _selected.Bounds;
                var vr = new Rect(ImageToView(b.TopLeft), ImageToView(b.BottomRight));
                vr.Inflate(4, 4);
                dc.DrawRectangle(null, new Pen(Accent, 1) { DashStyle = DashStyles.Dash }, vr);
            }
        }

        // Live ruler measurement.
        if (_measurement is var (a, b2, label))
        {
            var va = ImageToView(a); var vb = ImageToView(b2);
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x2D, 0x78)), 1);
            dc.DrawLine(pen, va, vb);
            var d = vb - va;
            var n = Math.Abs(d.X) > Math.Abs(d.Y) ? new Vector(0, 5) : new Vector(5, 0);
            dc.DrawLine(pen, va - n, va + n);
            dc.DrawLine(pen, vb - n, vb + n);
            DrawLabel(dc, label, _mouseView + new Vector(14, 18), center: false, bg: MakeFrozenBrush(Color.FromRgb(0xFF, 0x2D, 0x78)));
        }

        // Guide line following the mouse while Alt+S / Alt+D is held.
        if (_guide is MeasureAxis g && _mouseImg is Point mp)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), 1) { DashStyle = DashStyles.Dash };
            var p = ImageToView(new Point(Math.Floor(mp.X) + 0.5, Math.Floor(mp.Y) + 0.5));
            if (g == MeasureAxis.Vertical) dc.DrawLine(pen, new Point(p.X, 0), new Point(p.X, ActualHeight));
            else dc.DrawLine(pen, new Point(0, p.Y), new Point(ActualWidth, p.Y));
        }

        // Zoom-rect while Z-dragging.
        if (_drag == Drag.ZoomRect)
        {
            var r = new Rect(_dragStartView, _mouseView);
            dc.DrawRectangle(MakeFrozenBrush(Color.FromArgb(40, 10, 132, 255)), new Pen(Accent, 1), r);
        }

        // Pixel grid at very high zoom.
        if (_zoom >= 12) DrawPixelGrid(dc);
    }

    private void DrawPixelGrid(DrawingContext dc)
    {
        var tl = ViewToImage(new Point(0, 0));
        var br = ViewToImage(new Point(ActualWidth, ActualHeight));
        var pen = MakeFrozenPen(Color.FromArgb(40, 128, 128, 128), 1 / DpiScale);
        for (int x = (int)Math.Max(0, Math.Floor(tl.X)); x <= Math.Min(_doc.Base.Width, Math.Ceiling(br.X)); x++)
        {
            double vx = ImageToView(new Point(x, 0)).X;
            dc.DrawLine(pen, new Point(vx, 0), new Point(vx, ActualHeight));
        }
        for (int y = (int)Math.Max(0, Math.Floor(tl.Y)); y <= Math.Min(_doc.Base.Height, Math.Ceiling(br.Y)); y++)
        {
            double vy = ImageToView(new Point(0, y)).Y;
            dc.DrawLine(pen, new Point(0, vy), new Point(ActualWidth, vy));
        }
    }

    private void DrawHandle(DrawingContext dc, Point p, bool square)
    {
        if (square) dc.DrawRectangle(Brushes.White, HandlePen, new Rect(p.X - 4, p.Y - 4, 8, 8));
        else dc.DrawEllipse(Brushes.White, HandlePen, p, 5, 5);
    }

    private void DrawLabel(DrawingContext dc, string text, Point at, bool center, Brush? bg = null)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11.5, Brushes.White, DpiScale);
        double w = ft.Width + 12, h = ft.Height + 4;
        double x = center ? at.X - w / 2 : at.X, y = at.Y - h / 2;
        x = Math.Clamp(x, 2, Math.Max(2, ActualWidth - w - 2));
        y = Math.Clamp(y, 2, Math.Max(2, ActualHeight - h - 2));
        dc.DrawRoundedRectangle(bg ?? LabelBg, null, new Rect(x, y, w, h), 4, 4);
        dc.DrawText(ft, new Point(x + 6, y + 2));
    }

    public string SizeText(double w, double h)
    {
        double s = MeasureInPhysicalPixels ? 1 : _doc.Scale;
        return $"{Fmt(w / s)} × {Fmt(h / s)}";
    }

    public string Length(double px)
    {
        double s = MeasureInPhysicalPixels ? 1 : _doc.Scale;
        return Fmt(px / s);
    }

    private static string Fmt(double v) => Math.Abs(v - Math.Round(v)) < 0.01 ? Math.Round(v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);

    private Rect ViewRect(RectI r) => new(ImageToView(new Point(r.X, r.Y)), ImageToView(new Point(r.Right, r.Bottom)));

    private static Point[] MarqueeHandles(Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        return [r.TopLeft, new(cx, r.Top), r.TopRight, new(r.Right, cy), r.BottomRight, new(cx, r.Bottom), r.BottomLeft, new(r.Left, cy)];
    }

    // ---------------- hit testing ----------------

    private double HitTolerance => 4 / ViewScale;

    private Annotation? AnnotationAt(Point img)
    {
        for (int i = _doc.Annotations.Count - 1; i >= 0; i--)
            if (_doc.Annotations[i].HitTest(img, HitTolerance)) return _doc.Annotations[i];
        return null;
    }

    private int HandleAt(Point view)
    {
        if (_selected == null) return -1;
        var hs = _selected.Handles;
        for (int i = 0; i < hs.Count; i++)
            if ((ImageToView(hs[i]) - view).Length <= 8) return i;
        return -1;
    }

    private int MarqueeHandleAt(Point view)
    {
        if (_marquee is not RectI m) return -1;
        var hs = MarqueeHandles(ViewRect(m));
        for (int i = 0; i < hs.Length; i++)
            if ((hs[i] - view).Length <= 8) return i;
        return -1;
    }

    /// <summary>Which marquee edge the view point lies on (for click-to-auto-adjust one edge).</summary>
    private SelectionEdge? MarqueeEdgeAt(Point view)
    {
        if (_marquee is not RectI m) return null;
        var r = ViewRect(m);
        const double t = 4;
        if (view.Y > r.Top && view.Y < r.Bottom)
        {
            if (Math.Abs(view.X - r.Left) <= t) return SelectionEdge.Left;
            if (Math.Abs(view.X - r.Right) <= t) return SelectionEdge.Right;
        }
        if (view.X > r.Left && view.X < r.Right)
        {
            if (Math.Abs(view.Y - r.Top) <= t) return SelectionEdge.Top;
            if (Math.Abs(view.Y - r.Bottom) <= t) return SelectionEdge.Bottom;
        }
        return null;
    }

    // ---------------- mouse ----------------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_doc == null) return;
        var view = e.GetPosition(this);
        var img = ViewToImage(view);
        _dragStartView = view;
        _dragStartImg = img;
        _lastImg = img;
        _dragMoved = false;
        _checkpointed = false;

        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _spaceDown && _drag == Drag.None))
        {
            BeginDrag(Drag.Pan);
            _panAtDragStart = _pan;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        if (_editingText != null)
        {
            CommitText();
            if (_tool == ToolKind.Text) return;
        }

        if (_zDown)
        {
            BeginDrag(Drag.ZoomRect);
            return;
        }

        // Imprint guide or measurement.
        if (_guide is MeasureAxis g)
        {
            ImprintGuide(g, img);
            return;
        }
        if (_tool == ToolKind.Ruler)
        {
            BeginDrag(Drag.Measure); // a click still imprints the edge-to-edge measurement, on mouse up
            return;
        }
        if (_measurement != null && _measureAxis != null)
        {
            ImprintMeasurement();
            _measureAxis = null;
            _measurement = null;
            return;
        }

        if (e.ClickCount == 2 && AnnotationAt(img) is TextAnnotation dbl)
        {
            Select(dbl);
            BeginTextEdit(dbl);
            return;
        }

        // Handles of the selected object take priority in every tool.
        int h = HandleAt(view);
        if (h >= 0)
        {
            _handleIndex = h;
            BeginDrag(Drag.ObjectHandle);
            return;
        }

        if (_tool == ToolKind.ColorPicker)
        {
            PickColorAt(img, darkest: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            return;
        }

        var hit = AnnotationAt(img);
        bool drawingTool = _tool is not (ToolKind.Select or ToolKind.Ruler or ToolKind.Backdrop);

        if (hit != null && (!drawingTool || hit == _selected || _tool == ToolKind.Text && hit is TextAnnotation || hit.Kind == KindFor(_tool)))
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                _doc.Checkpoint();
                _checkpointed = true;
                hit = hit.Clone();
                hit.Seed = Random.Shared.Next();
                _doc.Annotations.Add(hit);
            }
            else
            {
                // Bring to front when selected.
                _doc.Annotations.Remove(hit);
                _doc.Annotations.Add(hit);
            }
            _marquee = null;
            Select(hit);
            if (_tool == ToolKind.Text && hit is TextAnnotation t && e.ClickCount == 1 && _selected == t && _drag == Drag.None)
            {
                // Single click on a text label with the text tool edits it.
                BeginDrag(Drag.MoveObject);
                return;
            }
            BeginDrag(Drag.MoveObject);
            return;
        }

        if (_tool == ToolKind.Select)
        {
            Select(null);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _doc.BaseBounds.Contains(img))
            {
                SetMarquee(RasterOps.FloodBounds(_doc.Base.Pixels, (int)img.X, (int)img.Y));
                return;
            }
            int mh = MarqueeHandleAt(view);
            if (mh >= 0)
            {
                _handleIndex = mh;
                _marqueeAtDragStart = _marquee!.Value;
                BeginDrag(Drag.MarqueeHandle);
                return;
            }
            if (_marquee is RectI cur && cur.Contains((int)img.X, (int)img.Y) && MarqueeEdgeAt(view) == null)
            {
                _marqueeAtDragStart = cur;
                BeginDrag(Drag.MoveMarquee);
                return;
            }
            if (MarqueeEdgeAt(view) != null)
            {
                _marqueeAtDragStart = _marquee!.Value;
                BeginDrag(Drag.MarqueeHandle);
                _handleIndex = -2; // edge click: decided on mouse up
                return;
            }
            _marquee = null;
            BeginDrag(Drag.Marquee);
            return;
        }

        if (_tool is ToolKind.Ruler or ToolKind.Backdrop)
        {
            Select(null);
            return;
        }

        // Start creating a new annotation.
        Select(null);
        _marquee = null;
        _creating = CreateFor(_tool, img);
        if (_creating == null) return;
        if (_creating is CounterAnnotation or TextAnnotation)
        {
            // Placed on mouse up (text supports click-drag for its pointer).
        }
        BeginDrag(Drag.Create);
    }

    private static string KindFor(ToolKind t) => t switch
    {
        ToolKind.Arrow or ToolKind.Line => "arrow",
        ToolKind.Rectangle => "rect",
        ToolKind.Oval => "oval",
        ToolKind.Text => "text",
        ToolKind.Freehand => "freehand",
        ToolKind.Highlighter => "highlighter",
        ToolKind.Spotlight => "spotlight",
        ToolKind.Counter => "counter",
        ToolKind.Pixelate or ToolKind.Erase => "effect",
        ToolKind.Magnifier => "magnifier",
        _ => "",
    };

    private void BeginDrag(Drag d)
    {
        _drag = d;
        CaptureMouse();
    }

    private Annotation? CreateFor(ToolKind tool, Point p)
    {
        Annotation? a = tool switch
        {
            ToolKind.Arrow => new ArrowAnnotation { Start = p, End = p },
            ToolKind.Line => new ArrowAnnotation { Start = p, End = p, Variant = ArrowAnnotation.Line },
            ToolKind.Rectangle => new RectangleAnnotation { P1 = p, P2 = p },
            ToolKind.Oval => new EllipseAnnotation { P1 = p, P2 = p },
            ToolKind.Spotlight => new SpotlightAnnotation { P1 = p, P2 = p },
            ToolKind.Pixelate => new EffectAnnotation { P1 = p, P2 = p, Mode = EffectMode.Pixelate },
            ToolKind.Erase => new EffectAnnotation { P1 = p, P2 = p, Mode = EffectMode.Erase },
            ToolKind.Freehand => new FreehandAnnotation(),
            ToolKind.Highlighter => new HighlighterAnnotation { Start = p, End = p },
            ToolKind.Counter => new CounterAnnotation { Center = p },
            ToolKind.Text => new TextAnnotation { Position = p },
            ToolKind.Magnifier => new MagnifierAnnotation { Source = p, Target = p, SourceRadius = 18 * _doc.Scale },
            _ => null,
        };
        if (a == null) return null;
        a = StyleNewAnnotation?.Invoke(tool, a) ?? a;
        if (a is FreehandAnnotation f) f.AddPoint(p);
        if (a is EffectAnnotation ef)
        {
            // Variants: Pixelate tool = pixelate/blur/pixelate-text; Erase tool = object/text-only.
            if (tool == ToolKind.Pixelate) { ef.Mode = ef.Variant == 1 ? EffectMode.Blur : EffectMode.Pixelate; ef.TextOnly = ef.Variant == 2; }
            else { ef.Mode = EffectMode.Erase; ef.TextOnly = ef.Variant == 1; }
        }
        if (a is CounterAnnotation c) c.Number = _doc.NextCounterNumber();
        return a;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_doc == null) return;
        var view = e.GetPosition(this);
        var img = ViewToImage(view);
        _mouseView = view;
        _mouseImg = img;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if ((view - _dragStartView).Length > 2) _dragMoved = true;

        switch (_drag)
        {
            case Drag.Pan:
                _pan = _panAtDragStart + (view - _dragStartView);
                _userZoomed = true;
                SnapPan();
                RepositionTextBox();
                ViewChanged?.Invoke();
                break;
            case Drag.Marquee:
                {
                    var a = _dragStartImg; var b = img;
                    if (shift)
                    {
                        double s = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                        b = new Point(a.X + Math.Sign(b.X - a.X) * s, a.Y + Math.Sign(b.Y - a.Y) * s);
                    }
                    var r = RectI.FromPoints((int)Math.Round(a.X), (int)Math.Round(a.Y), (int)Math.Round(b.X), (int)Math.Round(b.Y)).ClampTo(_doc.Base.Width, _doc.Base.Height);
                    _marquee = r.IsEmpty ? null : r;
                    StateChanged?.Invoke();
                    break;
                }
            case Drag.MoveMarquee:
                {
                    var d = img - _dragStartImg;
                    var m = _marqueeAtDragStart;
                    int nx = Math.Clamp(m.X + (int)Math.Round(d.X), 0, _doc.Base.Width - m.Width);
                    int ny = Math.Clamp(m.Y + (int)Math.Round(d.Y), 0, _doc.Base.Height - m.Height);
                    _marquee = m with { X = nx, Y = ny };
                    StateChanged?.Invoke();
                    break;
                }
            case Drag.MarqueeHandle when _handleIndex >= 0:
                {
                    var m = _marqueeAtDragStart;
                    int l = m.X, t = m.Y, r = m.Right, bt = m.Bottom;
                    int px = (int)Math.Round(img.X), py = (int)Math.Round(img.Y);
                    switch (_handleIndex)
                    {
                        case 0: l = px; t = py; break;
                        case 1: t = py; break;
                        case 2: r = px; t = py; break;
                        case 3: r = px; break;
                        case 4: r = px; bt = py; break;
                        case 5: bt = py; break;
                        case 6: l = px; bt = py; break;
                        case 7: l = px; break;
                    }
                    var nr = RectI.FromPoints(l, t, r, bt).ClampTo(_doc.Base.Width, _doc.Base.Height);
                    if (!nr.IsEmpty) _marquee = nr;
                    StateChanged?.Invoke();
                    break;
                }
            case Drag.Create when _creating != null:
                UpdateCreating(img, shift, view);
                break;
            case Drag.MoveObject when _selected != null:
                {
                    if (!_dragMoved) break;
                    if (!_checkpointed) { _doc.Checkpoint(); _checkpointed = true; }
                    var d = img - _lastImg;
                    _selected.Translate(d);
                    _lastImg = img;
                    RepositionTextBox();
                    break;
                }
            case Drag.ObjectHandle when _selected != null:
                if (!_checkpointed) { _doc.Checkpoint(); _checkpointed = true; }
                _selected.MoveHandle(_handleIndex, img, shift);
                break;
            case Drag.None:
                UpdateHoverCursor(view, img);
                break;
            case Drag.Measure when _dragMoved:
                var (from, to, length) = MeasureAnnotation.FromDrag(_dragStartImg, img);
                _measurement = (from, to, Length(length));
                break;
        }

        if (_measureAxis != null && _drag != Drag.Measure) UpdateMeasurement();
        ReportHover(img);
        InvalidateVisual();
    }

    private void UpdateCreating(Point img, bool shift, Point view)
    {
        if (_spaceDown)
        {
            // Hold space while drawing to move the object being drawn.
            _creating!.Translate(img - _lastImg);
            _lastImg = img;
            return;
        }
        _lastImg = img;
        switch (_creating)
        {
            case ArrowAnnotation ar:
                {
                    var end = shift ? Constrain(_dragStartImg, img) : img;
                    // Ctrl while drawing reverses the arrow (head at the press point).
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { ar.Start = end; ar.End = _dragStartImg; }
                    else { ar.Start = _dragStartImg; ar.End = end; }
                    break;
                }
            case BoxAnnotation box:
                box.DragTo(img, shift);
                break;
            case FreehandAnnotation f:
                f.AddPoint(img);
                break;
            case HighlighterAnnotation hl:
                hl.End = shift ? new Point(img.X, hl.Start.Y) : img;
                break;
            case MagnifierAnnotation mg:
                mg.Target = img;
                break;
            case TextAnnotation:
            case CounterAnnotation:
                break;
        }
    }

    private static Point Constrain(Point origin, Point p)
    {
        var v = p - origin;
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return origin + new Vector(Math.Cos(angle) * v.Length, Math.Sin(angle) * v.Length);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_doc == null) return;
        var view = e.GetPosition(this);
        var img = ViewToImage(view);
        var drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();

        switch (drag)
        {
            case Drag.Measure when _measurement != null:
                ImprintMeasurement();
                break;
            case Drag.Marquee when !_dragMoved:
                _marquee = null;
                break;
            case Drag.MarqueeHandle when _handleIndex == -2 && !_dragMoved:
                if (MarqueeEdgeAt(view) is SelectionEdge edge && _marquee is RectI m)
                    SetMarquee(RasterOps.AutoAdjustEdge(_doc.Base.Pixels, m, edge));
                break;
            case Drag.ZoomRect:
                if (_dragMoved) ZoomToRect(new Rect(_dragStartImg, img), 64);
                else ZoomOnPoint(img, Math.Min(64, Math.Max(_zoom * 2, 2)));
                break;
            case Drag.Create when _creating != null:
                FinishCreating(img);
                break;
            case Drag.MoveObject when _selected is TextAnnotation t && !_dragMoved && _tool == ToolKind.Text:
                BeginTextEdit(t);
                break;
            case Drag.MoveObject or Drag.ObjectHandle:
                if (_checkpointed)
                {
                    if (_selected != null) GrowCanvasFor(_selected);
                    _doc.RaiseChanged();
                }
                break;
        }
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    private void FinishCreating(Point img)
    {
        var a = _creating!;
        _creating = null;
        switch (a)
        {
            case TextAnnotation t:
                if (_dragMoved)
                {
                    // Click-drag: pointer tip at the press point, label where the mouse was released.
                    t.Tip = t.Variant == TextAnnotation.Bubble ? _dragStartImg : null;
                    t.Position = img;
                }
                _doc.Checkpoint();
                _doc.Annotations.Add(t);
                SelectDrawn(t);
                BeginTextEdit(t, isNew: true);
                return;
            case MagnifierAnnotation mg when !_dragMoved:
                mg.Target = mg.Source + new Vector(mg.TargetRadius * 1.6, -mg.TargetRadius * 1.6);
                break;
            case FreehandAnnotation f when f.Points.Count == 1:
                break; // single click draws a dot
            case CounterAnnotation:
                break;
            default:
                if (a.IsDegenerate) { InvalidateVisual(); return; }
                break;
        }
        if (a is BoxAnnotation b) b.Normalize();
        _doc.Checkpoint();
        _doc.Annotations.Add(a);
        GrowCanvasFor(a);
        _doc.RaiseChanged();
        SelectDrawn(a);
    }

    /// <summary>Objects drawn or moved past the image edge enlarge the canvas to keep them.</summary>
    private void GrowCanvasFor(Annotation a)
    {
        if (a is EffectAnnotation or SpotlightAnnotation or MeasureAnnotation) return;
        var b = a.Bounds;
        if (b.IsEmpty || _doc.Canvas.Contains(b)) return;
        _doc.ExpandCanvas(b);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_doc == null) return;
        var view = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomBy(Math.Pow(1.0018, e.Delta), view);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _pan += new Vector(e.Delta * 0.6, 0);
            _userZoomed = true;
            SnapPan(); RepositionTextBox(); InvalidateVisual(); ViewChanged?.Invoke();
        }
        else
        {
            _pan += new Vector(0, e.Delta * 0.6);
            _userZoomed = true;
            SnapPan(); RepositionTextBox(); InvalidateVisual(); ViewChanged?.Invoke();
        }
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseImg = null;
        HoverChanged?.Invoke(null, null);
        InvalidateVisual();
    }

    private void UpdateHoverCursor(Point view, Point img)
    {
        if (_spaceDown) { Cursor = Cursors.Hand; return; }
        if (_zDown) { Cursor = Cursors.Arrow; return; }
        if (HandleAt(view) >= 0) { Cursor = Cursors.SizeAll; return; }
        if (_tool == ToolKind.Select)
        {
            int mh = MarqueeHandleAt(view);
            if (mh >= 0) { Cursor = mh is 0 or 4 ? Cursors.SizeNWSE : mh is 2 or 6 ? Cursors.SizeNESW : mh is 1 or 5 ? Cursors.SizeNS : Cursors.SizeWE; return; }
            if (MarqueeEdgeAt(view) != null) { Cursor = Cursors.Hand; return; }
            if (_marquee is RectI m && m.Contains((int)img.X, (int)img.Y)) { Cursor = Cursors.SizeAll; return; }
        }
        var hit = AnnotationAt(img);
        if (hit != null && (_tool == ToolKind.Select || hit == _selected)) { Cursor = Cursors.SizeAll; return; }
        Cursor = _tool switch { ToolKind.Text => Cursors.IBeam, ToolKind.ColorPicker => Cursors.Pen, _ => Cursors.Cross };
    }

    private void ReportHover(Point img)
    {
        int x = (int)Math.Floor(img.X), y = (int)Math.Floor(img.Y);
        if (_doc.Base.Pixels.Contains(x, y))
        {
            var (r, g, b) = _doc.Base.Pixels.GetRgb(x, y);
            HoverChanged?.Invoke(Color.FromRgb(r, g, b), new Point(x, y));
        }
        else HoverChanged?.Invoke(null, null);
    }

    // ---------------- measurement, guides, colour ----------------

    private void ImprintMeasurement()
    {
        if (_measurement is not var (a, b, label)) return;
        _doc.Checkpoint();
        _doc.Annotations.Add(new MeasureAnnotation { Start = a, End = b, Label = label, Color = Color.FromRgb(0xFF, 0x2D, 0x78) });
        _doc.RaiseChanged();
    }

    private void UpdateMeasurement()
    {
        if (_measureAxis is not MeasureAxis axis || _mouseImg is not Point p) { _measurement = null; return; }
        int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y);
        if (!_doc.Base.Pixels.Contains(x, y)) { _measurement = null; return; }
        var (s, e) = RasterOps.Measure(_doc.Base.Pixels, x, y, axis, _measureOuter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        if (axis == MeasureAxis.Horizontal)
            _measurement = (new Point(s, y + 0.5), new Point(e, y + 0.5), Length(e - s));
        else
            _measurement = (new Point(x + 0.5, s), new Point(x + 0.5, e), Length(e - s));
    }

    private void ImprintGuide(MeasureAxis g, Point img)
    {
        _doc.Checkpoint();
        var c = _doc.Canvas;
        double x = Math.Floor(img.X) + 0.5, y = Math.Floor(img.Y) + 0.5;
        var m = g == MeasureAxis.Vertical
            ? new MeasureAnnotation { Start = new Point(x, c.Top), End = new Point(x, c.Bottom), IsGuide = true, Color = Color.FromRgb(0x0A, 0x84, 0xFF) }
            : new MeasureAnnotation { Start = new Point(c.Left, y), End = new Point(c.Right, y), IsGuide = true, Color = Color.FromRgb(0x0A, 0x84, 0xFF) };
        _doc.Annotations.Add(m);
        _doc.RaiseChanged();
    }

    private void PickColorAt(Point img, bool darkest)
    {
        int x = (int)Math.Floor(img.X), y = (int)Math.Floor(img.Y);
        var px = _doc.Base.Pixels;
        if (!px.Contains(x, y)) return;
        Color c;
        string what;
        if (darkest)
        {
            // Darkest pixel of the surrounding 20×20 area: the colour of the text under the cursor.
            int best = int.MaxValue; c = Colors.Black;
            for (int yy = Math.Max(0, y - 10); yy < Math.Min(px.Height, y + 10); yy++)
                for (int xx = Math.Max(0, x - 10); xx < Math.Min(px.Width, x + 10); xx++)
                {
                    var (r, g, b) = px.GetRgb(xx, yy);
                    int l = r * 299 + g * 587 + b * 114;
                    if (l < best) { best = l; c = Color.FromRgb(r, g, b); }
                }
            what = "Text color";
        }
        else
        {
            var (r, g, b) = px.GetRgb(x, y);
            c = Color.FromRgb(r, g, b);
            what = "Color";
        }
        ColorPicked?.Invoke(c, what);
    }

    public Color? AverageColorOfMarquee()
    {
        if (_marquee is not RectI m) return null;
        var px = _doc.Base.Pixels;
        long r = 0, g = 0, b = 0; long n = 0;
        int step = Math.Max(1, (int)Math.Sqrt((double)m.Width * m.Height / 250000));
        for (int y = m.Y; y < m.Bottom; y += step)
            for (int x = m.X; x < m.Right; x += step)
            {
                var (cr, cg, cb) = px.GetRgb(x, y);
                r += cr; g += cg; b += cb; n++;
            }
        return n == 0 ? null : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    // ---------------- keyboard ----------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_textBox != null && _textBox.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Escape) { CommitText(); e.Handled = true; }
            else if (e.Key == Key.Return && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { CommitText(); e.Handled = true; }
            else if (e.Key == Key.Tab && _mouseImg is Point mp)
            {
                // Tab while typing inserts the hex colour under the cursor.
                int x = (int)mp.X, y = (int)mp.Y;
                if (_doc.Base.Pixels.Contains(x, y))
                {
                    var (r, g, b) = _doc.Base.Pixels.GetRgb(x, y);
                    _textBox.SelectedText = $"#{r:X2}{g:X2}{b:X2}";
                    _textBox.CaretIndex = _textBox.SelectionStart + _textBox.SelectionLength;
                    _textBox.SelectionLength = 0;
                }
                e.Handled = true;
            }
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (_doc == null) { UnhandledKey?.Invoke(e); return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        e.Handled = true;

        // Guides: Alt+S vertical, Alt+D horizontal (shown while held).
        if (alt && !ctrl && key is Key.S or Key.D)
        {
            _guide = key == Key.S ? MeasureAxis.Vertical : MeasureAxis.Horizontal;
            InvalidateVisual();
            return;
        }

        // Style shortcuts on the selected object.
        if (_selected != null)
        {
            if (ctrl && key == Key.Return && _selected is FreehandAnnotation or HighlighterAnnotation or ArrowAnnotation)
            {
                // Ctrl+Enter cycles the variant: freehand stroke variability, highlighter caps, arrow style.
                _doc.Checkpoint();
                int count = _selected is ArrowAnnotation ? 5 : 2;
                _selected.Variant = (_selected.Variant + 1) % count;
                _doc.RaiseChanged();
                return;
            }
            if (alt && key is Key.Up or Key.Down && _selected is FreehandAnnotation fh)
            {
                _doc.Checkpoint();
                fh.Smoothness = Math.Clamp(fh.Smoothness + (key == Key.Up ? 1 : -1), 0, 8);
                _doc.RaiseChanged();
                Notify?.Invoke($"Smoothness {fh.Smoothness}");
                return;
            }
        }

        switch (key)
        {
            case Key.Space:
                _spaceDown = true;
                Cursor = Cursors.Hand;
                return;
            case Key.Z when !ctrl:
                _zDown = true;
                Cursor = Cursors.Arrow;
                return;
            case Key.Tab:
                if (_mouseImg is Point mp) PickColorAt(mp, darkest: shift);
                return;
            case Key.Escape:
                if (_measureAxis != null && _tool != ToolKind.Ruler) { _measureAxis = null; _measurement = null; InvalidateVisual(); return; }
                if (_selected != null) { Select(null); return; }
                if (_marquee != null) { ClearMarquee(); return; }
                if (_tool != ToolKind.Select) { Tool = ToolKind.Select; return; }
                break;
            case Key.Delete or Key.Back when _selected != null:
                _doc.Checkpoint();
                _doc.Annotations.Remove(_selected);
                Select(null);
                _doc.RaiseChanged();
                return;
            case Key.Delete or Key.Back when _marquee is RectI del:
                {
                    // Object removal: erase the selected area.
                    var pb = _doc.Base.Pixels.Clone();
                    RasterOps.Erase(pb, del);
                    _doc.ReplaceBase(pb);
                    ClearMarquee();
                    Notify?.Invoke("Erased");
                    return;
                }
            case Key.Return when _marquee is RectI crop:
                _doc.Crop(crop);
                _marquee = null;
                ZoomToFit();
                return;
            case Key.A when !ctrl && _marquee is RectI adj:
                SetMarquee(RasterOps.AutoAdjust(_doc.Base.Pixels, adj));
                return;
            case Key.B when !ctrl && _marquee is RectI blur:
                {
                    var pb = _doc.Base.Pixels.Clone();
                    RasterOps.Blur(pb, blur, Math.Max(2, (int)(8 * _doc.Scale)));
                    _doc.ReplaceBase(pb);
                    ClearMarquee();
                    return;
                }
            case Key.C when !ctrl && _marquee != null:
                if (AverageColorOfMarquee() is Color avg) ColorPicked?.Invoke(avg, "Average color");
                return;
            case Key.OemOpenBrackets or Key.OemCloseBrackets when _marquee is RectI grow:
                {
                    int d = (shift ? 10 : 1) * (key == Key.OemCloseBrackets ? 1 : -1);
                    var g = grow.Inflate(d, d).ClampTo(_doc.Base.Width, _doc.Base.Height);
                    if (!g.IsEmpty) SetMarquee(g);
                    return;
                }
            case Key.Q when !ctrl && _marquee is RectI q:
                ZoomOnPoint(new Point(q.X, q.Y), Math.Max(8, _zoom));
                return;
            case Key.W when !ctrl && _marquee is RectI w:
                ZoomOnPoint(new Point(w.Right, w.Bottom), Math.Max(8, _zoom));
                return;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                HandleArrow(key, ctrl, shift);
                return;
        }

        if (ctrl)
        {
            switch (key)
            {
                case Key.D1 or Key.NumPad1: ZoomToFit(true); return;
                case Key.D0 or Key.NumPad0: ZoomTo100(); return;
                case Key.D2 or Key.NumPad2:
                    if (_marquee is RectI zr) ZoomToRect(new Rect(zr.X, zr.Y, zr.Width, zr.Height));
                    else if (_selected != null) ZoomToRect(_selected.Bounds);
                    return;
                case Key.OemPlus or Key.Add: ZoomStep(1); return;
                case Key.OemMinus or Key.Subtract: ZoomStep(-1); return;
                case Key.D when _selected != null:
                    _doc.Checkpoint();
                    var dup = _selected.Clone();
                    dup.Translate(new Vector(12 * _doc.Scale, 12 * _doc.Scale));
                    _doc.Annotations.Add(dup);
                    Select(dup);
                    _doc.RaiseChanged();
                    return;
            }
        }

        // Digits on a selected object.
        if (!ctrl && !alt && DigitOf(key) is int digit && _selected != null)
        {
            _doc.Checkpoint();
            switch (_selected)
            {
                case SpotlightAnnotation sp when digit > 0: sp.Level = digit; break;
                case CounterAnnotation ct:
                    ct.Number = _counterTyping && ct.Number < 10 ? ct.Number * 10 + digit : digit;
                    _counterTyping = true;
                    break;
                case ImageAnnotation im: im.Opacity = digit == 0 ? 1 : digit / 10.0; break;
                default: _doc.Undo(); e.Handled = false; UnhandledKey?.Invoke(e); return;
            }
            _doc.RaiseChanged();
            return;
        }
        _counterTyping = false;

        // Tool shortcuts (single letters, no modifiers).
        if (!ctrl && !alt)
        {
            var t = Tools.All.FirstOrDefault(t => t.Shortcut == key);
            if (t != null)
            {
                Tool = t.Kind;
                return;
            }
        }

        e.Handled = false;
        UnhandledKey?.Invoke(e);
    }

    private bool _counterTyping;

    private static int? DigitOf(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => k - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => k - Key.NumPad0,
        _ => null,
    };

    private void HandleArrow(Key key, bool ctrl, bool shift)
    {
        int step = shift ? 10 : 1;
        int dx = key == Key.Left ? -1 : key == Key.Right ? 1 : 0;
        int dy = key == Key.Up ? -1 : key == Key.Down ? 1 : 0;
        if (_selected != null)
        {
            _doc.Checkpoint();
            _selected.Translate(new Vector(dx * step, dy * step));
            _doc.RaiseChanged();
            return;
        }
        if (_marquee is RectI m)
        {
            // Arrows nudge; Ctrl+arrows resize.
            var r = ctrl ? m with { Width = Math.Max(1, m.Width + dx * step), Height = Math.Max(1, m.Height + dy * step) } : m.Offset(dx * step, dy * step);
            var clamped = r.ClampTo(_doc.Base.Width, _doc.Base.Height);
            if (!clamped.IsEmpty) SetMarquee(clamped);
            return;
        }
        // Ruler: measure between edges at the cursor.
        _measureAxis = dx != 0 ? MeasureAxis.Horizontal : MeasureAxis.Vertical;
        _measureOuter = shift;
        UpdateMeasurement();
        InvalidateVisual();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Space) { _spaceDown = false; Cursor = Cursors.Cross; }
        if (key == Key.Z) { _zDown = false; Cursor = Cursors.Cross; }
        if (key is Key.S or Key.D or Key.LeftAlt or Key.RightAlt) { _guide = null; InvalidateVisual(); }
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) { UpdateMeasurement(); InvalidateVisual(); }
    }

    // ---------------- text editing ----------------

    public void BeginTextEdit(TextAnnotation t, bool isNew = false)
    {
        CommitText();
        _editingText = t;
        _textIsNew = isNew;
        _textBefore = t.Text;
        double fs = t.FontSize(_doc.Scale) * ViewScale;
        _textBox = new TextBox
        {
            Text = t.Text,
            FontFamily = new FontFamily(t.HandDrawn ? "Segoe Print" : "Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = Math.Max(1, fs),
            Foreground = new SolidColorBrush(t.Variant == TextAnnotation.Bubble ? Annotation.ContrastText(t.Color) : t.Color),
            CaretBrush = new SolidColorBrush(t.Variant == TextAnnotation.Bubble ? Annotation.ContrastText(t.Color) : t.Color),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            AcceptsTab = false,
            MinWidth = fs * 0.6,
        };
        _textBox.TextChanged += (_, _) =>
        {
            if (_editingText != null) { _editingText.Text = _textBox.Text; InvalidateVisual(); }
        };
        _textBox.LostKeyboardFocus += (_, ev) =>
        {
            if (ev.NewFocus is DependencyObject d && IsAncestorOf(d) && d != this) return;
            Dispatcher.BeginInvoke(new Action(CommitText), System.Windows.Threading.DispatcherPriority.Input);
        };
        Children.Add(_textBox);
        RepositionTextBox();
        _textBox.Focus();
        _textBox.SelectAll();
        InvalidateVisual();
    }

    private bool _textIsNew;
    private string _textBefore = "";

    private void RepositionTextBox()
    {
        if (_textBox == null || _editingText == null) return;
        double pad = _editingText.Padding(_doc.Scale);
        var p = ImageToView(new Point(_editingText.Position.X + pad, _editingText.Position.Y + pad));
        _textBox.FontSize = Math.Max(1, _editingText.FontSize(_doc.Scale) * ViewScale);
        SetLeft(_textBox, p.X - 2);
        SetTop(_textBox, p.Y);
    }

    public void CommitText()
    {
        if (_textBox == null || _editingText == null) return;
        var t = _editingText;
        var tb = _textBox;
        _textBox = null;
        _editingText = null;
        t.Text = tb.Text;
        Children.Remove(tb);
        if (string.IsNullOrWhiteSpace(t.Text))
        {
            _doc.Annotations.Remove(t);
            if (_selected == t) _selected = null;
        }
        else if (!_textIsNew && t.Text != _textBefore)
        {
            // Record the edit for undo: restore old text in a snapshot.
            var after = t.Text;
            t.Text = _textBefore;
            _doc.Checkpoint();
            t.Text = after;
        }
        _doc.RaiseChanged();
        Focus();
        InvalidateVisual();
    }

    public bool IsEditingText => _textBox != null;

    // ---------------- annotation clipboard ----------------

    private static Annotation? _clipboardAnnotation;

    public bool CopySelectedAnnotation()
    {
        if (_selected == null) return false;
        _clipboardAnnotation = _selected.Clone();
        return true;
    }

    public bool PasteAnnotation()
    {
        if (_clipboardAnnotation == null) return false;
        var a = _clipboardAnnotation.Clone();
        a.Translate(new Vector(14 * _doc.Scale, 14 * _doc.Scale));
        _clipboardAnnotation = a.Clone();
        _doc.Checkpoint();
        _doc.Annotations.Add(a);
        Select(a);
        _doc.RaiseChanged();
        return true;
    }

    public static bool HasAnnotationOnClipboard => _clipboardAnnotation != null;
    public static void ForgetAnnotationClipboard() => _clipboardAnnotation = null;

    /// <summary>Places an image on the canvas as a movable overlay (paste / drop).</summary>
    public void AddImageOverlay(System.Windows.Media.Imaging.BitmapSource bmp, Point? center = null)
    {
        var c = center ?? new Point(_doc.Canvas.X + _doc.Canvas.Width / 2, _doc.Canvas.Y + _doc.Canvas.Height / 2);
        var r = new Rect(c.X - bmp.PixelWidth / 2.0, c.Y - bmp.PixelHeight / 2.0, bmp.PixelWidth, bmp.PixelHeight);
        _doc.Checkpoint();
        var a = new ImageAnnotation { Image = bmp, P1 = r.TopLeft, P2 = r.BottomRight };
        _doc.Annotations.Add(a);
        _doc.ExpandCanvas(r);
        Select(a);
        _doc.RaiseChanged();
    }
}
