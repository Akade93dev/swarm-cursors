using System.Text;
using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Core.Visual;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Fallback = new(240, 240, 240);

    public double Luminance => 0.2126 * R + 0.7152 * G + 0.0722 * B;

    public string Css => $"rgb({R},{G},{B})";

    /// <summary>L1 色彩距离，用于判断两色是否属于同一视觉表面。</summary>
    public int Distance(Rgb other) =>
        Math.Abs(R - other.R) + Math.Abs(G - other.G) + Math.Abs(B - other.B);
}

public readonly record struct BorderSide(int Width, Rgb Color)
{
    public static readonly BorderSide None = new(0, default);

    public bool Visible => Width > 0;
}

/// <summary>从一个控件矩形里推断出的视觉样式：底色 + 四边边框。</summary>
public sealed record RegionStyle(Rgb Background, BorderSide Top, BorderSide Right, BorderSide Bottom, BorderSide Left)
{
    public static RegionStyle Flat(Rgb background) => new(background, BorderSide.None, BorderSide.None, BorderSide.None, BorderSide.None);

    public bool HasBorder => Top.Visible || Right.Visible || Bottom.Visible || Left.Visible;

    /// <summary>转成可以直接贴在重建元素上的 CSS。宽度按显示器缩放折回 CSS 像素。</summary>
    public string ToCss(double dpiScale)
    {
        var scale = dpiScale > 0 ? dpiScale : 1;
        var css = new StringBuilder();
        css.Append("background-color:").Append(Background.Css).Append(';');
        if (!HasBorder) return css.ToString();
        if (Same(Top, Right) && Same(Top, Bottom) && Same(Top, Left))
        {
            css.Append("border:").Append(Width(Top.Width, scale)).Append("px solid ").Append(Top.Color.Css).Append(';');
            return css.ToString();
        }
        AppendSide(css, "top", Top, scale);
        AppendSide(css, "right", Right, scale);
        AppendSide(css, "bottom", Bottom, scale);
        AppendSide(css, "left", Left, scale);
        return css.ToString();
    }

    private static bool Same(BorderSide a, BorderSide b) =>
        a.Visible == b.Visible && a.Width == b.Width && a.Color.Distance(b.Color) <= 6;

    private static void AppendSide(StringBuilder css, string side, BorderSide border, double scale)
    {
        if (!border.Visible) return;
        css.Append("border-").Append(side).Append(':')
            .Append(Width(border.Width, scale)).Append("px solid ")
            .Append(border.Color.Css).Append(';');
    }

    private static string Width(int physical, double scale)
    {
        var value = Math.Max(0.5, Math.Round(physical / scale * 2, MidpointRounding.AwayFromZero) / 2);
        return value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// 位图样式分析：从已经抓到的窗口截图里推断控件真正的底色、边框、文字颜色与字号。
/// 与原始 swarm 扩展里“只重绘真正有绘制的元素”的判定一致——用像素证据代替固定的灰色方块。
/// 采样带步长上限，保证几百个控件时仍然是毫秒级开销。
/// </summary>
public static class RegionStyleAnalyzer
{
    private const int MaxSamples = 4096;
    private const int MaxLineSamples = 192;
    private const int EdgeSkip = 3;
    private const int MaxBorderDepth = 16;
    private const int BorderContrast = 24;
    private const int WeakBorderContrast = 8;
    private const int TextContrast = 90;

    public static Rgb DominantColor(RgbaFrame frame, RectInt bounds, Rgb fallback = default)
    {
        var rect = Intersect(frame, bounds);
        if (rect.Width <= 0 || rect.Height <= 0) return fallback == default ? Rgb.Fallback : fallback;
        var step = Step(rect.Width, rect.Height, MaxSamples);
        var buckets = new Dictionary<int, Bucket>();
        for (var y = rect.Y; y < rect.Bottom; y += step)
        for (var x = rect.X; x < rect.Right; x += step)
        {
            var index = (y * frame.Width + x) * 4;
            if (frame.Pixels[index + 3] < 8) continue;
            Add(buckets, frame.Pixels[index], frame.Pixels[index + 1], frame.Pixels[index + 2]);
        }
        return Best(buckets, fallback == default ? Rgb.Fallback : fallback);
    }

    /// <summary>
    /// 取矩形“内圈”的主色作为表面底色：四边各取一条内缩 2–4px 的薄带，跳过四角。
    /// 文字笔画、输入框的选中高亮都落在靠中间的位置，所以不会被误当成底色；
    /// 矩形太小时退回整块主色。
    /// </summary>
    public static Rgb SurfaceColor(RgbaFrame frame, RectInt bounds, Rgb fallback = default)
    {
        var rect = Intersect(frame, bounds);
        var baseColor = fallback == default ? Rgb.Fallback : fallback;
        if (rect.Width < 10 || rect.Height < 10) return DominantColor(frame, rect, baseColor);
        const int inset = 2;
        const int depth = 3;
        const int corner = 4;
        var buckets = new Dictionary<int, Bucket>();
        var step = Math.Max(1, (rect.Width + rect.Height) * depth / (double)MaxSamples);
        for (var d = 0; d < depth; d++)
        {
            var rowTop = rect.Y + inset + d;
            var rowBottom = rect.Bottom - 1 - inset - d;
            var colLeft = rect.X + inset + d;
            var colRight = rect.Right - 1 - inset - d;
            if (rowTop > rowBottom || colLeft > colRight) break;
            for (var x = rect.X + corner; x < rect.Right - corner; x += (int)step)
            {
                AddPixel(frame, rowTop, x, buckets);
                AddPixel(frame, rowBottom, x, buckets);
            }
            for (var y = rect.Y + corner; y < rect.Bottom - corner; y += (int)step)
            {
                AddPixel(frame, y, colLeft, buckets);
                AddPixel(frame, y, colRight, buckets);
            }
        }
        return Best(buckets, baseColor);
    }

    /// <summary>按底色明度挑一个可读的正文色；用于内容被反色绘制（如选中态）时兜底。</summary>
    public static Rgb PreferredTextColor(Rgb background) =>
        background.Luminance > 140 ? new Rgb(32, 32, 32) : new Rgb(244, 244, 244);

    /// <summary>推断控件矩形的主色与可见边框。边框宽度是物理像素。</summary>
    public static RegionStyle Analyze(RgbaFrame frame, RectInt bounds, Rgb fallback = default)
    {
        var rect = Intersect(frame, bounds);
        var baseColor = fallback == default ? Rgb.Fallback : fallback;
        if (rect.Width <= 0 || rect.Height <= 0) return RegionStyle.Flat(baseColor);
        // 中央采样避开厚边框；内缩有界，给边框扫描保留不越过中心区的深度。
        var insetX = Math.Clamp(rect.Width / 3, 2, MaxBorderDepth);
        var insetY = Math.Clamp(rect.Height / 3, 2, MaxBorderDepth);
        var inner = new RectInt(
            rect.X + insetX,
            rect.Y + insetY,
            rect.Width - insetX * 2,
            rect.Height - insetY * 2);
        var background = inner.Width >= 4 && inner.Height >= 4
            ? DominantColor(frame, inner, DominantColor(frame, rect, baseColor))
            : DominantColor(frame, rect, baseColor);
        if (rect.Width < 12 || rect.Height < 12) return RegionStyle.Flat(background);
        return new RegionStyle(
            background,
            Scan(frame, rect, Edge.Top, background, insetY),
            Scan(frame, rect, Edge.Right, background, insetX),
            Scan(frame, rect, Edge.Bottom, background, insetY),
            Scan(frame, rect, Edge.Left, background, insetX));
    }

    /// <summary>在矩形里取与底色反差最大的主色，即文字/图标颜色。</summary>
    public static Rgb TextColor(RgbaFrame frame, RectInt bounds, Rgb background, int padding = 0)
    {
        var rect = Inset(Intersect(frame, bounds), Math.Max(0, padding));
        var fallback = background.Luminance > 140 ? new Rgb(32, 32, 32) : new Rgb(244, 244, 244);
        if (rect.Width <= 0 || rect.Height <= 0) return fallback;
        var step = Step(rect.Width, rect.Height, MaxSamples);
        var candidates = new List<(double Contrast, Rgb Color)>();
        for (var y = rect.Y; y < rect.Bottom; y += step)
        for (var x = rect.X; x < rect.Right; x += step)
        {
            var index = (y * frame.Width + x) * 4;
            if (frame.Pixels[index + 3] < 8) continue;
            var color = new Rgb(frame.Pixels[index], frame.Pixels[index + 1], frame.Pixels[index + 2]);
            if (color.Distance(background) < TextContrast) continue;
            candidates.Add((Math.Abs(color.Luminance - background.Luminance), color));
        }
        if (candidates.Count == 0) return fallback;
        // 先按明度反差筛选，再取量化主色，避免多色高反差像素被平均成不存在的颜色。
        candidates.Sort((a, b) => b.Contrast.CompareTo(a.Contrast));
        var take = Math.Max(8, candidates.Count / 5);
        var buckets = new Dictionary<int, Bucket>();
        for (var i = 0; i < take && i < candidates.Count; i++)
        {
            var color = candidates[i].Color;
            Add(buckets, color.R, color.G, color.B);
        }
        return Best(buckets, fallback);
    }

    /// <summary>
    /// 根据文字像素的行分布估计字号（物理像素）。按行切分文本块，取单行墨迹高度，
    /// 再按是否有降部、是否 CJK 折算成 em，避免固定系数造成的系统性偏小。
    /// </summary>
    public static int? EstimateFontSize(RgbaFrame frame, RectInt bounds, Rgb background, int padding = 0, string? text = null)
    {
        var rect = Inset(Intersect(frame, bounds), Math.Max(0, padding));
        if (rect.Width < 4 || rect.Height < 4) return null;
        var threshold = Math.Clamp(rect.Width / 100, 2, 4);
        var counts = new int[rect.Height];
        for (var row = 0; row < rect.Height; row++)
        {
            var count = 0;
            for (var col = 0; col < rect.Width; col++)
            {
                var index = ((rect.Y + row) * frame.Width + rect.X + col) * 4;
                var color = new Rgb(frame.Pixels[index], frame.Pixels[index + 1], frame.Pixels[index + 2]);
                if (color.Distance(background) >= TextContrast) count++;
            }
            counts[row] = count;
        }
        var runs = new List<(int Start, int Length)>();
        var start = -1;
        for (var row = 0; row <= counts.Length; row++)
        {
            var inked = row < counts.Length && counts[row] >= threshold;
            if (inked)
            {
                if (start < 0) start = row;
            }
            else if (start >= 0)
            {
                // 1px 的分隔线或边框残影不是文字行，直接丢弃，避免中位数被拉到 1px。
                if (row - start >= 2) runs.Add((start, row - start));
                start = -1;
            }
        }
        if (runs.Count == 0) return null;
        // 用单行墨迹高度而不是整块盒子高度：多行控件、带内边距的 Label 都不会被算成巨大字号。
        runs.Sort((a, b) => a.Length.CompareTo(b.Length));
        var line = runs[runs.Count / 2];
        if (line.Length < 4) return null;
        return Math.Clamp((int)Math.Round(line.Length / InkToEmRatio(counts, line, text)), 8, 96);
    }

    /// <summary>可见墨迹高度占 em 的比例：CJK 约 0.88，含降部约 0.92，只有帽高约 0.72。</summary>
    private static double InkToEmRatio(int[] counts, (int Start, int Length) line, string? text)
    {
        if (IsCjk(text)) return 0.88;
        var peak = 0;
        for (var row = line.Start; row < line.Start + line.Length; row++)
            peak = Math.Max(peak, counts[row]);
        if (peak <= 0) return 0.92;
        // 降部在基线下方形成覆盖率明显更低的尾巴；抗锯齿尾行通常只有 1 行，因此要求至少 2 行。
        var tail = 0;
        for (var row = line.Start + line.Length - 1; row >= line.Start; row--)
        {
            if (counts[row] >= peak * 0.35) break;
            tail++;
        }
        return tail >= 2 && tail <= line.Length / 2 ? 0.92 : 0.72;
    }

    private static bool IsCjk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (c >= 0x2E80) return true;
        return false;
    }

    private enum Edge { Top, Right, Bottom, Left }

    private static BorderSide Scan(RgbaFrame frame, RectInt rect, Edge edge, Rgb background, int maxDepth)
    {
        var horizontal = edge is Edge.Top or Edge.Bottom;
        var length = horizontal ? rect.Width : rect.Height;
        var inset = Math.Clamp(length / 3, 2, MaxBorderDepth);
        var edgeSkip = Math.Min(inset, Math.Max(EdgeSkip, (length - 12) / 2));
        var samples = Math.Min(MaxLineSamples, length - edgeSkip * 2);
        if (samples < 12) return BorderSide.None;
        var step = (length - edgeSkip * 2) / (double)(samples - 1);
        long r = 0, g = 0, b = 0;
        var thickness = 0;
        var returnedToBackground = false;
        for (var depth = 0; depth < maxDepth; depth++)
        {
            var offset = edge is Edge.Bottom or Edge.Right ? (horizontal ? rect.Height - 1 - depth : rect.Width - 1 - depth) : depth;
            long sr = 0, sg = 0, sb = 0;
            var usable = 0;
            for (var i = 0; i < samples; i++)
            {
                var along = Math.Min(length - 1, (int)Math.Round(edgeSkip + i * step));
                var x = horizontal ? rect.X + along : rect.X + offset;
                var y = horizontal ? rect.Y + offset : rect.Y + along;
                var index = (y * frame.Width + x) * 4;
                if (frame.Pixels[index + 3] < 8) continue;
                sr += frame.Pixels[index];
                sg += frame.Pixels[index + 1];
                sb += frame.Pixels[index + 2];
                usable++;
            }
            if (usable < 8) break;
            var color = new Rgb((byte)(sr / usable), (byte)(sg / usable), (byte)(sb / usable));
            var contrast = color.Distance(background);
            if (contrast < WeakBorderContrast)
            {
                returnedToBackground = thickness > 0;
                break;
            }
            if (thickness > 0 && color.Distance(new Rgb((byte)(r / thickness), (byte)(g / thickness), (byte)(b / thickness))) > 48) break;
            var weak = contrast <= BorderContrast;
            var tolerance = weak ? Math.Max(1, contrast / 4) : 48;
            // 强边框沿用 3/4 一致性；弱边框收紧容差并要求 7/8，避免把背景点计入候选。
            var consistent = 0;
            for (var i = 0; i < samples; i++)
            {
                var along = Math.Min(length - 1, (int)Math.Round(edgeSkip + i * step));
                var x = horizontal ? rect.X + along : rect.X + offset;
                var y = horizontal ? rect.Y + offset : rect.Y + along;
                var index = (y * frame.Width + x) * 4;
                var pixel = new Rgb(frame.Pixels[index], frame.Pixels[index + 1], frame.Pixels[index + 2]);
                if (pixel.Distance(color) <= tolerance) consistent++;
            }
            if (weak ? consistent * 8 < samples * 7 : consistent * 4 < samples * 3) break;
            r += color.R;
            g += color.G;
            b += color.B;
            thickness++;
        }
        // 大色块和渐变在扫描上限前不会回到背景，不能据此猜一个边框宽度。
        if (thickness == 0 || !returnedToBackground) return BorderSide.None;
        return new BorderSide(thickness, new Rgb((byte)(r / thickness), (byte)(g / thickness), (byte)(b / thickness)));
    }

    private static void AddPixel(RgbaFrame frame, int y, int x, Dictionary<int, Bucket> buckets)
    {
        if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) return;
        var index = (y * frame.Width + x) * 4;
        if (frame.Pixels[index + 3] < 8) return;
        Add(buckets, frame.Pixels[index], frame.Pixels[index + 1], frame.Pixels[index + 2]);
    }

    private static void Add(Dictionary<int, Bucket> buckets, byte r, byte g, byte b)
    {
        var key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
        if (buckets.TryGetValue(key, out var bucket))
            buckets[key] = bucket.Add(r, g, b);
        else
            buckets[key] = new Bucket(1, r, g, b);
    }

    private static Rgb Best(Dictionary<int, Bucket> buckets, Rgb fallback)
    {
        if (buckets.Count == 0) return fallback == default ? Rgb.Fallback : fallback;
        var best = default(Bucket);
        foreach (var bucket in buckets.Values)
            if (bucket.Count > best.Count) best = bucket;
        return new Rgb((byte)(best.R / best.Count), (byte)(best.G / best.Count), (byte)(best.B / best.Count));
    }

    private readonly record struct Bucket(int Count, long R, long G, long B)
    {
        public Bucket Add(byte r, byte g, byte b) => new(Count + 1, R + r, G + g, B + b);
    }

    private static int Step(int width, int height, int maxSamples)
    {
        var total = (long)width * height;
        if (total <= maxSamples) return 1;
        return Math.Max(1, (int)Math.Ceiling(Math.Sqrt(total / (double)maxSamples)));
    }

    private static RectInt Intersect(RgbaFrame frame, RectInt bounds)
    {
        var left = Math.Max(0, bounds.X);
        var top = Math.Max(0, bounds.Y);
        var right = Math.Min(frame.Width, bounds.Right);
        var bottom = Math.Min(frame.Height, bounds.Bottom);
        return new RectInt(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static RectInt Inset(RectInt rect, int amount)
    {
        var width = rect.Width - amount * 2;
        var height = rect.Height - amount * 2;
        if (width <= 0 || height <= 0) return new RectInt(rect.X, rect.Y, 0, 0);
        return new RectInt(rect.X + amount, rect.Y + amount, width, height);
    }
}
