using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class OcclusionSamplerTests
{
    [Fact]
    public void 采样点数量与去重符合网格约定()
    {
        var points = OcclusionSampler.SamplePoints(new RectInt(100, 200, 600, 300));
        Assert.Equal(OcclusionSampler.Grid * OcclusionSampler.Grid, points.Length);
        Assert.Equal(points.Length, points.Distinct().Count());
    }

    [Fact]
    public void 所有采样点都落在客户区内部()
    {
        var client = new RectInt(100, 200, 600, 300);
        foreach (var (x, y) in OcclusionSampler.SamplePoints(client))
        {
            Assert.InRange(x, client.X + 1, client.Right - 1);
            Assert.InRange(y, client.Y + 1, client.Bottom - 1);
        }
    }

    [Fact]
    public void 采样点按行列递增并包含中心点()
    {
        var client = new RectInt(0, 0, 600, 300);
        var points = OcclusionSampler.SamplePoints(client);
        Assert.Contains((300, 150), points);
        Assert.True(points[0].X < points[^1].X);
        Assert.True(points[0].Y < points[^1].Y);
    }

    [Fact]
    public void 极小客户区不越界()
    {
        var client = new RectInt(10, 10, 1, 1);
        foreach (var (x, y) in OcclusionSampler.SamplePoints(client))
        {
            Assert.Equal(10, x);
            Assert.Equal(10, y);
        }
    }
}
