using static Loupix.Raster.Tests.TestImages;

namespace Loupix.Raster.Tests;

public class RulerTests
{
    private static readonly uint Page = Rgb(246, 247, 249);
    private static readonly uint BoxFill = Rgb(220, 230, 250);
    private static readonly uint BorderC = Rgb(60, 80, 120);

    [Fact]
    public void Measure_BetweenTwoBoxes_GivesTheGap()
    {
        var img = Solid(400, 200, Page);
        Fill(img, new RectI(50, 40, 50, 100), BoxFill);
        Fill(img, new RectI(180, 40, 70, 100), BoxFill);

        Assert.Equal(new Span(100, 180), Ruler.Measure(img, 120, 90, Axis.Horizontal));
        Assert.Equal(80, Ruler.Measure(img, 120, 90, Axis.Horizontal).Length);
    }

    [Fact]
    public void Measure_InsideBorderedBox_GivesInnerSize_BothAxes()
    {
        var img = Solid(400, 300, Page);
        var box = new RectI(100, 60, 100, 120);
        Fill(img, box, BoxFill);
        Border(img, box, 1, BorderC);

        Assert.Equal(new Span(101, 199), Ruler.Measure(img, 150, 100, Axis.Horizontal));
        Assert.Equal(new Span(61, 179), Ruler.Measure(img, 150, 100, Axis.Vertical));
    }

    [Fact]
    public void Measure_ToleratesSmallVariation_StopsAtImageEdge()
    {
        var img = Solid(300, 50, Page);
        for (int x = 0; x < 300; x += 3) Set(img, x, 25, Rgb(240, 241, 243)); // within tolerance 10
        Assert.Equal(new Span(0, 300), Ruler.Measure(img, 10, 25, Axis.Horizontal));
        Assert.Equal(new Span(0, 50), Ruler.Measure(img, 10, 25, Axis.Vertical));
    }

    [Fact]
    public void Measure_OutsideImage_IsEmpty()
    {
        var img = Solid(10, 10, Page);
        Assert.Equal(0, Ruler.Measure(img, -1, 3, Axis.Horizontal).Length);
        Assert.Equal(0, Ruler.MeasureOuter(img, 3, 99, Axis.Vertical).Length);
    }

    [Fact]
    public void MeasureOuter_IncludesBorderAndAntiAliasing()
    {
        var img = Solid(400, 300, Page);
        // AA ring at 100 and 199, 2px border at [101,103) and [197,199), fill inside.
        var outer = new RectI(100, 50, 100, 100);
        Border(img, outer, 1, Blend(Page, BorderC, 0.4));
        Border(img, outer.Inflate(-1, -1), 2, BorderC);
        Fill(img, outer.Inflate(-3, -3), BoxFill);

        Assert.Equal(new Span(103, 197), Ruler.Measure(img, 150, 100, Axis.Horizontal));
        Assert.Equal(new Span(100, 200), Ruler.MeasureOuter(img, 150, 100, Axis.Horizontal));
        Assert.Equal(new Span(50, 150), Ruler.MeasureOuter(img, 150, 100, Axis.Vertical));
    }

    [Fact]
    public void MeasureOuter_WhiteBoxWithGreyBorderOnWhitePage()
    {
        var img = Solid(300, 100, White);
        Border(img, new RectI(40, 20, 120, 60), 1, Rgb(200, 200, 200));
        Assert.Equal(new Span(41, 159), Ruler.Measure(img, 100, 50, Axis.Horizontal));
        Assert.Equal(new Span(40, 160), Ruler.MeasureOuter(img, 100, 50, Axis.Horizontal));
    }

    [Fact]
    public void MeasureOuter_FlatBoxWithoutBorder_EqualsInner()
    {
        var img = Solid(300, 100, Page);
        Fill(img, new RectI(60, 10, 100, 80), Rgb(30, 120, 220));
        Assert.Equal(new Span(60, 160), Ruler.MeasureOuter(img, 100, 50, Axis.Horizontal));
    }

    [Fact]
    public void MeasureOuter_BorderTouchingImageEdge_RunsToEdge()
    {
        var img = Solid(200, 100, BoxFill);
        Fill(img, new RectI(0, 0, 2, 100), BorderC);       // left border at the image edge
        Fill(img, new RectI(150, 0, 2, 100), BorderC);     // right border, then page
        Fill(img, new RectI(152, 0, 48, 100), Page);
        Assert.Equal(new Span(2, 150), Ruler.Measure(img, 50, 50, Axis.Horizontal));
        Assert.Equal(new Span(0, 152), Ruler.MeasureOuter(img, 50, 50, Axis.Horizontal));
    }
}
