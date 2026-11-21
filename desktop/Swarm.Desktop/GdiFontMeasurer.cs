using System.Runtime.InteropServices;
using Swarm.Core.Visual;

namespace Swarm.Desktop;

/// <summary>
/// Tier B 的 GDI 度量实现：把候选字体建成 100px em 的 HFONT，用 GetTextExtentPoint32
/// 测文本宽度，用 GetTextMetrics / GetGlyphOutline 测帽高、x-height 与降部。
/// 与 Win32FontReader 一样只做系统调用，不做任何布局推断。
/// </summary>
internal sealed class GdiFontMeasurer : IFontProbeSource
{
    private const int ReferenceSize = 100;
    private const int MaxCachedTexts = 512;
    private const uint GgoMetrics = 0;

    private readonly Dictionary<(string Family, int Weight, bool Italic), nint> _fonts = new();
    private readonly Dictionary<(string Text, string Family, int Weight, bool Italic), FontProbe> _cache = new();
    private nint _dc;

    public FontProbe Measure(string text, string family, int weight, bool italic)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(family)) return default;
        lock (_cache)
        {
            var key = (text, family, weight, italic);
            if (_cache.TryGetValue(key, out var cached)) return cached;
            var probe = MeasureCore(text, family, weight, italic);
            if (_cache.Count >= MaxCachedTexts) _cache.Clear();
            _cache[key] = probe;
            return probe;
        }
    }

    private FontProbe MeasureCore(string text, string family, int weight, bool italic)
    {
        var dc = DeviceContext();
        if (dc == 0) return default;
        var font = Font(family, weight, italic);
        if (font == 0) return default;
        var previous = SelectObject(dc, font);
        try
        {
            if (!GetTextExtentPoint32(dc, text, text.Length, out var size)) return default;
            return new FontProbe(
                InkWidth(dc, text, size.Width),
                GlyphHeight(dc, 'H'),
                GlyphHeight(dc, 'x'),
                GlyphHeight(dc, 'k'),
                GlyphDescender(dc));
        }
        finally
        {
            if (previous != 0) SelectObject(dc, previous);
        }
    }

    /// <summary>
    /// GetTextExtentPoint32 返回的是含左右边距（A/C）的前进宽度，而截图上量到的是墨迹宽度。
    /// 小字号时边距占比可达 5%–10%，这里减掉首末字符的边距让反解字号不再系统性偏小。
    /// </summary>
    private static double InkWidth(nint dc, string text, int advance)
    {
        if (text.Length == 0) return advance;
        var first = new Abc();
        var last = new Abc();
        if (!GetCharAbcWidths(dc, text[0], text[0], ref first)) return advance;
        if (!GetCharAbcWidths(dc, text[^1], text[^1], ref last)) return advance;
        return Math.Max(1, advance - Math.Max(0, first.A) - Math.Max(0, last.C));
    }

    /// <summary>
    /// 缺字体时 GDI 会静默替换成别的字体，度量结果会冒充候选字体；
    /// 因此建好 HFONT 后回读面名，不一致就放弃该候选。
    /// </summary>
    private nint Font(string family, int weight, bool italic)
    {
        var key = (family, weight, italic);
        if (_fonts.TryGetValue(key, out var cached)) return cached;
        var font = CreateFont(
            -ReferenceSize,
            0,
            0,
            0,
            weight,
            italic ? 1u : 0u,
            0,
            0,
            1,
            4,
            0,
            5,
            0,
            family);
        if (font != 0)
        {
            var logFont = new LogFont { FaceName = string.Empty };
            if (GetObject(font, Marshal.SizeOf<LogFont>(), ref logFont) == 0 ||
                !string.Equals(logFont.FaceName, family, StringComparison.OrdinalIgnoreCase))
            {
                DeleteObject(font);
                font = 0;
            }
        }
        _fonts[key] = font;
        return font;
    }

    private nint DeviceContext()
    {
        if (_dc != 0) return _dc;
        var screen = GetDC(0);
        if (screen == 0) return 0;
        _dc = CreateCompatibleDC(screen);
        ReleaseDC(0, screen);
        return _dc;
    }

    private static double GlyphHeight(nint dc, char glyph)
    {
        var matrix = new Mat2 { M11 = 1 << 16, M22 = 1 << 16 };
        var metrics = default(GlyphMetrics);
        var size = GetGlyphOutline(dc, glyph, GgoMetrics, ref metrics, 0, 0, ref matrix);
        return size == uint.MaxValue ? 0 : metrics.BlackBoxY;
    }

    /// <summary>
    /// 用 'p' 的实际字形底边测降部深度。tmDescent 是字体的最大降部（含重音预留），
    /// 会比真实笔画深 0.05em 左右，足以让小字号的高度打分歧义。
    /// GLYPHMETRICS 的原点是字形墨盒相对基线的位置，Y 向上为正，
    /// 所以底边在基线下的深度是 BlackBoxY - Origin.Y（'H' 恰为 0，'p' 约 0.23em）。
    /// </summary>
    private static double GlyphDescender(nint dc)
    {
        var matrix = new Mat2 { M11 = 1 << 16, M22 = 1 << 16 };
        var metrics = default(GlyphMetrics);
        var size = GetGlyphOutline(dc, 'p', GgoMetrics, ref metrics, 0, 0, ref matrix);
        return size == uint.MaxValue ? 0 : Math.Max(0, metrics.BlackBoxY - metrics.Origin.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mat2
    {
        public int M11;
        public int M12;
        public int M21;
        public int M22;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Abc
    {
        public int A;
        public uint B;
        public int C;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphMetrics
    {
        public uint BlackBoxX;
        public uint BlackBoxY;
        public Point Origin;
        public short CellIncX;
        public short CellIncY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct LogFont
    {
        public int Height;
        public int Width;
        public int Escapement;
        public int Orientation;
        public int Weight;
        public byte Italic;
        public byte Underline;
        public byte StrikeOut;
        public byte CharSet;
        public byte OutPrecision;
        public byte ClipPrecision;
        public byte Quality;
        public byte PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")]
    private static extern nint CreateFont(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outputPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetObjectW")]
    private static extern int GetObject(nint handle, int size, ref LogFont logFont);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTextExtentPoint32(nint dc, string text, int length, out Size size);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetCharABCWidthsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCharAbcWidths(nint dc, uint first, uint last, ref Abc widths);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetGlyphOutlineW")]
    private static extern uint GetGlyphOutline(
        nint dc,
        uint glyph,
        uint format,
        ref GlyphMetrics metrics,
        uint bufferSize,
        nint buffer,
        ref Mat2 matrix);
}
