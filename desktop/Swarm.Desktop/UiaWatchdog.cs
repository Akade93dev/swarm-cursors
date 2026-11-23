using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Swarm.Desktop;

/// <summary>
/// UIA 看门狗：跨进程读取可能被恶意/故障的 provider 卡死，不能无限等待。
/// 超时按 PID 熔断：连续超时达到阈值后隔离一段时间，观察直接降级到 OCR，
/// 避免每次 observe 都白等一个完整超时。熔断是 per-PID 的，不影响其它应用。
/// </summary>
internal static class UiaWatchdog
{
    private static readonly ConcurrentDictionary<int, Health> HealthByPid = new();
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(3500);
    private const int FailureThreshold = 2;
    private const int MaxTrackedProcesses = 64;

    public static bool IsQuarantined(int processId) =>
        HealthByPid.TryGetValue(processId, out var health) && health.QuarantinedUntil > DateTimeOffset.UtcNow;

    /// <summary>按窗口所属进程判断是否处于 UIA 熔断期；熔断时输入路由直接跳过 Tier A/B 的 UIA 尝试。</summary>
    public static bool IsWindowQuarantined(nint window)
    {
        if (window == 0) return false;
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return IsQuarantined((int)processId);
    }

    public static async Task<UiaSnapshot?> ReadAsync(nint window, int processId, long generation, CancellationToken cancellationToken)
    {
        if (IsQuarantined(processId)) return null;

        var options = new UiaSnapshotOptions(IncludePatterns: true, FirstPassMilliseconds: 800, Generation: generation);
        var read = Task.Run(() => UiaSnapshotReader.Read(window, options), cancellationToken);
        var completed = await Task.WhenAny(read, Task.Delay(ReadTimeout, cancellationToken)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, read))
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkFailure(processId, timedOut: true);
            return null;
        }

        try
        {
            var snapshot = await read.ConfigureAwait(false);
            MarkSuccess(processId);
            return snapshot;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            MarkFailure(processId, timedOut: false);
            throw;
        }
    }

    public static void Reset(int processId) => HealthByPid.TryRemove(processId, out _);

    public static object Snapshot() => HealthByPid.Select(pair => new
    {
        processId = pair.Key,
        failures = pair.Value.Failures,
        timeouts = pair.Value.Timeouts,
        errors = pair.Value.Errors,
        reads = pair.Value.Reads,
        quarantinedUntil = pair.Value.QuarantinedUntil > DateTimeOffset.UtcNow
            ? pair.Value.QuarantinedUntil.ToString("O")
            : null,
    }).ToArray();

    private static void MarkFailure(int processId, bool timedOut)
    {
        PruneIfNeeded();
        var health = HealthByPid.GetOrAdd(processId, _ => new Health());
        lock (health)
        {
            health.Failures++;
            if (timedOut) health.Timeouts++;
            else health.Errors++;
            if (health.Failures >= FailureThreshold)
            {
                // 指数退避：卡死的 provider 每次解禁后仍会再卡一次，每次都泄漏一个线程池线程；
                // 固定 30s 循环一天会堆掉数千个卡死线程，所以连续熔断要逐次加长（最长 8 分钟）。
                health.Level = Math.Min(health.Level + 1, 5);
                health.QuarantinedUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30 * Math.Pow(2, health.Level - 1));
                health.Failures = 0;
            }
        }
    }

    /// <summary>进程数量超过上限时清掉“无失败、无熔断”的冷记录，避免长期运行按 PID 无限累积。</summary>
    private static void PruneIfNeeded()
    {
        if (HealthByPid.Count <= MaxTrackedProcesses) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in HealthByPid)
        {
            lock (pair.Value)
            {
                if (pair.Value.Failures == 0 && pair.Value.QuarantinedUntil <= now)
                    HealthByPid.TryRemove(pair.Key, out _);
            }
        }
    }

    private static void MarkSuccess(int processId)
    {
        var health = HealthByPid.GetOrAdd(processId, _ => new Health());
        lock (health)
        {
            health.Failures = 0;
            health.Level = 0;
            health.Reads++;
        }
    }

    private sealed class Health
    {
        public int Failures;
        public int Level;
        public long Timeouts;
        public long Errors;
        public long Reads;
        public DateTimeOffset QuarantinedUntil;
    }
}

/// <summary>会话隔离能力探测结果。soft = 单会话内租约仲裁；hard 仅作能力标记，未实现。</summary>
internal sealed record IsolationReport(string Mode, bool HardAvailable, IReadOnlyList<string> Reasons);

/// <summary>
/// 隔离能力探测：本机是 Windows 家庭版且无 TermService，硬隔离（RDP loopback 用户会话）不可用。
/// 这里只做事实探测并如实上报，绝不假装创建了隔离会话。
/// </summary>
internal static class IsolationProbe
{
    private static IsolationReport? _cached;

    public static IsolationReport Detect()
    {
        if (_cached != null) return _cached;

        var reasons = new List<string>();
        var edition = ReadEdition();
        var isHome = edition.Contains("Home", StringComparison.OrdinalIgnoreCase)
            || edition.Contains("Core", StringComparison.OrdinalIgnoreCase);
        if (isHome) reasons.Add($"Windows 版本为 {edition}：不支持远程桌面主机，无法用 RDP loopback 创建第二个用户会话");

        var availableGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0;
        if (availableGb < 32) reasons.Add($"可用内存 {availableGb:F1} GB：不足以让每个 agent 各占一个独立桌面会话");

        var termService = RegistryExists(@"SYSTEM\CurrentControlSet\Services\TermService");
        if (!termService) reasons.Add("TermService 未安装或已禁用");

        var hard = !isHome && availableGb >= 32 && termService;
        if (!hard) reasons.Add("当前采用 soft 隔离：窗口租约 + 输入仲裁 + 空闲门控，Tier A/B 效果与隔离会话等价");

        _cached = new IsolationReport(hard ? "hard" : "soft", hard, reasons);
        return _cached;
    }

    private static string ReadEdition()
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "EditionID",
                null) as string ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static bool RegistryExists(string path)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            return key != null;
        }
        catch
        {
            return false;
        }
    }
}
