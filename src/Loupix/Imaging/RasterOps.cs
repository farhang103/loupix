using Loupix.Core.Imaging;
using Loupix.Raster;

namespace Loupix.Imaging;

public enum MeasureAxis { Horizontal, Vertical }
public enum SelectionEdge { Left, Top, Right, Bottom }

/// <summary>The app's single entry point for pixel algorithms, backed by Loupix.Raster.</summary>
public static class RasterOps
{
    public static void Pixelate(PixelBuffer img, RectI r, int block) => Effects.Pixelate(img, r, block);

    public static void PixelateText(PixelBuffer img, RectI r, int block) => Effects.PixelateText(img, r, block);

    /// <summary>Gaussian blur; <paramref name="radius"/> is the standard deviation in pixels.</summary>
    public static void Blur(PixelBuffer img, RectI r, int radius) => Effects.Blur(img, r, radius);

    /// <summary>Removes the area "as if it was never there" by filling it from its surroundings.</summary>
    public static void Erase(PixelBuffer img, RectI r) => Effects.Erase(img, r);

    /// <summary>Removes only text inside the area, keeping backgrounds, borders and shapes.</summary>
    public static void EraseText(PixelBuffer img, RectI r) => Effects.EraseText(img, r);

    public static RectI AutoAdjust(PixelBuffer img, RectI sel) => SmartSelect.AutoAdjust(img, sel);

    public static RectI AutoAdjustEdge(PixelBuffer img, RectI sel, SelectionEdge edge) =>
        SmartSelect.AutoAdjustEdge(img, sel, edge switch
        {
            SelectionEdge.Left => Edge.Left,
            SelectionEdge.Top => Edge.Top,
            SelectionEdge.Right => Edge.Right,
            _ => Edge.Bottom,
        });

    public static RectI FloodBounds(PixelBuffer img, int x, int y) => SmartSelect.FloodBounds(img, x, y);

    /// <summary>Ruler: the run of similar pixels through (x, y); <paramref name="outer"/> includes the element's border.</summary>
    public static (int Start, int End) Measure(PixelBuffer img, int x, int y, MeasureAxis axis, bool outer)
    {
        if (!img.Contains(x, y)) return (0, 0);
        var a = axis == MeasureAxis.Horizontal ? Axis.Horizontal : Axis.Vertical;
        var span = outer ? Ruler.MeasureOuter(img, x, y, a) : Ruler.Measure(img, x, y, a);
        return (span.Start, span.End);
    }

    public static PixelBuffer Resize(PixelBuffer src, int width, int height) => Resampler.Resize(src, width, height);
}
