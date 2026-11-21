using Swarm.Core.Operations;

namespace Swarm.Tests;

public class OperationRunnerTests
{
    private sealed class Recorder : IOperationObserver
    {
        public List<OperationEvent> Events { get; } = new();
        public void Receive(OperationEvent operationEvent) => Events.Add(operationEvent);
    }

    private static OperationStep Step(string label, OperationKind kind = OperationKind.Click) =>
        new() { Id = Guid.NewGuid().ToString("N"), Kind = kind, Label = label };

    [Fact]
    public void 会话开始时广播会话事件()
    {
        var recorder = new Recorder();
        var runner = new OperationRunner(recorder);

        var session = runner.BeginSession("记事本：草稿", "pid 42");

        var started = Assert.Single(recorder.Events);
        Assert.Equal("session-start", started.Kind);
        Assert.Equal(session.Id, started.Session.Id);
        Assert.Equal("记事本：草稿", started.Session.Title);
    }

    [Fact]
    public async Task 步骤成功时先运行后成功()
    {
        var recorder = new Recorder();
        var runner = new OperationRunner(recorder);
        runner.BeginSession("测试", "pid 1");
        var step = Step("点击开始");

        await runner.RunAsync(step, _ => Task.CompletedTask, TimeSpan.FromSeconds(5));

        var steps = recorder.Events.Where(x => x.Kind == "step").ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal(OperationStatus.Running, steps[0].Step!.Status);
        Assert.Equal(OperationStatus.Succeeded, steps[1].Step!.Status);
        Assert.NotNull(steps[1].Step!.DurationMs);
    }

    [Fact]
    public async Task 步骤失败时记录错误并中断串行()
    {
        var recorder = new Recorder();
        var runner = new OperationRunner(recorder);
        runner.BeginSession("测试", "pid 1");
        var first = Step("输入");
        var second = Step("继续输入");
        var executed = 0;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunSequenceAsync(
            new (OperationStep, Func<CancellationToken, Task>)[]
            {
                (first, _ => throw new InvalidOperationException("元素不存在")),
                (second, _ => { executed++; return Task.CompletedTask; }),
            },
            TimeSpan.FromSeconds(5)));

        Assert.Equal("元素不存在", error.Message);
        Assert.Equal(0, executed);
        Assert.Equal(OperationStatus.Failed, first.Status);
        Assert.Equal("元素不存在", first.Error);
        Assert.Equal(OperationStatus.Pending, second.Status);
    }

    [Fact]
    public async Task 步骤超时标记失败()
    {
        var recorder = new Recorder();
        var runner = new OperationRunner(recorder);
        runner.BeginSession("测试", "pid 1");
        var step = Step("等待窗口");

        await Assert.ThrowsAnyAsync<TimeoutException>(() =>
            runner.RunAsync(step, async token => await Task.Delay(TimeSpan.FromSeconds(5), token), TimeSpan.FromMilliseconds(60)));

        Assert.Equal(OperationStatus.Failed, step.Status);
        Assert.Equal("操作超时", step.Error);
    }

    [Fact]
    public async Task 取消标记为已取消()
    {
        var recorder = new Recorder();
        var runner = new OperationRunner(recorder);
        runner.BeginSession("测试", "pid 1");
        var step = Step("点击");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(step, async token => await Task.Delay(TimeSpan.FromSeconds(5), token), TimeSpan.FromSeconds(5), cancel.Token));

        Assert.Equal(OperationStatus.Canceled, step.Status);
    }

    [Fact]
    public async Task 并行步骤互不阻塞且都成功()
    {
        var recorder = new Recorder();
        var runner = new OperationRunner(recorder);
        runner.BeginSession("测试", "pid 1");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Step("观察");
        var second = Step("刷新可视化");

        await runner.RunParallelAsync(
            new (OperationStep, Func<CancellationToken, Task>)[]
            {
                (first, async token => await gate.Task.WaitAsync(TimeSpan.FromSeconds(2), token)),
                (second, _ => { gate.TrySetResult(); return Task.CompletedTask; }),
            },
            TimeSpan.FromSeconds(5));

        Assert.Equal(OperationStatus.Succeeded, first.Status);
        Assert.Equal(OperationStatus.Succeeded, second.Status);
    }

    [Fact]
    public async Task 会话结束汇总失败状态()
    {
        var recorder = new Recorder();
        var runner = new OperationRunner(recorder);
        runner.BeginSession("测试", "pid 1");
        var step = Step("点击");
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(step, _ => throw new InvalidOperationException("失败"), TimeSpan.FromSeconds(1)));

        runner.EndSession();

        var ended = recorder.Events.Last();
        Assert.Equal("session-end", ended.Kind);
        Assert.Equal("failed", ended.Status);
        Assert.NotNull(ended.DurationMs);
    }
}
