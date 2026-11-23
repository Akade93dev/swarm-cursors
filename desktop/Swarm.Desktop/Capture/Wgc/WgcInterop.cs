using System.Runtime.InteropServices;

namespace Swarm.Desktop;

/// <summary>
/// Windows.Graphics.Capture 所需的最小 WinRT/COM 互操作层：只依赖 ABI 接口 GUID 与 vtable 顺序，
/// 不引入 CsWinRT 投影、不升级目标框架。GUID 与 vtable 布局取自 Windows SDK 头文件
/// （winrt/windows.graphics.capture.h、um/Windows.Graphics.Capture.Interop.h）。
/// 槽位约定：IUnknown 占 0-2，IInspectable 占 3-5，WinRT 接口方法从 6 开始；经典 COM 接口从 3 开始。
/// </summary>
internal static class WgcInterop
{
    internal static readonly Guid IidItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    internal static readonly Guid IidItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    internal static readonly Guid IidFramePoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    internal static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    internal static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    internal const string CaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    internal const string FramePoolClass = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";
    /// <summary>DirectXPixelFormat.B8G8R8A8UIntNormalized。</summary>
    internal const int PixelFormatBgra8 = 87;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SizeInt32
    {
        internal int Width;
        internal int Height;
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint device);

    internal static int GetActivationFactory(string className, in Guid iid, out nint factory)
    {
        factory = 0;
        var hr = WindowsCreateString(className, className.Length, out var classId);
        if (hr < 0) return hr;
        try
        {
            return RoGetActivationFactory(classId, iid, out factory);
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }

    internal static int WrapDxgiDevice(nint dxgiDevice, out nint winrtDevice) =>
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out winrtDevice);

    private static nint Slot(nint instance, int index) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance) + index * IntPtr.Size);

    private delegate int QueryInterfaceFn(nint self, ref Guid iid, out nint result);
    private delegate int RefCountFn(nint self);
    private delegate int CreateForWindowFn(nint self, nint hwnd, ref Guid iid, out nint result);
    private delegate int CreateFreeThreadedFn(nint self, nint device, int pixelFormat, int bufferCount, SizeInt32 size, out nint result);
    private delegate int TryGetNextFrameFn(nint self, out nint result);
    private delegate int CreateCaptureSessionFn(nint self, nint item, out nint result);
    private delegate int GetSizeFn(nint self, out SizeInt32 result);
    private delegate int GetSurfaceFn(nint self, out nint result);
    private delegate int GetContentSizeFn(nint self, out SizeInt32 result);
    private delegate int RecreateFn(nint self, nint device, int pixelFormat, int bufferCount, SizeInt32 size);
    private delegate int StartCaptureFn(nint self);
    private delegate int GetInterfaceFn(nint self, ref Guid iid, out nint result);

    internal static int QueryInterface(nint instance, in Guid iid, out nint result)
    {
        var requested = iid;
        return Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(Slot(instance, 0))(instance, ref requested, out result);
    }

    internal static void AddRef(nint instance)
    {
        if (instance != 0) Marshal.GetDelegateForFunctionPointer<RefCountFn>(Slot(instance, 1))(instance);
    }

    internal static void Release(nint instance)
    {
        if (instance != 0) Marshal.GetDelegateForFunctionPointer<RefCountFn>(Slot(instance, 2))(instance);
    }

    /// <summary>IGraphicsCaptureItemInterop::CreateForWindow（经典 COM，方法从槽位 3 起）。</summary>
    internal static int CreateForWindow(nint interop, nint window, in Guid iid, out nint item)
    {
        var requested = iid;
        return Marshal.GetDelegateForFunctionPointer<CreateForWindowFn>(Slot(interop, 3))(interop, window, ref requested, out item);
    }

    /// <summary>IDirect3D11CaptureFramePoolStatics2::CreateFreeThreaded：无需调用线程带 DispatcherQueue。</summary>
    internal static int CreateFreeThreaded(nint statics, nint winrtDevice, SizeInt32 size, out nint pool) =>
        Marshal.GetDelegateForFunctionPointer<CreateFreeThreadedFn>(Slot(statics, 6))(
            statics, winrtDevice, PixelFormatBgra8, 2, size, out pool);

    internal static int TryGetNextFrame(nint pool, out nint frame) =>
        Marshal.GetDelegateForFunctionPointer<TryGetNextFrameFn>(Slot(pool, 7))(pool, out frame);

    internal static int CreateCaptureSession(nint pool, nint item, out nint session) =>
        Marshal.GetDelegateForFunctionPointer<CreateCaptureSessionFn>(Slot(pool, 10))(pool, item, out session);

    internal static int GetItemSize(nint item, out SizeInt32 size) =>
        Marshal.GetDelegateForFunctionPointer<GetSizeFn>(Slot(item, 7))(item, out size);

    internal static int GetFrameSurface(nint frame, out nint surface) =>
        Marshal.GetDelegateForFunctionPointer<GetSurfaceFn>(Slot(frame, 6))(frame, out surface);

    internal static int GetFrameContentSize(nint frame, out SizeInt32 size) =>
        Marshal.GetDelegateForFunctionPointer<GetContentSizeFn>(Slot(frame, 8))(frame, out size);

    /// <summary>IDirect3D11CaptureFramePool::Recreate：窗口尺寸/DPI 变化后按新尺寸重建帧池。</summary>
    internal static int Recreate(nint pool, nint winrtDevice, SizeInt32 size) =>
        Marshal.GetDelegateForFunctionPointer<RecreateFn>(Slot(pool, 6))(pool, winrtDevice, PixelFormatBgra8, 2, size);

    internal static int StartCapture(nint session) =>
        Marshal.GetDelegateForFunctionPointer<StartCaptureFn>(Slot(session, 6))(session);

    /// <summary>IDirect3DDxgiInterfaceAccess::GetInterface：从 WinRT 表面取底层 D3D11 纹理。</summary>
    internal static int GetDxgiInterface(nint access, in Guid iid, out nint result)
    {
        var requested = iid;
        return Marshal.GetDelegateForFunctionPointer<GetInterfaceFn>(Slot(access, 3))(access, ref requested, out result);
    }
}
