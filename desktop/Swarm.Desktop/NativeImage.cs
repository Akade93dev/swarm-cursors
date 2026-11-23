using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swarm.Core.Capture;
using Swarm.Core.Semantics;
using Swarm.Core.Visual;

namespace Swarm.Desktop;

public static class NativeImage
{
    public static string ToDataUrl(RgbaFrame frame) => ToDataUrl(frame, null);

    /// <summary>导出 PNG 原始字节 (OCR/YOLOv8n 等视觉模型通用).</summary>
    public static byte[] ToPngBytes(RgbaFrame frame)
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
        return stream.ToArray();
    }

    /// <summary>
    /// 生成 data URL。传入 key 时把与 key 接近的像素抠成透明：控件裁剪图只保留自己画过的笔画和表面，
    /// 叠到连续底色上就不会带出一块和周围不同色的矩形，画面不会看起来像一块块拼的。
    /// </summary>
    public static string ToDataUrl(RgbaFrame frame, Rgb? key, int tolerance = 24)
    {
        var bgra = new byte[frame.Pixels.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = frame.Pixels[i + 2];
            bgra[i + 1] = frame.Pixels[i + 1];
            bgra[i + 2] = frame.Pixels[i];
            bgra[i + 3] = frame.Pixels[i + 3];
            if (key is { } k && frame.Pixels[i + 3] != 0
                && Math.Abs(frame.Pixels[i] - k.R) + Math.Abs(frame.Pixels[i + 1] - k.G) + Math.Abs(frame.Pixels[i + 2] - k.B) <= tolerance)
            {
                bgra[i + 3] = 0;
            }
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
