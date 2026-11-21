using Swarm.Core.Windows;

namespace Swarm.Tests;

public class WindowFilterTests
{
    [Theory]
    [InlineData("msedge.exe", "文档 - Edge", true)]
    [InlineData("msedge", "文档 - Edge", true)]
    [InlineData("MSEDGE.EXE", "文档 - Edge", true)]
    [InlineData("chrome.exe", "文档 - Edge", false)]
    [InlineData(null, "任意窗口", true)]
    [InlineData("", "任意窗口", true)]
    public void 进程名支持带或不带扩展名(string? process, string candidateTitle, bool expected)
    {
        Assert.Equal(expected, WindowFilter.Matches(process, null, "msedge", candidateTitle));
    }

    [Theory]
    [InlineData("swarm pixels", "目标 - swarm pixels - Edge", true)]
    [InlineData("SWARM", "目标 - swarm pixels - Edge", true)]
    [InlineData("notepad", "目标 - swarm pixels - Edge", false)]
    public void 标题按不区分大小写的包含匹配(string titleFilter, string candidateTitle, bool expected)
    {
        Assert.Equal(expected, WindowFilter.Matches(null, titleFilter, "msedge", candidateTitle));
    }

    [Fact]
    public void 空标题窗口一律排除()
    {
        Assert.False(WindowFilter.Matches(null, null, "msedge", ""));
        Assert.False(WindowFilter.Matches(null, null, "msedge", "   "));
    }
}
