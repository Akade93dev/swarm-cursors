using System.Runtime.InteropServices;
using Swarm.Core.Capture;
using Swarm.Core.Concurrency;
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
/// 窗口级抓屏入口：按后端链顺序尝试（WGC → PrintWindow → 屏幕裁剪），第一个非空白帧获胜。
/// 缓存与合并都是 per-window 的：不同窗口不会互相阻塞，同一窗口的并发观察共享同一帧；
/// 只有真正出帧的那一小段持有显示租约（等价于旧全局 Gate，但不再包含缓存判定）。
/// 写动作会使该窗口的世代 +1，旧帧立即失效，避免"点击后读到点击前画面"。
/// </summary>
public static class CaptureService
{
    private static readonly object Sync = new();
    private static readonly Dictionary<nint, CacheEntry> Cache = new();
    private static readonly Dictionary<nint, long> Generations = new();
    private static readonly SingleFlight<(nint Window, long Generation), WindowCapture> InFlight = new();
    private static readonly SemaphoreSlim DisplayGate = new(1, 1);
    private static readonly WgcSessionCache WgcSessions = new();
    private static readonly CapturePipeline Pipeline = CreatePipeline();
    private static readonly TimeSpan ReuseTtl = TimeSpan.FromMilliseconds(300);
    private static long _captures;
    private static long _reuseHits;
    private static long _gateTimeouts;

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
        var generation = CurrentGeneration(window);
        if (allowReuse && TryReuse(window, generation, out var cached)) return cached;

        return await InFlight.RunAsync((window, generation), async () =>
        {
            if (allowReuse && TryReuse(window, generation, out var shared)) return shared;

            if (!await DisplayGate.WaitAsync(TimeSpan.FromMilliseconds(3000), cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _gateTimeouts);
                throw new DesktopOperationException("capture_timeout", "抓屏排队超时：显示通道被长时间占用");
            }
            try
            {
                var capture = await Task.Run(() => Capture(window), cancellationToken).ConfigureAwait(false);
                lock (Sync)
                {
                    PruneClosedLocked();
                    Cache[window] = new CacheEntry(capture, DateTimeOffset.Now, generation);
                }
                Interlocked.Increment(ref _captures);
                return capture;
            }
            finally
            {
                DisplayGate.Release();
            }
        }).ConfigureAwait(false);
    }

    /// <summary>写动作后调用：让该窗口的缓存帧与在途观察立即作废。</summary>
    public static void Invalidate(nint window)
    {
        lock (Sync)
        {
            Generations[window] = (Generations.TryGetValue(window, out var generation) ? generation : 0) + 1;
            Cache.Remove(window);
        }
    }

    public static object Stats()
    {
        int cached;
        lock (Sync) cached = Cache.Count;
        return new
        {
            captures = Interlocked.Read(ref _captures),
            reuseHits = Interlocked.Read(ref _reuseHits),
            gateTimeouts = Interlocked.Read(ref _gateTimeouts),
            cachedWindows = cached,
            inflight = InFlight.Count,
            wgcSessions = WgcSessions.Count,
        };
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

    private static bool TryReuse(nint window, long generation, out WindowCapture capture)
    {
        lock (Sync)
        {
            if (Cache.TryGetValue(window, out var entry))
            {
                if (entry.Generation == generation &&
                    !entry.Capture.Occluded &&
                    DateTimeOffset.Now - entry.At < ReuseTtl)
                {
                    capture = entry.Capture;
                    Interlocked.Increment(ref _reuseHits);
                    return true;
                }
                // 过期或世代已变：立即丢弃。否则已关闭窗口的整帧 RGBA 会一直被字典强引用。
                Cache.Remove(window);
            }
        }
        capture = null!;
        return false;
    }

    /// <summary>清理已关闭窗口的缓存帧与世代；在写入新帧时顺手做，避免长期运行时内存只增不减。</summary>
    private static void PruneClosedLocked()
    {
        List<nint>? dead = null;
        foreach (var pair in Cache)
            if (!NativeMethods.IsWindow(pair.Key)) (dead ??= new List<nint>()).Add(pair.Key);
        if (dead == null) return;
        foreach (var window in dead)
        {
            Cache.Remove(window);
            Generations.Remove(window);
        }
    }

    private static long CurrentGeneration(nint window)
    {
        lock (Sync) return Generations.TryGetValue(window, out var generation) ? generation : 0;
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

    private sealed record CacheEntry(WindowCapture Capture, DateTimeOffset At, long Generation);
}
