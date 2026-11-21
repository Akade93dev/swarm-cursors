using Swarm.Core.Semantics;

namespace Swarm.Core.Capture;

public readonly record struct PixelDiffResult(bool Comparable, int ChangedPixels, int TotalPixels, double ChangedRatio)
{
    public static readonly PixelDiffResult Incomparable = new(false, 0, 0, 0);
}

/// <summary>
/// 区域内像素差异统计：按通道阈值忽略抗锯齿/压缩噪声，只统计真实变化。
/// 帧尺寸不一致或区域为空时返回 Comparable=false，调用方不得据此判成功。
/// </summary>
public static class PixelDiff
{
    public static PixelDiffResult Compare(RgbaFrame before, RgbaFrame after, RectInt? region = null, int channelTolerance = 24)
    {
        if (before.Width != after.Width || before.Height != after.Height) return PixelDiffResult.Incomparable;
        if (channelTolerance is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(channelTolerance));

        var target = region ?? new RectInt(0, 0, before.Width, before.Height);
        var left = Math.Max(0, target.X);
        var top = Math.Max(0, target.Y);
        var right = Math.Min(before.Width, target.X + target.Width);
        var bottom = Math.Min(before.Height, target.Y + target.Height);
        if (right <= left || bottom <= top) return PixelDiffResult.Incomparable;

        var changed = 0;
        for (var y = top; y < bottom; y++)
        {
            var row = y * before.Width;
            for (var x = left; x < right; x++)
            {
                var index = (row + x) * 4;
                if (Math.Abs(before.Pixels[index] - after.Pixels[index]) > channelTolerance ||
                    Math.Abs(before.Pixels[index + 1] - after.Pixels[index + 1]) > channelTolerance ||
                    Math.Abs(before.Pixels[index + 2] - after.Pixels[index + 2]) > channelTolerance)
                {
                    changed++;
                }
            }
        }

        var total = (right - left) * (bottom - top);
        return new PixelDiffResult(true, changed, total, changed / (double)total);
    }
}
