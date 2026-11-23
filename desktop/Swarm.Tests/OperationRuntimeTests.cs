using Swarm.Core.Capture;
using Swarm.Core.Operations;
using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class OperationRuntimeTests
{
    [Fact]
    public async Task TimeoutCancelsTheBodyInsteadOfLeavingItRunning()
    {
        var observer = new Collector();
        var runner = new OperationRunner(observer);
        runner.BeginSession("测试", "pid 1");
        var step = new OperationStep { Id = "a", Kind = OperationKind.Click, Label = "点击" };
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(step, async token =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult(true);
                throw;
            }
        }, TimeSpan.FromMilliseconds(100)));

        Assert.True(await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        var end = observer.Events.Last(item => item.Step?.Id == "a" && item.Phase == "end");
        Assert.Equal(OperationStatus.Failed, end.Step!.Status);
        Assert.Equal("操作超时", end.Step.Error);
    }

    [Fact]
    public async Task StopDrainsRunningStepsBeforeEndingTheSession()
    {
        var observer = new Collector();
        var runner = new OperationRunner(observer);
        runner.BeginSession("测试", "pid 1");
        var step = new OperationStep { Id = "slow", Kind = OperationKind.Wait, Label = "等待" };
        var run = runner.RunAsync(step, async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                await Task.Delay(80);
            }
        }, TimeSpan.FromSeconds(30));
        await Task.Delay(120);

        await runner.StopAsync(TimeSpan.FromSeconds(2));

        var stepEnd = observer.Events.FindIndex(item => item.Kind == "step" && item.Step?.Id == "slow" && item.Phase == "end");
        var sessionEnd = observer.Events.FindIndex(item => item.Kind == "session-end");
        Assert.True(stepEnd >= 0, "步骤结束事件缺失");
        Assert.True(sessionEnd > stepEnd, "session-end 必须晚于在飞步骤结束");
        Assert.Equal("stopped", observer.Events[sessionEnd].Status);
        Assert.Equal(OperationStatus.Canceled, observer.Events[stepEnd].Step!.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task EventsCarryMonotonicSeqSessionIdAndPhase()
    {
        var observer = new Collector();
        var runner = new OperationRunner(observer);
        var session = runner.BeginSession("测试", "pid 1");
        await runner.RunAsync(new OperationStep { Id = "1", Kind = OperationKind.Observe, Label = "观察" }, _ => Task.CompletedTask, TimeSpan.FromSeconds(2));
        await runner.RunAsync(new OperationStep { Id = "2", Kind = OperationKind.Click, Label = "点击" }, _ => Task.CompletedTask, TimeSpan.FromSeconds(2));
        runner.EndSession();

        Assert.All(observer.Events, item => Assert.Equal(session.Id, item.SessionId));
        Assert.Equal(Enumerable.Range(1, observer.Events.Count), observer.Events.Select(item => (int)item.Seq));
        Assert.Equal("start", observer.Events[0].Phase);
        Assert.Equal("end", observer.Events[^1].Phase);
        Assert.Equal(2, observer.Events.Count(item => item.Kind == "step" && item.Phase == "end"));
    }

    [Fact]
    public async Task ParallelFailureCancelsSiblingSteps()
    {
        var observer = new Collector();
        var runner = new OperationRunner(observer);
        runner.BeginSession("测试", "pid 1");
        var siblingCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = new OperationStep { Id = "fail", Kind = OperationKind.Click, Label = "失败" };
        var sibling = new OperationStep { Id = "sibling", Kind = OperationKind.Wait, Label = "并行" };

        await Assert.ThrowsAnyAsync<Exception>(() => runner.RunParallelAsync(new[]
        {
            (failing, (Func<CancellationToken, Task>)(_ => throw new InvalidOperationException("boom"))),
            (sibling, (Func<CancellationToken, Task>)(async token =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    siblingCanceled.TrySetResult(true);
                    throw;
                }
            })),
        }, TimeSpan.FromSeconds(5)));

        Assert.True(await siblingCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ExecutionSuccessDoesNotImplyVerification()
    {
        var observer = new Collector();
        var runner = new OperationRunner(observer);
        runner.BeginSession("测试", "pid 1");
        var step = new OperationStep { Id = "unverified", Kind = OperationKind.Click, Label = "点击" };
        step.ReportTransport(InputTier.Input, false, "像素变化不足", MessageEvidence.None);

        await runner.RunAsync(step, _ => Task.CompletedTask, TimeSpan.FromSeconds(2));

        var end = observer.Events.Last(item => item.Step?.Id == "unverified" && item.Phase == "end");
        Assert.Equal(OperationStatus.Succeeded, end.Step!.Status);
        Assert.False(end.Step.Verified);
        Assert.Equal(OperationVerificationStatus.Unverified, end.Step.Verification);
        Assert.Equal(MessageEvidence.None, end.Step.Evidence);
    }

    [Fact]
    public void ControlStatesShareTheSessionSequence()
    {
        var observer = new Collector();
        var runner = new OperationRunner(observer);
        var session = runner.BeginSession("测试", "pid 1");

        runner.ReportControl("paused");
        runner.ReportControl("running");

        var controls = observer.Events.Where(item => item.Kind == "control").ToList();
        Assert.Equal(new[] { "paused", "running" }, controls.Select(item => item.Status));
        Assert.All(controls, item => Assert.Equal(session.Id, item.SessionId));
        Assert.Equal(new long[] { 2, 3 }, controls.Select(item => item.Seq));
    }

    [Fact]
    public void BlankDetectionDistinguishesUniformAndDetailedFrames()
    {
        var uniform = new RgbaFrame(32, 32, Enumerable.Repeat((byte)255, 32 * 32 * 4).ToArray());
        var detailed = new RgbaFrame(32, 32, BuildNoise(32, 32));

        Assert.True(FrameStats.IsBlank(uniform));
        Assert.False(FrameStats.IsBlank(detailed));
    }

    private static byte[] BuildNoise(int width, int height)
    {
        var random = new Random(7);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return pixels;
    }

    private sealed class Collector : IOperationObserver
    {
        public List<OperationEvent> Events { get; } = new();

        public void Receive(OperationEvent operationEvent)
        {
            lock (Events) Events.Add(operationEvent);
        }
    }
}
