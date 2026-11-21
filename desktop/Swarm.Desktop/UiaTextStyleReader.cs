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
    IReadOnlyList<(string Text, SpanStyle Style)>? Spans = null);

/// <summary>
/// 读取 TextPattern 的字体属性。所有调用都是跨进程的，因此只在文本类控件上、
/// 在固定时间预算内读取；读不到就交给像素估计兜底，不阻塞重建。
/// 混排样式走“整段读属性 + Mixed 二分边界”，不再逐字符读 5 个属性。
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

    /// <summary>批量化验收目标：单元素跨进程调用不超过 40 次。</summary>
    public const int CallsPerElementTarget = 40;

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
                if (!native.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) continue;
                var text = (TextPattern)pattern;
                var range = FirstRange(text) ?? text.DocumentRange;
                var style = ReadRange(range, out var calls);
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
    /// 一次 GetText 取全文，一次整段读 5 个属性；只有 Mixed 的属性才用二分定位边界。
    /// <paramref name="calls"/> 是本次元素的跨进程调用次数，含 GetText 与整段属性读取。
    /// </summary>
    private static UiaTextStyle? ReadRange(TextPatternRange range, out int calls)
    {
        calls = 1 + AttributeCount;
        var raw = SafeRawText(range, MaxTextChars + 1);
        // 换行在重建里是空格；等长替换，索引才能与 range 对齐。
        var normalized = raw?.Replace('\r', ' ').Replace('\n', ' ');
        var text = normalized?.Trim();
        var values = ReadAttributes(range);
        // Mixed 必须换成 Core 的哨兵：UIA 的 MixedAttributeValue 只在整段读取时能用引用比较，
        // 分段逻辑不认识它，会把 COM 对象当成合法属性值。
        var rootValues = values
            .Select(value => ReferenceEquals(value, TextPattern.MixedAttributeValue) ? TextAttributeValues.Mixed : value)
            .ToArray();
        var mixed = rootValues.Any(value => ReferenceEquals(value, TextAttributeValues.Mixed));
        var style = ToSpanStyle(values);

        IReadOnlyList<(string Text, SpanStyle Style)>? spans = null;
        if (mixed && normalized != null)
        {
            // 只去掉尾部空白（通常是段落标记变成的空格），左侧保留以免索引错位。
            var spanText = normalized[..Math.Min(normalized.Length, MaxSpanChars)].TrimEnd();
            if (spanText.Length >= 2)
            {
                var read = ReadSpans(range, spanText, rootValues, style);
                calls += read.Calls;
                spans = read.Runs;
                style = read.Style;
            }
        }

        if (style.FontFamily == null && style.FontSizePoints == null && style.FontWeight == null
            && style.ColorCss == null && text == null)
        {
            return null;
        }
        return new UiaTextStyle(style.FontFamily, style.FontSizePoints, style.FontWeight, style.Italic, style.ColorCss, text)
        {
            Spans = spans,
        };
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
