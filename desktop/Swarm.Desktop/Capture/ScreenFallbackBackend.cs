using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

/// <summary>
/// 屏幕裁剪兜底：PrintWindow 拿不到有效像素时，从整屏截图里按客户区裁剪。
/// Occluded 只有在客户区采样点并非全部归属目标时才为真，避免把被遮挡画面当成窗口内容。
/// </summary>
internal sealed class ScreenFallbackBackend : ICaptureBackend
{
    public string Name => "screen";

    public CaptureAttempt TryCapture(nint window, CaptureGeometry geometry)
    {
        var fallback = ScreenCapture.CaptureForWindow(window);
        var offsetX = geometry.ClientScreen.X - fallback.WorkArea.X;
        var offsetY = geometry.ClientScreen.Y - fallback.WorkArea.Y;
        var frame = FrameOps.Crop(fallback.Frame, offsetX, offsetY, geometry.ClientBounds.Width, geometry.ClientBounds.Height)
                    ?? fallback.Frame;
        return new CaptureAttempt(frame, !IsUnobstructed(window, geometry.ClientScreen, geometry.ClientBounds));
    }

    /// <summary>客户区 5×5 采样全部命中目标即视为未遮挡；部分遮挡按保守策略判为遮挡。</summary>
    private static bool IsUnobstructed(nint window, RectInt clientScreen, RectInt clientBounds)
    {
        if (NativeMethods.GetForegroundWindow() == window) return true;
        var client = new RectInt(clientScreen.X, clientScreen.Y, clientBounds.Width, clientBounds.Height);
        foreach (var (sampleX, sampleY) in OcclusionSampler.SamplePoints(client))
        {
            var hit = NativeMethods.WindowFromPoint(new NativeMethods.Win32Point { X = sampleX, Y = sampleY });
            if (hit == 0 || NativeMethods.GetAncestor(hit, 2) != window) return false;
        }
        return true;
    }
}
