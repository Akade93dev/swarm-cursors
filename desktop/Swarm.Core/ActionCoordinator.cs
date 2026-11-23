namespace Swarm.Core.Actions;

public sealed class ActionCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource _generation = new();
    private bool _paused;
    private bool _stopped;

    public async Task RunAsync(
        Func<CancellationToken, Task> prepare,
        Func<CancellationToken, Task> execute,
        CancellationToken cancellationToken = default)
    {
        CancellationToken generationToken;
        lock (_sync)
        {
            if (_stopped) throw new InvalidOperationException("动作协调器已停止");
            if (_paused) throw new InvalidOperationException("人工接管中，动作已暂停");
            generationToken = _generation.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generationToken);
        var token = linked.Token;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            await prepare(token);
            token.ThrowIfCancellationRequested();
            await execute(token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 取消当前代次的在途动作并立即建立新代次：会话保持可用，区别于 Stop。
    /// 已交给 Win32 的原子输入无法撤回，但后续步骤会停下；取消时处于暂停/停止状态则无效果。
    /// </summary>
    public void CancelCurrent()
    {
        CancellationTokenSource generation;
        lock (_sync)
        {
            if (_stopped || _paused) return;
            generation = _generation;
            _generation = new CancellationTokenSource();
        }
        generation.Cancel();
    }

    public void Pause()
    {
        CancellationTokenSource generation;
        lock (_sync)
        {
            if (_stopped || _paused) return;
            _paused = true;
            generation = _generation;
        }
        generation.Cancel();
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_stopped) throw new InvalidOperationException("动作协调器已停止，不可恢复");
            if (!_paused) return;
            _generation = new CancellationTokenSource();
            _paused = false;
        }
    }

    public void Stop()
    {
        CancellationTokenSource generation;
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            generation = _generation;
        }
        generation.Cancel();
    }
}
