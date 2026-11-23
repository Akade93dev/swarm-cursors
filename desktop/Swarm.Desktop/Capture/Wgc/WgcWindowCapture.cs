using Swarm.Core.Capture;

namespace Swarm.Desktop;

internal sealed record WgcCaptureResult(bool Success, string Code, string Detail, RgbaFrame? Frame, long AgeMs)
{
    internal static WgcCaptureResult Fail(string code, string detail) => new(false, code, detail, null, 0);
}

/// <summary>
/// 单个窗口的 WGC 会话：item + FreeThreaded 帧池 + capture session 常驻，由后台线程持续取帧并只保留最新一帧，
/// 取帧时再做 GPU→CPU 读回。窗口尺寸/DPI 变化时按新尺寸 Recreate 帧池；最小化与窗口关闭返回显式错误码。
/// </summary>
internal sealed class WgcWindowCapture : IDisposable
{
    private readonly nint _window;
    private readonly object _gate = new();

    private const int SizeCheckIntervalMs = 500;

    private nint _interop, _item, _pool, _session;
    private int _width, _height;
    private long _lastSizeCheck;
    private long _lastAliveCheck;
    private Thread? _drain;
    private volatile bool _stopping;
    private nint _latestFrame;
    private long _latestAt;
    private long _frames;
    private string _drainError = "";

    internal WgcWindowCapture(nint window) => _window = window;

    internal nint Window => _window;

    /// <summary>累计从帧池取到的帧数，用于诊断会话是否还在出帧。</summary>
    internal long FramesReceived => Interlocked.Read(ref _frames);

    internal WgcCaptureResult Capture(int timeoutMs)
    {
        bool closed;
        lock (_gate) closed = !NativeMethods.IsWindow(_window);
        if (closed)
        {
            Dispose();
            return WgcCaptureResult.Fail("window_closed", "目标窗口已关闭");
        }
        if (NativeMethods.IsIconic(_window))
            return WgcCaptureResult.Fail("window_minimized", "目标窗口已最小化，请先还原");

        lock (_gate)
        {
            if (_session == 0)
            {
                var failure = StartLocked();
                if (failure is not null) return failure;
            }
        }

        var deadline = Environment.TickCount64 + Math.Max(1, timeoutMs);
        while (true)
        {
            nint frame = 0;
            long capturedAt = 0;
            string drainError;
            lock (_gate)
            {
                drainError = _drainError;
                if (_latestFrame != 0)
                {
                    frame = _latestFrame;
                    capturedAt = _latestAt;
                    WgcInterop.AddRef(frame);
                }
            }

            if (drainError.Length > 0) return WgcCaptureResult.Fail("wgc_session_failed", drainError);

            if (frame != 0)
            {
                try
                {
                    if (!WgcDevice.TryGet(out var device, out var context, out _, out var deviceError))
                        return WgcCaptureResult.Fail("wgc_device_failed", deviceError);
                    var pixels = WgcFrameReader.TryRead(device, context, frame, out var detail);
                    if (pixels is null) return WgcCaptureResult.Fail("wgc_read_failed", detail);
                    return new WgcCaptureResult(true, "ok", $"{pixels.Width}x{pixels.Height}", pixels,
                        Environment.TickCount64 - capturedAt);
                }
                finally
                {
                    WgcInterop.Release(frame);
                }
            }

            if (Environment.TickCount64 >= deadline)
                return WgcCaptureResult.Fail("wgc_frame_timeout", $"{timeoutMs}ms 内无帧");
            Thread.Sleep(5);
        }
    }

    private WgcCaptureResult? StartLocked()
    {
        if (!WgcDevice.TryGet(out _, out _, out var winrtDevice, out var deviceError))
            return WgcCaptureResult.Fail("wgc_device_failed", deviceError);

        var hr = WgcInterop.GetActivationFactory(WgcInterop.CaptureItemClass, WgcInterop.IidItemInterop, out _interop);
        if (hr < 0)
        {
            ResetLocked();
            return WgcCaptureResult.Fail("wgc_unsupported", $"IGraphicsCaptureItemInterop 0x{hr:X8}");
        }

        hr = WgcInterop.CreateForWindow(_interop, _window, WgcInterop.IidItem, out _item);
        if (hr < 0)
        {
            ResetLocked();
            return WgcCaptureResult.Fail("wgc_item_failed", $"CreateForWindow 0x{hr:X8}");
        }

        hr = WgcInterop.GetItemSize(_item, out var size);
        if (hr < 0 || size.Width <= 0 || size.Height <= 0)
        {
            ResetLocked();
            return WgcCaptureResult.Fail("wgc_item_failed", hr < 0 ? $"get_Size 0x{hr:X8}" : "窗口尺寸为 0");
        }

        hr = WgcInterop.GetActivationFactory(WgcInterop.FramePoolClass, WgcInterop.IidFramePoolStatics2, out var statics);
        if (hr < 0)
        {
            ResetLocked();
            return WgcCaptureResult.Fail("wgc_unsupported", $"IDirect3D11CaptureFramePoolStatics2 0x{hr:X8}");
        }

        try
        {
            hr = WgcInterop.CreateFreeThreaded(statics, winrtDevice, size, out _pool);
        }
        finally
        {
            WgcInterop.Release(statics);
        }
        if (hr < 0)
        {
            ResetLocked();
            return WgcCaptureResult.Fail("wgc_pool_failed", $"CreateFreeThreaded 0x{hr:X8}");
        }

        hr = WgcInterop.CreateCaptureSession(_pool, _item, out _session);
        if (hr >= 0) hr = WgcInterop.StartCapture(_session);
        if (hr < 0)
        {
            ResetLocked();
            return WgcCaptureResult.Fail("wgc_session_failed", $"StartCapture 0x{hr:X8}");
        }

        _width = size.Width;
        _height = size.Height;
        _lastSizeCheck = Environment.TickCount64;
        _frames = 0;
        _drainError = "";
        _stopping = false;
        _drain = new Thread(DrainLoop)
        {
            IsBackground = true,
            Name = $"wgc-drain-0x{_window:X}",
        };
        _drain.Start();
        return null;
    }

    /// <summary>持续取帧并只保留最新一帧：不在这里读像素，避免每帧都做 GPU→CPU 拷贝。</summary>
    private void DrainLoop()
    {
        if (!WgcDevice.TryGet(out _, out _, out var winrtDevice, out var deviceError))
        {
            lock (_gate) _drainError = deviceError;
            return;
        }

        while (!_stopping)
        {
            // 窗口关闭后会话可能永远不再出帧；后台线程必须自己退出，否则长期运行的进程会积累僵尸会话。
            if (Environment.TickCount64 - _lastAliveCheck >= SizeCheckIntervalMs)
            {
                _lastAliveCheck = Environment.TickCount64;
                if (!NativeMethods.IsWindow(_window)) return;
            }

            nint pool;
            nint item;
            lock (_gate)
            {
                pool = _pool;
                item = _item;
            }
            if (pool == 0) return;

            var hr = WgcInterop.TryGetNextFrame(pool, out var frame);
            if (hr < 0 || frame == 0)
            {
                // 尺寸/DPI 变化后 WGC 可能直接停帧，此时不能只靠帧自带尺寸触发 Recreate。
                if (item != 0 && Environment.TickCount64 - _lastSizeCheck >= SizeCheckIntervalMs)
                {
                    _lastSizeCheck = Environment.TickCount64;
                    if (WgcInterop.GetItemSize(item, out var itemSize) >= 0)
                        MaybeRecreate(pool, winrtDevice, itemSize);
                }
                Thread.Sleep(8);
                continue;
            }

            WgcInterop.GetFrameContentSize(frame, out var size);
            var recreate = false;
            lock (_gate)
            {
                if (size.Width > 0 && size.Height > 0 && (size.Width != _width || size.Height != _height))
                {
                    _width = size.Width;
                    _height = size.Height;
                    recreate = true;
                }
                WgcInterop.Release(_latestFrame);
                _latestFrame = frame; // 接管这一帧的引用
                _latestAt = Environment.TickCount64;
                Interlocked.Increment(ref _frames);
            }

            // 窗口尺寸/DPI 变化后按新尺寸重建帧池；失败时保留旧池，下一帧再试。
            if (recreate) WgcInterop.Recreate(pool, winrtDevice, size);
            Thread.Sleep(1);
        }
    }

    private void MaybeRecreate(nint pool, nint winrtDevice, WgcInterop.SizeInt32 size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        lock (_gate)
        {
            if (size.Width == _width && size.Height == _height) return;
            _width = size.Width;
            _height = size.Height;
        }
        WgcInterop.Recreate(pool, winrtDevice, size);
    }

    public void Dispose()
    {
        _stopping = true;
        var drain = _drain;
        // 抓帧调用卡住时宁可不释放 COM 对象，也不制造释放竞态。
        if (drain is not null && drain.IsAlive && !drain.Join(2000)) return;
        lock (_gate)
        {
            _drain = null;
            ResetLocked();
        }
    }

    private void ResetLocked()
    {
        WgcInterop.Release(_latestFrame);
        _latestFrame = 0;
        _latestAt = 0;
        WgcInterop.Release(_session);
        _session = 0;
        WgcInterop.Release(_pool);
        _pool = 0;
        WgcInterop.Release(_item);
        _item = 0;
        WgcInterop.Release(_interop);
        _interop = 0;
        _width = 0;
        _height = 0;
    }
}
