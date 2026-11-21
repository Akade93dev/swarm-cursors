using Swarm.Core.Actions;

namespace Swarm.Tests;

public class ActionCoordinatorTests
{
    [Fact]
    public async Task 渲染准备成功后才执行真实动作()
    {
        var coordinator = new ActionCoordinator();
        var events = new List<string>();
        await coordinator.RunAsync(
            _ => { events.Add("prepare"); return Task.CompletedTask; },
            _ => { events.Add("execute"); return Task.CompletedTask; });
        Assert.Equal(new[] { "prepare", "execute" }, events);
    }

    [Fact]
    public async Task 渲染准备失败时真实动作绝不执行()
    {
        var coordinator = new ActionCoordinator();
        var executed = false;
        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.RunAsync(
            _ => throw new TimeoutException("renderer timeout"),
            _ => { executed = true; return Task.CompletedTask; }));
        Assert.False(executed);
    }

    [Fact]
    public async Task 动作串行且急停会取消等待队列()
    {
        var coordinator = new ActionCoordinator();
        var firstExecute = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var first = coordinator.RunAsync(
            _ => Task.CompletedTask,
            async token =>
            {
                var now = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, now);
                firstExecute.TrySetResult();
                try { await releaseFirst.Task.WaitAsync(token); }
                finally { Interlocked.Decrement(ref active); }
            });
        await firstExecute.Task;
        var queuedExecuted = false;
        var queued = coordinator.RunAsync(
            _ => Task.CompletedTask,
            _ => { queuedExecuted = true; return Task.CompletedTask; });

        coordinator.Stop();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(queuedExecuted);
        Assert.Equal(1, maximum);
    }
}
