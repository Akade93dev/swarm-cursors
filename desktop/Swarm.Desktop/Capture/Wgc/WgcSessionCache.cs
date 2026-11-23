namespace Swarm.Desktop;

/// <summary>
/// 按窗口复用 WGC 会话。窗口关闭或会话不可用时立即丢弃，下次调用重新建立。
/// 只对"刚用过的会话"做失效回收，避免并发抓不同窗口时误释放别人正在用的会话。
/// 容量受限（默认 6）：WGC 每个会话都持有 D3D 纹理与取帧线程，内存紧张时按最久未用淘汰。
/// </summary>
internal sealed class WgcSessionCache : IDisposable
{
    private readonly Dictionary<nint, Entry> _sessions = new();
    private readonly object _gate = new();
    private readonly int _capacity;

    public WgcSessionCache(int? capacity = null) => _capacity = capacity ?? ReadCapacity();

    internal int Count
    {
        get { lock (_gate) return _sessions.Count; }
    }

    internal WgcCaptureResult Capture(nint window, int timeoutMs)
    {
        Entry entry;
        var evicted = new List<WgcWindowCapture>();
        lock (_gate)
        {
            PruneLocked(evicted);
            if (_sessions.TryGetValue(window, out var existing))
            {
                existing.LastUsed = DateTimeOffset.UtcNow;
                entry = existing;
            }
            else
            {
                entry = new Entry(new WgcWindowCapture(window));
                _sessions[window] = entry;
                EvictLocked(evicted);
            }
        }
        foreach (var stale in evicted) stale.Dispose();

        var result = entry.Session.Capture(timeoutMs);
        if (result.Code is "window_closed" or "wgc_unsupported" or "wgc_device_failed" or "wgc_item_failed")
            Drop(window, entry.Session);
        return result;
    }

    /// <summary>
    /// 清理已关闭窗口的会话：抓屏入口只在目标窗口仍存活时才会走到这里，
    /// 但用户可能切换到别的窗口，旧会话的取帧线程与 D3D 资源需要及时释放。
    /// </summary>
    private void PruneLocked(List<WgcWindowCapture> dead)
    {
        foreach (var pair in _sessions.Where(pair => !NativeMethods.IsWindow(pair.Key)).ToArray())
        {
            dead.Add(pair.Value.Session);
            _sessions.Remove(pair.Key);
        }
    }

    private void EvictLocked(List<WgcWindowCapture> evicted)
    {
        while (_sessions.Count > _capacity)
        {
            var oldest = _sessions.MinBy(pair => pair.Value.LastUsed);
            evicted.Add(oldest.Value.Session);
            _sessions.Remove(oldest.Key);
        }
    }

    private void Drop(nint window, WgcWindowCapture session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(window, out var current) && ReferenceEquals(current.Session, session))
                _sessions.Remove(window);
        }
        session.Dispose();
    }

    public void Dispose()
    {
        WgcWindowCapture[] all;
        lock (_gate)
        {
            all = _sessions.Values.Select(entry => entry.Session).ToArray();
            _sessions.Clear();
        }
        foreach (var session in all) session.Dispose();
    }

    private static int ReadCapacity()
    {
        var raw = Environment.GetEnvironmentVariable("SWARM_WGC_SESSIONS");
        return int.TryParse(raw, out var value) ? Math.Clamp(value, 1, 32) : 6;
    }

    private sealed class Entry
    {
        public Entry(WgcWindowCapture session)
        {
            Session = session;
            LastUsed = DateTimeOffset.UtcNow;
        }

        public WgcWindowCapture Session { get; }
        public DateTimeOffset LastUsed { get; set; }
    }
}
