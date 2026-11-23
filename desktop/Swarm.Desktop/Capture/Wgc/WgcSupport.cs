namespace Swarm.Desktop;

/// <summary>
/// WGC 可用性按"真实抓屏结果"记账，不做主动激活探测：默认视为可用，
/// 直到某次抓屏返回 wgc_unsupported（如 Win10 旧版缺 IDirect3D11CaptureFramePoolStatics2），
/// 之后本进程不再尝试 WGC，由抓屏链降级 PrintWindow/屏幕裁剪。这样既不多做 WinRT 激活，
/// 也不会因为上报线程的 COM 状态而误判——capabilities 是在首帧抓屏之后组装的，状态已被真实结果刷新。
/// </summary>
internal static class WgcSupport
{
    private const int Unknown = 0;
    private const int Available = 1;
    private const int Unsupported = 2;

    private static int _state = Unknown;
    private static string _reason = "";

    internal static bool IsAvailable => Volatile.Read(ref _state) != Unsupported;

    /// <summary>不可用原因（含 HRESULT），仅在 <see cref="IsAvailable"/> 为 false 时有意义。</summary>
    internal static string UnavailableReason => _reason;

    internal static void MarkAvailable() => Volatile.Write(ref _state, Available);

    internal static void MarkUnsupported(string reason)
    {
        _reason = reason;
        Volatile.Write(ref _state, Unsupported);
    }
}
