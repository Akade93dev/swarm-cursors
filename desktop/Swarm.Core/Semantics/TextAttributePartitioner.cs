namespace Swarm.Core.Semantics;

/// <summary>分段关心的五个字体属性，顺序与 <see cref="SpanStyle"/> 的字段一致。</summary>
public enum TextAttribute
{
    FontFamily,
    FontSize,
    FontWeight,
    Italic,
    Color,
}

/// <summary>
/// 属性读取探针。每次 <see cref="Read"/> 都是一次跨进程往返，因此调用次数就是成本；
/// 实现方必须把“区间内取值不一致”映射为 <see cref="TextAttributeValues.Mixed"/>。
/// </summary>
public interface ITextAttributeProbe
{
    object? Read(int start, int length, TextAttribute attribute);
}

/// <summary>UIA 的 Mixed 哨兵值。不能用 null 代替：null 表示 provider 不支持该属性。</summary>
public static class TextAttributeValues
{
    public static readonly object Mixed = new();
}

/// <summary>一段连续文本（UTF-16 码元区间）与它的五个属性值；null 表示该属性读不到。</summary>
public readonly record struct TextAttributeSegment(int Start, int Length, object?[] Values);

/// <summary>分段结果。<see cref="Calls"/> 为探针往返次数，<see cref="Truncated"/> 表示超出段数/调用预算。</summary>
public readonly record struct TextAttributePartition(
    IReadOnlyList<TextAttributeSegment> Segments,
    int Calls,
    bool Truncated);

/// <summary>
/// 用“整段读属性 + Mixed 二分边界”替代逐字符读取。
/// 逐字符读 5 个属性是 O(N×5) 次跨进程调用；这里只在整段为 Mixed 的属性上二分，
/// 复杂度降到 O(段数×属性数×logN)，且已确定的属性值随区间复用，不再重复读。
/// </summary>
public static class TextAttributePartitioner
{
    private const int AttributeCount = 5;

    /// <summary>“尚未确定”的占位符。与 null（provider 读不到）区分开。</summary>
    private static readonly object Unresolved = new();

    /// <summary>
    /// 把 <paramref name="text"/> 切成属性各自一致的连续段。
    /// <paramref name="rootValues"/> 是调用方已经读过的整段属性值（Mixed 用 <see cref="TextAttributeValues.Mixed"/>），
    /// 传进来可以省掉 5 次重复往返。
    /// </summary>
    public static TextAttributePartition Partition(
        string text,
        ITextAttributeProbe probe,
        IReadOnlyList<object?>? rootValues = null,
        int maxSegments = 12,
        int maxCalls = 64)
    {
        var segments = new List<TextAttributeSegment>();
        if (string.IsNullOrEmpty(text)) return new TextAttributePartition(segments, 0, false);

        var limit = Math.Max(1, maxSegments);
        var calls = 0;
        var truncated = false;

        // 调用方没给整段值时全部待定；给了就只把 Mixed 视为待定（null 是“不支持”，不再重读）。
        var root = new object?[AttributeCount];
        for (var i = 0; i < AttributeCount; i++)
        {
            if (rootValues == null)
            {
                root[i] = Unresolved;
                continue;
            }
            var value = i < rootValues.Count ? rootValues[i] : null;
            root[i] = ReferenceEquals(value, TextAttributeValues.Mixed) ? Unresolved : value;
        }

        Walk(0, text.Length, root);
        return new TextAttributePartition(segments, calls, truncated);

        void Walk(int start, int length, object?[] known)
        {
            if (length <= 0) return;
            var values = (object?[])known.Clone();
            for (var k = 0; k < AttributeCount; k++)
            {
                if (!ReferenceEquals(values[k], Unresolved)) continue;
                if (calls >= maxCalls) break;
                var value = Read(start, length, k);
                values[k] = ReferenceEquals(value, TextAttributeValues.Mixed) ? Unresolved : value;
            }

            var mixed = FirstUnresolved(values);
            if (mixed < 0)
            {
                Emit(start, length, values);
                return;
            }
            // 预算用尽或段数将满：整段收口，属性保持 null 交给调用方回退，绝不丢文字。
            if (segments.Count >= limit - 1 || calls >= maxCalls)
            {
                truncated = true;
                Emit(start, length, values);
                return;
            }

            var first = Read(start, 1, mixed);
            if (ReferenceEquals(first, TextAttributeValues.Mixed))
            {
                Emit(start, length, values);
                return;
            }

            var boundary = FindBoundary(text, start, start + length, mixed, first);
            if (boundary <= start || boundary >= start + length)
            {
                Emit(start, length, values);
                return;
            }

            values[mixed] = first;
            var right = (object?[])values.Clone();
            right[mixed] = Unresolved;
            Walk(start, boundary - start, values);
            Walk(boundary, start + length - boundary, right);
        }

        /// <summary>
        /// 二分找“值第一次不再是 first”的码元位置。用固定起点的前缀探测：右端点单调右移，
        /// 实现里每次探测只需移动一个端点；mixed 视为“已不同”，谓词仍单调。
        /// </summary>
        int FindBoundary(string content, int start, int end, int attribute, object? first)
        {
            var lo = start + 1;
            var hi = end + 1;
            while (lo < hi)
            {
                if (calls >= maxCalls) return -1;
                var mid = lo + (hi - lo) / 2;
                var value = Read(start, mid - start, attribute);
                if (ReferenceEquals(value, TextAttributeValues.Mixed) || !Equals(value, first)) hi = mid;
                else lo = mid + 1;
            }
            // lo 是第一个“包含变化”的前缀长度，切点因此在它之前一个码元。
            var boundary = lo - 1;
            if (boundary <= start || boundary >= end) return -1;
            // 代理对：切点顺延到码点之后，避免把 emoji 劈成两个孤立代理项。
            if (char.IsHighSurrogate(content[boundary - 1]) && char.IsLowSurrogate(content[boundary])) boundary++;
            return boundary >= end ? -1 : boundary;
        }

        object? Read(int start, int length, int attribute)
        {
            calls++;
            return probe.Read(start, length, (TextAttribute)attribute);
        }

        void Emit(int start, int length, object?[] values)
        {
            if (length <= 0) return;
            var resolved = new object?[AttributeCount];
            for (var i = 0; i < AttributeCount; i++)
            {
                resolved[i] = ReferenceEquals(values[i], Unresolved) ? null : values[i];
            }
            if (segments.Count >= limit)
            {
                // 已满额：并入最后一段，保持区间连续与全文覆盖。
                var last = segments[^1];
                segments[^1] = last with { Length = last.Length + length };
                truncated = true;
                return;
            }
            segments.Add(new TextAttributeSegment(start, length, resolved));
        }
    }

    private static int FirstUnresolved(object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (ReferenceEquals(values[i], Unresolved)) return i;
        }
        return -1;
    }
}
