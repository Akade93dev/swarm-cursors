namespace Swarm.Core.Capture;

public static class ScreenshotTiler
{
    public static ScreenshotModel Tile(RgbaFrame frame, int preferredTileSize = 128, int maxTiles = 1200)
    {
        if (preferredTileSize <= 0) throw new ArgumentOutOfRangeException(nameof(preferredTileSize));
        if (maxTiles <= 0) throw new ArgumentOutOfRangeException(nameof(maxTiles));
        var background = Average(frame.Pixels, frame.Width, frame.Height, 0, 0, frame.Width, frame.Height);
        var tileSize = preferredTileSize;
        while ((long)(frame.Width + tileSize - 1) / tileSize * ((frame.Height + tileSize - 1) / tileSize) > maxTiles)
            tileSize = checked(tileSize * 2);

        var tiles = new List<ScreenshotTile>();
        for (var y = 0; y < frame.Height; y += tileSize)
        for (var x = 0; x < frame.Width; x += tileSize)
        {
            var width = Math.Min(tileSize, frame.Width - x);
            var height = Math.Min(tileSize, frame.Height - y);
            var average = Average(frame.Pixels, frame.Width, frame.Height, x, y, width, height);
            if (IsUniform(frame.Pixels, frame.Width, x, y, width, height, average) && Near(average, background)) continue;
            tiles.Add(new ScreenshotTile(x, y, width, height, Crop(frame.Pixels, frame.Width, x, y, width, height)));
        }
        return new ScreenshotModel(frame.Width, frame.Height, Css(background), tiles);
    }

    private static byte[] Crop(byte[] source, int sourceWidth, int x, int y, int width, int height)
    {
        var result = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Array.Copy(source, ((y + row) * sourceWidth + x) * 4, result, row * width * 4, width * 4);
        return result;
    }

    private static (byte R, byte G, byte B) Average(byte[] pixels, int sourceWidth, int sourceHeight, int x, int y, int width, int height)
    {
        long r = 0, g = 0, b = 0;
        for (var row = 0; row < height; row++)
        for (var col = 0; col < width; col++)
        {
            var i = ((y + row) * sourceWidth + x + col) * 4;
            r += pixels[i];
            g += pixels[i + 1];
            b += pixels[i + 2];
        }
        var count = (long)width * height;
        return ((byte)((r + count / 2) / count), (byte)((g + count / 2) / count), (byte)((b + count / 2) / count));
    }

    private static bool IsUniform(byte[] pixels, int sourceWidth, int x, int y, int width, int height, (byte R, byte G, byte B) average)
    {
        for (var row = 0; row < height; row++)
        for (var col = 0; col < width; col++)
        {
            var i = ((y + row) * sourceWidth + x + col) * 4;
            if (Math.Abs(pixels[i] - average.R) > 5 || Math.Abs(pixels[i + 1] - average.G) > 5 || Math.Abs(pixels[i + 2] - average.B) > 5) return false;
        }
        return true;
    }

    private static bool Near((byte R, byte G, byte B) a, (byte R, byte G, byte B) b) =>
        Math.Abs(a.R - b.R) <= 6 && Math.Abs(a.G - b.G) <= 6 && Math.Abs(a.B - b.B) <= 6;

    private static string Css((byte R, byte G, byte B) color) => $"rgb({color.R},{color.G},{color.B})";
}
