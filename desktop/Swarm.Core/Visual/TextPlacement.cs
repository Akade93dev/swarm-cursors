using System;
using System.Text;
using Swarm.Core.Semantics;

namespace Swarm.Core.Visual;

/// <summary>文字在覆盖层上的落位矩形（CSS 像素）。</summary>
public readonly record struct PlacedText(double X, double Y, double W, double H);

/// <summary>
/// 用像素墨迹包围盒校正文字落位。UIA 给出的元素矩形通常包含内边距，
/// 直接按它左对齐会把整段文字推向左上，和真实画面错开。
/// </summary>
public static class TextPlacement
{
    /// <summary>小于该尺寸的墨迹视为噪声（边框残影、光标），不参与校正。</summary>
    public const double MinInk = 4;

    /// <summary>
    /// 标记同一视觉行内的文本片段。行内相邻片段的字形上缘各不相同，如果各自用墨迹反推行盒，
    /// 纵向位置会逐片段抖动；这些片段必须共用 UIA 行框的 y。
    /// 判定：垂直重叠过半、水平间距小于 1.5 倍字高、字高差不超过 20%。
    /// </summary>
    public static HashSet<string> SharedLineIds(IReadOnlyList<(string Id, double X, double Y, double W, double H)> boxes)
    {
        var shared = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                if (!SameLine(boxes[i], boxes[j])) continue;
                shared.Add(boxes[i].Id);
                shared.Add(boxes[j].Id);
            }
        }
        return shared;
    }

    private static bool SameLine(
        (string Id, double X, double Y, double W, double H) left,
        (string Id, double X, double Y, double W, double H) right)
    {
        var height = Math.Max(left.H, right.H);
        if (height <= 0) return false;
        if (Math.Abs(left.H - right.H) > height * 0.2) return false;
        var overlap = Math.Min(left.Y + left.H, right.Y + right.H) - Math.Max(left.Y, right.Y);
        if (overlap <= Math.Min(left.H, right.H) * 0.5) return false;
        var gap = Math.Max(left.X, right.X) - Math.Min(left.X + left.W, right.X + right.W);
        return gap < height * 1.5;
    }

    /// <summary>
    /// 单行文字把换行折叠成一个空格。UIA 的 Name 常把同一行的多段用 CRLF 拼起来
    /// （如记事本状态栏的“行 1，\r\n列 44”），直接交给 pre-wrap 会真的换行，后半段被挤出可视区。
    /// </summary>
    public static string CollapseLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace && builder.Length > 0) builder.Append(' ');
            pendingSpace = false;
            builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 部分 UIA provider 把像素值直接填进磅属性（Win11 记事本状态栏即如此）：
    /// 按磅换算后的字高超过控件（或真实墨迹）高度，物理上放不下，说明原值本来就是像素。
    /// </summary>
    public static double NormalizeFontSize(double cssFromPoints, double reported, double heightCss)
    {
        if (heightCss <= 0 || heightCss > 40) return cssFromPoints;
        return cssFromPoints > heightCss * 0.92 ? reported : cssFromPoints;
    }

    /// <summary>
    /// 文字落位。行矩形是行级证据，内容相同的多个片段必然共享同一个 y；
    /// 没有行矩形时，同一行的片段也只用 UIA 行框 y（<paramref name="useInkY"/> 为 false），
    /// 只有独行文本才用墨迹上缘推 y，避免单个字形的上缘差被放大成行内抖动。
    /// 宽度保留 UIA 框宽，最终宽度由渲染层实测后决定。
    /// </summary>
    public static PlacedText Align(
        RectInt? ink,
        RectInt? line,
        PlacedText fallback,
        double scale,
        double lineHeight,
        double fontSize,
        bool useInkY = true)
    {
        if (scale <= 0) return fallback;
        var halfLeading = Math.Max(0, (lineHeight - fontSize) / 2);
        if (line is RectInt row && row.Width >= MinInk && row.Height >= MinInk)
        {
            // 行矩形是相对截屏帧的绝对坐标，和墨迹同一坐标系。
            return new PlacedText(
                row.X / scale,
                row.Y / scale - halfLeading,
                Math.Max(fallback.W, row.Width / scale),
                Math.Max(fallback.H, Math.Min(row.Height / scale, lineHeight)));
        }
        if (ink is not RectInt box || box.Width < MinInk || box.Height < MinInk) return fallback;
        // 行内片段不再各自按墨迹校 y：同一行片段的墨迹上缘随字形变化，会造成纵向抖动。
        var y = useInkY ? box.Y / scale - halfLeading : fallback.Y;
        return new PlacedText(
            box.X / scale,
            y,
            Math.Max(fallback.W, box.Width / scale),
            Math.Max(fallback.H, Math.Min(box.Height / scale, lineHeight)));
    }
}
