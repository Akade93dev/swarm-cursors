using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Core.Governance;

/// <summary>模型通道像素脱敏：把敏感区域涂成高对比实心块。始终返回副本，避免污染抓帧缓存。</summary>
public static class RedactionRenderer
{
    public static RgbaFrame Mask(
        RgbaFrame frame,
        RectInt frameOrigin,
        IEnumerable<RectInt> physicalBounds,
        byte red = 18,
        byte green = 18,
        byte blue = 18,
        byte alpha = 255)
    {
        var pixels = (byte[])frame.Pixels.Clone();
        foreach (var bounds in physicalBounds)
        {
            var left = Math.Max(0, bounds.X - frameOrigin.X);
            var top = Math.Max(0, bounds.Y - frameOrigin.Y);
            var right = Math.Min(frame.Width, bounds.Right - frameOrigin.X);
            var bottom = Math.Min(frame.Height, bounds.Bottom - frameOrigin.Y);
            for (var y = top; y < bottom; y++)
            {
                var row = y * frame.Width * 4;
                for (var x = left; x < right; x++)
                {
                    var offset = row + x * 4;
                    pixels[offset] = red;
                    pixels[offset + 1] = green;
                    pixels[offset + 2] = blue;
                    pixels[offset + 3] = alpha;
                }
            }
        }
        return new RgbaFrame(frame.Width, frame.Height, pixels);
    }
}
