using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class RebuildStrategyTests
{
    private static UiaElementInfo Named(string id, string type, string name) =>
        new() { Id = id, ControlType = type, Name = name, Bounds = new RectInt(0, 0, 20, 20), HasVisual = true };

    private static UiaElementInfo Pane(string id, string name) =>
        new() { Id = id, ControlType = "Pane", Name = name, Bounds = new RectInt(0, 0, 400, 300) };

    private static DesktopElement Mapped(string id) =>
        new() { Id = id, Kind = "image", W = 20, H = 20 };

    [Fact]
    public void 命名控件足够时使用语义重建()
    {
        var elements = Enumerable.Range(0, 6).Select(i => Named($"b{i}", "Button", $"按钮{i}")).ToArray();
        var mapped = elements.Select(element => Mapped(element.Id)).ToArray();

        var decision = RebuildStrategy.Decide(elements, mapped, UiaTruncationReason.None);

        Assert.Equal(RebuildMode.Semantic, decision.Mode);
        Assert.Equal(6, decision.NamedControls);
        Assert.False(decision.Truncated);
    }

    [Fact]
    public void 只有窗口和面板名称时回退截屏()
    {
        // Electron/Chromium 的典型表现：树里只有窗口壳，没有可命名的控件。
        var elements = new UiaElementInfo[] { Pane("0", "TRAE CN"), Pane("0.0", "Agent") };
        var mapped = elements.Select(element => Mapped(element.Id)).ToArray();

        var decision = RebuildStrategy.Decide(elements, mapped, UiaTruncationReason.None);

        Assert.Equal(RebuildMode.Screenshot, decision.Mode);
        Assert.Equal(RebuildFallbackReason.SparseSemantics, decision.Reason);
        Assert.Equal(0, decision.NamedControls);
    }

    [Fact]
    public void 只有外壳但存在窗口矩形时改走框架重建()
    {
        // Chromium/Electron：命名控件不足，但窗口矩形本身可读，应保留底座像素并只重建框架。
        var elements = new UiaElementInfo[] { Pane("0", "TRAE CN"), Pane("0.1", "关闭") };
        var mapped = new[]
        {
            new DesktopElement { Id = "0", Kind = "rect", Semantic = "window", W = 400, H = 300 },
            Mapped("0.1"),
        };

        var decision = RebuildStrategy.Decide(elements, mapped, UiaTruncationReason.None);

        Assert.Equal(RebuildMode.Semantic, decision.Mode);
        Assert.Equal(RebuildFallbackReason.FrameOnly, decision.Reason);
        Assert.False(decision.IsFallback);
    }

    [Fact]
    public void 采集为空时回退并标记无元素()
    {
        var decision = RebuildStrategy.Decide(Array.Empty<UiaElementInfo>(), Array.Empty<DesktopElement>(), UiaTruncationReason.TimeLimit);

        Assert.Equal(RebuildMode.Screenshot, decision.Mode);
        Assert.Equal(RebuildFallbackReason.NoElements, decision.Reason);
        Assert.True(decision.Truncated);
    }

    [Fact]
    public void 映射结果为空时回退并保留命名计数()
    {
        var elements = new[] { Named("b1", "Button", "保存"), Named("b2", "Button", "取消") };

        var decision = RebuildStrategy.Decide(elements, Array.Empty<DesktopElement>(), UiaTruncationReason.None);

        Assert.Equal(RebuildMode.Screenshot, decision.Mode);
        Assert.Equal(RebuildFallbackReason.EmptyMapped, decision.Reason);
        Assert.Equal(2, decision.NamedControls);
    }

    [Theory]
    [InlineData(UiaTruncationReason.TimeLimit)]
    [InlineData(UiaTruncationReason.DepthLimit)]
    [InlineData(UiaTruncationReason.NodeLimit)]
    public void 截断但命名控件足够时仍语义重建并保留具体原因(UiaTruncationReason truncation)
    {
        var elements = Enumerable.Range(0, 8).Select(i => Named($"t{i}", "Text", $"文本{i}")).ToArray();
        var mapped = elements.Select(element => Mapped(element.Id)).ToArray();

        var decision = RebuildStrategy.Decide(elements, mapped, truncation);

        Assert.Equal(RebuildMode.Semantic, decision.Mode);
        Assert.True(decision.Truncated);
        Assert.Equal(truncation, decision.Truncation);
        Assert.Equal(8, decision.NamedControls);
    }

    [Fact]
    public void 离屏命名控件不计入语义数量()
    {
        var elements = new[]
        {
            new UiaElementInfo { Id = "hidden", ControlType = "Button", Name = "隐藏按钮", Bounds = new RectInt(0, 0, 20, 20), HasVisual = false },
            new UiaElementInfo { Id = "visible", ControlType = "Button", Name = "保存", Bounds = new RectInt(0, 0, 20, 20), HasVisual = true },
        };

        Assert.Equal(1, RebuildStrategy.CountNamed(elements));
    }

    [Fact]
    public void 空名称控件不计入命名数量()
    {
        var elements = new[]
        {
            Named("b1", "Button", " "),
            Named("t1", "Text", ""),
            Named("b2", "Button", "确定"),
        };

        Assert.Equal(1, RebuildStrategy.CountNamed(elements));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("progman")]
    public void 桌面外壳类名识别为桌面(string className)
    {
        Assert.True(RebuildStrategy.IsShellDesktop(className));
    }

    [Theory]
    [InlineData("CabinetWClass")]
    [InlineData("Notepad")]
    [InlineData("")]
    [InlineData(null)]
    public void 其他窗口类名不识别为桌面(string? className)
    {
        Assert.False(RebuildStrategy.IsShellDesktop(className));
    }

    [Fact]
    public void 桌面即使命名控件足够也强制截屏()
    {
        var elements = Enumerable.Range(0, 20).Select(i => Named($"t{i}", "Text", $"壁纸文字{i}")).ToArray();
        var mapped = elements.Select(element => Mapped(element.Id)).ToArray();

        var decision = RebuildStrategy.Decide(elements, mapped, UiaTruncationReason.None, shellDesktop: true);

        Assert.Equal(RebuildMode.Screenshot, decision.Mode);
        Assert.Equal(RebuildFallbackReason.ShellDesktop, decision.Reason);
        Assert.True(decision.IsFallback);
    }
}
