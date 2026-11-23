using System.Collections.Concurrent;
using Swarm.Core.Actions;
using Swarm.Core.Governance;
using Swarm.Core.Operations;
using Swarm.Core.Security;

namespace Swarm.Desktop;

/// <summary>目标窗口的身份指纹：窗口句柄 + 进程 + 进程启动时间 + 窗口类名，用于识别句柄复用。</summary>
internal sealed record TargetIdentity(nint Handle, int ProcessId, DateTime StartTimeUtc, string ClassName);

/// <summary>单次工具调用的审计上下文。每个会话自己的请求串行，故上下文可以挂在会话上。</summary>
internal sealed class AuditContext
{
    public string Decision { get; set; } = "allow";
    public string Result { get; set; } = "ok";
    public List<AuditRedaction> Redactions { get; } = new();
    public string? Text { get; set; }
    public AuditTarget? Target { get; set; }
    public string? Tier { get; set; }
}

/// <summary>
/// 一个 Gateway 对应一个桌面任务会话。所有原本挂在服务上的可变状态都在这里，
/// 服务本身只保留无状态的共享设施（注册表、租约、确认队列、抓屏、OCR）。
/// </summary>
internal sealed class TaskSession
{
    public required string Id { get; init; }
    public required int GatewayProcessId { get; init; }
    public required string Token { get; init; }
    public required TargetIdentity Target { get; init; }
    public required string TargetProcessName { get; init; }
    public required string TargetTitle { get; init; }
    public required TaskPolicy Policy { get; init; }
    public required OperationRunner Operations { get; init; }

    public ActionCoordinator Coordinator { get; } = new();
    public ConfirmationBroker Confirmations { get; } = new();
    public HashSet<string> SensitiveElements { get; } = new(StringComparer.Ordinal);
    public SemaphoreSlim RequestGate { get; } = new(1, 1);

    public AuditWriter? Audit { get; set; }
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset TaskStartedAt { get; } = DateTimeOffset.Now;
    public AuditContext AuditContext { get; set; } = new();
    public UiaSnapshot? LastSnapshot { get; set; }
    public WindowCapture? LastCapture { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public long ObservationGeneration { get; set; }
    public bool MaskAllText { get; set; }
    public int ActionCount { get; set; }
    public int WriteGeneration { get; set; }
    public bool ManualTakeover { get; set; }
    public bool Stopping { get; set; }
    public int InFlight { get; set; }
}

/// <summary>
/// 按 Gateway 进程索引的会话表。容量按内存实测设上限（默认 3），
/// 超限返回可读错误码而不是让整机换页。
/// </summary>
internal sealed class SessionRegistry
{
    private readonly Dictionary<int, TaskSession> _byGateway = new();
    private readonly object _sync = new();

    public SessionRegistry(int? capacity = null) => Capacity = capacity ?? ReadCapacity();

    public int Capacity { get; }

    public TaskSession? TryAdd(TaskSession session, out string? errorCode)
    {
        lock (_sync)
        {
            if (_byGateway.ContainsKey(session.GatewayProcessId))
            {
                errorCode = "already_running";
                return null;
            }
            if (_byGateway.Count >= Capacity)
            {
                errorCode = "capacity_exceeded";
                return null;
            }
            _byGateway[session.GatewayProcessId] = session;
            errorCode = null;
            return session;
        }
    }

    public TaskSession? ByGateway(int gatewayProcessId)
    {
        lock (_sync) return _byGateway.TryGetValue(gatewayProcessId, out var session) ? session : null;
    }

    public TaskSession? ById(string id)
    {
        lock (_sync) return _byGateway.Values.FirstOrDefault(session => session.Id == id);
    }

    public void Remove(TaskSession session)
    {
        lock (_sync)
        {
            if (_byGateway.TryGetValue(session.GatewayProcessId, out var current) && ReferenceEquals(current, session))
                _byGateway.Remove(session.GatewayProcessId);
        }
    }

    public IReadOnlyList<TaskSession> All()
    {
        lock (_sync) return _byGateway.Values.ToArray();
    }

    public int Count
    {
        get { lock (_sync) return _byGateway.Count; }
    }

    private static int ReadCapacity()
    {
        var raw = Environment.GetEnvironmentVariable("SWARM_MAX_SESSIONS");
        return int.TryParse(raw, out var value) ? Math.Clamp(value, 1, 8) : 3;
    }
}

/// <summary>跨会话的输入层级计数，用于 desktop_status 观察 Tier A/B/C 的真实分布。</summary>
internal static class TierCounters
{
    private static readonly ConcurrentDictionary<string, long> Counts = new(StringComparer.Ordinal);

    public static void Record(InputTier tier)
    {
        var key = tier.ToString().ToLowerInvariant();
        Counts.AddOrUpdate(key, 1, (_, value) => value + 1);
    }

    public static object Snapshot() => new
    {
        uia = Counts.TryGetValue("uia", out var uia) ? uia : 0,
        message = Counts.TryGetValue("message", out var message) ? message : 0,
        input = Counts.TryGetValue("input", out var input) ? input : 0,
        none = Counts.TryGetValue("none", out var none) ? none : 0,
    };
}
