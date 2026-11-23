namespace Swarm.Core.Governance;

/// <summary>确认请求的展示端：由 UI 层实现，必须是非模态的，不能阻塞消息泵或抢占焦点。</summary>
public interface IConfirmationPresenter
{
    Task<bool> AskAsync(string title, string message, bool danger, CancellationToken cancellationToken);
}

/// <summary>
/// 串行化的确认队列：多个会话同时请求确认时排队展示，避免对话框叠罗汉；
/// 每个确认都是非模态请求，UI 线程只负责展示，不参与等待。
/// </summary>
public sealed class ConfirmationQueue
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<bool> AskAsync(IConfirmationPresenter presenter, string title, string message, bool danger, CancellationToken cancellationToken = default)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await presenter.AskAsync(title, message, danger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }
}
