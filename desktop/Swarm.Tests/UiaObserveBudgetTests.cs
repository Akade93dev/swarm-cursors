using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class UiaObserveBudgetTests
{
    [Fact]
    public void 首轮预算内继续遍历()
    {
        var budget = new UiaObserveBudget(1200, 2000, 2500);
        Assert.Equal(UiaBudgetDecision.Continue, budget.Check(0));
        Assert.Equal(UiaBudgetDecision.Continue, budget.Check(1199));
    }

    [Fact]
    public void 首轮用尽后只续跑一次且不超过总超时()
    {
        var budget = new UiaObserveBudget(1200, 2000, 2500);
        Assert.Equal(UiaBudgetDecision.Extend, budget.Check(1200));
        Assert.Equal(2500, budget.LimitMilliseconds);
        Assert.Equal(UiaBudgetDecision.StopTimeLimit, budget.Check(2500));
    }

    [Fact]
    public void 续跑额度用尽后按超时收口()
    {
        var budget = new UiaObserveBudget(1000, 500, 5000);
        Assert.Equal(UiaBudgetDecision.Extend, budget.Check(1000));
        Assert.Equal(1500, budget.LimitMilliseconds);
        Assert.Equal(UiaBudgetDecision.Continue, budget.Check(1499));
        // 续跑只允许一次：即使离总超时还很远也不再放宽。
        Assert.Equal(UiaBudgetDecision.StopTimeLimit, budget.Check(1500));
    }

    [Fact]
    public void 总超时优先于续跑()
    {
        var budget = new UiaObserveBudget(1000, 5000, 1200);
        Assert.Equal(UiaBudgetDecision.StopTimeLimit, budget.Check(1200));
    }

    [Fact]
    public void 首轮预算被总超时夹紧()
    {
        var budget = new UiaObserveBudget(3000, 0, 800);
        Assert.Equal(800, budget.LimitMilliseconds);
        Assert.Equal(UiaBudgetDecision.Continue, budget.Check(799));
        Assert.Equal(UiaBudgetDecision.StopTimeLimit, budget.Check(800));
    }

    [Fact]
    public void 没有续跑额度时到点即截断()
    {
        var budget = new UiaObserveBudget(500, 0, 5000);
        Assert.Equal(UiaBudgetDecision.StopTimeLimit, budget.Check(500));
    }
}
