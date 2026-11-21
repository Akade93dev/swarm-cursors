namespace Swarm.Core.Capture;

public sealed class RgbaFrame
{
    public RgbaFrame(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length != checked(width * height * 4)) throw new ArgumentException("RGBA pixel length does not match dimensions", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
}

public sealed record ScreenshotTile(int X, int Y, int Width, int Height, byte[] Pixels);
public sealed record ScreenshotModel(int Width, int Height, string Background, IReadOnlyList<ScreenshotTile> Tiles);
public readonly record struct LogicalRect(double X, double Y, double Width, double Height);
public readonly record struct LogicalPoint(int X, int Y);
public readonly record struct LogicalSize(double Width, double Height);

public static class CoordinateConverter
{
    public static LogicalRect ToLogical(Swarm.Core.Semantics.RectInt physical, Swarm.Core.Semantics.RectInt monitor, double dpiScale)
    {
        CheckScale(dpiScale);
        return new LogicalRect((physical.X - monitor.X) / dpiScale, (physical.Y - monitor.Y) / dpiScale, physical.Width / dpiScale, physical.Height / dpiScale);
    }

    public static LogicalPoint ToPhysical(double x, double y, Swarm.Core.Semantics.RectInt monitor, double dpiScale)
    {
        CheckScale(dpiScale);
        return new LogicalPoint((int)Math.Round(monitor.X + x * dpiScale), (int)Math.Round(monitor.Y + y * dpiScale));
    }

    /// <summary>客户区物理像素 → window-client CSS 像素（原点为目标客户区左上）。</summary>
    public static LogicalRect ToClient(Swarm.Core.Semantics.RectInt physical, Swarm.Core.Semantics.RectInt clientScreen, double dpiScale)
    {
        CheckScale(dpiScale);
        return new LogicalRect(
            (physical.X - clientScreen.X) / dpiScale,
            (physical.Y - clientScreen.Y) / dpiScale,
            physical.Width / dpiScale,
            physical.Height / dpiScale);
    }

    /// <summary>window-client CSS 像素 → 屏幕物理像素（四舍五入到整像素）。</summary>
    public static LogicalPoint ToScreen(double clientX, double clientY, Swarm.Core.Semantics.RectInt clientScreen, double dpiScale)
    {
        CheckScale(dpiScale);
        return new LogicalPoint(
            (int)Math.Round(clientScreen.X + clientX * dpiScale),
            (int)Math.Round(clientScreen.Y + clientY * dpiScale));
    }

    /// <summary>客户区在所属显示器坐标系内的 CSS 矩形（覆盖层使用）。</summary>
    public static LogicalRect ToMonitorCss(Swarm.Core.Semantics.RectInt clientScreen, Swarm.Core.Semantics.RectInt monitor, double dpiScale)
    {
        CheckScale(dpiScale);
        return new LogicalRect(
            (clientScreen.X - monitor.X) / dpiScale,
            (clientScreen.Y - monitor.Y) / dpiScale,
            clientScreen.Width / dpiScale,
            clientScreen.Height / dpiScale);
    }

    /// <summary>客户区 CSS 视口尺寸。</summary>
    public static LogicalSize ClientViewport(Swarm.Core.Semantics.RectInt clientBounds, double dpiScale)
    {
        CheckScale(dpiScale);
        return new LogicalSize(clientBounds.Width / dpiScale, clientBounds.Height / dpiScale);
    }

    /// <summary>元素矩形是否完整落在客户区视口内；允许 1px 的物理/逻辑取整误差。</summary>
    public static bool FitsViewport(LogicalRect rect, LogicalSize viewport, double tolerance = 1) =>
        rect.Width > 0 && rect.Height > 0 &&
        rect.X >= 0 && rect.Y >= 0 &&
        rect.X + rect.Width <= viewport.Width + tolerance &&
        rect.Y + rect.Height <= viewport.Height + tolerance;

    /// <summary>window-client CSS 坐标是否落在视口内（含右下边界）。</summary>
    public static bool ContainsClientPoint(double x, double y, LogicalSize viewport) =>
        double.IsFinite(x) && double.IsFinite(y) &&
        x >= 0 && y >= 0 && x <= viewport.Width && y <= viewport.Height;

    private static void CheckScale(double dpiScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0) throw new ArgumentOutOfRangeException(nameof(dpiScale));
    }
}
