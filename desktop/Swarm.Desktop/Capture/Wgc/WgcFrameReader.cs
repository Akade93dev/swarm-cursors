using System.Runtime.InteropServices;
using Swarm.Core.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Swarm.Desktop;

/// <summary>
/// 把 WGC 帧的 D3D11 纹理读成内部 RGBA 帧。调用方负责保证 frame 在读取期间有效。
/// </summary>
internal static class WgcFrameReader
{
    internal static RgbaFrame? TryRead(
        ID3D11Device device,
        ID3D11DeviceContext context,
        nint frame,
        out string detail)
    {
        detail = "";
        var hr = WgcInterop.GetFrameSurface(frame, out var surface);
        if (hr < 0)
        {
            detail = $"get_Surface 0x{hr:X8}";
            return null;
        }

        nint access = 0;
        nint texture = 0;
        try
        {
            hr = WgcInterop.QueryInterface(surface, WgcInterop.IidDxgiInterfaceAccess, out access);
            if (hr < 0)
            {
                detail = $"IDirect3DDxgiInterfaceAccess 0x{hr:X8}";
                return null;
            }

            hr = WgcInterop.GetDxgiInterface(access, WgcInterop.IidTexture2D, out texture);
            if (hr < 0)
            {
                detail = $"ID3D11Texture2D 0x{hr:X8}";
                return null;
            }

            // Vortice 包装对象接管该引用，这里置零避免 finally 里二次释放。
            var owned = texture;
            texture = 0;
            return ReadTexture(device, context, owned, out detail);
        }
        finally
        {
            WgcInterop.Release(texture);
            WgcInterop.Release(access);
            WgcInterop.Release(surface);
        }
    }

    private static RgbaFrame? ReadTexture(
        ID3D11Device device,
        ID3D11DeviceContext context,
        nint texturePointer,
        out string detail)
    {
        detail = "";
        using var source = new ID3D11Texture2D(texturePointer);
        var desc = source.Description;
        if (desc.Format != Format.B8G8R8A8_UNorm)
        {
            detail = $"不支持的纹理格式 {desc.Format}";
            return null;
        }

        var width = (int)desc.Width;
        var height = (int)desc.Height;
        using var staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        context.CopyResource(staging, source);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowPitch = (int)mapped.RowPitch;
            var pixels = new byte[checked(width * height * 4)];
            for (var y = 0; y < height; y++)
                Marshal.Copy(mapped.DataPointer + y * rowPitch, pixels, y * width * 4, width * 4);
            // WGC 输出 BGRA，统一转成内部 RGBA 并补满不透明 alpha。
            for (var i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                pixels[i + 3] = 255;
            }
            return new RgbaFrame(width, height, pixels);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
