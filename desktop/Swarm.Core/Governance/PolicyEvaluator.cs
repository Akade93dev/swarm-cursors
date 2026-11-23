namespace Swarm.Core.Governance;

public sealed record PolicyDecision(bool Allowed, string? Reason)
{
    public static PolicyDecision Allow { get; } = new(true, null);

    public static PolicyDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// 纯策略判定：只读取策略与事实，绝不参考屏幕文本，避免被注入的界面文字影响授权结论。
/// </summary>
public static class PolicyEvaluator
{
    /// <summary>会改变目标状态的动作；observe 与 task_end/task_stop 不受 readOnly 限制。</summary>
    public static bool IsMutating(string kind) =>
        kind is "click" or "type_text" or "press_key" or "scroll" or "focus";

    public static PolicyDecision Begin(TaskPolicy policy, string processName, string title)
    {
        if (policy.AllowedProcesses is { Count: > 0 } processes &&
            !processes.Any(item => MatchesProcess(item, processName)))
        {
            return PolicyDecision.Deny("process_not_allowed");
        }
        if (policy.AllowedTitles is { Count: > 0 } titles &&
            !titles.Any(item => title.Contains(item, StringComparison.OrdinalIgnoreCase)))
        {
            return PolicyDecision.Deny("title_not_allowed");
        }
        return PolicyDecision.Allow;
    }

    public static PolicyDecision Action(
        TaskPolicy policy,
        string kind,
        TimeSpan elapsed,
        int executedActions,
        bool crossApp)
    {
        if (policy.ReadOnly && IsMutating(kind)) return PolicyDecision.Deny("read_only");
        if (elapsed.TotalMilliseconds > policy.MaxDurationMs) return PolicyDecision.Deny("time_exceeded");
        if (executedActions >= policy.MaxActions) return PolicyDecision.Deny("max_actions");
        if (crossApp && !policy.AllowCrossApp) return PolicyDecision.Deny("cross_app");
        return PolicyDecision.Allow;
    }

    internal static bool MatchesProcess(string allowed, string actual)
    {
        static string Normalize(string value) =>
            value.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? value.Trim()[..^4]
                : value.Trim();
        return string.Equals(Normalize(allowed), Normalize(actual), StringComparison.OrdinalIgnoreCase);
    }
}
