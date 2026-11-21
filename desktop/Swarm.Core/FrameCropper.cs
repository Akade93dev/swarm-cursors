using Swarm.Core.Semantics;

namespace Swarm.Core.Capture;

public static class FrameCropper
{
    /// <summary>把裁剪框收进画面边界；完全落在画面外时返回 null。</summary>
    public static RectInt? Clamp(RgbaFrame frame, RectInt bounds)
    {
        var x0 = Math.Max(0, bounds.X);
        var y0 = Math.Max(0, bounds.Y);
        var x1 = Math.Min(frame.Width, bounds.Right);
        var y1 = Math.Min(frame.Height, bounds.Bottom);
        if (x1 <= x0 || y1 <= y0) return null;
        return new RectInt(x0, y0, x1 - x0, y1 - y0);
    }

    public static RgbaFrame? Crop(RgbaFrame frame, RectInt bounds)
    {
        if (Clamp(frame, bounds) is not { } clamped) return null;

        var width = clamped.Width;
        var height = clamped.Height;
        var pixels = new byte[checked(width * height * 4)];
        for (var row = 0; row < height; row++)
            Array.Copy(frame.Pixels, ((clamped.Y + row) * frame.Width + clamped.X) * 4, pixels, row * width * 4, width * 4);
        return new RgbaFrame(width, height, pixels);
    }
}
