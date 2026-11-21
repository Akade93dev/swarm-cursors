namespace Swarm.Core.Windows;

/// <summary>目标窗口匹配规则：进程名可带或不带 .exe，标题按不区分大小写的包含匹配；空标题窗口直接排除。</summary>
public static class WindowFilter
{
    public static bool Matches(string? processName, string? titleFilter, string candidateProcess, string candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(candidateTitle)) return false;
        if (!string.IsNullOrWhiteSpace(processName))
        {
            var expected = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;
            if (!candidateProcess.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return string.IsNullOrWhiteSpace(titleFilter)
            || candidateTitle.Contains(titleFilter, StringComparison.OrdinalIgnoreCase);
    }
}
