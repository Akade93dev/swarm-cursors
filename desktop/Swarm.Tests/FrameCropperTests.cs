using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class FrameCropperTests
{
    [Fact]
    public void 控件裁剪会限制在截图边界内()
    {
        var pixels = new byte[4 * 3 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i / 4);
            pixels[i + 3] = 255;
        }
        var frame = new RgbaFrame(4, 3, pixels);

        var result = FrameCropper.Crop(frame, new RectInt(2, 1, 5, 4));

        Assert.NotNull(result);
        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(6, result.Pixels[0]);
        Assert.Equal(7, result.Pixels[4]);
        Assert.Equal(10, result.Pixels[8]);
    }

    [Fact]
    public void 裁剪框会返回真实可见区域用于原比例落位()
    {
        var frame = new RgbaFrame(100, 50, new byte[100 * 50 * 4]);

        var clamped = FrameCropper.Clamp(frame, new RectInt(-10, -5, 40, 20));

        Assert.Equal(new RectInt(0, 0, 30, 15), clamped);
    }

    [Fact]
    public void 完全位于截图外的控件不生成空图像()
    {
        var frame = new RgbaFrame(2, 2, new byte[16]);
        Assert.Null(FrameCropper.Crop(frame, new RectInt(5, 5, 2, 2)));
    }
}
