using Swarm.Core.Capture;
using Swarm.Core.Semantics;
using Swarm.Core.Visual;

namespace Swarm.Tests;

public class RegionStyleAnalyzerTests
{
    private static RgbaFrame Frame(int width, int height, byte r = 255, byte g = 255, byte b = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }
        return new RgbaFrame(width, height, pixels);
    }

    private static void Set(RgbaFrame frame, int x, int y, byte r, byte g, byte b)
    {
        var index = (y * frame.Width + x) * 4;
        frame.Pixels[index] = r;
        frame.Pixels[index + 1] = g;
        frame.Pixels[index + 2] = b;
        frame.Pixels[index + 3] = 255;
    }

    private static void Fill(RgbaFrame frame, RectInt rect, byte r, byte g, byte b)
    {
        for (var y = rect.Y; y < rect.Bottom; y++)
        for (var x = rect.X; x < rect.Right; x++)
            Set(frame, x, y, r, g, b);
    }

    [Fact]
    public void DominantColor_IgnoresMinorityTextPixels()
    {
        var frame = Frame(80, 40, 250, 250, 250);
        // 文字只占 10%，主色仍然应该是浅色底而不是两者的均值。
        for (var y = 10; y < 30; y++)
        for (var x = 10; x < 18; x++)
            Set(frame, x, y, 20, 20, 20);

        var color = RegionStyleAnalyzer.DominantColor(frame, new RectInt(0, 0, 80, 40));

        Assert.Equal(new Rgb(250, 250, 250), color);
    }

    [Fact]
    public void Analyze_DetectsOnePixelBorder()
    {
        var frame = Frame(60, 40, 245, 245, 245);
        Fill(frame, new RectInt(10, 5, 40, 30), 255, 255, 255);
        var region = new RectInt(10, 5, 40, 30);
        for (var x = region.X; x < region.Right; x++)
        {
            Set(frame, x, region.Y, 30, 30, 30);
            Set(frame, x, region.Bottom - 1, 30, 30, 30);
        }
        for (var y = region.Y; y < region.Bottom; y++)
        {
            Set(frame, region.X, y, 30, 30, 30);
            Set(frame, region.Right - 1, y, 30, 30, 30);
        }

        var style = RegionStyleAnalyzer.Analyze(frame, region);

        Assert.True(style.HasBorder);
        Assert.Equal(1, style.Top.Width);
        Assert.Equal(1, style.Left.Width);
        Assert.InRange(style.Top.Color.Distance(new Rgb(30, 30, 30)), 0, 3);
        Assert.Equal(new Rgb(255, 255, 255), style.Background);
    }

    [Fact]
    public void Analyze_FlatFillHasNoBorder()
    {
        var frame = Frame(60, 40, 40, 90, 160);

        var style = RegionStyleAnalyzer.Analyze(frame, new RectInt(5, 5, 50, 30));

        Assert.False(style.HasBorder);
        Assert.Equal(new Rgb(40, 90, 160), style.Background);
        Assert.Contains("background-color:rgb(40,90,160);", style.ToCss(1.5));
    }

    [Fact]
    public void TextColor_PicksGlyphColorInsteadOfBackground()
    {
        var frame = Frame(80, 30, 255, 255, 255);
        for (var y = 6; y < 24; y++)
        for (var x = 6; x < 12; x++)
            Set(frame, x, y, 30, 70, 190);

        var color = RegionStyleAnalyzer.TextColor(frame, new RectInt(0, 0, 80, 30), new Rgb(255, 255, 255));

        Assert.InRange(color.Distance(new Rgb(30, 70, 190)), 0, 4);
    }

    [Fact]
    public void EstimateFontSize_UsesLineCount()
    {
        var frame = Frame(120, 24, 255, 255, 255);
        for (var y = 5; y < 19; y++)
        for (var x = 8; x < 100; x += 10)
            Set(frame, x, y, 20, 20, 20);

        var size = RegionStyleAnalyzer.EstimateFontSize(frame, new RectInt(0, 0, 120, 24), new Rgb(255, 255, 255));

        Assert.NotNull(size);
        Assert.InRange(size!.Value, 12, 20);
    }

    [Fact]
    public void EstimateFontSize_SplitsMultipleLines()
    {
        var frame = Frame(120, 48, 255, 255, 255);
        for (var row = 0; row < 2; row++)
        for (var y = 4 + row * 24; y < 16 + row * 24; y++)
        for (var x = 8; x < 100; x += 10)
            Set(frame, x, y, 20, 20, 20);

        var size = RegionStyleAnalyzer.EstimateFontSize(frame, new RectInt(0, 0, 120, 48), new Rgb(255, 255, 255));

        Assert.NotNull(size);
        Assert.InRange(size!.Value, 12, 20);
    }

    [Fact]
    public void Analyze_DetectsEightPixelBorder()
    {
        var frame = Frame(100, 100, 245, 245, 245);
        var region = new RectInt(20, 20, 60, 60);
        Fill(frame, region, 255, 255, 255);
        for (var d = 0; d < 8; d++)
        {
            Fill(frame, new RectInt(region.X, region.Y + d, region.Width, 1), 30, 30, 30);
            Fill(frame, new RectInt(region.X, region.Bottom - 1 - d, region.Width, 1), 30, 30, 30);
            Fill(frame, new RectInt(region.X + d, region.Y, 1, region.Height), 30, 30, 30);
            Fill(frame, new RectInt(region.Right - 1 - d, region.Y, 1, region.Height), 30, 30, 30);
        }

        var style = RegionStyleAnalyzer.Analyze(frame, region);

        Assert.Equal(8, style.Top.Width);
        Assert.Equal(8, style.Right.Width);
        Assert.Equal(8, style.Bottom.Width);
        Assert.Equal(8, style.Left.Width);
        Assert.Equal(new Rgb(255, 255, 255), style.Background);
    }

    [Fact]
    public void Analyze_DetectsContinuousWeakBorder()
    {
        var frame = Frame(80, 50, 245, 245, 245);
        var region = new RectInt(10, 5, 50, 30);
        Fill(frame, region, 240, 240, 240);
        for (var x = region.X; x < region.Right; x++)
        {
            Set(frame, x, region.Y, 234, 234, 234);
            Set(frame, x, region.Bottom - 1, 234, 234, 234);
        }
        for (var y = region.Y; y < region.Bottom; y++)
        {
            Set(frame, region.X, y, 234, 234, 234);
            Set(frame, region.Right - 1, y, 234, 234, 234);
        }

        var style = RegionStyleAnalyzer.Analyze(frame, region);

        Assert.Equal(1, style.Top.Width);
        Assert.Equal(1, style.Left.Width);
        Assert.Equal(new Rgb(240, 240, 240), style.Background);
    }

    [Fact]
    public void Analyze_RejectsNoisyWeakLine()
    {
        var frame = Frame(80, 50, 240, 240, 240);
        var region = new RectInt(10, 5, 50, 30);
        for (var x = region.X; x < region.Right; x++)
        {
            var shade = x % 2 == 0 ? (byte)234 : (byte)240;
            Set(frame, x, region.Y, shade, shade, shade);
        }

        var style = RegionStyleAnalyzer.Analyze(frame, region);

        Assert.False(style.HasBorder);
    }

    [Fact]
    public void Analyze_DoesNotGuessBorderBeyondScanDepth()
    {
        var frame = Frame(80, 80, 245, 245, 245);
        var region = new RectInt(10, 5, 50, 60);
        Fill(frame, region, 255, 255, 255);
        Fill(frame, new RectInt(region.X, region.Y, region.Width, 20), 30, 30, 30);

        var style = RegionStyleAnalyzer.Analyze(frame, region);

        Assert.False(style.HasBorder);
    }

    [Fact]
    public void TextColor_UsesDominantClusterInsteadOfAveragingColors()
    {
        var frame = Frame(120, 30, 255, 255, 255);
        for (var i = 0; i < 100; i++)
        {
            var x = 5 + i % 10;
            var y = 5 + i / 10;
            if (i < 15) Set(frame, x, y, 255, 0, 0);
            else Set(frame, x, y, 0, 200, 0);
        }

        var color = RegionStyleAnalyzer.TextColor(frame, new RectInt(0, 0, 120, 30), new Rgb(255, 255, 255));

        Assert.InRange(color.Distance(new Rgb(255, 0, 0)), 0, 16);
    }

    [Fact]
    public void EstimateFontSize_DetectsSparseTextInWideRegion()
    {
        var frame = Frame(500, 30, 255, 255, 255);
        for (var y = 8; y < 20; y++)
        for (var x = 20; x <= 41; x += 20)
        {
            Set(frame, x, y, 20, 20, 20);
            Set(frame, x + 1, y, 20, 20, 20);
        }

        var size = RegionStyleAnalyzer.EstimateFontSize(frame, new RectInt(0, 0, 500, 30), new Rgb(255, 255, 255));

        Assert.NotNull(size);
        // 12px 是无降部的帽高，按 0.72em 折算后约 17px。
        Assert.InRange(size!.Value, 15, 19);
    }

    [Fact]
    public void EstimateFontSize_IgnoresSinglePixelNoiseRun()
    {
        var frame = Frame(120, 60);
        // 一条 1px 分隔线混进采样区；旧实现的下中位数会被它拉到 1px 并直接返回 null。
        Fill(frame, new RectInt(8, 5, 104, 1), 20, 20, 20);
        for (var y = 20; y < 38; y++)
        for (var x = 8; x < 110; x += 12)
            Set(frame, x, y, 20, 20, 20);

        var size = RegionStyleAnalyzer.EstimateFontSize(frame, new RectInt(0, 0, 120, 60), new Rgb(255, 255, 255));

        Assert.NotNull(size);
        Assert.InRange(size!.Value, 23, 27);
    }

    [Fact]
    public void EstimateFontSize_UsesCapHeightWhenNoDescenders()
    {
        var frame = Frame(120, 60);
        for (var y = 10; y < 30; y++)
        for (var x = 8; x <= 48; x += 20)
        {
            Set(frame, x, y, 20, 20, 20);
            Set(frame, x + 1, y, 20, 20, 20);
        }

        var size = RegionStyleAnalyzer.EstimateFontSize(frame, new RectInt(0, 0, 120, 60), new Rgb(255, 255, 255));

        Assert.NotNull(size);
        // 无降部时墨迹约等于帽高 0.72em，20 / 0.72 ≈ 28。
        Assert.InRange(size!.Value, 26, 29);
    }

    [Fact]
    public void EstimateFontSize_UsesFullInkWhenDescendersPresent()
    {
        var frame = Frame(120, 60);
        for (var y = 10; y < 26; y++)
        for (var x = 8; x <= 48; x += 20)
        {
            Set(frame, x, y, 20, 20, 20);
            Set(frame, x + 1, y, 20, 20, 20);
        }
        // 基线下方只剩一条笔画：这是降部特征，墨迹应按 0.92em 折算。
        for (var y = 26; y < 30; y++)
        {
            Set(frame, 28, y, 20, 20, 20);
            Set(frame, 29, y, 20, 20, 20);
        }

        var size = RegionStyleAnalyzer.EstimateFontSize(frame, new RectInt(0, 0, 120, 60), new Rgb(255, 255, 255));

        Assert.NotNull(size);
        Assert.InRange(size!.Value, 20, 23);
    }

    [Fact]
    public void EstimateFontSize_UsesCjkInkRatio()
    {
        var frame = Frame(120, 60);
        for (var y = 10; y < 28; y++)
        for (var x = 8; x < 100; x += 6)
        for (var d = 0; d < 4; d++)
            Set(frame, x + d, y, 20, 20, 20);

        var size = RegionStyleAnalyzer.EstimateFontSize(
            frame,
            new RectInt(0, 0, 120, 60),
            new Rgb(255, 255, 255),
            text: "显示名称");

        Assert.NotNull(size);
        // 汉字墨迹约占 0.88em，18 / 0.88 ≈ 20.5。
        Assert.InRange(size!.Value, 19, 22);
    }

    [Fact]
    public void TextColor_InsetExcludesBorder()
    {
        var frame = Frame(100, 60);
        var region = new RectInt(10, 10, 80, 40);
        for (var d = 0; d < 8; d++)
        {
            Fill(frame, new RectInt(region.X, region.Y + d, region.Width, 1), 0, 0, 0);
            Fill(frame, new RectInt(region.X, region.Bottom - 1 - d, region.Width, 1), 0, 0, 0);
            Fill(frame, new RectInt(region.X + d, region.Y, 1, region.Height), 0, 0, 0);
            Fill(frame, new RectInt(region.Right - 1 - d, region.Y, 1, region.Height), 0, 0, 0);
        }
        for (var i = 0; i < 15; i++)
            Set(frame, 45 + i % 5, 25 + i / 5, 30, 70, 190);

        var color = RegionStyleAnalyzer.TextColor(frame, region, new Rgb(255, 255, 255), padding: 10);

        Assert.InRange(color.Distance(new Rgb(30, 70, 190)), 0, 16);
    }

    [Fact]
    public void EstimateFontSize_InsetExcludesVerticalBorder()
    {
        var frame = Frame(100, 70);
        var region = new RectInt(10, 5, 60, 50);
        for (var d = 0; d < 6; d++)
        {
            Fill(frame, new RectInt(region.X, region.Y + d, region.Width, 1), 0, 0, 0);
            Fill(frame, new RectInt(region.X, region.Bottom - 1 - d, region.Width, 1), 0, 0, 0);
            Fill(frame, new RectInt(region.X + d, region.Y, 1, region.Height), 0, 0, 0);
            Fill(frame, new RectInt(region.Right - 1 - d, region.Y, 1, region.Height), 0, 0, 0);
        }
        for (var y = 24; y < 36; y++)
        for (var x = 30; x <= 40; x += 10)
        {
            Set(frame, x, y, 20, 20, 20);
            Set(frame, x + 1, y, 20, 20, 20);
        }

        var size = RegionStyleAnalyzer.EstimateFontSize(frame, region, new Rgb(255, 255, 255), padding: 8);

        Assert.NotNull(size);
        // 12px 无降部墨迹按 0.72em 折算后约 17px。
        Assert.InRange(size!.Value, 15, 19);
    }

    [Fact]
    public void ToCss_ConvertsPhysicalBorderWidthToCssPixels()
    {
        var style = new RegionStyle(
            new Rgb(250, 250, 250),
            new BorderSide(3, new Rgb(200, 200, 200)),
            new BorderSide(3, new Rgb(200, 200, 200)),
            new BorderSide(3, new Rgb(200, 200, 200)),
            new BorderSide(3, new Rgb(200, 200, 200)));

        var css = style.ToCss(1.5);

        Assert.Contains("background-color:rgb(250,250,250);", css);
        Assert.Contains("border:2px solid rgb(200,200,200);", css);
    }

    [Fact]
    public void SurfaceColor_IgnoresCenteredSelectionHighlight()
    {
        // 输入框获得焦点后文字被全选：中间一大片强调蓝，但内圈薄带仍然是白底。
        var frame = Frame(120, 40, 255, 255, 255);
        Fill(frame, new RectInt(12, 9, 96, 22), 0, 120, 212);

        var color = RegionStyleAnalyzer.SurfaceColor(frame, new RectInt(0, 0, 120, 40));

        Assert.True(color.Luminance > 200, $"expected light surface, got {color}");
    }

    [Fact]
    public void SurfaceColor_UsesRingInsteadOfGlyphs()
    {
        // 深色文字铺满中间，整块主色会被笔画带偏；内圈取色应得到真实底色。
        var frame = Frame(140, 40, 252, 252, 252);
        for (var x = 6; x < 134; x += 3)
        for (var y = 8; y < 32; y++)
        {
            Set(frame, x, y, 18, 18, 18);
            Set(frame, x + 1, y, 18, 18, 18);
        }

        var color = RegionStyleAnalyzer.SurfaceColor(frame, new RectInt(0, 0, 140, 40));

        Assert.InRange(color.Luminance, 240, 255);
    }

    [Theory]
    [InlineData(255, 255, 255, 32)]
    [InlineData(250, 250, 250, 32)]
    [InlineData(20, 24, 32, 244)]
    [InlineData(0, 120, 212, 244)]
    public void PreferredTextColor_FollowsSurfaceLuminance(byte r, byte g, byte b, byte expected)
    {
        var color = RegionStyleAnalyzer.PreferredTextColor(new Rgb(r, g, b));

        Assert.Equal(expected, color.R);
    }
}
