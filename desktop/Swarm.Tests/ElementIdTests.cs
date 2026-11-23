using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class ElementIdTests
{
    [Fact]
    public void 编码后能解析出同一代数()
    {
        var id = ElementId.Create(12, "0.1.2");

        Assert.Equal("g12:0.1.2", id);
        Assert.True(ElementId.TryParseGeneration(id, out var generation));
        Assert.Equal(12, generation);
    }

    [Fact]
    public void 没有代数前缀的ID解析失败()
    {
        Assert.False(ElementId.TryParseGeneration("0.1.2", out var generation));
        Assert.Equal(0, generation);
        Assert.False(ElementId.TryParseGeneration(null, out _));
        Assert.False(ElementId.TryParseGeneration("g:x", out _));
    }

    [Fact]
    public void 稳定路径去掉代数前缀()
    {
        Assert.Equal("0.1.2", ElementId.Stable("g7:0.1.2"));
        Assert.Equal("0.1.2", ElementId.Stable("0.1.2"));
        Assert.Equal("", ElementId.Stable("g7:"));
    }
}
