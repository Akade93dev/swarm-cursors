using System.Security.Cryptography;

namespace Swarm.Core.Governance;

/// <summary>
/// 一次性确认令牌：绑定 taskId 与动作指纹，过期即失效。
/// 客户端收到 needs_confirmation 后带同一动作 + confirmToken 重试。
/// </summary>
public sealed class ConfirmationBroker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public ConfirmationBroker(TimeSpan? lifetime = null)
    {
        Lifetime = lifetime ?? TimeSpan.FromSeconds(120);
    }

    public TimeSpan Lifetime { get; }

    public (string Token, int ExpiresInMs) Issue(string taskId, string fingerprint)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            Prune(now);
            _pending[token] = new Pending(taskId, fingerprint, now + Lifetime);
        }
        return (token, (int)Lifetime.TotalMilliseconds);
    }

    public bool TryConsume(string taskId, string? token, string fingerprint, out string reason)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            reason = "missing";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_pending.TryGetValue(token, out var pending))
            {
                reason = "unknown_or_used";
                return false;
            }
            if (pending.ExpiresAt <= now)
            {
                _pending.Remove(token);
                reason = "expired";
                return false;
            }
            if (!string.Equals(pending.TaskId, taskId, StringComparison.Ordinal))
            {
                reason = "task_mismatch";
                return false;
            }
            if (!string.Equals(pending.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                reason = "fingerprint_mismatch";
                return false;
            }
            _pending.Remove(token);
            reason = "";
            return true;
        }
    }

    public int PendingCount
    {
        get { lock (_gate) { Prune(DateTimeOffset.UtcNow); return _pending.Count; } }
    }

    private void Prune(DateTimeOffset now)
    {
        if (_pending.Count == 0) return;
        var expired = _pending.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray();
        foreach (var token in expired) _pending.Remove(token);
    }

    private sealed record Pending(string TaskId, string Fingerprint, DateTimeOffset ExpiresAt);
}
