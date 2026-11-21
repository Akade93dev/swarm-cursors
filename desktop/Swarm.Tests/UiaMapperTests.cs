using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class UiaMapperTests
{
    [Fact]
    public void 按钮映射为带语义和截图的图像任务()
    {
        var elements = new[]
        {
            new UiaElementInfo { Id = "b1", ControlType = "Button", Name = "保存", Bounds = new RectInt(10, 20, 90, 30), ProcessId = 42, HasVisual = true },
        };
        var result = UiaMapper.Map(elements, 800, 600);
        var item = Assert.Single(result);
        Assert.Equal("image", item.Kind);
        Assert.Equal("button", item.Semantic);
        Assert.Equal("b1", item.CropId);
        Assert.Equal(42, item.ProcessId);
    }

    [Fact]
    public void 输入框使用值生成文本任务并保留父子依赖()
    {
        var elements = new[]
        {
            new UiaElementInfo { Id = "panel", ControlType = "Pane", Bounds = new RectInt(0, 0, 300, 200), ProcessId = 42, HasVisual = true, Color = "rgb(240,240,240)" },
            new UiaElementInfo { Id = "edit", ParentId = "panel", ControlType = "Edit", Value = "Hello", Bounds = new RectInt(10, 20, 200, 30), ProcessId = 42 },
        };
        var result = UiaMapper.Map(elements, 800, 600);
        Assert.Equal(2, result.Count);
        Assert.Equal("text", result[1].Kind);
        Assert.Equal("Hello", result[1].Text);
        Assert.Equal("panel", result[1].Parent);
    }

    [Fact]
    public void 密码值始终掩码且离屏元素被过滤()
    {
        var elements = new[]
        {
            new UiaElementInfo { Id = "password", ControlType = "Edit", Value = "secret", IsPassword = true, Bounds = new RectInt(10, 20, 200, 30), ProcessId = 42 },
            new UiaElementInfo { Id = "outside", ControlType = "Button", Bounds = new RectInt(900, 20, 200, 30), ProcessId = 42, HasVisual = true },
        };
        var result = UiaMapper.Map(elements, 800, 600);
        var item = Assert.Single(result);
        Assert.Equal("••••••••", item.Text);
        Assert.DoesNotContain("secret", item.Text);
        Assert.True(item.Sensitive);
    }
}
