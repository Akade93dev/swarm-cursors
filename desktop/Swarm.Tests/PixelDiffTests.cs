using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class PixelDiffTests
{
    private static RgbaFrame Frame(int width, int height, byte value = 0)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }
        return new RgbaFrame(width, height, pixels);
    }

    private static void Set(RgbaFrame frame, int x, int y, byte r, byte g, byte b)
    {
        var index = (y * frame.Width + x) * 4;
        frame.Pixels[index] = r;
        frame.Pixels[index + 1] = g;
        frame.Pixels[index + 2] = b;
    }

    [Fact]
    public void 相同画面变化率为零()
    {
        var result = PixelDiff.Compare(Frame(10, 10), Frame(10, 10));
        Assert.True(result.Comparable);
        Assert.Equal(0, result.ChangedPixels);
        Assert.Equal(100, result.TotalPixels);
        Assert.Equal(0, result.ChangedRatio);
    }

    [Fact]
    public void 单像素变化按全帧面积计数()
    {
        var before = Frame(10, 10);
        var after = Frame(10, 10);
        Set(after, 3, 4, 255, 255, 255);
        var result = PixelDiff.Compare(before, after);
        Assert.True(result.Comparable);
        Assert.Equal(1, result.ChangedPixels);
        Assert.Equal(0.01, result.ChangedRatio, 4);
    }

    [Fact]
    public void 低于通道阈值的噪声被忽略()
    {
        var before = Frame(10, 10, 100);
        var after = Frame(10, 10, 110);
        Assert.Equal(0, PixelDiff.Compare(before, after, channelTolerance: 24).ChangedPixels);
    }

    [Fact]
    public void 超过通道阈值的变化被计入()
    {
        var before = Frame(10, 10, 100);
        var after = Frame(10, 10, 125);
        Assert.Equal(100, PixelDiff.Compare(before, after, channelTolerance: 24).ChangedPixels);
    }

    [Fact]
    public void 只统计指定区域内的变化()
    {
        var before = Frame(20, 20);
        var after = Frame(20, 20);
        Set(after, 1, 1, 255, 0, 0);
        Set(after, 15, 15, 255, 0, 0);
        var result = PixelDiff.Compare(before, after, new RectInt(0, 0, 5, 5));
        Assert.True(result.Comparable);
        Assert.Equal(1, result.ChangedPixels);
        Assert.Equal(25, result.TotalPixels);
    }

    [Fact]
    public void 负坐标区域会被裁剪到帧内()
    {
        var before = Frame(10, 10);
        var after = Frame(10, 10);
        Set(after, 0, 0, 255, 255, 255);
        var result = PixelDiff.Compare(before, after, new RectInt(-2, -2, 5, 5));
        Assert.True(result.Comparable);
        Assert.Equal(9, result.TotalPixels);
        Assert.Equal(1, result.ChangedPixels);
    }

    [Fact]
    public void 完全越界或空区域不可比较()
    {
        var frame = Frame(10, 10);
        Assert.False(PixelDiff.Compare(frame, frame, new RectInt(20, 20, 5, 5)).Comparable);
        Assert.False(PixelDiff.Compare(frame, frame, new RectInt(0, 0, 0, 5)).Comparable);
    }

    [Fact]
    public void 帧尺寸不一致不可比较()
    {
        Assert.False(PixelDiff.Compare(Frame(10, 10), Frame(11, 10)).Comparable);
    }

    [Fact]
    public void 非法通道阈值被拒绝()
    {
        var frame = Frame(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelDiff.Compare(frame, frame, null, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelDiff.Compare(frame, frame, null, 256));
    }
}
