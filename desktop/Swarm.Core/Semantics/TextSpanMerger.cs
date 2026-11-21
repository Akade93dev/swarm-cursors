namespace Swarm.Core.Semantics;

/// <summary>一段文字的样式。字段为 null 表示该属性读不到，由调用方回退到元素级样式。</summary>
public readonly record struct SpanStyle(
    string? FontFamily,
    double? FontSizePoints,
    int? FontWeight,
    bool? Italic,
    string? ColorCss);

/// <summary>
/// 把按码元对齐的样式合并成连续文本段。契约有两条：
/// 1）所有段的文字拼接后必须等于原文，绝不丢字符；
/// 2）切点不落在代理对中间，emoji 不会被劈成两个孤立代理项。
/// </summary>
public static class TextSpanMerger
{
    /// <summary>
    /// <paramref name="styles"/> 与 <paramref name="text"/> 逐 UTF-16 码元对齐；null 表示该位置读不到样式，
    /// 前导未知用 <paramref name="fallback"/>（元素级样式），其后未知沿用前一段。
    /// 相邻同样式合并，超过 <paramref name="maxSpans"/> 的部分并入最后一段。
    /// </summary>
    public static IReadOnlyList<(string Text, SpanStyle Style)> Merge(
        string text,
        IReadOnlyList<SpanStyle?> styles,
        SpanStyle fallback,
        int maxSpans = 12)
    {
        var result = new List<(string Text, SpanStyle Style)>();
        if (string.IsNullOrEmpty(text)) return result;
        var limit = Math.Max(1, maxSpans);

        var start = 0;
        var current = fallback;
        for (var i = 0; i < text.Length; i++)
        {
            var style = i < styles.Count ? styles[i] : null;
            if (style == null) continue;
            if (style.Value == current) continue;
            // 切点落在代理对中间：让整个码点留在前一段，等下一个码元再切。
            if (i > start && char.IsHighSurrogate(text[i - 1]) && char.IsLowSurrogate(text[i])) continue;
            if (i == start)
            {
                // 开头未知样式后的第一个可读样式：直接成为首段样式，不产生空段。
                current = style.Value;
                continue;
            }
            if (result.Count >= limit - 1)
            {
                // 段数已满：剩余文字整体并入当前段，保证拼接仍是完整原文。
                break;
            }
            result.Add((text[start..i], current));
            start = i;
            current = style.Value;
        }

        result.Add((text[start..], current));
        return result;
    }
}
