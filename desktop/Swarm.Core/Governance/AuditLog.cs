using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Swarm.Core.Governance;

public sealed record AuditRedaction(string Category, string Risk, bool Necessary);

public sealed record AuditTarget(string? Process, string? NameHash, string? ControlType);

/// <summary>一次工具调用的审计元数据；原文永不落盘，text 只以 SHA-256 记录。</summary>
public sealed record AuditEntry(
    string TaskId,
    DateTimeOffset Timestamp,
    string Tool,
    string? Tier,
    AuditTarget? Target,
    string PolicyDecision,
    IReadOnlyList<AuditRedaction> Redactions,
    string? TextHash,
    int TextLength,
    string Result,
    long DurationMs);

public static class AuditHash
{
    public static string? Sha256(string? text)
    {
        if (text is null) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>按任务追加写入 JSONL 审计日志，单文件超过阈值后轮转。</summary>
public sealed class AuditWriter : IDisposable
{
    public const long DefaultRotationBytes = 10 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _taskId;
    private readonly long _rotationBytes;
    private StreamWriter? _writer;
    private long _writtenBytes;
    private int _part;
    private long _sequence;
    private bool _disposed;

    public AuditWriter(string directory, string taskId, long rotationBytes = DefaultRotationBytes)
    {
        _directory = directory;
        _taskId = taskId;
        _rotationBytes = Math.Max(1024, rotationBytes);
        Directory.CreateDirectory(_directory);
    }

    public static string DefaultDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("SWARM_AUDIT_DIR");
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(local)
                ? Path.Combine(Path.GetTempPath(), "Swarm", "audit")
                : Path.Combine(local, "Swarm", "audit");
        }
    }

    public string CurrentPath => Path.Combine(_directory, FileName(_part));

    /// <summary>写入一行并返回本次使用的序号；调用方必须自行保证任务内串行。</summary>
    public long Write(AuditEntry entry)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var line = JsonSerializer.Serialize(new
            {
                taskId = entry.TaskId,
                seq = _sequence,
                ts = entry.Timestamp.ToString("O"),
                tool = entry.Tool,
                tier = entry.Tier,
                target = entry.Target,
                policyDecision = entry.PolicyDecision,
                redactions = entry.Redactions,
                textHash = entry.TextHash,
                textLength = entry.TextLength,
                result = entry.Result,
                durationMs = entry.DurationMs
            }, JsonOptions);

            var bytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (_writer is not null && _writtenBytes > 0 && _writtenBytes + bytes > _rotationBytes)
                Rotate();

            _writer ??= Open();
            _writer.WriteLine(line);
            _writer.Flush();
            _writtenBytes += bytes;
            return _sequence++;
        }
    }

    private StreamWriter Open()
    {
        var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        _writtenBytes = 0;
        _part++;
    }

    private string FileName(int part) => part == 0
        ? $"audit-{_taskId}.jsonl"
        : $"audit-{_taskId}.{part}.jsonl";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
