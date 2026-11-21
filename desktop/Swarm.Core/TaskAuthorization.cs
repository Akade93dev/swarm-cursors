using System.Security.Cryptography;
using System.Text;

namespace Swarm.Core.Security;

public sealed record AuthorizedSession(string Token, string TargetName, int ProcessId);

public sealed class TaskAuthorization
{
    private readonly object _sync = new();

    public AuthorizedSession? Current { get; private set; }

    public AuthorizedSession Start(string targetName, int processId)
    {
        if (string.IsNullOrWhiteSpace(targetName)) throw new ArgumentException("Target is required", nameof(targetName));
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        lock (_sync)
        {
            if (Current != null) throw new InvalidOperationException("Another desktop task is already active");
            Current = new AuthorizedSession(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), targetName, processId);
            return Current;
        }
    }

    public AuthorizedSession Validate(string token, int processId)
    {
        lock (_sync)
        {
            var session = Current;
            if (session == null || !Matches(session.Token, token) || session.ProcessId != processId)
                throw new UnauthorizedAccessException("Task token or target scope is invalid");
            return session;
        }
    }

    public void End(string token)
    {
        lock (_sync)
        {
            if (Current == null || !Matches(Current.Token, token)) throw new UnauthorizedAccessException("Task token is invalid");
            Current = null;
        }
    }

    public void Stop()
    {
        lock (_sync) Current = null;
    }

    private static bool Matches(string expected, string? supplied)
    {
        if (supplied == null) return false;
        var left = Encoding.ASCII.GetBytes(expected);
        var right = Encoding.ASCII.GetBytes(supplied);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

public static class RiskAssessment
{
    private static readonly string[] DangerousLabels =
    {
        "delete", "remove", "send", "submit", "pay", "purchase", "transfer", "empty trash",
        "删除", "移除", "发送", "提交", "付款", "支付", "转账", "购买", "清空", "永久", "关闭窗口",
    };

    public static bool RequiresConfirmation(string? label, string? semantic, string? key)
    {
        var target = $"{label} {semantic}";
        if (DangerousLabels.Any(word => target.Contains(word, StringComparison.OrdinalIgnoreCase))) return true;
        var chord = (key ?? string.Empty).Replace(" ", "", StringComparison.Ordinal);
        return chord.Equals("ALT+F4", StringComparison.OrdinalIgnoreCase) || chord.Equals("CTRL+W", StringComparison.OrdinalIgnoreCase);
    }
}
