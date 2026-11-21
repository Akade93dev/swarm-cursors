using System.Runtime.InteropServices;
using Swarm.Core.Visual;

namespace Swarm.Desktop;

/// <summary>
/// 通过 WM_GETFONT 读取经典 Win32 控件的真实字体。只对实现了该消息的控件有效
/// （实测原生 EDIT 可用，WinForms 的 Label/Button/RichTextBox 返回 NULL），失败时静默降级。
/// </summary>
internal static class Win32FontReader
{
    private const uint WmGetFont = 0x0031;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint TimeoutMilliseconds = 20;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static ResolvedFont? TryRead(nint hwnd, int processId, double dpiScale)
    {
        if (hwnd == 0 || dpiScale <= 0) return null;
        if (SendMessageTimeout(hwnd, WmGetFont, 0, 0, SmtoAbortIfHung, TimeoutMilliseconds, out var hfont) == 0) return null;
        if (hfont == 0) return null;
        var logFont = new LogFont { FaceName = string.Empty };
        if (GetObject(hfont, Marshal.SizeOf<LogFont>(), ref logFont) == 0) return null;

        var cellHeight = 0;
        var internalLeading = 0;
        if (logFont.Height > 0)
        {
            // 正值是按字符单元高给出的，需要再减内部行距才是 em；负值本身就是 em。
            if (!ReadTextMetrics(hfont, out cellHeight, out internalLeading)) return null;
        }

        var metrics = new LogFontMetrics(
            logFont.Height,
            cellHeight,
            internalLeading,
            logFont.Weight,
            logFont.Italic != 0,
            logFont.FaceName);
        // 非 DPI 感知的旧程序由系统位图拉伸，LOGFONT 是 96 DPI 逻辑值，不能再按屏占比缩小。
        var scale = IsDpiUnaware(processId) ? 1 : dpiScale;
        return LogFontConverter.Convert(metrics, scale);
    }

    private static bool ReadTextMetrics(nint hfont, out int cellHeight, out int internalLeading)
    {
        cellHeight = 0;
        internalLeading = 0;
        var dc = GetDC(0);
        if (dc == 0) return false;
        var previous = SelectObject(dc, hfont);
        try
        {
            if (!GetTextMetrics(dc, out var metrics)) return false;
            cellHeight = metrics.Height;
            internalLeading = metrics.InternalLeading;
            return cellHeight > 0;
        }
        finally
        {
            if (previous != 0) SelectObject(dc, previous);
            ReleaseDC(0, dc);
        }
    }

    private static bool IsDpiUnaware(int processId)
    {
        if (processId <= 0) return false;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0) return false;
        try
        {
            // 0 = DPI_UNAWARE，1 = SYSTEM_AWARE，2 = PER_MONITOR_AWARE。
            return GetProcessDpiAwareness(process, out var awareness) == 0 && awareness == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // 字段名必须保持与原生结构一致；StructLayout 的 CharSet 参数要写全名，否则会被同名字段遮蔽。
    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct LogFont
    {
        public int Height;
        public int Width;
        public int Escapement;
        public int Orientation;
        public int Weight;
        public byte Italic;
        public byte Underline;
        public byte StrikeOut;
        public byte CharSet;
        public byte OutPrecision;
        public byte ClipPrecision;
        public byte Quality;
        public byte PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct TextMetric
    {
        public int Height;
        public int Ascent;
        public int Descent;
        public int InternalLeading;
        public int ExternalLeading;
        public int AverageCharWidth;
        public int MaxCharWidth;
        public int Weight;
        public int Overhang;
        public int DigitizedAspectX;
        public int DigitizedAspectY;
        public char FirstChar;
        public char LastChar;
        public char DefaultChar;
        public char BreakChar;
        public byte Italic;
        public byte Underlined;
        public byte StruckOut;
        public byte PitchAndFamily;
        public byte CharSet;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hWnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetObjectW", SetLastError = true)]
    private static extern int GetObject(nint handle, int size, ref LogFont logFont);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint handle);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextMetricsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTextMetrics(nint dc, out TextMetric metrics);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("shcore.dll")]
    private static extern int GetProcessDpiAwareness(nint process, out int awareness);
}
