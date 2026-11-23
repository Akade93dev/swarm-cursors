namespace Swarm.Core.Semantics;

/// <summary>一次遍历在时间预算上的下一步动作。</summary>
public enum UiaBudgetDecision
{
    /// <summary>仍在当前阶段预算内。</summary>
    Continue,

    /// <summary>首轮预算用尽，但还可续跑一次。</summary>
    Extend,

    /// <summary>已到总超时或续跑额度用尽，按超时截断。</summary>
    StopTimeLimit,
}

/// <summary>
/// UIA 观察的时间预算：首轮预算 → 一次续跑 → 硬性总超时。
/// 只做判定，不读时钟；调用方用同一个 <c>Stopwatch</c> 判断，重抓等待也计入总超时。
/// </summary>
public sealed class UiaObserveBudget
{
    private readonly int _extensionMilliseconds;
    private readonly int _totalMilliseconds;
    private bool _extended;

    public UiaObserveBudget(int firstPassMilliseconds, int extensionMilliseconds, int totalMilliseconds)
    {
        FirstPassMilliseconds = Math.Max(1, firstPassMilliseconds);
        _extensionMilliseconds = Math.Max(0, extensionMilliseconds);
        _totalMilliseconds = Math.Max(1, totalMilliseconds);
        // 首轮预算不能超过总超时，否则第一轮就会把硬上限烧光。
        LimitMilliseconds = Math.Min(FirstPassMilliseconds, _totalMilliseconds);
    }

    public int FirstPassMilliseconds { get; }

    /// <summary>当前阶段的时间上限（首轮或续跑后的上限）。</summary>
    public int LimitMilliseconds { get; private set; }

    public int TotalMilliseconds => _totalMilliseconds;

    public UiaBudgetDecision Check(long elapsedMilliseconds)
    {
        if (elapsedMilliseconds >= _totalMilliseconds) return UiaBudgetDecision.StopTimeLimit;
        if (elapsedMilliseconds < LimitMilliseconds) return UiaBudgetDecision.Continue;
        if (!_extended && _extensionMilliseconds > 0)
        {
            _extended = true;
            LimitMilliseconds = Math.Min(_totalMilliseconds, LimitMilliseconds + _extensionMilliseconds);
            return UiaBudgetDecision.Extend;
        }
        return UiaBudgetDecision.StopTimeLimit;
    }
}
