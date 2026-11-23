using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

/// <summary>
/// 原 PrintWindow 抓屏路径：先试客户区，再试整窗渲染后裁剪。
/// 返回空帧表示该路径不适用（GPU/合成窗口常见），由后端链继续降级。
/// </summary>
internal sealed class PrintWindowBackend : ICaptureBackend
{
    private const uint PwClientOnly = 0x1;
    private const uint PwRenderFullContent = 0x2;

    public string Name => "print";

    public CaptureAttempt TryCapture(nint window, CaptureGeometry geometry)
    {
        var frame = CapturePrint(window, PwClientOnly | PwRenderFullContent, geometry.ClientBounds.Width, geometry.ClientBounds.Height)
                    ?? CapturePrintFullWindowClient(window, geometry, PwRenderFullContent)
                    ?? CapturePrintFullWindowClient(window, geometry, 0);
        return new CaptureAttempt(frame);
    }

    /// <summary>部分应用不支持 PW_CLIENTONLY：整窗渲染后按客户区偏移裁剪。</summary>
    private static RgbaFrame? CapturePrintFullWindowClient(nint window, CaptureGeometry geometry, uint flags)
    {
        if (!NativeMethods.GetWindowRect(window, out var windowRect)) return null;
        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        if (width <= 0 || height <= 0) return null;
        var full = CapturePrint(window, flags, width, height);
        if (full == null) return null;
        return FrameOps.Crop(
            full,
            geometry.ClientScreen.X - windowRect.Left,
            geometry.ClientScreen.Y - windowRect.Top,
            geometry.ClientBounds.Width,
            geometry.ClientBounds.Height);
    }

    private static RgbaFrame? CapturePrint(nint window, uint flags, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var screen = NativeMethods.GetDC(0);
        nint memory = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            if (screen == 0) return null;
            memory = NativeMethods.CreateCompatibleDC(screen);
            if (memory == 0) return null;
            bitmap = NativeMethods.CreateCompatibleBitmap(screen, width, height);
            if (bitmap == 0) return null;
            previous = NativeMethods.SelectObject(memory, bitmap);
            if (!NativeMethods.PrintWindow(window, memory, flags)) return null;
            return ScreenCapture.ReadPixels(memory, bitmap, width, height);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (previous != 0) NativeMethods.SelectObject(memory, previous);
            if (bitmap != 0) NativeMethods.DeleteObject(bitmap);
            if (memory != 0) NativeMethods.DeleteDC(memory);
            if (screen != 0) NativeMethods.ReleaseDC(0, screen);
        }
    }
}
