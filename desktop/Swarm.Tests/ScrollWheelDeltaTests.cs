using Swarm.Core.Actions;

namespace Swarm.Tests;

public class ScrollWheelDeltaTests
{
    [Theory]
    [InlineData(120, 120)]
    [InlineData(-120, -120)]
    [InlineData(0, 0)]
    [InlineData(119.6, 120)]
    [InlineData(-119.4, -119)]
    [InlineData(1200, 1200)]
    [InlineData(-1200, -1200)]
    public void 有限且在范围内的滚动量被规整为整数(double value, int expected)
    {
        Assert.True(ScrollWheelDelta.TryNormalize(value, out var delta));
        Assert.Equal(expected, delta);
    }

    [Theory]
    [InlineData(1200.5)]
    [InlineData(-1201)]
    [InlineData(1e300)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void 超出范围或非有限值被拒绝(double value)
    {
        Assert.False(ScrollWheelDelta.TryNormalize(value, out var delta));
        Assert.Equal(0, delta);
    }
}
