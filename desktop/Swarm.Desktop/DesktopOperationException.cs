namespace Swarm.Desktop;

/// <summary>带错误码的桌面操作异常，便于 MCP 侧区分超时、缺焦点、窗口移动等原因。</summary>
public sealed class DesktopOperationException : Exception
{
    public DesktopOperationException(string code, string message, string? reason = null) : base(message)
    {
        Code = code;
        Reason = reason;
    }

    public string Code { get; }

    /// <summary>细分原因，例如 app_ignores_message；用于上层决定是否先调用 focus。</summary>
    public string? Reason { get; }
}
