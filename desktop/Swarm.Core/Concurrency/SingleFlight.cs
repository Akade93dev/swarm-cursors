namespace Swarm.Core.Concurrency;

/// <summary>
/// 按 key 合并同一时刻的只读作业：多个调用者共享同一个在途任务，完成后立即移除。
/// 观察类请求因此不会排他，也不会对同一窗口重复抓屏/重复读 UIA。
/// 所有调用者拿到的是同一个 Task 实例，避免调用方以引用区分而误判。
/// </summary>
public sealed class SingleFlight<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, Task<TValue>> _inflight = new();
    private readonly object _sync = new();

    public int Count
    {
        get { lock (_sync) return _inflight.Count; }
    }

    public Task<TValue> RunAsync(TKey key, Func<Task<TValue>> factory)
    {
        lock (_sync)
        {
            if (_inflight.TryGetValue(key, out var existing)) return existing;

            var completion = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inflight[key] = completion.Task;
            _ = ExecuteAsync(key, factory, completion);
            return completion.Task;
        }
    }

    private async Task ExecuteAsync(TKey key, Func<Task<TValue>> factory, TaskCompletionSource<TValue> completion)
    {
        try
        {
            completion.TrySetResult(await factory().ConfigureAwait(false));
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        finally
        {
            lock (_sync) _inflight.Remove(key);
        }
    }
}
