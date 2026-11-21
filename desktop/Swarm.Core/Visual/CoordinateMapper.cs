using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Core.Visual;

/// <summary>被重建窗口所属进程的 DPI 感知级别。</summary>
public enum DpiAwareness
{
    Unaware,
    SystemAware,
    PerMonitorAware,
}

/// <summary>
/// 坐标与字号的唯一换算基准：原点取 work area 左上角（覆盖层摆放位置），
/// CSS 基准取覆盖层 DPI / 96。捕获、布局、字号与探针区域都走这里，禁止在别处再乘除 DPI。
/// </summary>
public readonly record struct CoordinateMapper
{
    public CoordinateMapper(RectInt workArea, uint overlayDpi)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0) throw new ArgumentOutOfRangeException(nameof(workArea));
        if (overlayDpi == 0) throw new ArgumentOutOfRangeException(nameof(overlayDpi));
        WorkArea = workArea;
        OverlayDpi = overlayDpi;
    }

    public RectInt WorkArea { get; }

    public uint OverlayDpi { get; }

    /// <summary>物理像素 / CSS 像素；覆盖层渲染 DPI 决定，与目标窗口 DPI 无关。</summary>
    public double CssScale => OverlayDpi / 96.0;

    /// <summary>屏幕物理矩形 → 覆盖层 CSS 矩形。</summary>
    public LogicalRect ToCss(RectInt physical) => new(
        (physical.X - WorkArea.X) / CssScale,
        (physical.Y - WorkArea.Y) / CssScale,
        physical.Width / CssScale,
        physical.Height / CssScale);

    /// <summary>屏幕物理尺寸 → 覆盖层 CSS 尺寸（不含原点偏移）。</summary>
    public LogicalSize ToCssSize(int width, int height) => new(width / CssScale, height / CssScale);

    /// <summary>
    /// 墨迹高度（物理像素）→ 覆盖层 CSS 字号。
    /// unaware / system-aware 会被 DWM 位图拉伸，窗口自认为 96 DPI，按覆盖层基准折算；
    /// per-monitor 不做位图拉伸，按目标窗口 DPI 折算。
    /// </summary>
    public double ToCssEm(double physicalHeight, DpiAwareness awareness, uint windowDpi)
    {
        if (!double.IsFinite(physicalHeight)) throw new ArgumentOutOfRangeException(nameof(physicalHeight));
        if (windowDpi == 0) throw new ArgumentOutOfRangeException(nameof(windowDpi));
        return awareness switch
        {
            DpiAwareness.Unaware or DpiAwareness.SystemAware => physicalHeight / CssScale,
            DpiAwareness.PerMonitorAware => physicalHeight * 96.0 / windowDpi,
            _ => throw new ArgumentOutOfRangeException(nameof(awareness)),
        };
    }
}
