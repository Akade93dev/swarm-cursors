using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Swarm.Core.Diagnostics;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

/// <summary>UIA TextPattern 暴露的真实字体信息，读不到时为 null。</summary>
public sealed record UiaTextStyle(
    string? FontFamily,
    double? FontSizePoints,
    int? FontWeight,
    bool? Italic,
    string? ColorCss,
    string? Text,
    bool HasSelection = false,
    IReadOnlyList<(string Text, SpanStyle Style)>? Spans = null,
    IReadOnlyList<RectInt>? Lines = null);

/// <summary>
/// 读取 TextPattern 的字体属性。所有调用都是跨进程的，因此只在文本类控件上、
/// 在固定时间预算内读取；读不到就交给像素估计兜底，不阻塞重建。
/// 混排样式优先用 <see cref="TextUnit.Format"/> 逐段走查（一次端点移动切出一段），
/// provider 不支持 Format 边界时回退到“整段读属性 + Mixed 二分边界”。
/// </summary>
public static class UiaTextStyleReader
{
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "Edit", "Document",
    };

    /// <summary>诊断：最近一次扫描的跨进程调用总数与单元素峰值，用于验证批量化效果。</summary>
    public static int LastTotalCalls { get; private set; }

    public static int LastMaxElementCalls { get; private set; }

    public static int LastElementCount { get; private set; }

    /// <summary>
    /// 单元素跨进程调用目标。Format 走查的实测口径：2 次基础（全文 + 行矩形）
    /// + 1 次收成空区间 + 每格式段 7 次（移动端点、取段文本、5 个属性）
    /// + 段间收起点（段数 − 1）；5 段 = 42，留 2 次余量。
    /// </summary>
    public const int CallsPerElementTarget = 44;

    public static IReadOnlyDictionary<string, UiaTextStyle> Read(UiaSnapshot snapshot, int maxElements = 64, int budgetMilliseconds = 300)
    {
        var result = new Dictionary<string, UiaTextStyle>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        var total = 0;
        var peak = 0;
        foreach (var element in snapshot.Elements)
        {
            if (result.Count >= maxElements || stopwatch.ElapsedMilliseconds > budgetMilliseconds) break;
            if (!TextTypes.Contains(element.ControlType)) continue;
            if (!snapshot.NativeElements.TryGetValue(element.Id, out var native)) continue;
            try
            {
                // TextPattern 已随观察批取进缓存（IncludePatterns 路径）：先查缓存，命中时零跨进程调用。
                if (!native.TryGetCachedPattern(TextPattern.Pattern, out var pattern) &&
                    !native.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) continue;
                var text = (TextPattern)pattern;
                var visible = FirstRange(text);
                var range = visible ?? text.DocumentRange;
                // DocumentRange 覆盖整篇文档，行矩形可能有成千上万条；只有可见范围才读行矩形。
                var style = ReadRange(range, readLines: visible != null || !element.ControlType.Equals("Document", StringComparison.OrdinalIgnoreCase), out var calls);
                if (style == null) continue;
                result[element.Id] = style with { HasSelection = HasSelection(text) };
                total += calls;
                peak = Math.Max(peak, calls);
            }
            catch (Exception error) when (IsUnavailable(error))
            {
                // 分支失效或 provider 不支持文本属性：跳过，走像素兜底。
            }
        }
        LastTotalCalls = total;
        LastMaxElementCalls = peak;
        LastElementCount = result.Count;
        if (peak > CallsPerElementTarget)
        {
            FileLog.Write("uia", $"text calls per element={peak} exceeds target={CallsPerElementTarget} total={total} elements={result.Count}");
        }
        return result;
    }

    /// <summary>
    /// 控件里是否有非空选区。选中态会把文字画成反色（白字 + 强调色底），
    /// 此时像素采样只能得到“被反色后的样子”，必须换一条取色路径。
    /// </summary>
    private static bool HasSelection(TextPattern text)
    {
        try
        {
            foreach (var range in text.GetSelection())
            {
                if (!string.IsNullOrEmpty(range.GetText(1))) return true;
            }
        }
        catch (Exception error) when (IsUnavailable(error))
        {
        }
        return false;
    }

    private static TextPatternRange? FirstRange(TextPattern text)
    {
        try
        {
            var ranges = text.GetVisibleRanges();
            return ranges.Length > 0 ? ranges[0] : null;
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            return null;
        }
    }

    /// <summary>
    /// 一次 GetText 取全文，再用 <see cref="TextUnit.Format"/> 逐段走查属性；
    /// provider 不支持格式边界或走查结果与文本对不上时，回退到整段读 + 二分。
    /// <paramref name="calls"/> 是本次元素的跨进程调用次数。
    /// </summary>
    private static UiaTextStyle? ReadRange(TextPatternRange range, bool readLines, out int calls)
    {
        calls = 1;
        var raw = SafeRawText(range, MaxTextChars + 1);
        // 换行在重建里是空格；等长替换，索引才能与 range 对齐。
        var normalized = raw?.Replace('\r', ' ').Replace('\n', ' ');
        var text = normalized?.Trim();
        // 行矩形是一次额外的跨进程调用，但能把同行片段的 y 统一到行级证据上。
        var lines = readLines ? SafeLineRects(range) : null;
        if (readLines) calls++;

        // 只去掉尾部空白（通常是段落标记变成的空格），左侧保留以免索引错位。
        var spanText = normalized is { Length: >= 2 }
            ? normalized[..Math.Min(normalized.Length, MaxSpanChars)].TrimEnd()
            : null;

        SpanStyle? style = null;
        IReadOnlyList<(string Text, SpanStyle Style)>? spans = null;
        var walked = false;
        if (spanText is { Length: >= 2 })
        {
            // 走查失败也要计入调用数，否则回退路径的峰值会被低报。
            walked = TryReadFormatRuns(range, spanText, out var formatStyle, out var formatSpans, out var formatCalls);
            calls += formatCalls;
            if (walked)
            {
                style = formatStyle;
                spans = formatSpans;
            }
        }
        if (!walked)
        {
            var values = ReadAttributes(range);
            calls += AttributeCount;
            // Mixed 必须换成 Core 的哨兵：UIA 的 MixedAttributeValue 只在整段读取时能用引用比较，
            // 分段逻辑不认识它，会把 COM 对象当成合法属性值。
            var rootValues = values
                .Select(value => ReferenceEquals(value, TextPattern.MixedAttributeValue) ? TextAttributeValues.Mixed : value)
                .ToArray();
            var mixed = rootValues.Any(value => ReferenceEquals(value, TextAttributeValues.Mixed));
            var uniform = ToSpanStyle(values);
            style = uniform;
            if (mixed && spanText is { Length: >= 2 })
            {
                var read = ReadSpans(range, spanText, rootValues, uniform);
                calls += read.Calls;
                spans = read.Runs;
                style = read.Style;
            }
        }

        if (style is not { } final) return null;
        if (final.FontFamily == null && final.FontSizePoints == null && final.FontWeight == null
            && final.ColorCss == null && text == null)
        {
            return null;
        }
        return new UiaTextStyle(final.FontFamily, final.FontSizePoints, final.FontWeight, final.Italic, final.ColorCss, text)
        {
            Spans = spans,
            Lines = lines,
        };
    }

    /// <summary>
    /// 用 <see cref="TextUnit.Format"/> 走查格式段：游标收成 range 起点的空区间后，
    /// 每次把 End 扩一个格式段，读该段属性与文字，再把 Start 收到 End。
    /// 返回 false 表示 provider 不支持格式边界或走查结果与文本不一致，调用方回退二分。
    /// </summary>
    private static bool TryReadFormatRuns(
        TextPatternRange range,
        string text,
        out SpanStyle style,
        out IReadOnlyList<(string Text, SpanStyle Style)>? runs,
        out int calls)
    {
        style = default;
        runs = null;
        calls = 0;
        try
        {
            var cursor = range.Clone();
            var styles = new SpanStyle?[text.Length];
            var offset = 0;
            var segments = 0;
            SpanStyle? uniform = null;
            cursor.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            calls++;
            while (offset < text.Length)
            {
                if (cursor.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Format, 1) == 0) return false;
                calls++;
                var chunk = cursor.GetText(text.Length - offset + 1);
                calls++;
                if (string.IsNullOrEmpty(chunk)) return false;
                if (chunk.Length > text.Length - offset) chunk = chunk[..(text.Length - offset)];
                // 强校验：格式段的文字必须与已读到的全文逐字符对上，否则说明端点单位不一致。
                if (!MatchesAt(chunk, text, offset)) return false;
                var values = ReadAttributes(cursor);
                calls += AttributeCount;
                if (values.Any(value => ReferenceEquals(value, TextPattern.MixedAttributeValue))) return false;
                var segment = ToSpanStyle(values);
                uniform ??= segment;
                for (var i = offset; i < offset + chunk.Length; i++) styles[i] = segment;
                offset += chunk.Length;
                segments++;
                if (segments > MaxSpans) return false;
                if (offset < text.Length)
                {
                    cursor.MoveEndpointByRange(TextPatternRangeEndpoint.Start, cursor, TextPatternRangeEndpoint.End);
                    calls++;
                }
            }
            if (offset != text.Length || uniform is not { } elementStyle) return false;
            style = elementStyle;
            var merged = TextSpanMerger.Merge(text, styles, elementStyle);
            runs = merged.Count > 1 ? merged : null;
            return true;
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            return false;
        }
    }

    /// <summary>格式段文字与全文在 <paramref name="offset"/> 处逐码元比较（换行已归一为空格）。</summary>
    private static bool MatchesAt(string chunk, string text, int offset)
    {
        if (chunk.Length > text.Length - offset) return false;
        for (var i = 0; i < chunk.Length; i++)
        {
            var c = chunk[i];
            if (c is '\r' or '\n') c = ' ';
            if (c != text[offset + i]) return false;
        }
        return true;
    }

    /// <summary>
    /// 读取行矩形（物理屏幕像素，每行一个矩形）。provider 不支持或返回空数组时返回 null，
    /// 由调用方回退到元素框聚类。数量设上限，避免整篇文档一次返回过多矩形。
    /// </summary>
    private static IReadOnlyList<RectInt>? SafeLineRects(TextPatternRange range)
    {
        try
        {
            // .NET 的 UIA 包装器返回 System.Windows.Rect[]（每行一个矩形），不是 flat double[]。
            var values = range.GetBoundingRectangles();
            if (values == null || values.Length == 0) return null;
            var count = Math.Min(values.Length, MaxLineRects);
            var lines = new List<RectInt>(count);
            for (var i = 0; i < count; i++)
            {
                var rect = values[i];
                if (!double.IsFinite(rect.X) || !double.IsFinite(rect.Y) || !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height)) continue;
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                lines.Add(new RectInt(
                    (int)Math.Round(rect.X),
                    (int)Math.Round(rect.Y),
                    (int)Math.Round(rect.Width),
                    (int)Math.Round(rect.Height)));
            }
            return lines.Count > 0 ? lines : null;
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            return null;
        }
    }

    /// <summary>
    /// 二分边界分段。分段结果必须按码元连续覆盖整段文字（强校验），否则整段放弃，
    /// 保留元素级样式，绝不出现“样式与文字错位”的继承。
    /// </summary>
    private static (
        IReadOnlyList<(string Text, SpanStyle Style)>? Runs,
        SpanStyle Style,
        int Calls) ReadSpans(
        TextPatternRange range,
        string text,
        IReadOnlyList<object?> rootValues,
        SpanStyle uniform)
    {
        UiaAttributeProbe? probe = null;
        try
        {
            probe = new UiaAttributeProbe(range, text.Length);
            var partition = TextAttributePartitioner.Partition(text, probe, rootValues, MaxSpans, MaxProbeCalls);
            var styles = new SpanStyle?[text.Length];
            var covered = 0;
            var elementStyle = uniform;
            var first = true;
            foreach (var segment in partition.Segments)
            {
                if (segment.Start != covered) return (null, uniform, probe.Calls);
                covered += segment.Length;
                if (covered > text.Length) return (null, uniform, probe.Calls);
                var segmentStyle = ToSpanStyle(segment.Values);
                // 元素级样式取首段：混合属性的整段读取是 Mixed，只有分段后才有可用值。
                if (first)
                {
                    elementStyle = Fill(segmentStyle, uniform);
                    first = false;
                }
                for (var i = segment.Start; i < segment.Start + segment.Length && i < styles.Length; i++)
                {
                    styles[i] = segmentStyle;
                }
            }
            if (covered != text.Length) return (null, uniform, probe.Calls);
            var runs = TextSpanMerger.Merge(text, styles, elementStyle);
            return (runs.Count > 1 ? runs : null, elementStyle, probe.Calls);
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            return (null, uniform, probe?.Calls ?? 0);
        }
    }

    private static IReadOnlyList<object?> ReadAttributes(TextPatternRange range) => new[]
    {
        Safe(range, TextPattern.FontNameAttribute),
        Safe(range, TextPattern.FontSizeAttribute),
        Safe(range, TextPattern.FontWeightAttribute),
        Safe(range, TextPattern.IsItalicAttribute),
        Safe(range, TextPattern.ForegroundColorAttribute),
    };

    private static SpanStyle ToSpanStyle(IReadOnlyList<object?> values) => new(
        values[(int)TextAttribute.FontFamily] as string,
        ToDouble(values[(int)TextAttribute.FontSize]),
        ToInt(values[(int)TextAttribute.FontWeight]),
        values[(int)TextAttribute.Italic] as bool?,
        ToColorCss(values[(int)TextAttribute.Color]));

    /// <summary>首段里读不到的属性用整段值补齐，避免元素级样式被一个空段清空。</summary>
    private static SpanStyle Fill(SpanStyle value, SpanStyle fallback) => new(
        value.FontFamily ?? fallback.FontFamily,
        value.FontSizePoints ?? fallback.FontSizePoints,
        value.FontWeight ?? fallback.FontWeight,
        value.Italic ?? fallback.Italic,
        value.ColorCss ?? fallback.ColorCss);

    /// <summary>逐字符读取只对短文本有意义：太长时重建会被切成碎片，且更慢。</summary>
    private const int MaxSpanChars = 80;

    private const int MaxTextChars = 200;

    private const int MaxSpans = 12;

    /// <summary>探针读取次数的软上限；超出后整段收口，属性缺失交给元素级回退。</summary>
    private const int MaxProbeCalls = 48;

    /// <summary>行矩形数量上限：多行文档只保留前面的行，避免一次返回过多矩形。</summary>
    private const int MaxLineRects = 256;

    private const int AttributeCount = 5;

    /// <summary>读原始文本（不 Trim），保证码元索引与 range 对齐。</summary>
    private static string? SafeRawText(TextPatternRange range, int maxLength)
    {
        try { return range.GetText(maxLength); }
        catch (Exception error) when (IsUnavailable(error)) { return null; }
    }

    private static object? Safe(TextPatternRange range, AutomationTextAttribute attribute)
    {
        try { return range.GetAttributeValue(attribute); }
        catch (Exception error) when (IsUnavailable(error)) { return null; }
    }

    private static double? ToDouble(object? value) =>
        value is double number && number > 0 && !double.IsNaN(number) ? number : null;

    private static int? ToInt(object? value)
    {
        var number = value switch
        {
            int i => i,
            short s => s,
            double d => (int)d,
            _ => 0,
        };
        return number is >= 100 and <= 900 ? number : null;
    }

    /// <summary>COLORREF 是 0x00BBGGRR，必须按 BGR 拆，不能直接当 RGB 用。</summary>
    private static string? ToColorCss(object? value)
    {
        if (value is not int color) return null;
        var r = color & 0xFF;
        var g = (color >> 8) & 0xFF;
        var b = (color >> 16) & 0xFF;
        return $"rgb({r},{g},{b})";
    }

    private static bool IsUnavailable(Exception error) =>
        error is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException;

    /// <summary>
    /// 把 [start, length) 映射到 UIA 子区间。端点移动同样是跨进程调用，所以用一个游标按差值移动：
    /// 二分时右端固定，每次探测只需移动起点一次。
    /// </summary>
    private sealed class UiaAttributeProbe : ITextAttributeProbe
    {
        private readonly TextPatternRange _cursor;
        private int _start;
        private int _end;

        public UiaAttributeProbe(TextPatternRange range, int length)
        {
            _cursor = range.Clone();
            // 游标先收成 range 起点的空区间，再一次性撑到正文长度。
            _cursor.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            Calls = 2;
            MoveTo(0, length);
        }

        public int Calls { get; private set; }

        public object? Read(int start, int length, TextAttribute attribute)
        {
            if (length <= 0) return null;
            MoveTo(start, length);
            Calls++;
            var value = _cursor.GetAttributeValue(AttributeOf(attribute));
            return ReferenceEquals(value, TextPattern.MixedAttributeValue) ? TextAttributeValues.Mixed : value;
        }

        private void MoveTo(int start, int length)
        {
            var end = start + length;
            if (end != _end)
            {
                _cursor.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, end - _end);
                Calls++;
            }
            if (start != _start)
            {
                _cursor.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, start - _start);
                Calls++;
            }
            _start = start;
            _end = end;
        }

        private static AutomationTextAttribute AttributeOf(TextAttribute attribute) => attribute switch
        {
            TextAttribute.FontFamily => TextPattern.FontNameAttribute,
            TextAttribute.FontSize => TextPattern.FontSizeAttribute,
            TextAttribute.FontWeight => TextPattern.FontWeightAttribute,
            TextAttribute.Italic => TextPattern.IsItalicAttribute,
            _ => TextPattern.ForegroundColorAttribute,
        };
    }
}
