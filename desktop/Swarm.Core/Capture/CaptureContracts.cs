using Swarm.Core.Semantics;

namespace Swarm.Core.Capture;

/// <summary>一次抓屏所需的窗口几何信息；由平台层读取，策略层只消费。</summary>
public readonly record struct CaptureGeometry(
    RectInt Monitor,
    RectInt ClientScreen,
    RectInt ClientBounds,
    double DpiScale);

/// <summary>单个抓屏后端的产出。Frame 为空表示该后端不可用或失败；FrameAgeMs 是帧的新鲜度参考。</summary>
public readonly record struct CaptureAttempt(RgbaFrame? Frame, bool Occluded = false, long FrameAgeMs = 0);

/// <summary>后端链的最终结果。Blank 为真表示没有任何后端拿到有效像素。</summary>
public sealed record CapturedWindow(RgbaFrame? Frame, bool Occluded, bool Blank, string Backend, long FrameAgeMs = 0);

/// <summary>
/// 抓屏后端。实现必须是无副作用的单次尝试：失败返回空帧，不要抛异常表示"不支持"；
/// 真正的异常（如屏幕读取失败）应向上抛出，由调用方暴露。
/// </summary>
public interface ICaptureBackend
{
    /// <summary>写入结果 Backend 字段的名称，例如 wgc / print / screen。</summary>
    string Name { get; }

    CaptureAttempt TryCapture(nint window, CaptureGeometry geometry);
}
