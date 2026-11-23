// TRISHUL HSP 单元测试 — 覆盖矩阵: 正常 / 边界 / 空值 / 融合去重 / Containment / SEED
// 对应源码: Swarm.Core/Perception/HierarchicalScreenParser.cs + OcrModels.cs

using Swarm.Core.Perception;

namespace Swarm.Tests;

public class HierarchicalScreenParserTests
{
    // ======== 空值 / 边界 ========

    [Fact]
    public void 全空输入返回空列表()
    {
        var result = HierarchicalScreenParser.Build(
            Array.Empty<HspRegion>(),
            Array.Empty<HspInteractive>(),
            Array.Empty<OcrLine>(),
            viewportWidth: 1000, viewportHeight: 800);

        Assert.Empty(result);
    }

    [Fact]
    public void 零视口尺寸归一化不出NaN()
    {
        var region = new HspRegion("r1", "TitleBar", 0, 0, 0.5, 0.05);
        var interactive = new HspInteractive("i1", "Button", "保存", 0.1, 0.02, 0.08, 0.03, 1.0);

        // viewportWidth=0 时 Layer 1/2 输入已经是 [0,1] 归一化的, 但 Build 内部会再次做除法
        // 验证不会抛异常且产出合理
        var result = HierarchicalScreenParser.Build(
            new[] { region }, new[] { interactive }, Array.Empty<OcrLine>(),
            viewportWidth: 0, viewportHeight: 0);

        Assert.NotEmpty(result);
        Assert.All(result, e => Assert.False(double.IsNaN(e.X) || double.IsNaN(e.Y) || double.IsNaN(e.Width) || double.IsNaN(e.Height)));
    }

    // ======== 纯 UIA 路径 (无 OCR) ========

    [Fact]
    public void 只有UIA_Region和Interactive被正确分层分配MarkId()
    {
        var regions = new[]
        {
            new HspRegion("r1", "TitleBar", 0, 0, 1.0, 0.08),
            new HspRegion("r2", "MenuBar", 0, 0.08, 1.0, 0.06),
        };
        var interactives = new[]
        {
            new HspInteractive("i1", "Button", "保存", 0.5, 0.01, 0.06, 0.04, 1.0),
            new HspInteractive("i2", "Button", "取消", 0.6, 0.01, 0.06, 0.04, 1.0),
        };

        var result = HierarchicalScreenParser.Build(regions, interactives, Array.Empty<OcrLine>(), 1000, 800).ToList();

        Assert.Equal(4, result.Count);
        Assert.True(result.All(e => e.MarkId >= 1 && e.MarkId <= 4));
        // Layer 1 在前 (MarkId 小), Layer 2 在后
        Assert.Equal(1, result[0].HierarchyLevel);
        Assert.Equal(1, result[1].HierarchyLevel);
        Assert.Equal(2, result[2].HierarchyLevel);
        Assert.Equal(2, result[3].HierarchyLevel);
    }

    // ======== 纯 OCR 路径 ========

    [Fact]
    public void 只有OCR_置信度过低的被过滤()
    {
        var ocr = new[]
        {
            new OcrLine("保存", 0.5, 0.5, 0.05, 0.03, 0.95),    // 过 0.80
            new OcrLine("取消", 0.6, 0.5, 0.05, 0.03, 0.60),   // 不过
            new OcrLine("确定", 0.7, 0.5, 0.05, 0.03, 0.85),   // 过
            new OcrLine("   " , 0.1, 0.1, 0.01, 0.01, 0.99),   // 只有空格
        };

        var result = HierarchicalScreenParser.Build(
            Array.Empty<HspRegion>(), Array.Empty<HspInteractive>(), ocr,
            1000, 800).ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Text == "保存");
        Assert.Contains(result, e => e.Text == "确定");
        Assert.All(result, e => Assert.True(e.FromOcrOnly));
    }

    [Fact]
    public void OCR段落类型被过滤()
    {
        // Paragraph kind 会被 SetOfMarkBuilder.ClassifyStatic 过滤掉
        var ocr = new[]
        {
            new OcrLine("This is a very long paragraph text that should be filtered out", 0.1, 0.5, 0.4, 0.05, 0.95),
            new OcrLine("OK", 0.5, 0.1, 0.03, 0.02, 0.95),
        };

        var result = HierarchicalScreenParser.Build(
            Array.Empty<HspRegion>(), Array.Empty<HspInteractive>(), ocr,
            1000, 800).ToList();

        Assert.Single(result);
        Assert.Equal("OK", result[0].Text);
    }

    // ======== 融合路径: UIA + OCR 去重 ========

    [Fact]
    public void UIA与OCR重叠_OCR被UIA侧覆盖()
    {
        // UIA 和 OCR 几乎同样的位置 + 文本
        var interactives = new[]
        {
            new HspInteractive("i1", "Button", "保存", 0.5, 0.01, 0.06, 0.04, 1.0),
        };
        var ocr = new[]
        {
            new OcrLine("保存", 0.505, 0.012, 0.058, 0.038, 0.95), // 与 UIA IoU ≥ 0.3
        };

        var result = HierarchicalScreenParser.Build(
            Array.Empty<HspRegion>(), interactives, ocr, 1000, 800).ToList();

        // 应该只剩 UIA 那一个, OCR 被去重
        Assert.Single(result);
        Assert.Equal("保存", result[0].Text);
        Assert.Equal("Button", result[0].UiaControlType);
        Assert.False(result[0].FromOcrOnly);
    }

    [Fact]
    public void OCR只有UIA没有_OCR被保留()
    {
        var interactives = new[]
        {
            new HspInteractive("i1", "Button", "保存", 0.5, 0.01, 0.06, 0.04, 1.0),
        };
        var ocr = new[]
        {
            new OcrLine("忘记密码", 0.8, 0.01, 0.06, 0.03, 0.95), // 完全不同位置
        };

        var result = HierarchicalScreenParser.Build(
            Array.Empty<HspRegion>(), interactives, ocr, 1000, 800).ToList();

        Assert.Equal(2, result.Count);
        var uiaOne = result.Single(e => !e.FromOcrOnly);
        var ocrOne = result.Single(e => e.FromOcrOnly);
        Assert.Equal("保存", uiaOne.Text);
        Assert.Equal("忘记密码", ocrOne.Text);
    }

    // ======== Layer 2 内部 IoU 贪心合并 ========

    [Fact]
    public void Layer2内部重叠被贪心合并()
    {
        var ocr = new[]
        {
            new OcrLine("确", 0.5, 0.5, 0.06, 0.04, 0.95),
            new OcrLine("定", 0.5, 0.5, 0.06, 0.04, 0.90),
        };

        var result = HierarchicalScreenParser.Build(
            Array.Empty<HspRegion>(), Array.Empty<HspInteractive>(), ocr,
            1000, 800).ToList();

        // 把所有信息塞进 Assert.True 的消息里
        var allInfo = $"Count={result.Count}, Texts=[{string.Join(",", result.Select(r => $"[{r.Text}]"))}]";
        Assert.True(result.Count == 1 && result[0].Text.Contains("确") && result[0].Text.Contains("定"), allInfo);
    }

    // ======== Containment + innermost 父区域 ========

    [Fact]
    public void 嵌套区域_元素归属innermost父区域()
    {
        // TitleBar (整个顶部) 内有 MenuBar (稍小)
        var regions = new[]
        {
            new HspRegion("r1", "TitleBar", 0, 0, 1.0, 0.08),
            new HspRegion("r2", "MenuBar", 0.1, 0.04, 0.8, 0.04),
        };
        // 元素在 MenuBar 内 (也同时在 TitleBar 内)
        var interactives = new[]
        {
            new HspInteractive("i1", "Button", "File", 0.15, 0.05, 0.05, 0.02, 1.0),
        };

        var result = HierarchicalScreenParser.Build(regions, interactives, Array.Empty<OcrLine>(), 1000, 800).ToList();

        var layer2 = result.Single(e => e.HierarchyLevel == 2);
        Assert.NotNull(layer2.ParentMarkId);

        // 找到 MenuBar 的 MarkId 应该比 TitleBar 大 (因为 Layer 1 按输入顺序分配)
        var titleBarMarkId = result.Single(e => e.HierarchyLevel == 1 && e.Text == "TitleBar").MarkId;
        var menuBarMarkId = result.Single(e => e.HierarchyLevel == 1 && e.Text == "MenuBar").MarkId;
        // 元素父应该是 MenuBar (innermost), 不是 TitleBar
        Assert.Equal(menuBarMarkId, layer2.ParentMarkId);
        Assert.NotEqual(titleBarMarkId, layer2.ParentMarkId);
    }

    [Fact]
    public void 完全在所有区域外的元素_无ParentMarkId()
    {
        var regions = new[]
        {
            new HspRegion("r1", "ContentArea", 0.2, 0.3, 0.6, 0.5), // 中间区域
        };
        // 元素在区域外
        var interactives = new[]
        {
            new HspInteractive("i1", "Button", "弹出对话框", 0.05, 0.05, 0.1, 0.05, 1.0),
        };

        var result = HierarchicalScreenParser.Build(regions, interactives, Array.Empty<OcrLine>(), 1000, 800).ToList();
        var layer2 = result.Single(e => e.HierarchyLevel == 2);
        Assert.Null(layer2.ParentMarkId);
    }

    // ======== SEED 空间语义生成 ========

    [Fact]
    public void SEED_元素在区域中间生成空间描述()
    {
        var regions = new[]
        {
            new HspRegion("r1", "TitleBar", 0, 0, 1.0, 0.1),
        };
        // 按钮 relY = (0.005+0.02-0)/0.1 = 0.25 < 0.33 → "上半部"
        var interactives = new[]
        {
            new HspInteractive("i1", "Button", "关闭", 0.93, 0.005, 0.04, 0.03, 1.0), // TitleBar 右上角
        };

        var result = HierarchicalScreenParser.Build(regions, interactives, Array.Empty<OcrLine>(), 1000, 800).ToList();
        var closeBtn = result.Single(e => e.Text == "关闭");

        Assert.NotNull(closeBtn.SpatialContext);
        Assert.Contains("右侧", closeBtn.SpatialContext);
        Assert.Contains("上半部", closeBtn.SpatialContext);
        Assert.Contains("TitleBar", closeBtn.SpatialContext);
    }

    [Fact]
    public void SEED_完全在外的元素_无SpatialContext()
    {
        var regions = new[]
        {
            new HspRegion("r1", "ContentArea", 0.2, 0.2, 0.5, 0.5),
        };
        var interactives = new[]
        {
            new HspInteractive("i1", "Button", "X", 0.01, 0.01, 0.03, 0.03, 1.0),
        };

        var result = HierarchicalScreenParser.Build(regions, interactives, Array.Empty<OcrLine>(), 1000, 800).ToList();
        var btn = result.Single(e => e.Text == "X");
        Assert.Null(btn.SpatialContext);
    }

    // ======== 向后兼容: SetOfMarkBuilder.Build(OcrLines) 不受影响 ========

    [Fact]
    public void 旧Build入口_SoMElement字段全有默认值()
    {
        var ocr = new[]
        {
            new OcrLine("保存", 0.5, 0.5, 0.06, 0.04, 0.95),
        };
        var result = SetOfMarkBuilder.Build(ocr).ToList();

        Assert.Single(result);
        Assert.Equal(2, result[0].HierarchyLevel);    // 默认 Layer 2
        Assert.Null(result[0].ParentMarkId);         // 默认 null
        Assert.Null(result[0].SpatialContext);       // 默认 null
        Assert.Null(result[0].UiaControlType);       // 默认 null (纯 OCR)
        Assert.Equal(0, result[0].OverlapFlags);     // 默认 0
    }

    // ======== SoMElement OverlapFlags 位标志 ========

    [Fact]
    public void OverlapFlags_位标志语义正确()
    {
        // bit0=0x01=HasUiaMatch, bit1=0x02=FromOcrOnly
        var uiaOne = new SoMElement(1, "保存", 0, 0, 0.1, 0.05, SoMKind.Button, 1.0,
            HierarchyLevel: 2, UiaControlType: "Button", OverlapFlags: 0b01);
        var ocrOne = new SoMElement(2, "取消", 0.2, 0, 0.1, 0.05, SoMKind.Button, 0.95,
            HierarchyLevel: 2, OverlapFlags: 0b10);

        Assert.True(uiaOne.HasUiaMatch);
        Assert.False(uiaOne.FromOcrOnly);
        Assert.False(ocrOne.HasUiaMatch);
        Assert.True(ocrOne.FromOcrOnly);
    }
}
