namespace Swarm.Desktop;

/// <summary>
/// WGC 单帧探针：建一个临时会话取一帧后立即释放，用于诊断与自检，不进入默认抓屏链。
/// </summary>
internal static class WgcProbe
{
    internal static WgcCaptureResult TryCaptureOnce(nint window, int timeoutMs = 2000)
    {
        using var session = new WgcWindowCapture(window);
        return session.Capture(timeoutMs);
    }
}
