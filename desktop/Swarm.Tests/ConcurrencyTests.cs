using Swarm.Core.Concurrency;
using Swarm.Core.Governance;

namespace Swarm.Tests;

public class ConcurrencyTests
{
    [Fact]
    public async Task 同一窗口的写动作串行且不同窗口并行()
    {
        var leases = new LeaseManager();
        using var windowA = await leases.AcquireWindowAsync(1, "a", TimeSpan.FromSeconds(1));
        using var windowB = await leases.AcquireWindowAsync(2, "b", TimeSpan.FromSeconds(1));

        var secondA = leases.AcquireWindowAsync(1, "c", TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.False(secondA.IsCompleted, "同一窗口的第二个租约不应立即获得");

        windowA.Dispose();
        using var granted = await secondA.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(secondA.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task 窗口租约按先进先出授予()
    {
        var leases = new LeaseManager();
        var first = await leases.AcquireWindowAsync(9, "first", TimeSpan.FromSeconds(1));
        var second = leases.AcquireWindowAsync(9, "second", TimeSpan.FromSeconds(5));
        var third = leases.AcquireWindowAsync(9, "third", TimeSpan.FromSeconds(5));
        await Task.Delay(30);

        first.Dispose();
        using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(30);
        Assert.False(third.IsCompleted, "后到者不能越过先到者");

        secondLease.Dispose();
        using var thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(third.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task 窗口租约等待超时会抛出带队列信息的异常()
    {
        var leases = new LeaseManager();
        using var held = await leases.AcquireWindowAsync(3, "holder", TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<LeaseTimeoutException>(
            () => leases.AcquireWindowAsync(3, "waiter", TimeSpan.FromMilliseconds(60)));
        Assert.Equal("window:0x3", error.Code);
        Assert.True(error.QueueLength >= 1);
    }

    [Fact]
    public async Task 显示租约同一时刻只允许一个持有者()
    {
        var leases = new LeaseManager();
        var first = await leases.AcquireDisplayAsync("first", TimeSpan.FromSeconds(1));
        var second = leases.AcquireDisplayAsync("second", TimeSpan.FromSeconds(5));

        await Task.Delay(40);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var granted = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(1, leases.Snapshot().MaxConcurrentDisplay);
    }

    [Fact]
    public async Task 只读作业按同一把Key合并()
    {
        var flight = new SingleFlight<string, int>();
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Factory()
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return 42;
        }

        var first = flight.RunAsync("window-1", Factory);
        var second = flight.RunAsync("window-1", Factory);
        var other = flight.RunAsync("window-2", Factory);
        Assert.Same(first, second);
        Assert.NotSame(first, other);
        Assert.Equal(2, flight.Count);

        release.SetResult();
        Assert.Equal(42, await first);
        Assert.Equal(42, await second);
        Assert.Equal(42, await other);
        Assert.Equal(2, calls);
        Assert.Equal(0, flight.Count);
    }

    [Fact]
    public async Task 确认队列串行展示且返回各自结果()
    {
        var queue = new ConfirmationQueue();
        var presenter = new RecordingPresenter();
        var first = queue.AskAsync(presenter, "t1", "m1", false);
        var second = queue.AskAsync(presenter, "t2", "m2", true);

        await WaitUntilAsync(() => presenter.Started == 1);
        Assert.Equal(1, presenter.Started);

        presenter.Complete(true);
        Assert.True(await first);
        await WaitUntilAsync(() => presenter.Started == 2);
        presenter.Complete(false);
        Assert.False(await second);
        Assert.Equal(new[] { "t1", "t2" }, presenter.Titles);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("condition not met");
    }

    private sealed class RecordingPresenter : IConfirmationPresenter
    {
        private readonly object _sync = new();
        private TaskCompletionSource<bool>? _pending;
        private readonly List<string> _titles = new();

        public int Started { get; private set; }

        public IReadOnlyList<string> Titles
        {
            get { lock (_sync) return _titles.ToArray(); }
        }

        public Task<bool> AskAsync(string title, string message, bool danger, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                Started++;
                _titles.Add(title);
                _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _pending.Task;
            }
        }

        public void Complete(bool result)
        {
            TaskCompletionSource<bool>? pending;
            lock (_sync) pending = _pending;
            pending?.TrySetResult(result);
        }
    }
}
