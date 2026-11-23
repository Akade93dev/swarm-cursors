using Swarm.Core.Semantics;
using Swarm.Core.Visual;

namespace Swarm.Tests;

public class TextPlacementTests
{
    [Fact]
    public void 墨迹偏置时落位对齐到墨迹左上()
    {
        // 真实文字墨迹（绝对坐标）从 (122,224) 开始。
        var ink = new RectInt(122, 224, 380, 28);
        var fallback = new PlacedText(100, 200, 400, 60);

        var placed = TextPlacement.Align(ink, null, fallback, 1.5, 18.85, 14.5);

        Assert.Equal(122 / 1.5, placed.X, 3);
        // 墨迹是笔画上缘，行盒再上移半行距，字形才不会被行高整体压低。
        Assert.Equal(224 / 1.5 - (18.85 - 14.5) / 2, placed.Y, 3);
        // 宽度只放大不缩小：墨迹比控件窄时保持控件宽，避免文字被挤到一行里重排。
        Assert.Equal(400, placed.W, 3);
        Assert.Equal(60, placed.H, 3);
    }

    [Fact]
    public void 文字比控件宽时按墨迹宽度放大避免裁字()
    {
        var ink = new RectInt(0, 0, 48, 14);
        var fallback = new PlacedText(0, 0, 35, 16);

        var placed = TextPlacement.Align(ink, null, fallback, 1, 18.85, 14);

        Assert.Equal(48, placed.W, 3);
        Assert.Equal(16, placed.H, 3);
    }

    [Fact]
    public void 行矩形优先于墨迹决定落位()
    {
        // 行矩形是 provider 给的行级证据，比单个片段的墨迹上缘稳定。
        var ink = new RectInt(140, 230, 300, 20);
        var row = new RectInt(122, 224, 380, 28);
        var fallback = new PlacedText(100, 200, 400, 60);

        var placed = TextPlacement.Align(ink, row, fallback, 1.5, 18.85, 14.5);

        Assert.Equal(122 / 1.5, placed.X, 3);
        Assert.Equal(224 / 1.5 - (18.85 - 14.5) / 2, placed.Y, 3);
        Assert.Equal(400, placed.W, 3);
        Assert.Equal(60, placed.H, 3);
    }

    [Fact]
    public void 同一行片段不再使用各自墨迹纵向校正()
    {
        // 两个片段的墨迹上缘相差 6px，但同属一行，必须落在同一个 y。
        var fallbackA = new PlacedText(100, 200, 120, 19);
        var fallbackB = new PlacedText(230, 200, 120, 19);

        var a = TextPlacement.Align(new RectInt(100, 226, 110, 16), null, fallbackA, 1, 20.8, 19, useInkY: false);
        var b = TextPlacement.Align(new RectInt(232, 232, 110, 12), null, fallbackB, 1, 20.8, 19, useInkY: false);

        Assert.Equal(200, a.Y, 3);
        Assert.Equal(200, b.Y, 3);
        // 水平方向仍按墨迹校正。
        Assert.Equal(100, a.X, 3);
        Assert.Equal(232, b.X, 3);
    }

    [Fact]
    public void 同一视觉行的相邻片段共享行标记()
    {
        var boxes = new (string Id, double X, double Y, double W, double H)[]
        {
            ("a", 652, 100, 251, 19),
            ("b", 903, 100, 445, 19),
            ("c", 1350, 100, 100, 19),
            ("far", 652, 240, 100, 19),
            ("gap", 1600, 100, 60, 19),
            ("tall", 1460, 100, 50, 30),
        };

        var shared = TextPlacement.SharedLineIds(boxes);

        Assert.Contains("a", shared);
        Assert.Contains("b", shared);
        Assert.Contains("c", shared);
        // 换行后垂直不重叠。
        Assert.DoesNotContain("far", shared);
        // 间距 150px 超过 1.5 倍字高（约 28.5px）。
        Assert.DoesNotContain("gap", shared);
        // 字高差超过 20% 的不是同一行片段。
        Assert.DoesNotContain("tall", shared);
    }

    [Fact]
    public void 单行文本把分段换行折叠成空格()
    {
        Assert.Equal("行 1， 列 44", TextPlacement.CollapseLine("行 1，\r\n列 44"));
        Assert.Equal("Windows (CRLF)", TextPlacement.CollapseLine(" Windows\t(CRLF) "));
        Assert.Equal(string.Empty, TextPlacement.CollapseLine(null));
    }

    [Fact]
    public void 磅值换算后放不进控件时按像素处理()
    {
        // 记事本状态栏把 12（像素）填进磅属性，换算成 16 超过控件高 16。
        Assert.Equal(12, TextPlacement.NormalizeFontSize(16, 12, 16));
        // 文档正文的 11 磅换算成 14.67，控件高 922，正常保留。
        Assert.Equal(14.67, TextPlacement.NormalizeFontSize(14.67, 11, 922));
        // 没有控件高度信息时不擅自改动。
        Assert.Equal(16, TextPlacement.NormalizeFontSize(16, 12, 0));
    }

    [Fact]
    public void 没有有效墨迹时保持原落位()
    {
        var fallback = new PlacedText(12, 34, 100, 20);

        Assert.Equal(fallback, TextPlacement.Align(null, null, fallback, 1.5, 18.85, 14.5));
        Assert.Equal(fallback, TextPlacement.Align(new RectInt(1, 1, 2, 2), null, fallback, 1.5, 18.85, 14.5));
    }

    [Fact]
    public void 独行文本仍用墨迹校正纵向位置()
    {
        var ink = new RectInt(122, 224, 380, 28);
        var fallback = new PlacedText(100, 200, 400, 60);

        var placed = TextPlacement.Align(ink, null, fallback, 1.5, 18.85, 14.5, useInkY: true);

        Assert.Equal(224 / 1.5 - (18.85 - 14.5) / 2, placed.Y, 3);
    }
}
