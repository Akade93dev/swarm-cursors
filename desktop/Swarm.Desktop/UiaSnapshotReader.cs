using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

public sealed record UiaSnapshotOptions(
    bool IncludePatterns = false,
    int MaxNodes = 800,
    int MaxDepth = 12,
    int FirstPassMilliseconds = 1200,
    int ExtensionMilliseconds = 2000);

public sealed record UiaSnapshot(
    nint WindowHandle,
    int ProcessId,
    string ProcessName,
    string Title,
    RectInt WindowBounds,
    IReadOnlyList<UiaElementInfo> Elements,
    IReadOnlyDictionary<string, AutomationElement> NativeElements,
    TargetCapabilities Capabilities,
    UiaTruncationReason Truncation = UiaTruncationReason.None);

public static class UiaSnapshotReader
{
    private sealed record Pending(AutomationElement Element, string Id, string? ParentId, int Depth);

    /// <summary>
    /// 读取目标窗口的辅助功能树。使用缓存请求批量取属性，并用两阶段时间预算：
    /// 首轮 <see cref="UiaSnapshotOptions.FirstPassMilliseconds"/>，命中上限后再续跑
    /// <see cref="UiaSnapshotOptions.ExtensionMilliseconds"/>，避免慢 provider（UWP、桌面图标层）
    /// 在 1.5 秒内被截断成十几条记录。
    /// </summary>
    public static UiaSnapshot Read(nint window, UiaSnapshotOptions? options = null)
    {
        options ??= new UiaSnapshotOptions();
        if (window == 0 || !NativeMethods.IsWindow(window)) throw new InvalidOperationException("目标窗口已关闭");
        if (NativeMethods.IsIconic(window)) throw new InvalidOperationException("请先还原最小化的目标窗口");
        var root = AutomationElement.FromHandle(window) ?? throw new InvalidOperationException("无法读取目标窗口的 UIA 信息");
        var rootInfo = root.Current;
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        var processName = SafeProcessName((int)processId);
        if (!NativeMethods.GetWindowRect(window, out var nativeBounds)) throw new InvalidOperationException("无法读取目标窗口位置");
        var windowBounds = new RectInt(nativeBounds.Left, nativeBounds.Top, nativeBounds.Right - nativeBounds.Left, nativeBounds.Bottom - nativeBounds.Top);

        var results = new List<UiaElementInfo>();
        var nativeById = new Dictionary<string, AutomationElement>();
        var pending = new Queue<Pending>();
        pending.Enqueue(new Pending(root, "0", null, 0));
        var walker = TreeWalker.ControlViewWalker;
        var stopwatch = Stopwatch.StartNew();
        var limit = Math.Max(1, options.FirstPassMilliseconds);
        var extended = false;
        var truncation = UiaTruncationReason.None;

        // 一次性缓存所需属性，避免逐元素跨进程往返（主要是 .Current 与 GetSupportedPatterns 的开销）。
        var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        request.Add(AutomationElement.IsPasswordProperty);
        request.Add(AutomationElement.ProcessIdProperty);
        // WM_GETFONT 兜底要用原生句柄；放进缓存请求里读取，避免逐元素跨进程往返。
        request.Add(AutomationElement.NativeWindowHandleProperty);
        request.Add(ValuePattern.Pattern);

        // Activate 返回作用域令牌，释放时恢复调用方原有的缓存请求。
        using (request.Activate())
        {
            while (pending.Count > 0)
            {
                if (results.Count >= options.MaxNodes)
                {
                    truncation = UiaTruncationReason.NodeLimit;
                    break;
                }
                if (stopwatch.ElapsedMilliseconds > limit)
                {
                    if (!extended)
                    {
                        extended = true;
                        limit += Math.Max(1, options.ExtensionMilliseconds);
                        continue;
                    }
                    truncation = UiaTruncationReason.TimeLimit;
                    break;
                }

                var node = pending.Dequeue();
                AutomationElement.AutomationElementInformation current;
                try
                {
                    current = node.Element.Current;
                }
                catch (Exception error) when (IsUnavailable(error))
                {
                    continue;
                }

                var bounds = current.BoundingRectangle;
                var rect = new RectInt(
                    (int)Math.Round(bounds.X),
                    (int)Math.Round(bounds.Y),
                    (int)Math.Round(bounds.Width),
                    (int)Math.Round(bounds.Height));
                var controlType = current.ControlType?.ProgrammaticName?.Replace("ControlType.", "", StringComparison.Ordinal);
                var included = controlType is { Length: > 0 } && rect.Width > 0 && rect.Height > 0
                    && !double.IsInfinity(bounds.X) && !double.IsInfinity(bounds.Y);

                string? value = null;
                if (included && (controlType is "Edit" or "Document"))
                {
                    try
                    {
                        // 老式 WinForms/MSAA provider 往往不支持缓存模式，退回实时读取，否则会把
                        // 输入框内容误当成关联标签名。
                        if (node.Element.TryGetCachedPattern(ValuePattern.Pattern, out var pattern) ||
                            node.Element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                            value = ((ValuePattern)pattern).Current.Value;
                    }
                    catch (Exception error) when (IsUnavailable(error))
                    {
                        value = null;
                    }
                }

                if (included)
                {
                    nativeById[node.Id] = node.Element;
                    results.Add(new UiaElementInfo
                    {
                        Id = node.Id,
                        ParentId = node.ParentId,
                        ControlType = controlType!,
                        Name = current.Name,
                        Value = value,
                        Bounds = rect,
                        ProcessId = current.ProcessId,
                        IsPassword = current.IsPassword,
                        HasVisual = !current.IsOffscreen && rect.Width >= 2 && rect.Height >= 2,
                        Patterns = options.IncludePatterns ? SupportedPatterns(node.Element) : Array.Empty<string>(),
                    });
                }

                // 不可见且无尺寸的容器不承载可见内容，跳过其子树。
                if (!included && current.IsOffscreen && (rect.Width <= 0 || rect.Height <= 0)) continue;

                if (node.Depth >= options.MaxDepth)
                {
                    try
                    {
                        if (walker.GetFirstChild(node.Element) != null) truncation = UiaTruncationReason.DepthLimit;
                    }
                    catch (Exception error) when (IsUnavailable(error))
                    {
                        // 分支不可用时忽略深度探测。
                    }
                    continue;
                }

                try
                {
                    var child = walker.GetFirstChild(node.Element);
                    var index = 0;
                    while (child != null)
                    {
                        pending.Enqueue(new Pending(child, $"{node.Id}.{index}", included ? node.Id : node.ParentId, node.Depth + 1));
                        child = walker.GetNextSibling(child);
                        index++;
                    }
                }
                catch (Exception error) when (IsUnavailable(error))
                {
                    // 关闭中的分支失效，不影响已经读取的控件。
                }
            }
        }

        var capabilities = TargetCapabilitiesDetector.Detect(
            processName,
            NativeMethods.WindowClass(window),
            results.SelectMany(item => item.Patterns),
            uiaAvailable: true);
        return new UiaSnapshot(window, (int)processId, processName, rootInfo.Name ?? processName, windowBounds, results, nativeById, capabilities, truncation);
    }

    private static string[] SupportedPatterns(AutomationElement element)
    {
        try
        {
            return element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName).ToArray();
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsUnavailable(Exception error) =>
        error is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException;

    private static string SafeProcessName(int processId)
    {
        try { return Process.GetProcessById(processId).ProcessName; }
        catch (ArgumentException) { return "unknown"; }
    }
}
