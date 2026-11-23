// TRISHUL HSP (Hierarchical Screen Parsing) — 训练免费双层粒度 GUI 解析
// 参考: arXiv:2502.08226 TRISHUL: Towards Region Identification and Screen Hierarchy Understanding
// 核心思路: 粗粒度大区域 (Layer 1) + 细粒度可交互元素 (Layer 2), 加 SEED 空间增强描述
//
// 依赖: Swarm.Core (零 WPF/WinRT) — 只吃归一化 bbox + 字符串, 产出 SoMElement
// DesktopTaskService 负责把 UIA + OCR 翻译成这里用的通用接口

namespace Swarm.Core.Perception;

/// <summary>HSP 输入 — 粗粒度 UIA 容器级元素 (Layer 1 候选).</summary>
public sealed record HspRegion(
    string Id,
    string RegionType,             // "TitleBar" / "MenuBar" / "Sidebar" / "StatusBar" / "ContentArea"
    double X, double Y, double W, double H); // 归一化 [0,1]

/// <summary>HSP 输入 — 细粒度可交互元素 (Layer 2 候选).</summary>
public sealed record HspInteractive(
    string SourceId,               // UIA element.Id 或 OCR 行的 hash
    string? UiaControlType,        // "Button" / "Edit" / "ComboBox" / null (纯 OCR)
    string Text,
    double X, double Y, double W, double H,
    double Confidence);

public static class HierarchicalScreenParser
{
    // 粗粒度容器 ControlType → RegionType 映射表
    private static readonly Dictionary<string, string> ContainerMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TitleBar"] = "TitleBar",
        ["MenuBar"] = "MenuBar",
        ["ToolBar"] = "MenuBar",
        ["StatusBar"] = "StatusBar",
        ["ScrollBar"] = "StatusBar",
        ["SplitButton"] = "MenuBar",
        ["SystemMenuBar"] = "MenuBar",
    };

    // 可交互 ControlType → SoMKind 映射表
    private static readonly Dictionary<string, SoMKind> InteractiveMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Button"] = SoMKind.Button,
        ["SplitButton"] = SoMKind.Button,
        ["CheckBox"] = SoMKind.Button,
        ["RadioButton"] = SoMKind.Button,
        ["Hyperlink"] = SoMKind.Link,
        ["MenuItem"] = SoMKind.Button,
        ["ComboBox"] = SoMKind.Label,
        ["Edit"] = SoMKind.Label,
        ["Document"] = SoMKind.Label,
        ["TabItem"] = SoMKind.Button,
        ["ListItem"] = SoMKind.Button,
        ["Slider"] = SoMKind.Button,
        ["Custom"] = SoMKind.Unknown,
    };

    /// <summary>
    /// TRISHUL HSP — 双层粒度 SoM 融合构建.
    /// Layer 1: UIA 容器级区域 → 分配 MarkId + RegionType
    /// Layer 2: UIA 可交互元素 + OCR 行 → 合并重叠 + 分配 ParentMarkId + SEED 空间描述
    /// </summary>
    public static IReadOnlyList<SoMElement> Build(
        IReadOnlyList<HspRegion> regions,
        IReadOnlyList<HspInteractive> interactives,
        IReadOnlyList<OcrLine> ocrLines,
        double viewportWidth,
        double viewportHeight)
    {
        var result = new List<SoMElement>(regions.Count + interactives.Count + ocrLines.Count);
        int markId = 1;

        // ===== LAYER 1: 粗粒度大区域 =====
        var regionByMarkId = new Dictionary<int, HspRegion>();
        var layer1Regions = regions.Where(r => r.W > 0 && r.H > 0).ToList();
        foreach (var r in layer1Regions)
        {
            var kind = r.RegionType switch
            {
                "TitleBar" => SoMKind.TitleBar,
                "MenuBar" => SoMKind.MenuBar,
                "Sidebar" => SoMKind.Sidebar,
                "ContentArea" => SoMKind.ContentArea,
                "StatusBar" => SoMKind.StatusBar,
                _ => SoMKind.ContentArea,
            };
            var id = markId++;
            regionByMarkId[id] = r;
            result.Add(new SoMElement(
                MarkId: id,
                Text: r.RegionType,
                X: r.X, Y: r.Y, Width: r.W, Height: r.H,
                Kind: kind,
                Confidence: 1.0,
                HierarchyLevel: 1));
        }

        // ===== LAYER 2: 细粒度可交互 =====
        // 把 UIA interactives 和 OCR lines 先转成统一候选, 然后合并重叠
        var candidates = new List<Layer2Candidate>();

        // UIA interactives 优先 (有 UIA 信号更准)
        foreach (var u in interactives.Where(i => i.W > 0 && i.H > 0))
        {
            var kind = u.UiaControlType != null && InteractiveMap.TryGetValue(u.UiaControlType, out var k) ? k : SetOfMarkBuilder.ClassifyStatic(u.Text);
            candidates.Add(new Layer2Candidate(
                SourceId: u.SourceId,
                Text: u.Text,
                X: u.X, Y: u.Y, W: u.W, H: u.H,
                Kind: kind,
                Confidence: u.Confidence,
                UiaControlType: u.UiaControlType,
                FromOcrOnly: false));
        }

        // OCR lines 追加 (只加不与 UIA 重叠的, 避免重复)
        foreach (var o in ocrLines)
        {
            if (o.Confidence < 0.80) continue;
            if (IsPunctuationOnly(o.Text)) continue;
            if (o.Text.Trim().Length == 0) continue;
            if (OverlapsAnyUia(candidates, o.X, o.Y, o.Width, o.Height)) continue;
            var kind = SetOfMarkBuilder.ClassifyStatic(o.Text);
            if (kind == SoMKind.Paragraph) continue;
            candidates.Add(new Layer2Candidate(
                SourceId: $"ocr_{o.Text.GetHashCode():X8}",
                Text: o.Text,
                X: o.X, Y: o.Y, W: o.Width, H: o.Height,
                Kind: kind,
                Confidence: o.Confidence,
                UiaControlType: null,
                FromOcrOnly: true));
        }

        // 合并 Layer 2 内部重叠 (贪心)
        var merged = MergeOverlappingLayer2(candidates);

        // ===== CONTAINMENT + SEED: 给每个 Layer 2 元素查父区域 + 生成空间描述 =====
        foreach (var c in merged)
        {
            var (parentMarkId, spatialContext) = ResolveParentAndSpatial(c, regionByMarkId);
            var flags = c.FromOcrOnly ? 0b10 : 0b01; // UIA overlap 或纯 OCR

            result.Add(new SoMElement(
                MarkId: markId++,
                Text: c.Text,
                X: c.X, Y: c.Y, Width: c.W, Height: c.H,
                Kind: c.Kind,
                Confidence: c.Confidence,
                HierarchyLevel: 2,
                ParentMarkId: parentMarkId,
                SpatialContext: spatialContext,
                UiaControlType: c.UiaControlType,
                OverlapFlags: flags));
        }

        return result;
    }

    // ========== 辅助 ==========

    private static bool IsPunctuationOnly(string text)
    {
        foreach (var c in text)
            if (!char.IsPunctuation(c) && !char.IsWhiteSpace(c)) return false;
        return true;
    }

    private static bool OverlapsAnyUia(List<Layer2Candidate> uia, double x, double y, double w, double h)
    {
        // 用 IoU ≥ 0.3 视为重叠, 跳过
        foreach (var u in uia)
        {
            double ix1 = Math.Max(u.X, x), iy1 = Math.Max(u.Y, y);
            double ix2 = Math.Min(u.X + u.W, x + w), iy2 = Math.Min(u.Y + u.H, y + h);
            if (ix2 <= ix1 || iy2 <= iy1) continue;
            double inter = (ix2 - ix1) * (iy2 - iy1);
            double union = u.W * u.H + w * h - inter;
            if (union > 0 && inter / union >= 0.3) return true;
        }
        return false;
    }

    private static List<Layer2Candidate> MergeOverlappingLayer2(List<Layer2Candidate> input)
    {
        if (input.Count <= 1) return input;
        var sorted = input.OrderBy(c => c.Y).ToList();
        var result = new List<Layer2Candidate>(sorted.Count);
        Layer2Candidate? current = null;
        foreach (var c in sorted)
        {
            if (current == null) { current = c; continue; }
            if (IoU(current, c) >= 0.50)
            {
                current = Merge(current, c);
            }
            else
            {
                result.Add(current);
                current = c;
            }
        }
        if (current != null) result.Add(current);
        return result;
    }

    private static double IoU(Layer2Candidate a, Layer2Candidate b)
    {
        double ix1 = Math.Max(a.X, b.X), iy1 = Math.Max(a.Y, b.Y);
        double ix2 = Math.Min(a.X + a.W, b.X + b.W), iy2 = Math.Min(a.Y + a.H, b.Y + b.H);
        if (ix2 <= ix1 || iy2 <= iy1) return 0;
        double inter = (ix2 - ix1) * (iy2 - iy1);
        double union = a.W * a.H + b.W * b.H - inter;
        return union > 0 ? inter / union : 0;
    }

    private static Layer2Candidate Merge(Layer2Candidate a, Layer2Candidate b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        double x2 = Math.Max(a.X + a.W, b.X + b.W), y2 = Math.Max(a.Y + a.H, b.Y + b.H);
        string text = string.Join(" ", new[] { a.Text, b.Text }.Where(s => !string.IsNullOrWhiteSpace(s)));
        // UIA type 优先 (比 OCR 更准)
        string? uiaType = a.UiaControlType ?? b.UiaControlType;
        // FromOcrOnly: 两边都纯 OCR 才标记 true
        bool fromOcrOnly = a.FromOcrOnly && b.FromOcrOnly;
        // Kind 选更具体的那个 (Button > Label > Unknown)
        var kind = (int)a.Kind < (int)b.Kind ? a.Kind : b.Kind;
        return new Layer2Candidate(
            a.SourceId + "+" + b.SourceId,
            text, x, y, x2 - x, y2 - y,
            kind,
            Math.Max(a.Confidence, b.Confidence),
            uiaType,
            fromOcrOnly);
    }

    /// <summary>
    /// SEED (Spatially Enhanced Element Description) — 给元素生成空间语义.
    /// 1) 找最近的 Layer 1 父区域 (bContainment)
    /// 2) 生成相对位置描述: "在 TitleBar 内" / "在 MenuBar 右侧" / "在 ContentArea 上半部分"
    /// </summary>
    private static (int? ParentMarkId, string? SpatialContext) ResolveParentAndSpatial(
        Layer2Candidate element,
        Dictionary<int, HspRegion> regionsByMarkId)
    {
        int? parentId = null;
        HspRegion? containing = null;

        // 找完全包含的 (bContainment ≥ 0.8)
        foreach (var kv in regionsByMarkId)
        {
            var r = kv.Value;
            if (element.X >= r.X - 0.001 && element.Y >= r.Y - 0.001 &&
                element.X + element.W <= r.X + r.W + 0.001 &&
                element.Y + element.H <= r.Y + r.H + 0.001)
            {
                // 选最窄的那个 (嵌套区域时取 innermost)
                if (containing == null || r.W * r.H < containing.W * containing.H)
                {
                    containing = r;
                    parentId = kv.Key;
                }
            }
        }

        if (containing == null) return (null, null);

        // 生成相对位置
        string position = ComputeRelativePosition(element, containing);
        return (parentId, $"在 {containing.RegionType} {position}".Trim());
    }

    private static string ComputeRelativePosition(Layer2Candidate e, HspRegion parent)
    {
        double exCenter = e.X + e.W / 2;
        double eyCenter = e.Y + e.H / 2;
        double pxCenter = parent.X + parent.W / 2;
        double pyCenter = parent.Y + parent.H / 2;
        double relX = (exCenter - parent.X) / parent.W; // 0..1 在 parent 内的相对位置
        double relY = (eyCenter - parent.Y) / parent.H;

        var parts = new List<string>();
        // 水平
        if (relX < 0.33) parts.Add("左侧");
        else if (relX > 0.67) parts.Add("右侧");
        else parts.Add("中间");
        // 垂直
        if (relY < 0.33) parts.Add("上半部");
        else if (relY > 0.67) parts.Add("下半部");
        else parts.Add("中部");

        return string.Join(" ", parts);
    }

    private sealed record Layer2Candidate(
        string SourceId,
        string Text,
        double X, double Y, double W, double H,
        SoMKind Kind,
        double Confidence,
        string? UiaControlType,
        bool FromOcrOnly);
}
