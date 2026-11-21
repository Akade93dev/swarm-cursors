namespace Swarm.Core.Capture;

/// <summary>
/// 基于像素统计的空白检测。UIA 的元素计数有缓存延迟，不能作为截图是否有效的判据；
/// 这里按网格采样颜色种类与亮度极差，只用于决定是否把窗口抓屏降级为屏幕裁剪。
/// </summary>
public static class FrameStats
{
    public static bool IsBlank(RgbaFrame frame, int grid = 16, int tolerance = 6)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return true;
        var cells = Math.Max(2, grid);
        var stepX = Math.Max(1, frame.Width / cells);
        var stepY = Math.Max(1, frame.Height / cells);
        var colors = new HashSet<int>();
        var min = 255;
        var max = 0;
        var samples = 0;

        for (var y = stepY / 2; y < frame.Height; y += stepY)
        {
            for (var x = stepX / 2; x < frame.Width; x += stepX)
            {
                var index = (y * frame.Width + x) * 4;
                int r = frame.Pixels[index];
                int g = frame.Pixels[index + 1];
                int b = frame.Pixels[index + 2];
                // 量化到 5 位，避免压缩噪声把纯色画面判成非空白。
                colors.Add(((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));
                var luminance = (r * 299 + g * 587 + b * 114) / 1000;
                if (luminance < min) min = luminance;
                if (luminance > max) max = luminance;
                samples++;
                if (colors.Count > 2) return false;
            }
        }

        return samples == 0 || colors.Count <= 2 || max - min <= tolerance;
    }
}
