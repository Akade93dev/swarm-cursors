namespace Swarm.Core.Semantics;

/// <summary>一段文字的样式。字段为 null 表示该属性读不到，由调用方回退到元素级样式。</summary>
public readonly record struct SpanStyle(
    string? FontFamily,
    double? FontSizePoints,
    int? FontWeight,
    bool? Italic,
    string? ColorCss);

/// <summary>
/// 把逐字符的 UIA 样式合并成连续文本段。逐字符读取是跨进程调用里最省事的方式，
/// 但属性和文本必须重新拼回“段”，否则重建时每个字符都会变成一个节点。
/// </summary>
public static class TextSpanMerger
{
    /// <summary>
    /// <paramref name="styles"/> 与 <paramref name="text"/> 逐 UTF-16 码元对齐；
    /// null 表示该位置读不到样式，沿用前一段。相邻同样式合并，超过 <paramref name="maxSpans"/>
    /// 的部分并入最后一段，保证拼接后仍是完整文本。
    /// </summary>
    public static IReadOnlyList<(string Text, SpanStyle Style)> Merge(
        string text,
        IReadOnlyList<SpanStyle?> styles,
        int maxSpans = 12)
    {
        var result = new List<(string Text, SpanStyle Style)>();
        if (string.IsNullOrEmpty(text) || styles.Count == 0 || maxSpans <= 0) return result;

        var start = 0;
        SpanStyle? current = null;
        for (var i = 0; i < text.Length; i++)
        {
            var style = i < styles.Count ? styles[i] : null;
            // 未知样式跟随当前段；开头就未知时先跳过，等遇到第一个可读样式再定段。
            if (style == null) continue;
            if (current == null)
            {
                current = style;
                start = i;
                continue;
            }
            if (style.Value != current.Value)
            {
                result.Add((text[start..i], current.Value));
                start = i;
                current = style;
                if (result.Count == maxSpans - 1)
                {
                    // 已用满额度：剩下的文字整体并入最后一段，保留全部内容。
                    result.Add((text[start..], style.Value));
                    return result;
                }
            }
        }
        if (current != null) result.Add((text[start..], current.Value));
        return result;
    }
}
