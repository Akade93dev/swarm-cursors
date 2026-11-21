namespace Swarm.Core.Actions;

public sealed class ActionCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    public async Task RunAsync(
        Func<CancellationToken, Task> prepare,
        Func<CancellationToken, Task> execute,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
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

    public void Stop() => _stop.Cancel();
}
