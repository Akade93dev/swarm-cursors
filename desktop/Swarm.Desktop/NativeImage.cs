using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

public static class NativeImage
{
    public static string ToDataUrl(RgbaFrame frame)
    {
        var bgra = new byte[frame.Pixels.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = frame.Pixels[i + 2];
            bgra[i + 1] = frame.Pixels[i + 1];
            bgra[i + 2] = frame.Pixels[i];
            bgra[i + 3] = frame.Pixels[i + 3];
        }
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, bgra, frame.Width * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}";
    }

    public static string AverageCss(RgbaFrame frame, RectInt bounds)
    {
        var crop = FrameCropper.Crop(frame, bounds);
        if (crop == null) return "rgb(240,240,240)";
        long r = 0, g = 0, b = 0;
        for (var i = 0; i < crop.Pixels.Length; i += 4)
        {
            r += crop.Pixels[i];
            g += crop.Pixels[i + 1];
            b += crop.Pixels[i + 2];
        }
        var count = (long)crop.Width * crop.Height;
        return $"rgb({r / count},{g / count},{b / count})";
    }
}
