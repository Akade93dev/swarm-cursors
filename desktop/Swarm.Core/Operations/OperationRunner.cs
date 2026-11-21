using System.Runtime.ExceptionServices;

namespace Swarm.Core.Operations;

/// <summary>
/// 操作协同框架：把 AI 的每一步操作建模为可观察的步骤流，与截图拼装完全解耦。
/// 步骤可串行（RunSequenceAsync）或并行（RunParallelAsync），执行体负责真正的前置与注入。
/// 事件带 sessionId、seq 与 phase，渲染层据此丢弃过期事件；停止时先排空在飞步骤再结束会话。
/// </summary>
public sealed class OperationRunner
{
    private static readonly TimeSpan DefaultDrain = TimeSpan.FromMilliseconds(800);
    private readonly IOperationObserver _observer;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _inflight = new();
    private OperationSession? _session;
    private CancellationTokenSource? _cancel;
    private long _seq;
    private int _succeeded;
    private int _failed;
    private bool _stopping;

    public OperationRunner(IOperationObserver observer) => _observer = observer;

    public OperationSession? Session
    {
        get
        {
            lock (_gate) return _session;
        }
    }

    public bool IsStopping
    {
        get
        {
            lock (_gate) return _stopping;
        }
    }

    public OperationSession BeginSession(string title, string target)
    {
        var session = new OperationSession(Guid.NewGuid().ToString("N"), title, target, DateTimeOffset.Now);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _cancel;
            _session = session;
            _cancel = new CancellationTokenSource();
            _seq = 0;
            _succeeded = 0;
            _failed = 0;
            _stopping = false;
            _inflight.Clear();
        }
        previous?.Dispose();
        Emit("session-start", session, phase: "start");
        return session;
    }

    public void EndSession() => EndSessionCore(null);

    /// <summary>
    /// 停止会话：禁止新步骤 → 取消会话令牌 → 等待在飞步骤（默认 800ms）→ 发出 session-end。
    /// 保证步骤的结束事件早于 session-end，避免渲染层面板复活或状态错乱。
    /// </summary>
    public async Task StopAsync(TimeSpan? drain = null)
    {
        List<Task> waits;
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (_session == null) return;
            _stopping = true;
            cancel = _cancel;
            waits = _inflight.Values.Select(item => (Task)item.Task).ToList();
        }

        try
        {
            cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 上一次结束已释放令牌，忽略。
        }

        if (waits.Count > 0)
        {
            try
            {
                await Task.WhenAll(waits).WaitAsync(drain ?? DefaultDrain);
            }
            catch
            {
                // 超时或步骤以异常结束都继续收尾；动作执行体自身按令牌终止。
            }
        }

        EndSessionCore("stopped");
    }

    public async Task<OperationStep> RunAsync(
        OperationStep step,
        Func<CancellationToken, Task> body,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var session = Session ?? throw new InvalidOperationException("没有进行中的 AI 操作会话");
        var registration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken sessionToken;
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("任务正在停止，未执行新的操作");
            if (_session == null) throw new InvalidOperationException("没有进行中的 AI 操作会话");
            sessionToken = _cancel?.Token ?? CancellationToken.None;
            _inflight[step.Id] = registration;
        }

        step.Start();
        Emit("step", session, View(step), phase: "start");

        var timeoutCts = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionToken, timeoutCts.Token);
        var bodyTask = Task.Run(() => body(linked.Token), linked.Token);
        var timedOut = false;
        try
        {
            Task? timeoutTask = timeout == Timeout.InfiniteTimeSpan ? null : Task.Delay(timeout, timeoutCts.Token);
            var winner = timeoutTask == null ? bodyTask : await Task.WhenAny(bodyTask, timeoutTask);
            if (timeoutTask != null && winner == timeoutTask)
            {
                timedOut = true;
                timeoutCts.Cancel();
                step.Fail("操作超时");
                lock (_gate) _failed++;
                Emit("step", session, View(step), phase: "end");
                // 执行体可能忽略令牌，观察其异常后释放；不再等待，避免幽灵输入阻塞调用方。
                _ = bodyTask.ContinueWith(
                    task =>
                    {
                        _ = task.Exception;
                        linked.Dispose();
                        timeoutCts.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw new TimeoutException("操作超时");
            }

            timeoutCts.Cancel();
            await bodyTask;
            step.Succeed();
            lock (_gate) _succeeded++;
            Emit("step", session, View(step), phase: "end");
            return step;
        }
        catch (OperationCanceledException)
        {
            step.Cancel();
            Emit("step", session, View(step), phase: "end");
            throw;
        }
        catch (Exception error) when (!(error is TimeoutException && timedOut))
        {
            step.Fail(error is TimeoutException ? "操作超时" : error.Message);
            lock (_gate) _failed++;
            Emit("step", session, View(step), phase: "end");
            throw;
        }
        finally
        {
            lock (_gate) _inflight.Remove(step.Id);
            registration.TrySetResult();
            if (bodyTask.IsCompleted)
            {
                linked.Dispose();
                timeoutCts.Dispose();
            }
        }
    }

    /// <summary>串行执行一组步骤，任一步失败即停止。</summary>
    public async Task RunSequenceAsync(
        IReadOnlyList<(OperationStep Step, Func<CancellationToken, Task> Body)> steps,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        foreach (var item in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunAsync(item.Step, item.Body, timeout, cancellationToken);
        }
    }

    /// <summary>并行执行互不依赖的步骤，例如同时刷新观察与推进可视化；任一失败会取消其余步骤。</summary>
    public async Task RunParallelAsync(
        IReadOnlyList<(OperationStep Step, Func<CancellationToken, Task> Body)> steps,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var group = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = steps.Select(item => RunAsync(item.Step, item.Body, timeout, group.Token)).ToArray();
        Exception? failure = null;
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception error)
        {
            failure = error;
            group.Cancel();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // 兄弟步骤的取消异常不再覆盖首个失败。
            }
        }

        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void EndSessionCore(string? forcedStatus)
    {
        OperationSession? session;
        int succeeded;
        int failed;
        lock (_gate)
        {
            session = _session;
            if (session == null) return;
            _session = null;
            _stopping = false;
            succeeded = _succeeded;
            failed = _failed;
            try
            {
                _cancel?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
            _cancel = null;
        }

        var status = forcedStatus ?? (failed == 0 ? "succeeded" : succeeded == 0 ? "failed" : "partial");
        Emit("session-end", session, status: status, durationMs: (DateTimeOffset.Now - session.StartedAt).TotalMilliseconds, phase: "end");
    }

    private void Emit(
        string kind,
        OperationSession session,
        OperationStepView? view = null,
        string? status = null,
        double? durationMs = null,
        string? phase = null)
    {
        long seq;
        lock (_gate) seq = ++_seq;
        _observer.Receive(new OperationEvent(kind, session, view, status, durationMs, phase, seq));
    }

    private static OperationStepView View(OperationStep step) =>
        new(step.Id, step.Kind, step.Label, step.Status, step.Error, step.DurationMs, step.Tier, step.Verified, step.Note);
}
