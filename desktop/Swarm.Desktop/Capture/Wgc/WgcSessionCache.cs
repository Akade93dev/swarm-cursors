namespace Swarm.Desktop;

/// <summary>
/// 按窗口复用 WGC 会话。窗口关闭或会话不可用时立即丢弃，下次调用重新建立。
/// 只对"刚用过的会话"做失效回收，避免并发抓不同窗口时误释放别人正在用的会话。
/// </summary>
internal sealed class WgcSessionCache : IDisposable
{
    private readonly Dictionary<nint, WgcWindowCapture> _sessions = new();
    private readonly object _gate = new();

    internal int Count
    {
        get { lock (_gate) return _sessions.Count; }
    }

    internal WgcCaptureResult Capture(nint window, int timeoutMs)
    {
        Prune();
        var session = GetOrCreate(window);
        var result = session.Capture(timeoutMs);
        if (result.Code is "window_closed" or "wgc_unsupported" or "wgc_device_failed" or "wgc_item_failed")
            Drop(window, session);
        return result;
    }

    /// <summary>
    /// 清理已关闭窗口的会话：抓屏入口只在目标窗口仍存活时才会走到这里，
    /// 但用户可能切换到别的窗口，旧会话的取帧线程与 D3D 资源需要及时释放。
    /// </summary>
    private void Prune()
    {
        var dead = new List<WgcWindowCapture>();
        lock (_gate)
        {
            foreach (var session in _sessions.Values)
                if (!NativeMethods.IsWindow(session.Window)) dead.Add(session);
            foreach (var session in dead) _sessions.Remove(session.Window);
        }
        foreach (var session in dead) session.Dispose();
    }

    private WgcWindowCapture GetOrCreate(nint window)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(window, out var existing)) return existing;
            var created = new WgcWindowCapture(window);
            _sessions[window] = created;
            return created;
        }
    }

    private void Drop(nint window, WgcWindowCapture session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(window, out var current) && ReferenceEquals(current, session))
                _sessions.Remove(window);
        }
        session.Dispose();
    }

    public void Dispose()
    {
        WgcWindowCapture[] all;
        lock (_gate)
        {
            all = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        foreach (var session in all) session.Dispose();
    }
}
