using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Swarm.Desktop;

/// <summary>UIA TextPattern 暴露的真实字体信息，读不到时为 null。</summary>
public sealed record UiaTextStyle(
    string? FontFamily,
    double? FontSizePoints,
    int? FontWeight,
    bool? Italic,
    string? ColorCss,
    string? Text,
    bool HasSelection = false);

/// <summary>
/// 逐个控件读取 TextPattern 的字体属性。所有调用都是跨进程的，因此只在文本类控件上、
/// 在固定时间预算内读取；读不到就交给像素估计兜底，不阻塞重建。
/// </summary>
public static class UiaTextStyleReader
{
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "Edit", "Document",
    };

    public static IReadOnlyDictionary<string, UiaTextStyle> Read(UiaSnapshot snapshot, int maxElements = 64, int budgetMilliseconds = 300)
    {
        var result = new Dictionary<string, UiaTextStyle>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();
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
                var style = ReadRange(range);
                if (style != null) result[element.Id] = style with { HasSelection = HasSelection(text) };
            }
            catch (Exception error) when (IsUnavailable(error))
            {
                // 分支失效或 provider 不支持文本属性：跳过，走像素兜底。
            }
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

    private static UiaTextStyle? ReadRange(TextPatternRange range)
    {
        var (fontName, size, weight, italic, color) = ReadAttributes(range);
        if (ReferenceEquals(fontName, TextPattern.MixedAttributeValue) ||
            ReferenceEquals(size, TextPattern.MixedAttributeValue) ||
            ReferenceEquals(weight, TextPattern.MixedAttributeValue) ||
            ReferenceEquals(italic, TextPattern.MixedAttributeValue) ||
            ReferenceEquals(color, TextPattern.MixedAttributeValue))
        {
            // 混排时缩到前 8 个字符再读一次，拿到的是该段文字的实际样式而不是 Mixed。
            var head = range.Clone();
            head.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            head.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 8);
            (fontName, size, weight, italic, color) = ReadAttributes(head);
        }

        var family = fontName as string;
        var points = ToDouble(size);
        var fontWeight = ToInt(weight);
        var isItalic = italic as bool?;
        var css = ToColorCss(color);
        var text = SafeText(range);
        if (family == null && points == null && fontWeight == null && css == null && text == null) return null;
        return new UiaTextStyle(family, points, fontWeight, isItalic, css, text);
    }

    private static (object?, object?, object?, object?, object?) ReadAttributes(TextPatternRange range) =>
        (
            Safe(range, TextPattern.FontNameAttribute),
            Safe(range, TextPattern.FontSizeAttribute),
            Safe(range, TextPattern.FontWeightAttribute),
            Safe(range, TextPattern.IsItalicAttribute),
            Safe(range, TextPattern.ForegroundColorAttribute));

    /// <summary>
    /// TextPattern 的正文可以补上 ValuePattern 缺失的 Edit/Document 内容（WinForms 常见），
    /// 限长读取，避免把整篇文档搬进重建视图。
    /// </summary>
    private static string? SafeText(TextPatternRange range)
    {
        try
        {
            var text = range.GetText(200);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            return null;
        }
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
}
