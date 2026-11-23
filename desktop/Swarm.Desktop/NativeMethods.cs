using System.Runtime.InteropServices;
using System.Text;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

internal static class NativeMethods
{
    internal const int WmHotKey = 0x0312;
    internal const int WsExTransparent = 0x00000020;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExNoActivate = 0x08000000;
    /// <summary>AI 输入派发写入的 ExtraInfo 标记（"SWAR"）：钩子据此放行，避免 HUD 吞掉本机注入的点击。</summary>
    internal const nint SwarmInputMarker = 0x53574152;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const int SwRestore = 9;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromRect(ref Win32Rect lprc, uint dwFlags);

    [DllImport("Shcore.dll")]
    internal static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static extern int GetWindowLong32(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    internal static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Win32Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out Win32Rect lpRect);

    /// <summary>DWMWA_EXTENDED_FRAME_BOUNDS：不含不可见调整边框的可见窗口矩形。</summary>
    private const int DwmwaExtendedFrameBounds = 9;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hWnd, int attribute, out Win32Rect value, int size);

    /// <summary>
    /// 取与屏幕可见内容对齐的窗口矩形：GetWindowRect 含约 7–11px 的不可见调整边框，
    /// 而 WGC 帧从可见边框开始，直接用 GetWindowRect 裁剪会让客户区偏出若干像素。
    /// </summary>
    internal static bool TryGetVisibleWindowRect(nint window, out Win32Rect rect)
    {
        if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<Win32Rect>()) == 0)
            return true;
        return GetWindowRect(window, out rect);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hWnd, out Win32Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(nint hWnd, ref Win32Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(nint hWnd, ref Win32Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendMessageTimeoutText(nint hWnd, uint msg, nint wParam, StringBuilder lParam, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static extern nint SendMessageTimeoutValue(nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(Win32Point point);

    [DllImport("user32.dll")]
    internal static extern nint ChildWindowFromPointEx(nint hWnd, Win32Point point, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint hWnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowDisplayAffinity(nint hWnd, out uint affinity);


    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetActiveWindow(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(nint hWnd, string text, string caption, uint type);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowEx(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    /// <summary>读取窗口类名；读取失败时返回 null。</summary>
    internal static string? WindowClass(nint hWnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hWnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    /// <summary>是否为承载桌面图标的窗口：类名为桌面外壳且挂着 SHELLDLL_DefView。</summary>
    internal static bool IsDesktopShell(nint hWnd) =>
        RebuildStrategy.IsShellDesktop(WindowClass(hWnd)) && FindDesktopView(hWnd) != 0;

    /// <summary>查找桌面图标视图：先看直接子窗口，再看 WorkerW 一层（旧版幻灯片壁纸会这样嵌套）。</summary>
    private static nint FindDesktopView(nint hWnd)
    {
        var view = FindWindowEx(hWnd, 0, "SHELLDLL_DefView", null);
        if (view != 0) return view;
        var host = FindWindowEx(hWnd, 0, "WorkerW", null);
        return host != 0 ? FindWindowEx(host, 0, "SHELLDLL_DefView", null) : 0;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateCompatibleDC(nint hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateCompatibleBitmap(nint hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint SelectObject(nint hDC, nint hGdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint hGdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(nint hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, nint hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int GetDIBits(nint hdc, nint hbmp, uint uStartScan, uint cScanLines, byte[]? lpvBits, ref BitmapInfo lpbi, uint uUsage);

    internal const int WhKeyboardLl = 13;
    internal const int WhMouseLl = 14;
    internal const int WmLButtonDown = 0x0201;
    internal const int WmLButtonUp = 0x0202;
    internal const int WmRButtonDown = 0x0204;
    internal const int WmRButtonUp = 0x0205;
    internal const int WmMButtonDown = 0x0207;
    internal const int WmMButtonUp = 0x0208;
    internal const int WmMouseWheel = 0x020A;
    internal const int WmXButtonDown = 0x020B;
    internal const int WmXButtonUp = 0x020C;
    internal const int WmMouseHWheel = 0x020E;
    internal const int WmKeyDown = 0x0100;
    internal const int WmKeyUp = 0x0101;
    internal const int WmSysKeyDown = 0x0104;
    internal const int WmSysKeyUp = 0x0105;

    internal delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Win32Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardHookInfo
    {
        internal uint VirtualKey;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHookInfo
    {
        internal Win32Point Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Win32Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal Win32Rect Monitor;
        internal Win32Rect WorkArea;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        internal uint[] Colors;
    }
}
