using Swarm.Core.Semantics;

namespace Swarm.Core.Capture;

public static class FrameCropper
{
    public static RgbaFrame? Crop(RgbaFrame frame, RectInt bounds)
    {
        var x0 = Math.Max(0, bounds.X);
        var y0 = Math.Max(0, bounds.Y);
        var x1 = Math.Min(frame.Width, bounds.Right);
        var y1 = Math.Min(frame.Height, bounds.Bottom);
        if (x1 <= x0 || y1 <= y0) return null;

        var width = x1 - x0;
        var height = y1 - y0;
        var pixels = new byte[checked(width * height * 4)];
        for (var row = 0; row < height; row++)
            Array.Copy(frame.Pixels, ((y0 + row) * frame.Width + x0) * 4, pixels, row * width * 4, width * 4);
        return new RgbaFrame(width, height, pixels);
    }
}
