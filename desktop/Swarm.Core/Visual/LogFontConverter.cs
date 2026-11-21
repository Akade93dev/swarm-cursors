namespace Swarm.Core.Visual;

/// <summary>从 LOGFONT 读到的字体度量；Height 为负表示 em 高度，为正表示字符单元高度。</summary>
public readonly record struct LogFontMetrics(
    int Height,
    int CellHeight,
    int InternalLeading,
    int Weight,
    bool Italic,
    string? FaceName);

/// <summary>换算成 CSS 像素后可用的字体样式，字段为 null 表示该来源没有提供。</summary>
public readonly record struct ResolvedFont(string? Family, double? SizeCss, int? Weight, bool? Italic);

/// <summary>把 LOGFONT 度量换算成 CSS 像素。纯函数，便于单测。</summary>
public static class LogFontConverter
{
    private const int MinWeight = 100;
    private const int MaxWeight = 900;

    public static ResolvedFont? Convert(LogFontMetrics font, double dpiScale)
    {
        if (dpiScale <= 0) return null;
        var em = font.Height < 0
            ? -font.Height
            : font.Height > 0 && font.CellHeight > 0
                ? font.CellHeight - font.InternalLeading
                : 0;
        // 0 表示由字体映射器决定，拿不到真实字号；超出合理范围的值多半是误读。
        if (em < 4 || em > 400) return null;
        var family = string.IsNullOrWhiteSpace(font.FaceName) ? null : font.FaceName.Trim();
        var weight = font.Weight is >= MinWeight and <= MaxWeight ? font.Weight : (int?)null;
        return new ResolvedFont(family, em / dpiScale, weight, font.Italic);
    }
}
