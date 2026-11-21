using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class CoordinateConverterTests
{
    private static readonly RectInt Monitor = new(-1000, 0, 1920, 1080);
    private static readonly RectInt ClientScreen = new(640, 400, 640, 400);
    private static readonly LogicalSize Viewport = new(640.0 / 1.5, 400.0 / 1.5);

    [Fact]
    public void 物理像素按显示器原点与DPI转换为CSS坐标()
    {
        var result = CoordinateConverter.ToLogical(
            new RectInt(-900, 150, 150, 75),
            Monitor,
            1.5);
        Assert.Equal(100.0 / 1.5, result.X, 3);
        Assert.Equal(100, result.Y, 3);
        Assert.Equal(100, result.Width, 3);
        Assert.Equal(50, result.Height, 3);
    }

    [Fact]
    public void CSS坐标可还原到当前显示器物理像素()
    {
        var result = CoordinateConverter.ToPhysical(100, 50, new RectInt(-1000, 100, 1920, 1080), 1.25);
        Assert.Equal(-875, result.X);
        Assert.Equal(162, result.Y);
    }

    [Fact]
    public void 元素物理矩形换算为窗口客户区CSS坐标()
    {
        var physical = new RectInt(ClientScreen.X + 150, ClientScreen.Y + 300, 150, 75);
        var result = CoordinateConverter.ToClient(physical, ClientScreen, 1.5);
        Assert.Equal(100, result.X, 3);
        Assert.Equal(200, result.Y, 3);
        Assert.Equal(100, result.Width, 3);
        Assert.Equal(50, result.Height, 3);
    }

    [Fact]
    public void 客户区CSS坐标换算为屏幕物理像素并四舍五入()
    {
        var result = CoordinateConverter.ToScreen(100, 200, ClientScreen, 1.5);
        Assert.Equal(790, result.X);
        Assert.Equal(700, result.Y);
    }

    [Fact]
    public void 客户区左上角对应屏幕客户区原点()
    {
        var result = CoordinateConverter.ToScreen(0, 0, ClientScreen, 1.5);
        Assert.Equal(ClientScreen.X, result.X);
        Assert.Equal(ClientScreen.Y, result.Y);
    }

    [Fact]
    public void 非整数CSS坐标按最近物理像素取整()
    {
        var result = CoordinateConverter.ToScreen(0.34, 0.34, ClientScreen, 1.5);
        Assert.Equal(641, result.X);
        Assert.Equal(401, result.Y);
    }

    [Fact]
    public void CSS与物理坐标在多档DPI下往返误差不超过一个物理像素()
    {
        var origin = new RectInt(1920, 120, 800, 600);
        foreach (var dpi in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            foreach (var x in new[] { 0.0, 1.0, 13.0, 200.0, 799.0 })
            {
                foreach (var y in new[] { 0.0, 7.0, 99.0, 300.0, 599.0 })
                {
                    var screen = CoordinateConverter.ToScreen(x, y, origin, dpi);
                    var back = CoordinateConverter.ToClient(new RectInt(screen.X, screen.Y, 1, 1), origin, dpi);
                    Assert.InRange(Math.Abs(back.X - x), 0, 1 / dpi + 1e-9);
                    Assert.InRange(Math.Abs(back.Y - y), 0, 1 / dpi + 1e-9);
                }
            }
        }
    }

    [Fact]
    public void 显示器相对CSS在负坐标副屏上保持偏移()
    {
        var result = CoordinateConverter.ToMonitorCss(new RectInt(-900, 150, 150, 75), Monitor, 1.5);
        Assert.Equal(100.0 / 1.5, result.X, 3);
        Assert.Equal(100, result.Y, 3);
        Assert.Equal(100, result.Width, 3);
        Assert.Equal(50, result.Height, 3);
    }

    [Fact]
    public void 右侧副屏按显示器原点扣除水平偏移()
    {
        var result = CoordinateConverter.ToMonitorCss(new RectInt(2500, 100, 600, 400), new RectInt(1920, 0, 1920, 1080), 1.25);
        Assert.Equal(464, result.X, 3);
        Assert.Equal(80, result.Y, 3);
        Assert.Equal(480, result.Width, 3);
        Assert.Equal(320, result.Height, 3);
    }

    [Fact]
    public void 同一客户区CSS在混合DPI下映射到不同物理像素()
    {
        var at100 = CoordinateConverter.ToScreen(100, 100, ClientScreen, 1.0);
        var at150 = CoordinateConverter.ToScreen(100, 100, ClientScreen, 1.5);
        Assert.Equal(740, at100.X);
        Assert.Equal(500, at100.Y);
        Assert.Equal(790, at150.X);
        Assert.Equal(550, at150.Y);
    }

    [Fact]
    public void 客户区视口尺寸按DPI缩放()
    {
        var result = CoordinateConverter.ClientViewport(new RectInt(0, 0, 640, 400), 1.5);
        Assert.Equal(640.0 / 1.5, result.Width, 3);
        Assert.Equal(400.0 / 1.5, result.Height, 3);

        var native = CoordinateConverter.ClientViewport(new RectInt(0, 0, 640, 400), 1.0);
        Assert.Equal(640, native.Width, 3);
        Assert.Equal(400, native.Height, 3);
    }

    [Fact]
    public void 完全位于视口内的元素被接受()
    {
        Assert.True(CoordinateConverter.FitsViewport(new LogicalRect(10, 20, 100, 40), Viewport));
    }

    [Fact]
    public void 右下边界刚好贴边的元素被接受()
    {
        var rect = new LogicalRect(Viewport.Width - 100, Viewport.Height - 40, 100, 40);
        Assert.True(CoordinateConverter.FitsViewport(rect, Viewport));
    }

    [Fact]
    public void 右下方向一像素取整误差被容忍()
    {
        var rect = new LogicalRect(0, 0, Viewport.Width + 1, Viewport.Height + 1);
        Assert.True(CoordinateConverter.FitsViewport(rect, Viewport));
    }

    [Fact]
    public void 超出视口超过一像素的元素被拒绝()
    {
        Assert.False(CoordinateConverter.FitsViewport(new LogicalRect(-2, 0, 50, 50), Viewport));
        Assert.False(CoordinateConverter.FitsViewport(new LogicalRect(0, 0, Viewport.Width + 2, 50), Viewport));
        Assert.False(CoordinateConverter.FitsViewport(new LogicalRect(0, 0, 50, Viewport.Height + 2), Viewport));
    }

    [Fact]
    public void 零尺寸元素即使位置合法也被拒绝()
    {
        Assert.False(CoordinateConverter.FitsViewport(new LogicalRect(10, 10, 0, 50), Viewport));
        Assert.False(CoordinateConverter.FitsViewport(new LogicalRect(10, 10, 50, 0), Viewport));
    }

    [Fact]
    public void 客户区点包含左上角与右下边界()
    {
        Assert.True(CoordinateConverter.ContainsClientPoint(0, 0, Viewport));
        Assert.True(CoordinateConverter.ContainsClientPoint(Viewport.Width, Viewport.Height, Viewport));
        Assert.True(CoordinateConverter.ContainsClientPoint(Viewport.Width - 0.01, Viewport.Height - 0.01, Viewport));
    }

    [Fact]
    public void 客户区点拒绝越界与非法数值()
    {
        Assert.False(CoordinateConverter.ContainsClientPoint(-0.01, 10, Viewport));
        Assert.False(CoordinateConverter.ContainsClientPoint(10, -0.01, Viewport));
        Assert.False(CoordinateConverter.ContainsClientPoint(Viewport.Width + 0.01, 10, Viewport));
        Assert.False(CoordinateConverter.ContainsClientPoint(10, Viewport.Height + 0.01, Viewport));
        Assert.False(CoordinateConverter.ContainsClientPoint(double.NaN, 10, Viewport));
        Assert.False(CoordinateConverter.ContainsClientPoint(10, double.PositiveInfinity, Viewport));
    }

    [Fact]
    public void 非正或非有限DPI被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CoordinateConverter.ToLogical(new RectInt(0, 0, 1, 1), Monitor, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoordinateConverter.ToPhysical(1, 1, Monitor, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoordinateConverter.ToClient(new RectInt(0, 0, 1, 1), ClientScreen, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoordinateConverter.ToScreen(1, 1, ClientScreen, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoordinateConverter.ToMonitorCss(ClientScreen, Monitor, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoordinateConverter.ClientViewport(new RectInt(0, 0, 1, 1), double.PositiveInfinity));
    }

    [Fact]
    public void 元素裁剪按客户区物理矩形相交判断()
    {
        var client = new RectInt(640, 400, 640, 400);
        Assert.True(new RectInt(620, 380, 60, 60).Intersects(client));
        Assert.True(new RectInt(1270, 790, 60, 60).Intersects(client));
        Assert.False(new RectInt(1280, 400, 60, 60).Intersects(client));
        Assert.False(new RectInt(100, 100, 60, 60).Intersects(client));
        Assert.False(new RectInt(640, 400, 0, 50).Intersects(client));
    }
}
