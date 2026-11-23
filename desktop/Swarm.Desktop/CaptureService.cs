using System.Runtime.InteropServices;
using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

/// <summary>
/// 窗口客户区抓屏结果。Backend 为实际出帧的后端（wgc/print/screen）；
/// Occluded 仅在屏幕裁剪降级且实测被遮挡时为真；FrameAgeMs 是 WGC 帧距采集时刻的时差。
/// </summary>
public sealed record WindowCapture(
    RgbaFrame Frame,
    RectInt Monitor,
    RectInt ClientScreen,
    RectInt ClientBounds,
    double DpiScale,
    nint Window,
    bool Occluded,
    bool Blank,
    string Backend = "print",
    long FrameAgeMs = 0)
{
    public int MonitorOffsetX => ClientScreen.X - Monitor.X;
    public int MonitorOffsetY => ClientScreen.Y - Monitor.Y;
}

/// <summary>
/// 窗口级抓屏入口：按后端链顺序尝试（WGC → PrintWindow → 屏幕裁剪），第一个非空白帧获胜；
/// 空白/失败时降级并如实标记 occluded。同一窗口 300ms 内复用，避免并发重复抓屏。
/// </summary>
public static class CaptureService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly WgcSessionCache WgcSessions = new();
    private static readonly CapturePipeline Pipeline = CreatePipeline();
    private static WindowCapture? _last;
    private static DateTimeOffset _lastAt;
    private static nint _lastWindow;

    private static CapturePipeline CreatePipeline()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SWARM_CAPTURE_BACKEND"), "screen", StringComparison.OrdinalIgnoreCase))
            return new CapturePipeline(new ScreenFallbackBackend());

        return new CapturePipeline(
            new WgcWindowBackend(WgcSessions),
            new PrintWindowBackend(),
            new ScreenFallbackBackend());
    }

    public static async Task<WindowCapture> CaptureAsync(nint window, CancellationToken cancellationToken = default, bool allowReuse = true)
    {
        EnsureCapturable(window);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (allowReuse && _last != null && _lastWindow == window && !_last.Occluded &&
                DateTimeOffset.Now - _lastAt < TimeSpan.FromMilliseconds(300))
            {
                return _last;
            }

            var capture = await Task.Run(() => Capture(window), cancellationToken).ConfigureAwait(false);
            _last = capture;
            _lastAt = DateTimeOffset.Now;
            _lastWindow = window;
            return capture;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void EnsureCapturable(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindow(window))
            throw new DesktopOperationException("window_closed", "目标窗口已关闭");
        if (NativeMethods.IsIconic(window))
            throw new DesktopOperationException("window_minimized", "目标窗口已最小化，请先还原");
    }

    public static (RectInt Monitor, RectInt ClientScreen, RectInt ClientBounds, double DpiScale) Geometry(nint window)
    {
        EnsureCapturable(window);
        var monitorHandle = NativeMethods.MonitorFromWindow(window, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref info)) throw new DesktopOperationException("window_closed", "无法读取目标所在显示器");
        var monitor = new RectInt(
            info.Monitor.Left,
            info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left,
            info.Monitor.Bottom - info.Monitor.Top);

        if (!NativeMethods.GetClientRect(window, out var client)) throw new DesktopOperationException("window_closed", "无法读取目标客户区");
        var origin = new NativeMethods.Win32Point { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(window, ref origin)) throw new DesktopOperationException("window_closed", "无法定位目标客户区");
        var clientScreen = new RectInt(origin.X, origin.Y, Math.Max(1, client.Right - client.Left), Math.Max(1, client.Bottom - client.Top));
        var dpi = NativeMethods.GetDpiForWindow(window);
        return (monitor, clientScreen, new RectInt(0, 0, clientScreen.Width, clientScreen.Height), (dpi == 0 ? 96u : dpi) / 96.0);
    }

    private static WindowCapture Capture(nint window)
    {
        var (monitor, clientScreen, clientBounds, dpi) = Geometry(window);
        var captured = Pipeline.Capture(
            window,
            new CaptureGeometry(monitor, clientScreen, clientBounds, dpi));

        if (captured.Frame is not { } frame)
            throw new DesktopOperationException("capture_unavailable", "抓屏失败：所有后端均未返回画面");

        return new WindowCapture(
            frame,
            monitor,
            clientScreen,
            clientBounds,
            dpi,
            window,
            captured.Occluded,
            captured.Blank,
            captured.Backend,
            captured.FrameAgeMs);
    }

    /// <summary>等比缩放到最长边不超过 maxEdge，降低截图经 IPC/MCP 传输的体积。</summary>
    public static RgbaFrame Scale(RgbaFrame frame, int maxEdge)
    {
        if (maxEdge <= 0 || (frame.Width <= maxEdge && frame.Height <= maxEdge)) return frame;
        var ratio = maxEdge / (double)Math.Max(frame.Width, frame.Height);
        var width = Math.Max(1, (int)Math.Round(frame.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(frame.Height * ratio));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(frame.Height - 1, (int)(y / ratio));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(frame.Width - 1, (int)(x / ratio));
                Buffer.BlockCopy(frame.Pixels, (sourceY * frame.Width + sourceX) * 4, pixels, (y * width + x) * 4, 4);
            }
        }
        return new RgbaFrame(width, height, pixels);
    }
}
