namespace Swarm.Core.Concurrency;

/// <summary>租约等待超时：区分窗口排队与显示闸门排队，便于上层给出可操作的错误码。</summary>
public sealed class LeaseTimeoutException : TimeoutException
{
    public LeaseTimeoutException(string code, string owner, TimeSpan waited, int queueLength)
        : base($"租约等待超时：{code}（{waited.TotalMilliseconds:F0} ms，队列 {queueLength}）")
    {
        Code = code;
        Owner = owner;
        WaitedMs = (long)waited.TotalMilliseconds;
        QueueLength = queueLength;
    }

    public string Code { get; }
    public string Owner { get; }
    public long WaitedMs { get; }
    public int QueueLength { get; }
}

public sealed record LeaseStats(
    long WindowAcquisitions,
    long WindowTimeouts,
    long DisplayAcquisitions,
    long DisplayTimeouts,
    int MaxConcurrentDisplay,
    int DisplayWaiters,
    int WindowWaiters);

/// <summary>
/// 两级租约：<c>WindowLease(hwnd)</c> 允许同一窗口上的写动作串行、不同窗口并行；
/// <c>DisplayLease</c> 是叶子锁，只在真正抓屏的那一小段时间持有。
/// 顺序恒定 Window → Display，Display 内不得再申请任何锁，避免死锁。
/// 队列严格 FIFO：同会话连续获取会自然排到队尾，不会饿死其它会话。
/// </summary>
public sealed class LeaseManager
{
    private readonly FairAsyncLock _display = new();
    private readonly Dictionary<nint, WindowGate> _windows = new();
    private readonly object _sync = new();
    private long _windowAcquisitions;
    private long _windowTimeouts;
    private long _displayAcquisitions;
    private long _displayTimeouts;
    private int _displayHolders;
    private int _maxConcurrentDisplay;
    private const int WindowGatePruneThreshold = 32;

    public async Task<IDisposable> AcquireWindowAsync(nint window, string owner, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        WindowGate gate;
        lock (_sync)
        {
            if (!_windows.TryGetValue(window, out gate!))
            {
                gate = new WindowGate();
                _windows[window] = gate;
            }
            // 引用计数保证“移出字典”只发生在无人持有该闸门时，避免同一窗口出现两把锁。
            gate.Refs++;
            if (_windows.Count > WindowGatePruneThreshold) PruneIdleGatesLocked(window);
        }

        var waited = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var lease = await gate.Lock.AcquireAsync(owner, timeout, cancellationToken);
            Interlocked.Increment(ref _windowAcquisitions);
            return new WindowLease(lease, this, gate);
        }
        catch (LeaseTimeoutException error)
        {
            ReleaseGate(gate);
            Interlocked.Increment(ref _windowTimeouts);
            throw new LeaseTimeoutException($"window:0x{window:x}", owner, waited.Elapsed, error.QueueLength);
        }
        catch
        {
            ReleaseGate(gate);
            throw;
        }
    }

    private void ReleaseGate(WindowGate gate)
    {
        lock (_sync) gate.Refs--;
    }

    /// <summary>长期运行时窗口句柄会不断累积；只回收“无引用”的闸门，不影响仍在排队的窗口。</summary>
    private void PruneIdleGatesLocked(nint keep)
    {
        List<nint>? dead = null;
        foreach (var pair in _windows)
            if (pair.Key != keep && pair.Value.Refs == 0) (dead ??= new List<nint>()).Add(pair.Key);
        if (dead == null) return;
        foreach (var window in dead) _windows.Remove(window);
    }

    private sealed class WindowGate
    {
        public readonly FairAsyncLock Lock = new();
        public int Refs;
    }

    private sealed class WindowLease : IDisposable
    {
        private readonly IDisposable _inner;
        private readonly LeaseManager _manager;
        private readonly WindowGate _gate;
        private int _disposed;

        public WindowLease(IDisposable inner, LeaseManager manager, WindowGate gate)
        {
            _inner = inner;
            _manager = manager;
            _gate = gate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _inner.Dispose();
            _manager.ReleaseGate(_gate);
        }
    }

    public async Task<IDisposable> AcquireDisplayAsync(string owner, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var lease = await _display.AcquireAsync(owner, timeout, cancellationToken);
            Interlocked.Increment(ref _displayAcquisitions);
            var holders = Interlocked.Increment(ref _displayHolders);
            UpdateMax(holders);
            return new DisplayLease(lease, this);
        }
        catch (LeaseTimeoutException error)
        {
            Interlocked.Increment(ref _displayTimeouts);
            throw new LeaseTimeoutException("display", owner, waited.Elapsed, error.QueueLength);
        }
    }

    public LeaseStats Snapshot()
    {
        int windowWaiters;
        lock (_sync) windowWaiters = _windows.Values.Sum(gate => gate.Lock.WaitingCount);
        return new LeaseStats(
            Interlocked.Read(ref _windowAcquisitions),
            Interlocked.Read(ref _windowTimeouts),
            Interlocked.Read(ref _displayAcquisitions),
            Interlocked.Read(ref _displayTimeouts),
            Volatile.Read(ref _maxConcurrentDisplay),
            _display.WaitingCount,
            windowWaiters);
    }

    private void UpdateMax(int holders)
    {
        var current = Volatile.Read(ref _maxConcurrentDisplay);
        while (holders > current)
        {
            var seen = Interlocked.CompareExchange(ref _maxConcurrentDisplay, holders, current);
            if (seen == current) return;
            current = seen;
        }
    }

    private void ReleaseDisplay(IDisposable inner)
    {
        Interlocked.Decrement(ref _displayHolders);
        inner.Dispose();
    }

    private sealed class DisplayLease : IDisposable
    {
        private readonly IDisposable _inner;
        private readonly LeaseManager _manager;
        private int _disposed;

        public DisplayLease(IDisposable inner, LeaseManager manager)
        {
            _inner = inner;
            _manager = manager;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _manager.ReleaseDisplay(_inner);
        }
    }

    /// <summary>严格 FIFO 的异步互斥；取消的等待者留在队列中由 Pump 跳过，不破坏顺序。</summary>
    private sealed class FairAsyncLock
    {
        private readonly object _sync = new();
        private readonly Queue<Waiter> _waiters = new();
        private bool _held;

        public int WaitingCount
        {
            get { lock (_sync) return _waiters.Count; }
        }

        public async Task<IDisposable> AcquireAsync(string owner, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Waiter waiter;
            lock (_sync)
            {
                // 只有“无人持有且无人排队”才能直接拿到；否则必须入队，
                // 否则 Release 与下一次 Acquire 之间的空隙会让新来的请求插到队首（破坏 FIFO）。
                if (!_held && _waiters.Count == 0)
                {
                    _held = true;
                    return new Releaser(this);
                }
                waiter = new Waiter(owner);
                _waiters.Enqueue(waiter);
            }

            using var registration = cancellationToken.Register(static state => ((Waiter)state!).Cancel(), waiter);
            if (timeout <= TimeSpan.Zero)
            {
                if (waiter.Cancel()) throw new LeaseTimeoutException("lease", owner, TimeSpan.Zero, WaitingCount);
                return await waiter.Task.ConfigureAwait(false);
            }

            var completed = await Task.WhenAny(waiter.Task, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false);
            if (ReferenceEquals(completed, waiter.Task))
                return await waiter.Task.ConfigureAwait(false);

            if (waiter.Cancel())
                throw new LeaseTimeoutException("lease", owner, timeout, WaitingCount);
            // 超时的同一瞬间被授予：按授予处理，避免拿到租约却报告超时。
            return await waiter.Task.ConfigureAwait(false);
        }

        private void Release()
        {
            lock (_sync) _held = false;
            Pump();
        }

        private void Pump()
        {
            while (true)
            {
                Waiter next;
                lock (_sync)
                {
                    if (_held || _waiters.Count == 0) return;
                    next = _waiters.Dequeue();
                    _held = true;
                }

                if (next.Grant(new Releaser(this))) return;
                // 等待者在出队的同时被取消：回滚占用，继续找下一个。
                lock (_sync) _held = false;
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly FairAsyncLock _owner;
            private int _released;

            public Releaser(FairAsyncLock owner) => _owner = owner;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                _owner.Release();
            }
        }

        private sealed class Waiter
        {
            private readonly TaskCompletionSource<IDisposable> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Waiter(string owner) => Owner = owner;

            public string Owner { get; }

            public Task<IDisposable> Task => _completion.Task;

            public bool Grant(IDisposable lease) => _completion.TrySetResult(lease);

            public bool Cancel() => _completion.TrySetCanceled();
        }
    }
}
