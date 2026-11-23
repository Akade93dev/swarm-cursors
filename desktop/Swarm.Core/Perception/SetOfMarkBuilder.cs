// P2 OCR/SoM 视觉兜底 — Set-of-Mark 轻量规则构建 (Core 层, 零 WinRT 依赖)
// 原理: 过滤 OCR 结果中的"可能可交互"文本, 给每个分配 MarkId, 提升多模态模型 grounding.
// 不跑 FastSAM/ONNX 分割模型 — 那是 ~1GB, 对 .NET 进程过重.

namespace Swarm.Core.Perception;

public static class SetOfMarkBuilder
{
    // 判定阈值 — 可配置, 不改源码
    private const double MinConfidence = 0.80;
    private const int MaxInteractiveLength = 40;      // 超过这个长度多半是段落
    private const double MaxOverlapRatio = 0.50;       // bbox 重叠超过此比例视为同一元素

    // 按钮关键词 (大小写不敏感) — 覆盖 OK/确定/保存/提交/搜索/删除/发送/登录 等
    private static readonly HashSet<string> ButtonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ok", "确定", "好", "保存", "提交", "搜索", "查找", "删除", "发送", "登录", "退出",
        "注册", "取消", "关闭", "下一步", "上一步", "应用", "确认", "是", "否",
        "register", "login", "sign in", "sign out", "submit", "save", "search", "find",
        "delete", "send", "close", "cancel", "next", "previous", "apply", "confirm",
    };

    /// <summary>从 OCR 结果构建 SoM 编号元素列表.</summary>
    public static IReadOnlyList<SoMElement> Build(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0) return Array.Empty<SoMElement>();

        // 1) 过滤: confidence 够高 + 非标点 + 长度合理
        var candidates = lines
            .Where(l => l.Confidence >= MinConfidence)
            .Where(l => !IsPunctuationOnly(l.Text))
            .Where(l => l.Text.Trim().Length > 0)
            .ToList();

        // 2) 合并重叠 bbox (同一按钮有时被 OCR 拆成多行)
        var merged = MergeOverlapping(candidates).ToList();

        // 3) 过滤掉长段落 (判定为不可交互)
        var interactive = merged
            .Where(l => l.Text.Length <= MaxInteractiveLength)
            .ToList();

        // 4) 分配 MarkId + 类型猜测
        var result = new List<SoMElement>(interactive.Count);
        int id = 1;
        foreach (var line in interactive)
        {
            var kind = Classify(line.Text);
            // 段落类直接跳过 (长文本大概率不可交互)
            if (kind == SoMKind.Paragraph) continue;
            result.Add(new SoMElement(
                MarkId: id++,
                Text: line.Text,
                X: line.X,
                Y: line.Y,
                Width: line.Width,
                Height: line.Height,
                Kind: kind,
                Confidence: line.Confidence));
        }
        return result;
    }

    private static SoMKind Classify(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > MaxInteractiveLength) return SoMKind.Paragraph;
        // Link: 包含 http / https / www. / ://
        if (trimmed.Contains("http", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("www.", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("://", StringComparison.Ordinal))
            return SoMKind.Link;
        // Button: 以关键词开头或等于关键词 (允许末尾标点)
        var lower = trimmed.ToLowerInvariant();
        foreach (var kw in ButtonWords)
        {
            if (lower.StartsWith(kw, StringComparison.Ordinal) &&
                (trimmed.Length == kw.Length || !char.IsLetterOrDigit(trimmed[kw.Length])))
                return SoMKind.Button;
        }
        // 短文本 → Label/Unknown
        return trimmed.Length <= 20 ? SoMKind.Label : SoMKind.Unknown;
    }

    private static bool IsPunctuationOnly(string text)
    {
        foreach (var c in text)
            if (!char.IsPunctuation(c) && !char.IsWhiteSpace(c)) return false;
        return true;
    }

    private static IEnumerable<OcrLine> MergeOverlapping(List<OcrLine> lines)
    {
        // 简单贪心: 按 Y 排序, 依次合并与当前 bbox 有显著重叠的行
        if (lines.Count <= 1) return lines;
        var sorted = lines.OrderBy(l => l.Y).ToList();
        var result = new List<OcrLine>(sorted.Count);
        OcrLine? current = null;
        foreach (var line in sorted)
        {
            if (current == null) { current = line; continue; }
            if (Overlaps(current, line, MaxOverlapRatio))
            {
                current = Merge(current, line);
            }
            else
            {
                result.Add(current);
                current = line;
            }
        }
        if (current != null) result.Add(current);
        return result;
    }

    private static bool Overlaps(OcrLine a, OcrLine b, double ratio)
    {
        double ax2 = a.X + a.Width, ay2 = a.Y + a.Height;
        double bx2 = b.X + b.Width, by2 = b.Y + b.Height;
        double ix1 = Math.Max(a.X, b.X), iy1 = Math.Max(a.Y, b.Y);
        double ix2 = Math.Min(ax2, bx2), iy2 = Math.Min(ay2, by2);
        if (ix2 <= ix1 || iy2 <= iy1) return false;
        double inter = (ix2 - ix1) * (iy2 - iy1);
        double union = a.Width * a.Height + b.Width * b.Height - inter;
        return union > 0 && inter / union >= ratio;
    }

    private static OcrLine Merge(OcrLine a, OcrLine b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double x2 = Math.Max(a.X + a.Width, b.X + b.Width);
        double y2 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        // 置信度取较高者, 文本拼接 (空格分隔)
        string text = string.Join(" ", new[] { a.Text, b.Text }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new OcrLine(text, x, y, x2 - x, y2 - y, Math.Max(a.Confidence, b.Confidence));
    }
}
