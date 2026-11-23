namespace Swarm.Core.Operations;

public enum OperationKind
{
    Observe,
    Focus,
    Click,
    Type,
    Key,
    Scroll,
    Wait,
    Custom,
}

public enum OperationStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Canceled,
}

public enum OperationVerificationStatus
{
    NotRequested,
    Passed,
    Unverified,
}

/// <summary>实际生效的输入通道：UIA 模式、定向窗口消息、全局输入。</summary>
public enum InputTier
{
    None,
    Uia,
    Message,
    Input,
}

/// <summary>一次 AI 操作步骤，独立于截图重建，可单独序列化给覆盖层。</summary>
public sealed class OperationStep
{
    public required string Id { get; init; }
    public required OperationKind Kind { get; init; }
    public required string Label { get; init; }
    public OperationStatus Status { get; private set; } = OperationStatus.Pending;
    public InputTier Tier { get; private set; } = InputTier.None;
    public bool Verified { get; private set; }
    public OperationVerificationStatus Verification { get; private set; } = OperationVerificationStatus.NotRequested;
    public string? Evidence { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public string? Error { get; private set; }

    public double? DurationMs => StartedAt.HasValue && EndedAt.HasValue
        ? (EndedAt.Value - StartedAt.Value).TotalMilliseconds
        : null;

    public static OperationKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "observe" => OperationKind.Observe,
        "focus" => OperationKind.Focus,
        "click" => OperationKind.Click,
        "type" or "type_text" => OperationKind.Type,
        "key" or "press_key" => OperationKind.Key,
        "scroll" => OperationKind.Scroll,
        "wait" => OperationKind.Wait,
        _ => OperationKind.Custom,
    };

    /// <summary>由执行体在完成前回填，供覆盖层显示通道与独立验证证据。</summary>
    public void ReportTransport(InputTier tier, bool verified, string? note = null, string? evidence = null)
    {
        Tier = tier;
        Verified = verified;
        Verification = verified
            ? OperationVerificationStatus.Passed
            : evidence == null
                ? OperationVerificationStatus.NotRequested
                : OperationVerificationStatus.Unverified;
        Evidence = evidence;
        Note = string.IsNullOrWhiteSpace(note) ? null : note;
    }

    internal void Start()
    {
        Status = OperationStatus.Running;
        StartedAt = DateTimeOffset.Now;
    }

    internal void Succeed()
    {
        Status = OperationStatus.Succeeded;
        EndedAt = DateTimeOffset.Now;
    }

    internal void Fail(string? error)
    {
        Status = OperationStatus.Failed;
        Error = string.IsNullOrWhiteSpace(error) ? "操作失败" : error;
        EndedAt = DateTimeOffset.Now;
    }

    internal void Cancel()
    {
        Status = OperationStatus.Canceled;
        EndedAt = DateTimeOffset.Now;
    }
}

public sealed record OperationSession(string Id, string Title, string Target, DateTimeOffset StartedAt);

/// <summary>事件中的步骤快照，避免观察者看到被后续状态覆盖的可变对象。</summary>
public sealed record OperationStepView(
    string Id,
    OperationKind Kind,
    string Label,
    OperationStatus Status,
    string? Error,
    double? DurationMs,
    InputTier Tier = InputTier.None,
    bool Verified = false,
    string? Note = null,
    OperationVerificationStatus Verification = OperationVerificationStatus.NotRequested,
    string? Evidence = null);

/// <summary>操作事件：session-start、step、session-end。带会话 id、序号与阶段，渲染层据此丢弃过期事件。</summary>
public sealed record OperationEvent(
    string Kind,
    OperationSession Session,
    OperationStepView? Step = null,
    string? Status = null,
    double? DurationMs = null,
    string? Phase = null,
    long Seq = 0)
{
    public string SessionId => Session.Id;
}

public interface IOperationObserver
{
    void Receive(OperationEvent operationEvent);
}
