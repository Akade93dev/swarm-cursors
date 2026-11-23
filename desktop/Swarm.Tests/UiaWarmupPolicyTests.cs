using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class UiaWarmupPolicyTests
{
    private static TargetCapabilities Chromium() => TargetCapabilitiesDetector.Detect("msedge", "Chrome_WidgetWin_1", null, true);

    private static TargetCapabilities Electron() => TargetCapabilitiesDetector.Detect("Trae CN", "Chrome_WidgetWin_1", null, true);

    private static TargetCapabilities Native() => TargetCapabilitiesDetector.Detect("notepad", "Notepad", null, true);

    [Fact]
    public void Chromium首次只抓到外壳时需要重抓()
    {
        Assert.True(UiaWarmupPolicy.ShouldRetry(Chromium(), 14, 0));
        Assert.True(UiaWarmupPolicy.ShouldRetry(Electron(), 3, 0));
    }

    [Fact]
    public void 节点足够时不再重抓()
    {
        Assert.False(UiaWarmupPolicy.ShouldRetry(Chromium(), UiaWarmupPolicy.SparseElementCount, 0));
        Assert.False(UiaWarmupPolicy.ShouldRetry(Electron(), 62, 0));
    }

    [Fact]
    public void 原生应用不做惰性建树重抓()
    {
        Assert.False(UiaWarmupPolicy.ShouldRetry(Native(), 2, 0));
    }

    [Fact]
    public void 重抓次数有上限()
    {
        Assert.True(UiaWarmupPolicy.ShouldRetry(Chromium(), 1, UiaWarmupPolicy.MaxAttempts - 1));
        Assert.False(UiaWarmupPolicy.ShouldRetry(Chromium(), 1, UiaWarmupPolicy.MaxAttempts));
    }
}
