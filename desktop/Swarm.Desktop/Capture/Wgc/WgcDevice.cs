using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Swarm.Desktop;

/// <summary>
/// 进程级共享的 D3D11 设备与 WinRT 设备包装。WGC 帧池与会话都挂在它上面，避免每个窗口各建一套设备。
/// 设备一旦创建就活到进程结束：D3D11 设备/上下文默认线程安全，可被多个抓屏会话共用。
/// </summary>
internal static class WgcDevice
{
    private static readonly object Gate = new();
    private static ID3D11Device? _device;
    private static ID3D11DeviceContext? _context;
    private static nint _winrtDevice;
    private static string? _error;

    internal static bool TryGet(
        out ID3D11Device device,
        out ID3D11DeviceContext context,
        out nint winrtDevice,
        out string error)
    {
        lock (Gate)
        {
            if (_device is not null && _context is not null)
            {
                device = _device;
                context = _context;
                winrtDevice = _winrtDevice;
                error = "";
                return true;
            }

            device = null!;
            context = null!;
            winrtDevice = 0;
            if (_error is not null)
            {
                error = _error;
                return false;
            }

            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            var created = D3D11.D3D11CreateDevice(
                0, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                levels, out var createdDevice, out _, out var createdContext);
            if (created.Failure)
            {
                createdDevice?.Dispose();
                createdContext?.Dispose();
                _error = created.Description;
                error = _error;
                return false;
            }

            using var dxgiDevice = createdDevice.QueryInterface<IDXGIDevice>();
            var hr = WgcInterop.WrapDxgiDevice(dxgiDevice.NativePointer, out var winrt);
            if (hr < 0)
            {
                createdDevice.Dispose();
                createdContext.Dispose();
                _error = $"CreateDirect3D11DeviceFromDXGIDevice 0x{hr:X8}";
                error = _error;
                return false;
            }

            _device = createdDevice;
            _context = createdContext;
            _winrtDevice = winrt;
            device = createdDevice;
            context = createdContext;
            winrtDevice = winrt;
            error = "";
            return true;
        }
    }
}
