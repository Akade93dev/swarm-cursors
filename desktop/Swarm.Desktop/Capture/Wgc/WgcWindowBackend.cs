using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

/// <summary>
/// WGC 主后端：帧是整窗物理像素（含非客户区），这里按可见窗口矩形把客户区裁出来，
/// 与 PrintWindow/屏幕裁剪输出同一坐标系。任何不可用（Win10 无 FreeThreaded 帧池、
/// 设备/会话失败、裁剪对不齐）都返回空帧交给后端链降级，不向上抛异常。
/// </summary>
internal sealed class WgcWindowBackend(WgcSessionCache sessions) : ICaptureBackend
{
    /// <summary>首帧需等会话建立（实测约 160ms），复用会话后 3–5ms；超时即降级，不阻塞动作。</summary>
    internal const int TimeoutMs = 600;

    public string Name => "wgc";

    public CaptureAttempt TryCapture(nint window, CaptureGeometry geometry)
    {
        if (!WgcSupport.IsAvailable) return new CaptureAttempt(null);

        var result = sessions.Capture(window, TimeoutMs);
        if (result.Code == "wgc_unsupported")
        {
            WgcSupport.MarkUnsupported(result.Detail);
            return new CaptureAttempt(null);
        }
        if (!result.Success || result.Frame is not { } frame) return new CaptureAttempt(null);
        WgcSupport.MarkAvailable();

        var client = CropToClient(window, frame, geometry);
        if (client is null) return new CaptureAttempt(null);
        return new CaptureAttempt(client, Occluded: false, FrameAgeMs: result.AgeMs);
    }

    private static RgbaFrame? CropToClient(nint window, RgbaFrame frame, CaptureGeometry geometry)
    {
        var width = geometry.ClientBounds.Width;
        var height = geometry.ClientBounds.Height;
        if (width <= 0 || height <= 0) return null;
        if (frame.Width == width && frame.Height == height) return frame;

        if (!NativeMethods.TryGetVisibleWindowRect(window, out var visible)) return null;
        var x = geometry.ClientScreen.X - visible.Left;
        var y = geometry.ClientScreen.Y - visible.Top;
        // 帧与可见窗口对不上时说明两者坐标系已经错位，宁可降级也不返回一张偏移的画面。
        if (x < 0 || y < 0 || x + width > frame.Width + 2 || y + height > frame.Height + 2) return null;
        return FrameOps.Crop(frame, x, y, width, height);
    }
}
