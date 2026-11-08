using static Loupix.Raster.Tests.TestImages;

namespace Loupix.Raster.Tests;

public class SmartSelectTests
{
    private static readonly uint Page = Rgb(250, 250, 250);

    /// <summary>A white page with a textured object (every row/column through it is non-uniform).</summary>
    private static (PixelBuffer Img, RectI Obj) PageWithObject()
    {
        var img = Solid(400, 300, Page);
        var obj = new RectI(100, 50, 100, 70);
        Photo(img, obj, 30, 1);
        return (img, obj);
    }

    [Fact]
    public void AutoAdjust_TrimsEmptyMargins()
    {
        var (img, obj) = PageWithObject();
        var sel = RectI.FromLTRB(80, 30, 230, 150);
        Assert.Equal(obj, SmartSelect.AutoAdjust(img, sel));
    }

    [Fact]
    public void AutoAdjust_ExpandsToObjectWhenStartingInside()
    {
        var (img, obj) = PageWithObject();
        var sel = RectI.FromLTRB(120, 60, 150, 100);
        Assert.Equal(obj, SmartSelect.AutoAdjust(img, sel));
    }

    [Fact]
    public void AutoAdjust_MixedEdges()
    {
        var (img, obj) = PageWithObject();
        // Left/top outside (trim), right/bottom inside (grow).
        var sel = RectI.FromLTRB(70, 20, 160, 90);
        Assert.Equal(obj, SmartSelect.AutoAdjust(img, sel));
    }

    [Fact]
    public void AutoAdjust_StopsAtImageBorder()
    {
        var img = Solid(200, 100, Page);
        Photo(img, new RectI(0, 0, 80, 100), 30, 2);
        var sel = RectI.FromLTRB(10, 10, 40, 50);
        Assert.Equal(new RectI(0, 0, 80, 100), SmartSelect.AutoAdjust(img, sel));
    }

    [Fact]
    public void AutoAdjust_UniformArea_ReturnsSelectionUnchanged()
    {
        var img = Solid(200, 100, Page);
        var sel = new RectI(10, 10, 50, 50);
        Assert.Equal(sel, SmartSelect.AutoAdjust(img, sel));
    }

    [Fact]
    public void AutoAdjust_SnapsToBorderedBox()
    {
        var img = Solid(300, 200, Page);
        var box = new RectI(50, 40, 150, 90);
        Fill(img, box, Rgb(230, 236, 255));
        Border(img, box, 1, Rgb(80, 90, 140));
        var sel = RectI.FromLTRB(35, 25, 220, 150);
        Assert.Equal(box, SmartSelect.AutoAdjust(img, sel));
    }

    [Fact]
    public void AutoAdjustEdge_MovesOnlyThatEdge()
    {
        var (img, obj) = PageWithObject();
        var sel = RectI.FromLTRB(80, 30, 230, 150);
        Assert.Equal(RectI.FromLTRB(obj.X, 30, 230, 150), SmartSelect.AutoAdjustEdge(img, sel, Edge.Left));
        Assert.Equal(RectI.FromLTRB(80, obj.Y, 230, 150), SmartSelect.AutoAdjustEdge(img, sel, Edge.Top));
        Assert.Equal(RectI.FromLTRB(80, 30, obj.Right, 150), SmartSelect.AutoAdjustEdge(img, sel, Edge.Right));
        Assert.Equal(RectI.FromLTRB(80, 30, 230, obj.Bottom), SmartSelect.AutoAdjustEdge(img, sel, Edge.Bottom));

        var inside = RectI.FromLTRB(120, 60, 150, 100);
        Assert.Equal(RectI.FromLTRB(120, 60, obj.Right, 100), SmartSelect.AutoAdjustEdge(img, inside, Edge.Right));
    }

    [Fact]
    public void FloodBounds_LShapedRegion()
    {
        var img = Solid(300, 200, Page);
        var c = Rgb(40, 120, 200);
        Fill(img, new RectI(50, 30, 20, 100), c);
        Fill(img, new RectI(50, 110, 120, 20), c);
        Fill(img, new RectI(200, 30, 20, 20), c); // separate region, not connected
        Assert.Equal(RectI.FromLTRB(50, 30, 170, 130), SmartSelect.FloodBounds(img, 55, 40));
        Assert.Equal(new RectI(200, 30, 20, 20), SmartSelect.FloodBounds(img, 210, 40));
    }

    [Fact]
    public void FloodBounds_RespectsToleranceAndConnectivity()
    {
        var img = Solid(200, 100, Page);
        var c = Rgb(200, 60, 60);
        Fill(img, new RectI(20, 20, 60, 40), c);
        Fill(img, new RectI(80, 20, 40, 40), Rgb(204, 64, 57)); // within tolerance 6 of c
        Fill(img, new RectI(120, 20, 40, 40), Rgb(215, 60, 60)); // outside tolerance
        Set(img, 159, 59, c);                                    // diagonal-only neighbour of nothing relevant
        Assert.Equal(new RectI(20, 20, 100, 40), SmartSelect.FloodBounds(img, 30, 30));
        Assert.Equal(new RectI(20, 20, 140, 40), SmartSelect.FloodBounds(img, 30, 30, tolerance: 16));
    }

    [Fact]
    public void FloodBounds_RegionWithHoleAndSnake()
    {
        var img = Solid(120, 120, Page);
        var c = Rgb(10, 10, 10);
        // A spiral-ish snake forcing the fill to go up and down.
        Fill(img, new RectI(10, 10, 5, 100), c);
        Fill(img, new RectI(10, 105, 40, 5), c);
        Fill(img, new RectI(45, 20, 5, 90), c);
        Fill(img, new RectI(45, 20, 50, 5), c);
        Fill(img, new RectI(90, 20, 5, 80), c);
        Assert.Equal(RectI.FromLTRB(10, 10, 95, 110), SmartSelect.FloodBounds(img, 12, 12));
        // The page region (with the snake as a hole) spans the whole image.
        Assert.Equal(new RectI(0, 0, 120, 120), SmartSelect.FloodBounds(img, 30, 30));
    }

    [Fact]
    public void FloodBounds_OutsideImage_IsEmpty()
    {
        var img = Solid(10, 10, Page);
        Assert.True(SmartSelect.FloodBounds(img, 10, 0).IsEmpty);
    }
}
