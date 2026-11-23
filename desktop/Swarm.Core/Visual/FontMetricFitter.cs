using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Core.Visual;

/// <summary>
/// 候选字体在 100px em 下的度量：宽度是整段文本的 advance 宽，
/// 其余是帽高/x-height/上升部/降部深度。全部用同一套 GDI 度量，单位与截图一致（设备像素）。
/// </summary>
public readonly record struct FontProbe(double Width, double CapHeight, double XHeight, double Ascender, double Descender);

/// <summary>字体度量来源。真实实现走 GDI；测试注入合成数据，避免依赖本机字体。</summary>
public interface IFontProbeSource
{
    FontProbe Measure(string text, string family, int weight, bool italic);
}

/// <summary>一个待尝试的字体组合。italic 默认不进候选集：静态截图无法可靠区分正斜体。</summary>
public readonly record struct FontCandidate(string Family, int Weight, bool Italic)
{
    private static readonly FontCandidate[] Defaults =
    {
        new("Segoe UI", 400, false),
        new("Segoe UI", 700, false),
        new("Microsoft YaHei UI", 400, false),
        new("Microsoft YaHei UI", 700, false),
        new("Arial", 400, false),
        new("Arial", 700, false),
        new("Tahoma", 400, false),
        new("Tahoma", 700, false),
        new("Calibri", 400, false),
        new("Calibri", 700, false),
    };

    public static IReadOnlyList<FontCandidate> Default => Defaults;
}

/// <summary>反解输入：文本来自 UIA，墨迹宽高与笔画宽度来自截图（设备像素），DpiScale 用于折回 CSS 像素。</summary>
public sealed record FontFitRequest(
    string Text,
    double InkWidth,
    double InkHeight,
    double? StrokeWidth,
    double DpiScale);

/// <summary>反解结果。SizePhysical 是设备像素下的 em，SizeCss 已除以 DPI 缩放。</summary>
public sealed record FontFitResult(
    string Family,
    int Weight,
    double SizePhysical,
    double SizeCss,
    double Confidence,
    double Score);

/// <summary>
/// Tier B：字体度量反解（analysis-by-synthesis）。
/// 对每个候选字体用同一 GDI 度量测 100px 下的文本宽度，线性反解字号；
/// 再用预测墨迹高度与笔画宽度比打分，取最优者。置信度不足时返回 null，交给像素估计。
/// </summary>
public sealed class FontMetricFitter
{
    /// <summary>
    /// 笔画宽度比先验。文献值是渲染前的 8%–12% / 18%–25%，但截图上按“核心像素”量到的
    /// GDI 笔画含半像素取整，实测常规 ≈15%、粗体 ≈30%，这里用校准后的值。
    /// </summary>
    private const double LightStrokeRatio = 0.10;
    private const double RegularStrokeRatio = 0.15;
    private const double BoldStrokeRatio = 0.30;

    /// <summary>无法区分候选时宁可回退，也不要“自信地猜错”。</summary>
    private const double MinConfidence = 0.15;

    /// <summary>
    /// 绝对分门限：领先第二名再多，只要最优候选本身的度量误差超过这个值，就说明模型根本不适用。
    /// </summary>
    private const double AbsoluteMinScore = 0.25;

    /// <summary>单候选没有对照组，置信度只能来自绝对质量，因此封顶而不给满分。</summary>
    private const double SingleCandidateConfidenceCap = 0.8;

    private const double ConfidenceFloor = 0.02;
    private const double MinSize = 6;
    private const double MaxSize = 400;
    // 系统默认 UI 字体在 Windows 上覆盖绝大多数控件；实测小字号时 Tahoma/Calibri 的
    // 度量会与 Segoe UI 在抗锯齿噪声内打平，若先验太弱就会把常规文本误判成冷门字体。
    private const double OtherFacePenalty = 0.2;
    private const double LatinOnCjkFacePenalty = 0.1;
    private const double RatioWeight = 0.8;

    private readonly IFontProbeSource _source;

    public FontMetricFitter(IFontProbeSource source) => _source = source;

    /// <summary>Tier C 与 Tier B 共用的字重阈值：笔画宽 / x-height ≥ 0.22 视为粗体（核心像素口径）。</summary>
    public static int ClassifyWeight(double strokeRatio) => strokeRatio >= 0.22 ? 700 : 400;

    /// <summary>
    /// 从文字区域提取墨迹与核心笔画，再按既定字体族和字号分类字重。
    /// 主路径在 UIA、Win32 与 Fit 都缺字重时使用；墨迹不足时返回 null，交由调用方回退。
    /// </summary>
    public int? ClassifyWeightFromInk(
        RgbaFrame frame,
        RectInt region,
        Rgb background,
        int padding,
        string? text,
        string family,
        double fontSizeCss,
        double dpiScale)
    {
        if (string.IsNullOrWhiteSpace(text) || region.Width < 4 || region.Height < 4) return null;
        var ink = RegionStyleAnalyzer.InkBounds(frame, region, background, padding);
        if (ink is not RectInt box || box.Width < 4 || box.Height < 4) return null;
        var stroke = RegionStyleAnalyzer.StrokeWidth(frame, box, background);
        return stroke is double width
            ? ClassifyWeightFromInk(text, family, width, fontSizeCss, dpiScale)
            : null;
    }

    /// <summary>
    /// IA2 路径专用字重分类：字体族与字号已由 IA2 给出，只需从截图像素笔画宽度推断字重。
    /// 与 Fit 不同，这里不做字号/族反解——中文长文本的宽度拟合常因字间距失败，
    /// 但笔画宽度与 x-height 的比值不受影响，用 GDI 真实 x-height 归一化后选最接近的先验即可。
    /// 没有 Light 变体的字体族（非 Segoe UI）不返回 300，避免渲染层写 font-weight:300 却无对应字形。
    /// 度量失败或误差过大时返回 null，调用方回退，宁可不改也不乱判。
    /// </summary>
    public int? ClassifyWeightFromInk(string text, string family, double strokePhysical, double fontSizeCss, double dpiScale)
    {
        if (string.IsNullOrWhiteSpace(text) || strokePhysical <= 0 || fontSizeCss <= 0 || dpiScale <= 0) return null;
        var probe = _source.Measure(text, family, 400, false);
        if (probe.Width <= 0 || probe.XHeight <= 0) return null;
        var scale = fontSizeCss * dpiScale / 100.0;
        if (scale <= 0) return null;
        var ratio = strokePhysical / (probe.XHeight * scale);
        var hasLight = family.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase);
        var best = 400;
        var bestErr = Math.Abs(ratio - RegularStrokeRatio);
        if (hasLight)
        {
            var e = Math.Abs(ratio - LightStrokeRatio);
            if (e < bestErr) { best = 300; bestErr = e; }
        }
        var boldErr = Math.Abs(ratio - BoldStrokeRatio);
        if (boldErr < bestErr) { best = 700; bestErr = boldErr; }
        // 离最近先验仍太远说明笔画测量异常，回退交由调用方处理。
        return bestErr <= 0.08 ? best : null;
    }

    public FontFitResult? Fit(FontFitRequest request, IReadOnlyList<FontCandidate>? candidates = null)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.InkWidth <= 0 || request.InkHeight <= 0) return null;
        if (request.DpiScale <= 0) return null;
        // 同字号粗体只宽 5%–7%，仅凭宽度定字重会被墨迹测量误差带偏；
        // 没有笔画宽度时整条反解都不成立，交给像素估计而不是猜一个结果。
        if (request.StrokeWidth is not double stroke || stroke <= 0) return null;
        var traits = TextTraits.Of(request.Text);
        var list = candidates ?? FontCandidate.Default;
        var scored = new List<FontFitResult>(list.Count);
        foreach (var candidate in list)
        {
            var probe = _source.Measure(request.Text, candidate.Family, candidate.Weight, candidate.Italic);
            if (probe.Width <= 0) continue;
            var sizePhysical = request.InkWidth / (probe.Width / 100);
            if (sizePhysical is < MinSize or > MaxSize) continue;
            var scale = sizePhysical / 100;
            // 控件高度不足时 Label/Button 会把降部裁掉，而且裁切深度随控件高度任意变化：
            // 墨迹高度落在 [到基线, 含降部] 区间内都算吻合，只有超出区间才算误差。
            // 这样既不会把裁切文本判成更小字号，也不会因裁切深度不同而偏袒某个字体。
            var capOnly = PredictInkHeight(probe, traits, false) * scale;
            var withDescender = PredictInkHeight(probe, traits, true) * scale;
            var heightError = 0.0;
            if (request.InkHeight < capOnly) heightError = capOnly - request.InkHeight;
            else if (request.InkHeight > withDescender) heightError = request.InkHeight - withDescender;
            heightError /= request.InkHeight;
            var ratioError = 0.0;
            if (probe.XHeight > 0)
            {
                var ratio = stroke / (probe.XHeight * scale);
                var prior = candidate.Weight < 400 ? LightStrokeRatio
                    : candidate.Weight >= 600 ? BoldStrokeRatio
                    : RegularStrokeRatio;
                ratioError = Math.Abs(ratio - prior);
            }
            var facePenalty = IsUiFamily(candidate.Family) ? 0 : OtherFacePenalty;
            // YaHei UI 的拉丁字形与 Segoe UI 同源，纯 ASCII 文本从度量上无法分族；
            // Windows 默认 UI 字体是 Segoe UI，中文优先字体需要更强的度量证据才应胜出。
            if (!traits.HasCjk && IsCjkFamily(candidate.Family)) facePenalty += LatinOnCjkFacePenalty;
            var score = heightError + RatioWeight * ratioError + 0.3 * facePenalty;
            scored.Add(new FontFitResult(
                candidate.Family,
                candidate.Weight,
                sizePhysical,
                sizePhysical / request.DpiScale,
                0,
                score));
        }
        if (scored.Count == 0) return null;
        scored.Sort((a, b) => a.Score.CompareTo(b.Score));
        var best = scored[0];
        if (best.Score > AbsoluteMinScore) return null;
        if (scored.Count == 1)
        {
            // 没有第二名可比：用绝对质量折出置信度，并封顶，避免“唯一候选=满分”。
            var single = Math.Clamp(1 - best.Score, 0, SingleCandidateConfidenceCap);
            return single < MinConfidence ? null : best with { Confidence = single };
        }
        var second = scored[1];
        var confidence = Math.Clamp((second.Score - best.Score) / Math.Max(best.Score, ConfidenceFloor), 0, 1);
        if (confidence < MinConfidence) return null;
        return best with { Confidence = confidence };
    }

    /// <summary>
    /// 预测墨迹高度：文字最高处取实际出现的字形类别（大写/数字用帽高、小写上升部用 'k' 高），
    /// 可选加降部深度。CapHeight 比 Ascender 小 3%–5%，混用会让标题系统性偏小。
    /// </summary>
    private static double PredictInkHeight(FontProbe probe, TextTraits traits, bool includeDescender)
    {
        var top = 0.0;
        if (traits.HasAscender) top = Math.Max(top, probe.Ascender);
        if (traits.HasCap || traits.HasCjk) top = Math.Max(top, probe.CapHeight);
        if (top <= 0) top = probe.XHeight;
        return top + (includeDescender && traits.HasDescender ? probe.Descender : 0);
    }

    /// <summary>系统默认 UI 字体不额外惩罚，其余候选加固定惩罚，避免冷门字体靠巧合胜出。</summary>
    private static bool IsUiFamily(string family) =>
        family.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase) ||
        family.Equals("Segoe UI Light", StringComparison.OrdinalIgnoreCase) ||
        family.StartsWith("Microsoft YaHei", StringComparison.OrdinalIgnoreCase);

    private static bool IsCjkFamily(string family) =>
        family.StartsWith("Microsoft YaHei", StringComparison.OrdinalIgnoreCase);

    /// <summary>决定墨迹高度应该按帽高、上升部还是 x-height 预测的文本特征。</summary>
    private readonly record struct TextTraits(bool HasCap, bool HasAscender, bool HasDescender, bool HasCjk)
    {
        public static TextTraits Of(string text)
        {
            var cap = false;
            var ascender = false;
            var descender = false;
            var cjk = false;
            foreach (var c in text)
            {
                if (c >= 0x2E80) cjk = true;
                // 数字与大写顶到帽高；bdfhkl 的小写上升部比帽高高 3%–5%，'t' 只到 0.65em 不算。
                if (char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c)) cap = true;
                if ("bdfhkl".Contains(char.ToLowerInvariant(c))) ascender = true;
                if ("gjpqy".Contains(char.ToLowerInvariant(c))) descender = true;
            }
            return new TextTraits(cap, ascender, descender, cjk);
        }
    }
}
