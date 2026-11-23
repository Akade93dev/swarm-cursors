// P2 OCR/SoM 视觉兜底 — 数据模型与抽象接口 (Core 层零 WinRT 依赖)
// SoMElement 增强: TRISHUL HSP (粗→细双层粒度) + SEED (空间增强语义)
// 参考文献: TRISHUL arXiv:2502.08226 "Towards Region Identification and Screen Hierarchy Understanding for Large VLM based GUI Agents"

using Swarm.Core.Capture;

namespace Swarm.Core.Perception;

/// <summary>OCR 识别的一行文字 + bbox + confidence (归一化坐标 0-1, 与 capture.Frame 分辨率相乘得物理像素).</summary>
public sealed record OcrLine(
    string Text,
    double X,          // 归一化左上角 X [0,1]
    double Y,          // 归一化左上角 Y [0,1]
    double Width,      // 归一化宽度 [0,1]
    double Height,     // 归一化高度 [0,1]
    double Confidence); // [0,1]

/// <summary>
/// SoM (Set-of-Mark) 编号元素 — TRISHUL HSP 融合版.
/// Layer 1 (粗粒度大区域) + Layer 2 (细粒度可交互元素), 加上 SEED 空间语义标注.
/// 纯 OCR-only fallback 路径保持向后兼容.
/// </summary>
public sealed record SoMElement(
    int MarkId,
    string Text,
    double X,
    double Y,
    double Width,
    double Height,
    SoMKind Kind,
    double Confidence,
    // TRISHUL HSP + SEED 新增 (全部有默认值, 旧 Build(OcrLines) 不受影响)
    int HierarchyLevel = 2,                // 1=粗粒度大区域, 2=细粒度可交互元素
    int? ParentMarkId = null,              // 父区域 MarkId (HSP 粗→细 containment)
    string? SpatialContext = null,          // SEED 空间描述: "在 MenuBar 右侧" / "在 TitleBar 内"
    string? UiaControlType = null,          // UIA ControlType (Button/Edit/ComboBox...), 纯 OCR 时 null
    int OverlapFlags = 0)                  // bit0=与UIA重叠, bit1=纯OCR无UIA, bit2=被UIA容器包含
{
    public bool FromOcrOnly => (OverlapFlags & 0b10) != 0;
    public bool HasUiaMatch => (OverlapFlags & 0b01) != 0;
}

public enum SoMKind
{
    /// <summary>无法判定, 可能可交互.</summary>
    Unknown,
    /// <summary>按钮: 短文本 + 包含 OK/确定/保存/提交/搜索 等关键词.</summary>
    Button,
    /// <summary>链接: 包含 http/https/www/://.</summary>
    Link,
    /// <summary>输入框/标签: 短文本非标点, 可能是可编辑区旁边的 label.</summary>
    Label,
    /// <summary>长文本段落, 大概率不可交互.</summary>
    Paragraph,
    // TRISHUL HSP 新增区域类型
    /// <summary>TitleBar / HeaderBar — 窗口顶部标题栏.</summary>
    TitleBar,
    /// <summary>MenuBar / ToolBar — 菜单栏/工具栏.</summary>
    MenuBar,
    /// <summary>Sidebar / Navigation — 侧边栏/导航区.</summary>
    Sidebar,
    /// <summary>ContentArea / Document — 主内容/文档区.</summary>
    ContentArea,
    /// <summary>StatusBar / Footer — 底部状态栏.</summary>
    StatusBar,
}

/// <summary>OCR 引擎抽象 (WinRT / Tesseract / mock 都实现这个).</summary>
public interface IOcrService
{
    /// <summary>识别 RGBA 帧中的文字. 返回空列表 = OCR 不可用或无文字.</summary>
    Task<IReadOnlyList<OcrLine>> OcrAsync(RgbaFrame frame, CancellationToken ct = default);

    /// <summary>引擎是否可用 (平台/依赖缺失时 false, 避免调用 OcrAsync 再失败).</summary>
    bool IsAvailable { get; }
}
