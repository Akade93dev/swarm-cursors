using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Swarm.Core.Windows;

namespace Swarm.Desktop;

public sealed record WindowCandidate(nint Handle, int ProcessId, string ProcessName, string Title);

public static class WindowLocator
{
    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maximum);

    public static WindowCandidate Find(string? processName, string? title, nint ownWindow)
    {
        if (string.IsNullOrWhiteSpace(processName) && string.IsNullOrWhiteSpace(title))
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == 0 || foreground == ownWindow) throw new InvalidOperationException("请先激活目标窗口，或指定 targetProcess / targetTitle");
            return Describe(foreground);
        }
        var candidates = new List<WindowCandidate>();
        EnumWindows((window, _) =>
        {
            if (window == ownWindow || !IsWindowVisible(window)) return true;
            WindowCandidate candidate;
            // 受保护/提权进程会让 ProcessName 抛 Win32Exception；异常不能穿出原生回调，直接跳过该窗口。
            try { candidate = Describe(window); }
            catch (Exception) { return true; }
            if (!WindowFilter.Matches(processName, title, candidate.ProcessName, candidate.Title)) return true;
            candidates.Add(candidate);
            return true;
        }, 0);
        if (candidates.Count == 0) throw new InvalidOperationException("没有找到匹配的可见目标窗口");
        if (candidates.Count > 1) throw new InvalidOperationException("目标窗口不唯一，请同时提供更具体的 targetProcess 和 targetTitle");
        return candidates[0];
    }

    public static WindowCandidate Describe(nint window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) throw new ArgumentException("Window does not exist", nameof(window));
        string processName;
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Win32Exception error)
        {
            // 提权/受保护进程：这不是“找不到窗口”，而是权限不足以读取进程信息。
            throw new InvalidOperationException($"目标进程受保护或权限不足：{error.Message}", error);
        }
        var length = GetWindowTextLength(window);
        var text = new StringBuilder(length + 1);
        GetWindowText(window, text, text.Capacity);
        return new WindowCandidate(window, (int)processId, processName, text.ToString());
    }

    public static void Activate(nint window)
    {
        if (NativeMethods.GetForegroundWindow() == window) return;
        if (NativeMethods.IsIconic(window)) NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
        NativeMethods.SetForegroundWindow(window);
        if (NativeMethods.GetForegroundWindow() == window) return;

        var currentThread = NativeMethods.GetCurrentThreadId();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(window, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread && NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        try
        {
            NativeMethods.BringWindowToTop(window);
            NativeMethods.SetForegroundWindow(window);
            NativeMethods.SetActiveWindow(window);
        }
        finally
        {
            if (attachedTarget) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
        if (NativeMethods.GetForegroundWindow() != window) throw new InvalidOperationException("无法激活目标窗口，请手动将其置于前台后重试");
    }
}
