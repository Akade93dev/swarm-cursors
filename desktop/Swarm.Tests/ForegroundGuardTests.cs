using Swarm.Core.Operations;

namespace Swarm.Tests;

/// <summary>
/// 前台不变判定：UIA 模式可能把目标推到前台，这里只做纯逻辑判定，不依赖真实窗口。
/// 语义是"动作结束时前台是否回到动作前的前台"，并单独记录过程中是否发生过切换。
/// </summary>
public class ForegroundGuardTests
{
    [Fact]
    public void 前台未变化时视为未变()
    {
        var result = ForegroundGuard.Evaluate(beforeRoot: 11, afterRoot: 11, targetRoot: 22, restoredRoot: 11);

        Assert.True(result.Unchanged);
        Assert.False(result.Restored);
    }

    [Fact]
    public void 被推到目标前台且已恢复()
    {
        var result = ForegroundGuard.Evaluate(beforeRoot: 11, afterRoot: 22, targetRoot: 22, restoredRoot: 11);

        Assert.True(result.Unchanged);
        Assert.True(result.Restored);
    }

    [Fact]
    public void 被推到目标前台且恢复失败()
    {
        var result = ForegroundGuard.Evaluate(beforeRoot: 11, afterRoot: 22, targetRoot: 22, restoredRoot: 22);

        Assert.False(result.Unchanged);
        Assert.False(result.Restored);
    }

    [Fact]
    public void 动作前前台就在目标内时不承担保持责任()
    {
        var result = ForegroundGuard.Evaluate(beforeRoot: 22, afterRoot: 22, targetRoot: 22, restoredRoot: 22);

        Assert.True(result.Unchanged);
        Assert.False(result.Restored);
    }

    [Fact]
    public void 动作前前台未知时按未变处理()
    {
        var result = ForegroundGuard.Evaluate(beforeRoot: 0, afterRoot: 22, targetRoot: 22, restoredRoot: 22);

        Assert.True(result.Unchanged);
        Assert.False(result.Restored);
    }
}
