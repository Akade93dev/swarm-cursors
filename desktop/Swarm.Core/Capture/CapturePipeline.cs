namespace Swarm.Core.Capture;

/// <summary>
/// 按顺序尝试抓屏后端：第一个拿到"非空白帧"的后端获胜；若全部失败，返回最后一次尝试
/// 并如实标记 Blank，供上层决定是否报错或展示。后端抛出的异常原样向上传递。
/// </summary>
public sealed class CapturePipeline
{
    private readonly ICaptureBackend[] _backends;

    public CapturePipeline(params ICaptureBackend[] backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        if (backends.Length == 0)
            throw new ArgumentException("至少需要一个抓屏后端", nameof(backends));
        if (Array.Exists(backends, backend => backend is null))
            throw new ArgumentException("抓屏后端不能为 null", nameof(backends));
        _backends = [.. backends];
    }

    public CapturedWindow Capture(nint window, CaptureGeometry geometry)
    {
        CaptureAttempt last = default;
        var lastName = "none";

        foreach (var backend in _backends)
        {
            var attempt = backend.TryCapture(window, geometry);
            last = attempt;
            lastName = backend.Name;

            if (attempt.Frame is { } frame && !FrameStats.IsBlank(frame))
                return new CapturedWindow(frame, attempt.Occluded, Blank: false, backend.Name, attempt.FrameAgeMs);
        }

        var blank = last.Frame is null || FrameStats.IsBlank(last.Frame);
        return new CapturedWindow(last.Frame, last.Occluded, blank, lastName, last.FrameAgeMs);
    }
}
