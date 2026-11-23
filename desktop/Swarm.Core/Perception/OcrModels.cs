// P2 OCR/SoM 视觉兜底 — 数据模型与抽象接口 (Core 层零 WinRT 依赖)

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

/// <summary>SoM (Set-of-Mark) 编号元素 — 轻量规则版: 给可能可交互的文本加编号.</summary>
public sealed record SoMElement(
    int MarkId,           // 编号 1..N (在返回列表里唯一)
    string Text,
    double X,             // 归一化 bbox (同 OcrLine)
    double Y,
    double Width,
    double Height,
    SoMKind Kind,         // 启发式类型猜测
    double Confidence);

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
}

/// <summary>OCR 引擎抽象 (WinRT / Tesseract / mock 都实现这个).</summary>
public interface IOcrService
{
    /// <summary>识别 RGBA 帧中的文字. 返回空列表 = OCR 不可用或无文字.</summary>
    Task<IReadOnlyList<OcrLine>> OcrAsync(RgbaFrame frame, CancellationToken ct = default);

    /// <summary>引擎是否可用 (平台/依赖缺失时 false, 避免调用 OcrAsync 再失败).</summary>
    bool IsAvailable { get; }
}
