using Swarm.Core.Capture;

namespace Swarm.Tests;

public class ScreenshotTilerTests
{
    private static RgbaFrame Solid(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }
        return new RgbaFrame(width, height, pixels);
    }

    [Fact]
    public void 纯色截图只生成背景不生成冗余图块()
    {
        var result = ScreenshotTiler.Tile(Solid(4, 4, 255, 255, 255), 2, 20);
        Assert.Empty(result.Tiles);
        Assert.Equal("rgb(255,255,255)", result.Background);
    }

    [Fact]
    public void 不同颜色区域保留对应坐标和像素()
    {
        var frame = Solid(4, 2, 255, 255, 255);
        for (var y = 0; y < 2; y++)
        for (var x = 2; x < 4; x++)
        {
            var i = (y * 4 + x) * 4;
            frame.Pixels[i] = frame.Pixels[i + 1] = frame.Pixels[i + 2] = 0;
        }

        var result = ScreenshotTiler.Tile(frame, 2, 20);
        Assert.Equal(2, result.Tiles.Count);
        Assert.Contains(result.Tiles, tile => tile.X == 0 && tile.Width == 2);
        Assert.Contains(result.Tiles, tile => tile.X == 2 && tile.Width == 2);
        Assert.All(result.Tiles, tile => Assert.Equal(16, tile.Pixels.Length));
    }

    [Fact]
    public void 图块数量超限时自动增大切片()
    {
        var frame = Solid(8, 8, 255, 255, 255);
        for (var i = 0; i < frame.Pixels.Length; i += 8)
        {
            frame.Pixels[i] = frame.Pixels[i + 1] = frame.Pixels[i + 2] = 0;
        }
        var result = ScreenshotTiler.Tile(frame, 1, 4);
        Assert.True(result.Tiles.Count <= 4);
        Assert.Equal(8, result.Width);
        Assert.Equal(8, result.Height);
    }
}
