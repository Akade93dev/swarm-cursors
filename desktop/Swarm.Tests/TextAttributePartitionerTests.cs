using Swarm.Core.Semantics;

namespace Swarm.Tests;

/// <summary>
/// 二分边界分段：把 UIA 的“整段读属性 + Mixed 二分”算法抽成纯逻辑，
/// 用逐码元的假数据模拟 provider，验证分段边界、代理对对齐与调用次数预算。
/// </summary>
public class TextAttributePartitionerTests
{
    private static readonly object?[] Regular = { "Segoe UI", 10.0, 400, false, 0 };

    private static readonly object?[] Italic = { "Segoe UI", 10.0, 400, true, 0 };

    private static readonly object?[] Bold = { "Segoe UI", 10.0, 700, false, 0 };

    /// <summary>逐码元给值，Read 按区间是否一致返回 Mixed，并统计调用次数。</summary>
    private sealed class FakeProbe : ITextAttributeProbe
    {
        private readonly IReadOnlyList<object?[]> _perCodeUnit;

        public FakeProbe(IReadOnlyList<object?[]> perCodeUnit) => _perCodeUnit = perCodeUnit;

        public int Calls { get; private set; }

        public object? Read(int start, int length, TextAttribute attribute)
        {
            Calls++;
            var index = (int)attribute;
            var first = _perCodeUnit[start][index];
            for (var i = start + 1; i < start + length; i++)
            {
                if (!Equals(_perCodeUnit[i][index], first)) return TextAttributeValues.Mixed;
            }
            return first;
        }
    }

    private static FakeProbe Probe(string text, params (int Start, object?[] Values)[] segments)
    {
        var perCodeUnit = new object?[text.Length][];
        // 每个区间铺到下一个区间的起点，最后一个铺到末尾。
        for (var s = 0; s < segments.Length; s++)
        {
            var from = segments[s].Start;
            var to = s + 1 < segments.Length ? segments[s + 1].Start : text.Length;
            for (var i = from; i < to; i++) perCodeUnit[i] = segments[s].Values;
        }
        return new FakeProbe(perCodeUnit);
    }

    [Fact]
    public void Partition_ReturnsSingleSegmentForUniformText()
    {
        var text = new string('a', 12);
        var probe = Probe(text, (0, Regular));

        var result = TextAttributePartitioner.Partition(text, probe);

        var segment = Assert.Single(result.Segments);
        Assert.Equal(0, segment.Start);
        Assert.Equal(text.Length, segment.Length);
        Assert.Equal("Segoe UI", segment.Values[(int)TextAttribute.FontFamily]);
        Assert.Equal(5, probe.Calls);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Partition_SplitsAtFirstMixedAttributeBoundary()
    {
        var text = new string('a', 30);
        var probe = Probe(text, (0, Regular), (10, Italic));

        var result = TextAttributePartitioner.Partition(text, probe);

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal((0, 10), (result.Segments[0].Start, result.Segments[0].Length));
        Assert.Equal((10, 20), (result.Segments[1].Start, result.Segments[1].Length));
        Assert.Equal(false, result.Segments[0].Values[(int)TextAttribute.Italic]);
        Assert.Equal(true, result.Segments[1].Values[(int)TextAttribute.Italic]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Partition_ResolvesEveryAttributeForThreeMixedSegments()
    {
        var text = new string('a', 30);
        var third = new object?[] { "Arial", 12.0, 400, false, 16777215 };
        var second = new object?[] { "Arial", 11.0, 700, true, 255 };
        var probe = Probe(text, (0, Regular), (10, second), (20, third));

        var result = TextAttributePartitioner.Partition(text, probe);

        Assert.Equal(3, result.Segments.Count);
        Assert.Equal((0, 10), (result.Segments[0].Start, result.Segments[0].Length));
        Assert.Equal((10, 10), (result.Segments[1].Start, result.Segments[1].Length));
        Assert.Equal((20, 10), (result.Segments[2].Start, result.Segments[2].Length));
        foreach (var segment in result.Segments)
        {
            Assert.DoesNotContain(segment.Values, value => value is null);
        }
    }

    [Fact]
    public void Partition_StaysWithinCallBudgetForLongThreeSegmentText()
    {
        var text = new string('a', 200);
        var second = new object?[] { "Arial", 11.0, 700, true, 255 };
        var third = new object?[] { "Tahoma", 12.0, 400, false, 16777215 };
        var probe = Probe(text, (0, Regular), (100, second), (170, third));

        var result = TextAttributePartitioner.Partition(text, probe);

        Assert.Equal(3, result.Segments.Count);
        Assert.True(probe.Calls <= 40, $"calls={probe.Calls}");
    }

    [Fact]
    public void Partition_KeepsFullCoverageWhenSegmentLimitIsReached()
    {
        var text = new string('a', 40);
        var probe = new FakeProbe(text.Select((_, i) => i switch
        {
            < 5 => Regular,
            < 10 => Italic,
            < 15 => Bold,
            _ => Regular,
        }).ToArray());

        var result = TextAttributePartitioner.Partition(text, probe, maxSegments: 3);

        Assert.Equal(3, result.Segments.Count);
        Assert.True(result.Truncated);
        var covered = 0;
        foreach (var segment in result.Segments)
        {
            Assert.Equal(covered, segment.Start);
            covered += segment.Length;
        }
        Assert.Equal(text.Length, covered);
    }

    [Fact]
    public void Partition_DoesNotSplitSurrogatePair()
    {
        // 样式在第 1 个码元（低代理）处变化：切点必须顺延到 2，不能把 emoji 劈成两半。
        const string text = "😀😀abc";
        var probe = Probe(text, (0, Regular), (1, Italic));

        var result = TextAttributePartitioner.Partition(text, probe);

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(0, result.Segments[0].Start);
        Assert.Equal(2, result.Segments[0].Length);
        Assert.Equal(text.Length, result.Segments[0].Length + result.Segments[1].Length);
    }

    [Fact]
    public void Partition_ReturnsEmptyForEmptyText()
    {
        var result = TextAttributePartitioner.Partition("", new FakeProbe(Array.Empty<object?[]>()));

        Assert.Empty(result.Segments);
        Assert.Equal(0, result.Calls);
    }
}
