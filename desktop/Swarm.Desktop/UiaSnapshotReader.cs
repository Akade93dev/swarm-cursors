using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Swarm.Core.Diagnostics;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

public sealed record UiaSnapshotOptions(
    bool IncludePatterns = false,
    int MaxNodes = 800,
    int MaxDepth = 24,
    int FirstPassMilliseconds = 1200,
    int ExtensionMilliseconds = 2000,
    int TotalTimeoutMilliseconds = 2500,
    long Generation = 0);

/// <summary>一次观察的读取成本：用于验证批取是否真的减少了跨进程调用与总耗时。</summary>
public sealed record UiaReadStats(
    int Elements,
    int PatternProbes,
    int LivePatternQueries,
    int LivePropertyReads,
    long ElapsedMilliseconds,
    bool TimedOut);

public sealed record UiaSnapshot(
    nint WindowHandle,
    int ProcessId,
    string ProcessName,
    string Title,
    RectInt WindowBounds,
    IReadOnlyList<UiaElementInfo> Elements,
    IReadOnlyDictionary<string, AutomationElement> NativeElements,
    TargetCapabilities Capabilities,
    UiaTruncationReason Truncation = UiaTruncationReason.None,
    UiaReadStats? Stats = null,
    long Generation = 0);

public static class UiaSnapshotReader
{
    /// <summary>硬超时的额外宽限：超过总超时后再等这么久，仍无结果就返回已读到的部分。</summary>
    private const int HardGraceMilliseconds = 400;

    /// <summary>
    /// 进缓存请求的模式集合。全部是动作阶梯与能力画像会用到的模式；
    /// 逐元素只查询这张固定表（纯内存命中），不再调用 <c>GetSupportedPatterns</c> 跨进程枚举。
    /// </summary>
    private static readonly AutomationPattern[] CachedPatterns =
    {
        InvokePattern.Pattern,
        TogglePattern.Pattern,
        SelectionItemPattern.Pattern,
        SelectionPattern.Pattern,
        ExpandCollapsePattern.Pattern,
        ValuePattern.Pattern,
        ScrollPattern.Pattern,
        TextPattern.Pattern,
        RangeValuePattern.Pattern,
        GridItemPattern.Pattern,
        WindowPattern.Pattern,
        TransformPattern.Pattern,
    };

    private sealed record Pending(AutomationElement Element, string Id, string? ParentId, int Depth);

    private sealed record SnapshotHeader(int ProcessId, string ProcessName, string ClassName, string Title, RectInt Bounds);

    private sealed record WalkResult(int Elements);

    private sealed record BufferCapture(
        SnapshotHeader? Header,
        IReadOnlyList<UiaElementInfo> Elements,
        IReadOnlyDictionary<string, AutomationElement> Native,
        UiaTruncationReason Truncation);

    /// <summary>
    /// 读取目标窗口的辅助功能树。属性与模式走 <see cref="CacheRequest"/> 批量缓存，
    /// 遍历在专属 MTA 线程上执行并受总超时约束：超时返回已读到的部分并标
    /// <see cref="UiaTruncationReason.TimeLimit"/>，不让一次 observe 无限挂起。
    /// 两阶段时间预算（首轮 + 一次续跑）保留，给惰性建树的 Chromium 留出重抓机会。
    /// </summary>
    public static UiaSnapshot Read(nint window, UiaSnapshotOptions? options = null)
    {
        options ??= new UiaSnapshotOptions();
        if (window == 0 || !NativeMethods.IsWindow(window)) throw new InvalidOperationException("目标窗口已关闭");
        if (NativeMethods.IsIconic(window)) throw new InvalidOperationException("请先还原最小化的目标窗口");

        var sink = new SnapshotBuffer();
        var clock = Stopwatch.StartNew();
        var worker = new Thread(() => ReadOnWorker(window, options, sink))
        {
            IsBackground = true,
            Name = "swarm-uia-read",
        };
        // 主线程（WPF 覆盖层）是 STA，UIA 跨进程调用在 STA 上要封送且容易被 UI 消息拖住；强制 MTA。
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();

        var finished = sink.Wait(Math.Max(200, options.TotalTimeoutMilliseconds) + HardGraceMilliseconds);
        var error = sink.Error;
        if (finished && error != null) error.Throw();

        var captured = sink.Take(!finished);
        var elapsed = clock.ElapsedMilliseconds;
        // 软超时（遍历自己按预算收口）与硬超时（卡在跨进程调用上）对调用方是同一种事实：结果被时间截断。
        var timedOut = captured.Truncation == UiaTruncationReason.TimeLimit;
        var stats = new UiaReadStats(
            captured.Elements.Count,
            sink.PatternProbes,
            sink.LivePatternQueries,
            sink.LivePropertyReads,
            elapsed,
            timedOut);
        // 超时后工作线程可能仍在 Set/Add：此时不释放同步原语，让它自然结束。
        if (finished) sink.Dispose();

        var header = captured.Header ?? FallbackHeader(window);
        var capabilities = TargetCapabilitiesDetector.Detect(
            header.ProcessName,
            header.ClassName,
            captured.Elements.SelectMany(item => item.Patterns),
            uiaAvailable: true);
        FileLog.Write(
            "uia",
            $"observe elements={stats.Elements} ms={stats.ElapsedMilliseconds} timedOut={stats.TimedOut} " +
            $"patterns={stats.PatternProbes} livePatternQueries={stats.LivePatternQueries} liveReads={stats.LivePropertyReads}");
        return new UiaSnapshot(
            window,
            header.ProcessId,
            header.ProcessName,
            header.Title,
            header.Bounds,
            captured.Elements,
            captured.Native,
            capabilities,
            captured.Truncation,
            stats,
            options.Generation);
    }

    private static void ReadOnWorker(nint window, UiaSnapshotOptions options, SnapshotBuffer sink)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var budget = new UiaObserveBudget(options.FirstPassMilliseconds, options.ExtensionMilliseconds, options.TotalTimeoutMilliseconds);
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            var processName = SafeProcessName((int)processId);
            if (!NativeMethods.GetWindowRect(window, out var nativeBounds)) throw new InvalidOperationException("无法读取目标窗口位置");
            var className = NativeMethods.WindowClass(window) ?? string.Empty;
            // 先落窗口信息（全是本地 Win32 调用）；标题要等根元素建好后再补，
            // 这样即使硬超时发生在 UIA 调用里，返回的部分结果也带着正确的进程与窗口范围。
            sink.SetHeader(new SnapshotHeader(
                (int)processId,
                processName,
                className,
                processName,
                new RectInt(nativeBounds.Left, nativeBounds.Top, nativeBounds.Right - nativeBounds.Left, nativeBounds.Bottom - nativeBounds.Top)));

            // 只按窗口类/进程名做一次判断，决定是否需要为惰性建树的 Chromium 再做几次抓取。
            var warmupProbe = TargetCapabilitiesDetector.Detect(processName, className, null, uiaAvailable: true);
            sink.BeginAttempt();
            var walk = WalkOnce(window, options, budget, clock, sink);
            var retries = 0;
            while (UiaWarmupPolicy.ShouldRetry(warmupProbe, walk.Elements, retries) &&
                   clock.ElapsedMilliseconds < options.TotalTimeoutMilliseconds)
            {
                var remaining = (int)Math.Max(0, options.TotalTimeoutMilliseconds - clock.ElapsedMilliseconds);
                Thread.Sleep(Math.Min(UiaWarmupPolicy.DelayMilliseconds, remaining));
                sink.BeginAttempt();
                var next = WalkOnce(window, options, budget, clock, sink);
                retries++;
                var grew = next.Elements > walk.Elements;
                walk = next;
                if (!grew) break;
            }
            sink.Complete();
        }
        catch (Exception error)
        {
            sink.Fail(error);
        }
    }

    private static WalkResult WalkOnce(
        nint window,
        UiaSnapshotOptions options,
        UiaObserveBudget budget,
        Stopwatch clock,
        SnapshotBuffer sink)
    {
        var count = 0;
        var patternProbes = 0;
        var livePatternQueries = 0;
        var liveReads = 0;
        var truncation = UiaTruncationReason.None;
        // 一次性缓存所需属性与模式，避免逐元素跨进程往返（主要是 .Current 与 GetSupportedPatterns 的开销）。
        var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        request.Add(AutomationElement.IsPasswordProperty);
        request.Add(AutomationElement.ProcessIdProperty);
        // WM_GETFONT 兜底要用原生句柄；放进缓存请求里读取，避免逐元素跨进程往返。
        request.Add(AutomationElement.NativeWindowHandleProperty);
        // ValuePattern 始终缓存；模式集合只在调用方要 patterns[] 时才加进请求，避免给覆盖层重建增加无用负载。
        request.Add(ValuePattern.Pattern);
        if (options.IncludePatterns)
        {
            foreach (var pattern in CachedPatterns)
            {
                if (pattern != ValuePattern.Pattern) request.Add(pattern);
            }
        }

        // Activate 返回作用域令牌，释放时恢复调用方原有的缓存请求。
        using (request.Activate())
        {
            // 根元素也必须在缓存作用域内创建，否则它和后续节点的 Cached 都会退化为实时读取。
            AutomationElement root;
            try
            {
                root = AutomationElement.FromHandle(window) ?? throw new InvalidOperationException("无法读取目标窗口的 UIA 信息");
            }
            catch (ElementNotAvailableException error)
            {
                // 窗口在这两步之间关闭；转成 InvalidOperationException，重建流程按“窗口不可用”降级。
                throw new InvalidOperationException("目标窗口已关闭或暂不可用", error);
            }
            if (TryReadNode(root, out var rootInfo) == NodeRead.Failed) throw new InvalidOperationException("无法读取目标窗口的 UIA 信息");
            var title = rootInfo.Name;
            if (!string.IsNullOrEmpty(title)) sink.SetTitle(title);

            var pending = new Queue<Pending>();
            pending.Enqueue(new Pending(root, ElementId.Create(options.Generation, "0"), null, 0));
            while (pending.Count > 0)
            {
                if (count >= options.MaxNodes)
                {
                    truncation = UiaTruncationReason.NodeLimit;
                    break;
                }
                var decision = budget.Check(clock.ElapsedMilliseconds);
                if (decision == UiaBudgetDecision.StopTimeLimit)
                {
                    truncation = UiaTruncationReason.TimeLimit;
                    break;
                }
                if (decision == UiaBudgetDecision.Extend) continue;

                var node = pending.Dequeue();
                var read = TryReadNode(node.Element, out var info);
                if (read == NodeRead.Failed) continue;
                // 根元素与少数不认缓存请求的 provider 会走实时读取；这是批取层唯一的逐节点兜底。
                if (read == NodeRead.Live) liveReads++;

                var bounds = info.Bounds;
                var rect = new RectInt(
                    (int)Math.Round(bounds.X),
                    (int)Math.Round(bounds.Y),
                    (int)Math.Round(bounds.Width),
                    (int)Math.Round(bounds.Height));
                var controlType = info.Type?.ProgrammaticName?.Replace("ControlType.", "", StringComparison.Ordinal);
                var included = controlType is { Length: > 0 } && rect.Width > 0 && rect.Height > 0
                    && !double.IsInfinity(bounds.X) && !double.IsInfinity(bounds.Y);

                string? value = null;
                if (included && (controlType is "Edit" or "Document"))
                {
                    try
                    {
                        // 老式 WinForms/MSAA provider 往往不支持缓存模式，退回实时读取，否则会把
                        // 输入框内容误当成关联标签名。
                        object? pattern = null;
                        if (node.Element.TryGetCachedPattern(ValuePattern.Pattern, out var cachedValue))
                        {
                            pattern = cachedValue;
                        }
                        else
                        {
                            livePatternQueries++;
                            if (node.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var currentValue))
                                pattern = currentValue;
                        }
                        if (pattern is ValuePattern valuePattern) value = valuePattern.Current.Value;
                    }
                    catch (Exception error) when (IsUnavailable(error))
                    {
                        value = null;
                    }
                }

                if (included)
                {
                    var patterns = Array.Empty<string>();
                    if (options.IncludePatterns)
                    {
                        var names = new List<string>(CachedPatterns.Length);
                        foreach (var pattern in CachedPatterns)
                        {
                            patternProbes++;
                            if (node.Element.TryGetCachedPattern(pattern, out _)) names.Add(pattern.ProgrammaticName);
                        }
                        if (names.Count > 0) patterns = names.ToArray();
                    }
                    sink.Add(
                        new UiaElementInfo
                        {
                            Id = node.Id,
                            ParentId = node.ParentId,
                            ControlType = controlType!,
                            Name = info.Name,
                            Value = value,
                            Bounds = rect,
                            ProcessId = info.ProcessId,
                            IsPassword = info.IsPassword,
                            HasVisual = !info.IsOffscreen && rect.Width >= 2 && rect.Height >= 2,
                            Patterns = patterns,
                        },
                        node.Element);
                    count++;
                }

                // 不可见且无尺寸的容器不承载可见内容，跳过其子树。
                if (!included && info.IsOffscreen && (rect.Width <= 0 || rect.Height <= 0)) continue;

                if (node.Depth >= options.MaxDepth)
                {
                    try
                    {
                        if (node.Element.FindAll(TreeScope.Children, Automation.ControlViewCondition).Count > 0)
                            truncation = UiaTruncationReason.DepthLimit;
                    }
                    catch (Exception error) when (IsUnavailable(error))
                    {
                        // 分支不可用时忽略深度探测。
                    }
                    continue;
                }

                try
                {
                    // 一次 FindAll 取回该父节点的全部可见子元素，子元素属性随缓存请求一并带回；
                    // 比逐个子元素 GetFirstChild/GetNextSibling + 逐属性实时读取少一个数量级的跨进程往返。
                    var children = node.Element.FindAll(TreeScope.Children, Automation.ControlViewCondition);
                    var childIndex = 0;
                    foreach (AutomationElement child in children)
                    {
                        pending.Enqueue(new Pending(child, $"{node.Id}.{childIndex}", included ? node.Id : node.ParentId, node.Depth + 1));
                        childIndex++;
                    }
                }
                catch (Exception error) when (IsUnavailable(error))
                {
                    // 关闭中的分支失效，不影响已经读取的控件。
                }
            }
        }

        sink.AddProbes(patternProbes, livePatternQueries, liveReads);
        if (truncation != UiaTruncationReason.None) sink.SetTruncation(truncation);
        return new WalkResult(count);
    }

    private enum NodeRead
    {
        Cached,
        Live,
        Failed,
    }

    private readonly record struct NodeInfo(
        string? Name,
        ControlType? Type,
        Rect Bounds,
        bool IsOffscreen,
        bool IsPassword,
        int ProcessId);

    /// <summary>
    /// 优先读缓存值；缓存作用域外的元素（如根元素）或个别属性未被缓存时，
    /// 整节点回退到实时读取，避免把“未缓存”当成“元素不存在”。
    /// </summary>
    private static NodeRead TryReadNode(AutomationElement element, out NodeInfo info)
    {
        try
        {
            info = Read(element.Cached);
            // 缓存没填上控件类型时补一次实时值，否则整棵子树会被当成无类型内容丢掉。
            if (info.Type != null) return NodeRead.Cached;
        }
        catch (Exception error) when (IsUnavailable(error))
        {
        }

        try
        {
            info = Read(element.Current);
            return NodeRead.Live;
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            info = default;
            return NodeRead.Failed;
        }
    }

    private static NodeInfo Read(AutomationElement.AutomationElementInformation info) => new(
        info.Name,
        info.ControlType,
        info.BoundingRectangle,
        info.IsOffscreen,
        info.IsPassword,
        info.ProcessId);

    /// <summary>硬超时且工作线程还没写出窗口信息时的本地兜底：只走 Win32，不再碰 UIA。</summary>
    private static SnapshotHeader FallbackHeader(nint window)
    {
        try
        {
            var description = WindowLocator.Describe(window);
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            NativeMethods.GetWindowRect(window, out var bounds);
            return new SnapshotHeader(
                (int)processId,
                description.ProcessName,
                NativeMethods.WindowClass(window) ?? string.Empty,
                description.Title,
                new RectInt(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return new SnapshotHeader(0, "unknown", string.Empty, "unknown", default);
        }
    }

    private static bool IsUnavailable(Exception error) =>
        error is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException;

    private static string SafeProcessName(int processId)
    {
        try { return Process.GetProcessById(processId).ProcessName; }
        catch (ArgumentException) { return "unknown"; }
    }

    /// <summary>
    /// 工作线程与调用线程之间的交接区：遍历过程中只追加到最后一次尝试的缓冲里，
    /// 正常完成或硬超时时再整体复制一份，避免逐节点复制的开销。
    /// </summary>
    private sealed class SnapshotBuffer : IDisposable
    {
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _done = new(false);
        private List<UiaElementInfo> _elements = new();
        private Dictionary<string, AutomationElement> _native = new(StringComparer.Ordinal);
        private UiaTruncationReason _truncation;
        private SnapshotHeader? _header;
        private ExceptionDispatchInfo? _error;
        private int _patternProbes;
        private int _livePatternQueries;
        private int _liveReads;

        public int PatternProbes { get { lock (_gate) return _patternProbes; } }

        public int LivePatternQueries { get { lock (_gate) return _livePatternQueries; } }

        public int LivePropertyReads { get { lock (_gate) return _liveReads; } }

        public ExceptionDispatchInfo? Error { get { lock (_gate) return _error; } }

        public void SetHeader(SnapshotHeader header)
        {
            lock (_gate) _header = header;
        }

        public void SetTitle(string title)
        {
            lock (_gate)
            {
                if (_header != null) _header = _header with { Title = title };
            }
        }

        public void BeginAttempt()
        {
            lock (_gate)
            {
                _elements = new List<UiaElementInfo>();
                _native = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
                _truncation = UiaTruncationReason.None;
            }
        }

        public void Add(UiaElementInfo element, AutomationElement native)
        {
            lock (_gate)
            {
                _elements.Add(element);
                _native[element.Id] = native;
            }
        }

        public void AddProbes(int patternProbes, int livePatternQueries, int liveReads)
        {
            lock (_gate)
            {
                _patternProbes += patternProbes;
                _livePatternQueries += livePatternQueries;
                _liveReads += liveReads;
            }
        }

        public void SetTruncation(UiaTruncationReason reason)
        {
            lock (_gate)
            {
                if (_truncation == UiaTruncationReason.None) _truncation = reason;
            }
        }

        public void Complete() => _done.Set();

        public void Fail(Exception error)
        {
            lock (_gate) _error = ExceptionDispatchInfo.Capture(error);
            _done.Set();
        }

        public bool Wait(int milliseconds) => _done.Wait(milliseconds);

        public BufferCapture Take(bool timedOut)
        {
            lock (_gate)
            {
                var truncation = timedOut && _truncation == UiaTruncationReason.None
                    ? UiaTruncationReason.TimeLimit
                    : _truncation;
                return new BufferCapture(
                    _header,
                    _elements.ToArray(),
                    new Dictionary<string, AutomationElement>(_native, StringComparer.Ordinal),
                    truncation);
            }
        }

        public void Dispose() => _done.Dispose();
    }
}
