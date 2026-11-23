using System.Text;

namespace Swarm.Core.Semantics;

/// <summary>IA2 单个字符的屏幕矩形（物理像素）。</summary>
public readonly record struct Ia2Glyph(int Offset, RectInt Bounds);

/// <summary>IA2 attributes() 解析出的文本样式；字号统一换算成 CSS 像素。</summary>
public sealed record Ia2TextStyle(
    string? FontFamily,
    double? FontSizePx,
    string? ColorCss,
    string? BackgroundCss,
    bool? Bold,
    bool? Italic);

/// <summary>一个可访问文本节点：角色、完整文本与逐字符几何。</summary>
public sealed record Ia2TextNode(
    int Role,
    string Text,
    IReadOnlyList<Ia2Glyph> Glyphs,
    Ia2TextStyle? Style = null);

/// <summary>由字符几何重建出的一行文字，矩形为屏幕物理像素。</summary>
public sealed record Ia2Line(string Text, RectInt Bounds, int Role, Ia2TextStyle? Style = null);

/// <summary>一条 IA2 行取代某个 UIA 文本元素。</summary>
public sealed record Ia2TextPlacement(DesktopElement Element, Ia2Line Line);

/// <summary>
/// 把 IA2 的逐字符矩形聚成视觉行。字符矩形来自字形推进而不是元素框，
/// 因此行内间距、换行点都按真实字符恢复，不依赖 UIA 的宽度与行框。
/// </summary>
public static class Ia2LineBuilder
{
    /// <summary>逐字符矩形最小高度容差：同一行末尾偶发 2px 抖动，纯精确 y 会拆成两行。</summary>
    private const double MinimumLineTolerance = 2;

    /// <summary>把单个文本节点的字符矩形聚成行；整行中心落在 <paramref name="clip"/> 外的丢弃。</summary>
    public static IReadOnlyList<Ia2Line> Build(Ia2TextNode node, RectInt clip)
    {
        if (string.IsNullOrEmpty(node.Text) || node.Glyphs.Count == 0) return Array.Empty<Ia2Line>();
        var glyphs = node.Glyphs
            .Where(glyph => glyph.Bounds.Width > 0 && glyph.Bounds.Height > 0)
            .OrderBy(glyph => glyph.Offset)
            .ToList();
        if (glyphs.Count == 0) return Array.Empty<Ia2Line>();

        var lines = new List<Ia2Line>();
        var current = new List<Ia2Glyph>();
        foreach (var glyph in glyphs)
        {
            if (glyph.Offset < 0 || glyph.Offset >= node.Text.Length) continue;
            if (current.Count > 0 && !SameLine(current[^1], glyph)) Flush();
            current.Add(glyph);
        }
        Flush();
        return lines;

        void Flush()
        {
            if (current.Count > 0 && BuildLine(node, current, clip) is { } line) lines.Add(line);
            current.Clear();
        }
    }

    /// <summary>同一节点的多行去重：重复节点（容器与子节点同时暴露文本）只保留一份。</summary>
    public static IReadOnlyList<Ia2Line> Deduplicate(IEnumerable<Ia2Line> lines)
    {
        var seen = new HashSet<(int X, int Y, int W, int H, string Text)>();
        var result = new List<Ia2Line>();
        foreach (var line in lines)
        {
            var bounds = line.Bounds;
            if (!seen.Add((bounds.X, bounds.Y, bounds.Width, bounds.Height, line.Text))) continue;
            result.Add(line);
        }
        return result;
    }

    private static bool SameLine(Ia2Glyph previous, Ia2Glyph glyph)
    {
        var height = Math.Max(1, Math.Max(previous.Bounds.Height, glyph.Bounds.Height));
        var tolerance = Math.Max(MinimumLineTolerance, height * 0.4);
        if (Math.Abs(glyph.Bounds.Y - previous.Bounds.Y) > tolerance) return false;
        // 正常字符推进可能有 1~2px 回退（字距/连字）；只有明显回折（换行）才另起一行。
        return glyph.Bounds.X >= previous.Bounds.X - Math.Max(MinimumLineTolerance, previous.Bounds.Width);
    }

    private static Ia2Line? BuildLine(Ia2TextNode node, IReadOnlyList<Ia2Glyph> glyphs, RectInt clip)
    {
        var first = 0;
        var last = glyphs.Count - 1;
        while (first <= last && char.IsWhiteSpace(node.Text[glyphs[first].Offset])) first++;
        while (last >= first && char.IsWhiteSpace(node.Text[glyphs[last].Offset])) last--;
        if (first > last) return null;

        var text = Slice(node.Text, glyphs[first].Offset, glyphs[last].Offset + 1);
        if (text.Length == 0) return null;

        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        for (var index = first; index <= last; index++)
        {
            var box = glyphs[index].Bounds;
            left = Math.Min(left, box.X);
            top = Math.Min(top, box.Y);
            right = Math.Max(right, box.Right);
            bottom = Math.Max(bottom, box.Bottom);
        }
        var bounds = new RectInt(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        // 整行（含中心）落在捕获矩形外时丢弃：IA2 树里常混有过期/离屏节点。
        var centerX = bounds.X + bounds.Width / 2;
        var centerY = bounds.Y + bounds.Height / 2;
        if (centerX < clip.X || centerY < clip.Y || centerX >= clip.Right || centerY >= clip.Bottom) return null;
        return new Ia2Line(text, bounds, node.Role, node.Style);
    }

    /// <summary>按 UTF-16 偏移切片，并保证不把代理对切成半个字符。</summary>
    private static string Slice(string text, int start, int end)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start])) start--;
        if (end > 0 && end < text.Length && char.IsLowSurrogate(text[end])) end++;
        var builder = new StringBuilder(end - start);
        foreach (var ch in text.AsSpan(start, end - start))
        {
            // 单行内部不应再有换行；控制字符折叠成空格，避免渲染层真的断行。
            builder.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return builder.ToString().Trim();
    }
}

/// <summary>解析 IA2 <c>attributes()</c> 返回的 <c>key:value;</c> 串。</summary>
public static class Ia2TextAttributes
{
    public static Ia2TextStyle? Parse(string? attributes)
    {
        if (string.IsNullOrWhiteSpace(attributes)) return null;
        string? family = null;
        string? color = null;
        string? background = null;
        double? size = null;
        bool? bold = null;
        bool? italic = null;
        foreach (var part in attributes.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf(':');
            if (separator <= 0) continue;
            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (value.Length == 0) continue;
            switch (key.ToLowerInvariant())
            {
                case "font-family":
                    family = value;
                    break;
                case "font-size":
                    size = ParseFontSize(value);
                    break;
                case "color":
                    color = ParseColor(value);
                    break;
                case "background-color":
                    background = ParseColor(value);
                    break;
                case "font-weight":
                    bold = value.Equals("bold", StringComparison.OrdinalIgnoreCase) || value is "600" or "700" or "800" or "900"
                        ? true
                        : value.Equals("normal", StringComparison.OrdinalIgnoreCase) || value is "400" or "500"
                            ? false
                            : bold;
                    break;
                case "font-style":
                    italic = value.StartsWith("italic", StringComparison.OrdinalIgnoreCase)
                        || value.StartsWith("oblique", StringComparison.OrdinalIgnoreCase)
                        ? true
                        : value.Equals("normal", StringComparison.OrdinalIgnoreCase)
                            ? false
                            : italic;
                    break;
            }
        }
        if (family == null && size == null && color == null && background == null && bold == null && italic == null) return null;
        return new Ia2TextStyle(family, size, color, background, bold, italic);
    }

    /// <summary>IA2 的 font-size 常是磅值；统一折算成 CSS 像素（1pt = 96/72 px）。</summary>
    public static double? ParseFontSize(string value)
    {
        var text = value.Trim();
        var factor = 1d;
        if (text.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            factor = 96d / 72d;
            text = text[..^2];
        }
        else if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2];
        }
        return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size) && size > 0
            ? size * factor
            : null;
    }

    private static string? ParseColor(string value)
    {
        var color = value.Trim();
        return color.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) || color.StartsWith('#') ? color : null;
    }
}

/// <summary>
/// 把 IA2 行与 UIA 文本元素配对。只替换文本内容能对上的元素：
/// IA2 树里还混着按钮、菜单、离屏容器，宁可漏替换也不要把别的文字画上来。
/// </summary>
public static class Ia2TextMatcher
{
    /// <summary>替换一个元素要求其正文至少有一半字符被 IA2 行覆盖，避免只替换半段造成缺字。</summary>
    private const double MinimumCoverage = 0.5;

    /// <summary>行中心允许超出元素框的物理像素；IA2 字符 em 框与 UIA 行框存在 2~3px 基线差。</summary>
    private const int CenterMargin = 6;

    public static IReadOnlyList<Ia2TextPlacement> Match(
        IReadOnlyList<DesktopElement> mapped,
        IReadOnlyDictionary<string, UiaElementInfo> physicalById,
        IReadOnlyList<Ia2Line> lines)
    {
        if (lines.Count == 0) return Array.Empty<Ia2TextPlacement>();
        var candidates = new List<Candidate>();
        foreach (var element in mapped)
        {
            if (element.Kind != "text" || element.Semantic != "text" || element.Sensitive) continue;
            if (!physicalById.TryGetValue(element.Id, out var info)) continue;
            var text = Squash(info.Name);
            if (text.Length == 0) continue;
            candidates.Add(new Candidate(element, info.Bounds, text, (long)info.Bounds.Width * info.Bounds.Height));
        }
        if (candidates.Count == 0) return Array.Empty<Ia2TextPlacement>();

        var chosen = new Dictionary<string, List<Ia2Line>>(StringComparer.Ordinal);
        foreach (var line in lines.OrderBy(item => item.Bounds.Y).ThenBy(item => item.Bounds.X))
        {
            var text = Squash(line.Text);
            if (text.Length == 0) continue;
            Candidate? best = null;
            foreach (var candidate in candidates)
            {
                if (!Covers(candidate.Bounds, line.Bounds)) continue;
                if (!Compatible(candidate.Text, text)) continue;
                // 多个候选都能对上时取面积最小的那个，避免把行归到覆盖整窗的容器文本。
                if (best != null && candidate.Area >= best.Area) continue;
                best = candidate;
            }
            if (best == null) continue;
            if (!chosen.TryGetValue(best.Element.Id, out var list)) chosen[best.Element.Id] = list = new List<Ia2Line>();
            list.Add(line);
        }

        var placements = new List<Ia2TextPlacement>();
        foreach (var (id, matched) in chosen)
        {
            var candidate = candidates.First(item => item.Element.Id == id);
            var covered = matched.Sum(line => Squash(line.Text).Length);
            if (covered < candidate.Text.Length * MinimumCoverage) continue;
            foreach (var line in matched.OrderBy(item => item.Bounds.Y).ThenBy(item => item.Bounds.X))
            {
                placements.Add(new Ia2TextPlacement(candidate.Element, line));
            }
        }
        return placements;
    }

    private sealed record Candidate(DesktopElement Element, RectInt Bounds, string Text, long Area);

    /// <summary>行的中心落在元素框（含容差）内，且水平方向至少重叠 40%，才算同一块文字。</summary>
    private static bool Covers(RectInt box, RectInt line)
    {
        var centerX = line.X + line.Width / 2;
        var centerY = line.Y + line.Height / 2;
        if (centerX < box.X - CenterMargin || centerX >= box.Right + CenterMargin) return false;
        if (centerY < box.Y - CenterMargin || centerY >= box.Bottom + CenterMargin) return false;
        var horizontal = Math.Min(box.Right, line.Right) - Math.Max(box.X, line.X);
        return horizontal > 0 && horizontal >= Math.Min(box.Width, line.Width) * 0.4;
    }

    /// <summary>忽略空白后互相包含即视为同一段文字；多行元素的正文包含单行行的文本。</summary>
    private static bool Compatible(string element, string line) =>
        element.Contains(line, StringComparison.Ordinal) || line.Contains(element, StringComparison.Ordinal);

    private static string Squash(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch)) builder.Append(ch);
        }
        return builder.ToString();
    }
}
