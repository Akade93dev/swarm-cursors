using Swarm.Core.Capture;

namespace Swarm.Desktop;

/// <summary>抓屏后端共用的像素裁剪；行为与原 CaptureService.Crop 保持一致。</summary>
internal static class FrameOps
{
    public static RgbaFrame? Crop(RgbaFrame source, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var left = Math.Clamp(x, 0, source.Width - 1);
        var top = Math.Clamp(y, 0, source.Height - 1);
        var right = Math.Clamp(x + width, left + 1, source.Width);
        var bottom = Math.Clamp(y + height, top + 1, source.Height);
        var w = right - left;
        var h = bottom - top;
        if (w == source.Width && h == source.Height) return source;
        var pixels = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
        {
            Buffer.BlockCopy(source.Pixels, ((top + row) * source.Width + left) * 4, pixels, row * w * 4, w * 4);
        }
        return new RgbaFrame(w, h, pixels);
    }
}
