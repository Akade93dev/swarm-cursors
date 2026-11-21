using Swarm.Core.Semantics;

namespace Swarm.Core.Capture;

/// <summary>遮挡判定的采样点：在客户区内按 6 等分取 5×5 个点，全部归属目标窗口才视为可见。</summary>
public static class OcclusionSampler
{
    public const int Grid = 5;

    public static (int X, int Y)[] SamplePoints(RectInt client)
    {
        var points = new (int X, int Y)[Grid * Grid];
        var index = 0;
        for (var row = 1; row <= Grid; row++)
        {
            for (var column = 1; column <= Grid; column++)
            {
                points[index++] = (
                    client.X + client.Width * column / (Grid + 1),
                    client.Y + client.Height * row / (Grid + 1));
            }
        }
        return points;
    }
}
