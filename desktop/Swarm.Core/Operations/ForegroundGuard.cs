namespace Swarm.Core.Operations;

/// <summary>
/// UIA 模式可能被提供程序顺带 SetFocus，把目标窗口推到前台。这里用动作前后的"前台根窗口"
/// 判定前台是否真的变了、变化是否已被恢复；结论原样返回给调用方，不隐瞒中间发生过切换。
/// </summary>
public static class ForegroundGuard
{
    /// <param name="Unchanged">动作结束时前台与动作前一致（含已经恢复的情况）。</param>
    /// <param name="Restored">过程中曾切到目标，但已恢复到动作前的前台。</param>
    public readonly record struct Result(bool Unchanged, bool Restored);

    /// <param name="beforeRoot">动作前的前台根窗口；0 表示未知。</param>
    /// <param name="afterRoot">动作刚结束时前台在目标内的根窗口。</param>
    /// <param name="targetRoot">目标窗口的根。</param>
    /// <param name="restoredRoot">尝试恢复之后的前台根窗口（未恢复时与 afterRoot 相同）。</param>
    public static Result Evaluate(nint beforeRoot, nint afterRoot, nint targetRoot, nint restoredRoot)
    {
        // 动作前前台未知，或本来就在目标内：这次动作没有"保持用户前台"的责任。
        if (beforeRoot == 0 || beforeRoot == targetRoot) return new Result(true, false);
        if (afterRoot == beforeRoot) return new Result(true, false);
        return restoredRoot == beforeRoot ? new Result(true, true) : new Result(false, false);
    }
}
