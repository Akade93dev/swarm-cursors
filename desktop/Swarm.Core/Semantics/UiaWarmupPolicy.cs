namespace Swarm.Core.Semantics;

/// <summary>
/// Chromium/Electron 的辅助功能树是惰性构建的：UIA 客户端第一次查询时才真正开始生成，
/// 首次快照往往只有窗口外壳和少量命名控件，重建出来就像一张截图。
/// 命中该策略时，读取器应短暂等待后重新抓取，直到节点数稳定。
/// </summary>
public static class UiaWarmupPolicy
{
    /// <summary>低于该节点数视为“只抓到了外壳”。</summary>
    public const int SparseElementCount = 24;

    /// <summary>最多重抓次数，避免慢应用把重建拖成数秒。</summary>
    public const int MaxAttempts = 3;

    /// <summary>每次重抓前的等待时间，给渲染进程留出建树时间。</summary>
    public const int DelayMilliseconds = 350;

    public static bool ShouldRetry(TargetCapabilities capabilities, int elementCount, int attempt) =>
        attempt < MaxAttempts &&
        (capabilities.IsChromium || capabilities.IsElectron) &&
        elementCount < SparseElementCount;
}
