using System.ComponentModel;
using System.Runtime.InteropServices;
using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

public sealed record CapturedScreen(RgbaFrame Frame, RectInt Monitor, double DpiScale, nint SourceWindow);

public static class ScreenCapture
{
    private const uint CaptureBlt = 0x40000000;
    private const uint SrcCopy = 0x00CC0020;

    public static CapturedScreen CaptureForWindow(nint window)
    {
        var monitorHandle = NativeMethods.MonitorFromWindow(window, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var monitor = new RectInt(
            info.Monitor.Left,
            info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left,
            info.Monitor.Bottom - info.Monitor.Top);
        var dpi = window == 0 ? 96u : NativeMethods.GetDpiForWindow(window);
        if (dpi == 0) dpi = 96;
        return new CapturedScreen(Capture(monitor), monitor, dpi / 96.0, window);
    }

    private static RgbaFrame Capture(RectInt bounds)
    {
        var screen = NativeMethods.GetDC(0);
        nint memory = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            if (screen == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法获取屏幕设备上下文");
            memory = NativeMethods.CreateCompatibleDC(screen);
            if (memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建截图上下文");
            bitmap = NativeMethods.CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建截图位图");
            previous = NativeMethods.SelectObject(memory, bitmap);
            if (!NativeMethods.BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, SrcCopy | CaptureBlt))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "屏幕捕获失败");

            return ReadPixels(memory, bitmap, bounds.Width, bounds.Height);
        }
        finally
        {
            if (previous != 0) NativeMethods.SelectObject(memory, previous);
            if (bitmap != 0) NativeMethods.DeleteObject(bitmap);
            if (memory != 0) NativeMethods.DeleteDC(memory);
            if (screen != 0) NativeMethods.ReleaseDC(0, screen);
        }
    }

    /// <summary>把已绘制到兼容位图的像素读成 RGBA；供屏幕捕获与窗口 PrintWindow 共用。</summary>
    internal static RgbaFrame ReadPixels(nint memory, nint bitmap, int width, int height)
    {
        var info = new NativeMethods.BitmapInfo
        {
            Header = new NativeMethods.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            },
            Colors = new uint[256],
        };
        var pixels = new byte[checked(width * height * 4)];
        if (NativeMethods.GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "读取屏幕像素失败");
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            pixels[i + 3] = 255;
        }
        return new RgbaFrame(width, height, pixels);
    }
}
