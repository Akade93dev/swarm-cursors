using Swarm.Core.Capture;
using Swarm.Core.Semantics;
using Swarm.Core.Visual;

namespace Swarm.Tests;

public class CoordinateMapperTests
{
    private static readonly RectInt WorkArea = new(-1000, 40, 1920, 1040);

    public static TheoryData<uint, DpiAwareness> DpiAndAwareness => new()
    {
        { 96, DpiAwareness.Unaware },
        { 96, DpiAwareness.SystemAware },
        { 96, DpiAwareness.PerMonitorAware },
        { 120, DpiAwareness.Unaware },
        { 120, DpiAwareness.SystemAware },
        { 120, DpiAwareness.PerMonitorAware },
        { 144, DpiAwareness.Unaware },
        { 144, DpiAwareness.SystemAware },
        { 144, DpiAwareness.PerMonitorAware },
        { 192, DpiAwareness.Unaware },
        { 192, DpiAwareness.SystemAware },
        { 192, DpiAwareness.PerMonitorAware },
    };

    [Theory]
    [InlineData(96u, 1.0)]
    [InlineData(120u, 1.25)]
    [InlineData(144u, 1.5)]
    [InlineData(192u, 2.0)]
    public void 覆盖层CSS比例等于覆盖层DPI除以96(uint overlayDpi, double expected)
    {
        var mapper = new CoordinateMapper(WorkArea, overlayDpi);
        Assert.Equal(expected, mapper.CssScale, 6);
    }

    [Theory]
    [MemberData(nameof(DpiAndAwareness))]
    public void 物理矩形按work_area原点换算为覆盖层CSS(uint overlayDpi, DpiAwareness awareness)
    {
        _ = awareness;
        var mapper = new CoordinateMapper(WorkArea, overlayDpi);
        var scale = overlayDpi / 96.0;
        var physical = new RectInt(
            WorkArea.X + (int)Math.Round(160 * scale),
            WorkArea.Y + (int)Math.Round(96 * scale),
            (int)Math.Round(320 * scale),
            (int)Math.Round(128 * scale));

        var rect = mapper.ToCss(physical);

        Assert.Equal(160, rect.X, 6);
        Assert.Equal(96, rect.Y, 6);
        Assert.Equal(320, rect.Width, 6);
        Assert.Equal(128, rect.Height, 6);
    }

    [Theory]
    [MemberData(nameof(DpiAndAwareness))]
    public void 物理尺寸按覆盖层CSS比例换算(uint overlayDpi, DpiAwareness awareness)
    {
        _ = awareness;
        var mapper = new CoordinateMapper(WorkArea, overlayDpi);
        var size = mapper.ToCssSize((int)Math.Round(320 * (overlayDpi / 96.0)), (int)Math.Round(128 * (overlayDpi / 96.0)));

        Assert.Equal(320, size.Width, 6);
        Assert.Equal(128, size.Height, 6);
    }

    [Theory]
    [MemberData(nameof(DpiAndAwareness))]
    public void 字高按awareness换算为覆盖层CSS像素(uint overlayDpi, DpiAwareness awareness)
    {
        var mapper = new CoordinateMapper(WorkArea, overlayDpi);
        const double cssEm = 16.0;
        var physical = cssEm * (overlayDpi / 96.0);

        var result = mapper.ToCssEm(physical, awareness, overlayDpi);

        Assert.Equal(cssEm, result, 6);
    }

    [Fact]
    public void 逐显示器感知窗口按窗口DPI换算字高()
    {
        var mapper = new CoordinateMapper(WorkArea, 96);
        Assert.Equal(16, mapper.ToCssEm(24, DpiAwareness.PerMonitorAware, 144), 6);
    }

    [Fact]
    public void 位图拉伸窗口忽略窗口DPI按覆盖层基准换算字高()
    {
        var mapper = new CoordinateMapper(WorkArea, 144);
        Assert.Equal(16, mapper.ToCssEm(24, DpiAwareness.Unaware, 96), 6);
        Assert.Equal(16, mapper.ToCssEm(24, DpiAwareness.SystemAware, 96), 6);
    }

    [Fact]
    public void 左上角坐标映射到CSS原点()
    {
        var mapper = new CoordinateMapper(new RectInt(0, 40, 2560, 1560), 144);
        var rect = mapper.ToCss(new RectInt(0, 40, 24, 24));
        Assert.Equal(0, rect.X, 6);
        Assert.Equal(0, rect.Y, 6);
    }

    [Fact]
    public void 非正覆盖层DPI被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoordinateMapper(WorkArea, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoordinateMapper(default, 96));
    }

    [Fact]
    public void 非正窗口DPI或未定义的awareness被拒绝()
    {
        var mapper = new CoordinateMapper(WorkArea, 96);
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.ToCssEm(16, DpiAwareness.Unaware, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.ToCssEm(16, DpiAwareness.PerMonitorAware, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.ToCssEm(16, (DpiAwareness)99, 96));
    }
}
