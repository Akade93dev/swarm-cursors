using System.Runtime.InteropServices;
using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

/// <summary>窗口客户区抓屏结果。Backend=screen 表示 PrintWindow 失败后降级屏幕裁剪；Occluded 仅在降级且实测被遮挡时为真。</summary>
public sealed record WindowCapture(
    RgbaFrame Frame,
    RectInt Monitor,
    RectInt ClientScreen,
    RectInt ClientBounds,
    double DpiScale,
    nint Window,
    bool Occluded,
    bool Blank,
    string Backend = "print")
{
    public int MonitorOffsetX => ClientScreen.X - Monitor.X;
    public int MonitorOffsetY => ClientScreen.Y - Monitor.Y;
}

/// <summary>
/// 窗口级抓屏：优先 PrintWindow 客户区，遮挡时仍能得到干净像素；
/// 空白/失败时降级为整屏裁剪并如实标记 occluded。同一窗口 300ms 内复用，避免并发重复抓屏。
/// </summary>
public static class CaptureService
{
    private const uint PwClientOnly = 0x1;
    private const uint PwRenderFullContent = 0x2;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static WindowCapture? _last;
    private static DateTimeOffset _lastAt;
    private static nint _lastWindow;

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

        var frame = CapturePrint(window, PwClientOnly | PwRenderFullContent, clientBounds.Width, clientBounds.Height)
                    ?? CapturePrintFullWindowClient(window, clientScreen, clientBounds, PwRenderFullContent)
                    ?? CapturePrintFullWindowClient(window, clientScreen, clientBounds, 0);
        var backend = "print";
        var occluded = false;
        var blank = frame == null || FrameStats.IsBlank(frame);
        if (frame == null || blank)
        {
            var fallback = ScreenCapture.CaptureForWindow(window);
            var offsetX = clientScreen.X - fallback.WorkArea.X;
            var offsetY = clientScreen.Y - fallback.WorkArea.Y;
            frame = Crop(fallback.Frame, offsetX, offsetY, clientBounds.Width, clientBounds.Height) ?? fallback.Frame;
            backend = "screen";
            // 屏幕像素只有在目标确实可见时才可信：前台，或客户区采样点全部归属目标。
            occluded = !IsUnobstructed(window, clientScreen, clientBounds);
            blank = FrameStats.IsBlank(frame);
        }

        return new WindowCapture(frame, monitor, clientScreen, clientBounds, dpi, window, occluded, blank, backend);
    }

    /// <summary>部分应用不支持 PW_CLIENTONLY：整窗渲染后按客户区偏移裁剪。</summary>
    private static RgbaFrame? CapturePrintFullWindowClient(nint window, RectInt clientScreen, RectInt clientBounds, uint flags)
    {
        if (!NativeMethods.GetWindowRect(window, out var windowRect)) return null;
        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        if (width <= 0 || height <= 0) return null;
        var full = CapturePrint(window, flags, width, height);
        if (full == null) return null;
        return Crop(full, clientScreen.X - windowRect.Left, clientScreen.Y - windowRect.Top, clientBounds.Width, clientBounds.Height);
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

    private static RgbaFrame? CapturePrint(nint window, uint flags, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var screen = NativeMethods.GetDC(0);
        nint memory = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            if (screen == 0) return null;
            memory = NativeMethods.CreateCompatibleDC(screen);
            if (memory == 0) return null;
            bitmap = NativeMethods.CreateCompatibleBitmap(screen, width, height);
            if (bitmap == 0) return null;
            previous = NativeMethods.SelectObject(memory, bitmap);
            if (!NativeMethods.PrintWindow(window, memory, flags)) return null;
            return ScreenCapture.ReadPixels(memory, bitmap, width, height);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (previous != 0) NativeMethods.SelectObject(memory, previous);
            if (bitmap != 0) NativeMethods.DeleteObject(bitmap);
            if (memory != 0) NativeMethods.DeleteDC(memory);
            if (screen != 0) NativeMethods.ReleaseDC(0, screen);
        }
    }

    private static RgbaFrame? Crop(RgbaFrame source, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var left = Math.Clamp(x, 0, source.Width - 1);
        var top = Math.Clamp(y, 0, source.Height - 1);
        var right = Math.Clamp(x + width, left + 1, source.Width);
        var bottom = Math.Clamp(y + height, top + 1, source.Height);
        var w = right - left;
        var h = bottom - top;
        if (w == source.Width && h == source.Height) return source;
        var pixels = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
        {
            Buffer.BlockCopy(source.Pixels, ((top + row) * source.Width + left) * 4, pixels, row * w * 4, w * 4);
        }
        return new RgbaFrame(w, h, pixels);
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
