using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class Ia2LineBuilderTests
{
    private static readonly RectInt Clip = new(0, 0, 3000, 2000);

    private static Ia2Glyph Glyph(int offset, int x, int y, int width = 20, int height = 29) =>
        new(offset, new RectInt(x, y, width, height));

    private static Ia2TextNode Node(string text, params Ia2Glyph[] glyphs) => new(41, text, glyphs);

    [Fact]
    public void 单行字符按推进聚成一行且宽度取非空白字形范围()
    {
        var node = Node("abc", Glyph(0, 100, 50), Glyph(1, 120, 50), Glyph(2, 140, 50));
        var line = Assert.Single(Ia2LineBuilder.Build(node, Clip));
        Assert.Equal("abc", line.Text);
        Assert.Equal(new RectInt(100, 50, 60, 29), line.Bounds);
    }

    [Fact]
    public void 同一行末尾两像素抖动不拆行()
    {
        var node = Node("ab", Glyph(0, 100, 1210), Glyph(1, 120, 1212));
        var line = Assert.Single(Ia2LineBuilder.Build(node, Clip));
        Assert.Equal("ab", line.Text);
    }

    [Fact]
    public void 纵向位移超过容差拆成两行()
    {
        var node = Node("ab", Glyph(0, 100, 763), Glyph(1, 120, 796));
        var lines = Ia2LineBuilder.Build(node, Clip);
        Assert.Equal(2, lines.Count);
        Assert.Equal("a", lines[0].Text);
        Assert.Equal("b", lines[1].Text);
    }

    [Fact]
    public void x明显回折视为换行()
    {
        var node = Node("ab", Glyph(0, 2179, 763), Glyph(1, 1407, 763));
        var lines = Ia2LineBuilder.Build(node, Clip);
        Assert.Equal(2, lines.Count);
        Assert.Equal(2179, lines[0].Bounds.X);
        Assert.Equal(1407, lines[1].Bounds.X);
    }

    [Fact]
    public void 代理对不会被切成半个字符()
    {
        // "A😀B" 的 UTF-16 长度为 4；第二行从代理对的高位开始。
        var text = "A😀B";
        var node = Node(text, Glyph(0, 0, 0), Glyph(1, 20, 0), Glyph(3, 60, 40));
        var lines = Ia2LineBuilder.Build(node, Clip);
        Assert.Equal(2, lines.Count);
        Assert.Equal("A😀", lines[0].Text);
        Assert.Equal("B", lines[1].Text);
        Assert.DoesNotContain('\uFFFD', string.Concat(lines.Select(line => line.Text)));
    }

    [Fact]
    public void 行两端的空白字符不参与矩形也不进入文本()
    {
        var node = Node(" a ", Glyph(0, 90, 0), Glyph(1, 110, 0, 12), Glyph(2, 130, 0));
        var line = Assert.Single(Ia2LineBuilder.Build(node, Clip));
        Assert.Equal("a", line.Text);
        Assert.Equal(new RectInt(110, 0, 12, 29), line.Bounds);
    }

    [Fact]
    public void 中心落在捕获矩形外的行被丢弃()
    {
        var node = Node("x", Glyph(0, 5000, 5000));
        Assert.Empty(Ia2LineBuilder.Build(node, new RectInt(0, 0, 800, 600)));
    }

    [Fact]
    public void 重复的位置与文本只保留一行()
    {
        var line = new Ia2Line("重复", new RectInt(10, 20, 40, 29), 41);
        var result = Ia2LineBuilder.Deduplicate(new[] { line, line });
        Assert.Single(result);
    }

    [Fact]
    public void 属性解析把磅值折算成CSS像素并保留颜色()
    {
        var style = Ia2TextAttributes.Parse(
            "background-color:rgb(245,245,245);color:rgb(49,53,58);font-family:Segoe UI;font-size:10.5pt;font-weight:bold;font-style:italic;");
        Assert.NotNull(style);
        Assert.Equal("Segoe UI", style!.FontFamily);
        Assert.Equal(14, style.FontSizePx!.Value, 3);
        Assert.Equal("rgb(49,53,58)", style.ColorCss);
        Assert.Equal("rgb(245,245,245)", style.BackgroundCss);
        Assert.True(style.Bold);
        Assert.True(style.Italic);
    }

    [Fact]
    public void 像素字号保持原值而非再次换算()
    {
        var style = Ia2TextAttributes.Parse("font-size:13px;");
        Assert.Equal(13, style!.FontSizePx!.Value, 3);
    }

    [Fact]
    public void 多行元素被同文本的IA2行替换()
    {
        var element = TextElement("e1", "第一行第二行", new RectInt(0, 0, 200, 60));
        var lines = new[]
        {
            new Ia2Line("第一行", new RectInt(10, 10, 80, 20), 41),
            new Ia2Line("第二行", new RectInt(10, 40, 80, 20), 41),
        };
        var placements = Ia2TextMatcher.Match(new[] { element }, Physics(element), lines);
        Assert.Equal(2, placements.Count);
        Assert.All(placements, placement => Assert.Equal("e1", placement.Element.Id));
    }

    [Fact]
    public void 文本对不上的行不替换UIA元素()
    {
        var element = TextElement("e1", "保存", new RectInt(0, 0, 80, 20));
        var placements = Ia2TextMatcher.Match(
            new[] { element },
            Physics(element),
            new[] { new Ia2Line("取消", new RectInt(5, 2, 40, 20), 41) });
        Assert.Empty(placements);
    }

    [Fact]
    public void 覆盖不足一半字符时不替换避免缺字()
    {
        var element = TextElement("e1", "第一行第二行第三行", new RectInt(0, 0, 200, 60));
        var placements = Ia2TextMatcher.Match(
            new[] { element },
            Physics(element),
            new[] { new Ia2Line("第一行", new RectInt(10, 10, 60, 20), 41) });
        Assert.Empty(placements);
    }

    [Fact]
    public void 密码元素不参与IA2替换()
    {
        var element = TextElement("e1", "secret", new RectInt(0, 0, 200, 20), sensitive: true);
        var placements = Ia2TextMatcher.Match(
            new[] { element },
            Physics(element),
            new[] { new Ia2Line("secret", new RectInt(10, 2, 60, 20), 42) });
        Assert.Empty(placements);
    }

    [Fact]
    public void 行同时命中容器与小元素时选择面积更小的元素()
    {
        var outer = TextElement("outer", "同段文字", new RectInt(0, 0, 1000, 600));
        var inner = TextElement("inner", "同段文字", new RectInt(0, 0, 100, 20));
        var placements = Ia2TextMatcher.Match(
            new[] { outer, inner },
            Physics(outer, inner),
            new[] { new Ia2Line("同段文字", new RectInt(10, 2, 60, 20), 41) });
        var placement = Assert.Single(placements);
        Assert.Equal("inner", placement.Element.Id);
    }

    private static DesktopElement TextElement(string id, string text, RectInt bounds, bool sensitive = false) => new()
    {
        Id = id,
        Kind = "text",
        X = bounds.X,
        Y = bounds.Y,
        W = bounds.Width,
        H = bounds.Height,
        Semantic = "text",
        Text = text,
        Sensitive = sensitive,
    };

    private static Dictionary<string, UiaElementInfo> Physics(params DesktopElement[] elements) => elements.ToDictionary(
        element => element.Id,
        element => new UiaElementInfo
        {
            Id = element.Id,
            ControlType = "Text",
            Name = element.Text,
            Bounds = new RectInt((int)element.X, (int)element.Y, (int)element.W, (int)element.H),
            HasVisual = true,
        });
}
