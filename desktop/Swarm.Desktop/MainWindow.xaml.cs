using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Swarm.Core.Diagnostics;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    /// <summary>后台重建总预算：超时降级为仅截图，绝不让 UIA/编码把输入卡住。</summary>
    private static readonly TimeSpan RebuildBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// IA2 读取预算：与 UIA 并行读取，正常会在 UIA 返回前完成。
    /// 返回后最多再等 500ms，避免个别窗口的 COM 卡顿把重建拖慢。
    /// </summary>
    private static readonly TimeSpan Ia2Budget = TimeSpan.FromMilliseconds(500);
    private readonly TaskCompletionSource _rendererReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? _rebuildReady;
    private bool _rebuildRenderFailed;
    private CancellationTokenSource? _rebuildCts;
    private readonly DesktopTaskService _tasks;
    private readonly DesktopIpcServer _ipcServer;
    private readonly DesktopControlServer _controlServer;
    private readonly DispatcherTimer _pointerPump;
    private nint _handle;
    private nint _keyboardHook;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private bool _transition;
    private bool _active;
    private bool _closed;
    private OverlayMode _overlayMode;
    private string? _overlayKey;
    private double _overlayDpi = 1;
    private bool _pointerPosted;
    private int _pointerX;
    private int _pointerY;
    private Rect _hudRect;
    private bool _hudRectValid;
    private NativeMethods.LowLevelKeyboardProc? _mouseProc;
    private nint _mouseHook;
    private string? _hotkeyWarning;

    private enum OverlayMode
    {
        None,
        Rebuild,
        Task,
    }

    public MainWindow()
    {
        InitializeComponent();
        _tasks = new DesktopTaskService(this);
        _ipcServer = new DesktopIpcServer(HandleIpc);
        _controlServer = new DesktopControlServer(() => _tasks.CancelCurrent());
        _pointerPump = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _pointerPump.Tick += (_, _) => SamplePointer();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public nint NativeHandle => _handle;
    public Task RendererReady => _rendererReady.Task;
    public bool RendererAvailable => !_closed && _rendererReady.Task.IsCompletedSuccessfully;

    /// <summary>渲染层不可用时立即返回，避免视觉故障把每个操作阻塞 10 秒。</summary>
    public async Task TryWaitRendererAsync(TimeSpan timeout)
    {
        if (_closed || _rendererReady.Task.IsCompleted) return;
        await Task.WhenAny(_rendererReady.Task, Task.Delay(timeout));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwarmCursors", "WebView2");
            Directory.CreateDirectory(dataDirectory);
            // 桌面覆盖层是点击穿透的，收不到真实 pointerdown；AudioContext 会被 Chromium
            // 自动播放策略卡在 suspended 态，导致整个语义重建无声。显式放开自动播放，让上下文
            // 一创建就是 running，JS 侧 unlockSound 作为键盘路径的兜底。
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            };
            var environment = await CoreWebView2Environment.CreateAsync(null, dataDirectory, options);
            await WebView.EnsureCoreWebView2Async(environment);
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.WebMessageReceived += OnWebMessage;
            WebView.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) _rendererReady.TrySetException(new InvalidOperationException($"渲染页面加载失败：{args.WebErrorStatus}"));
            };
            var rendererDirectory = Path.Combine(AppContext.BaseDirectory, "renderer");
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping("swarm.desktop", rendererDirectory, CoreWebView2HostResourceAccessKind.Allow);
            // 虚拟主机映射同样会进 Chromium 磁盘缓存：升级程序后旧的 CSS/JS 仍会生效，
            // 渲染层修复看起来“没生效”。启动时清一次磁盘缓存，保证随程序发布的渲染文件一定是最新的。
            await WebView.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            WebView.Source = new Uri("https://swarm.desktop/index.html");
            _ipcServer.Start();
            _controlServer.Start();
            if (_hotkeyWarning is not null)
                MessageBox.Show(this, _hotkeyWarning, "Swarm Desktop 热键不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("未找到 WebView2 Runtime。请安装 Microsoft Edge WebView2 Runtime 后重试。", "Swarm Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            _rendererReady.TrySetException(error);
            FileLog.Write("desktop", $"renderer init failed: {error}");
            MessageBox.Show(error.Message, "Swarm Desktop 初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<string> HandleIpc(string line, int processId, Func<double, string?, Task> progress)
    {
        var operation = Dispatcher.InvokeAsync(() => _tasks.HandleJsonAsync(line, processId, progress));
        var request = await operation.Task;
        return await request;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_handle);
        source.AddHook(WindowMessage);
        // 仅测试用：置 1 时允许截图工具拍到覆盖层，用于验证首帧是否空白。
        var capturable = Environment.GetEnvironmentVariable("SWARM_CAPTURE_OVERLAY") == "1";
        NativeMethods.SetWindowDisplayAffinity(_handle, capturable ? 0u : 0x11u);
        EnsureClickThrough();
        const uint altShiftNoRepeat = 0x0001 | 0x0004 | 0x4000;
        var unavailable = new List<string>();
        if (!NativeMethods.RegisterHotKey(_handle, 1, altShiftNoRepeat, 0x53)) unavailable.Add("Alt+Shift+S（重建）");
        if (!NativeMethods.RegisterHotKey(_handle, 2, altShiftNoRepeat, 0x55)) unavailable.Add("Alt+Shift+U（任务重建）");
        // 全局硬停：Ctrl+Alt+Shift+Esc，不依赖目标是否在前台；注册失败必须明确告警，不能静默。
        if (!NativeMethods.RegisterHotKey(_handle, 4, 0x0001 | 0x0002 | 0x0004 | 0x4000, 0x1B)) unavailable.Add("Ctrl+Alt+Shift+Esc（急停）");
        if (!NativeMethods.RegisterHotKey(_handle, 5, 0x0001 | 0x0002 | 0x0004 | 0x4000, 0x52)) unavailable.Add("Ctrl+Alt+Shift+R（恢复 AI 控制）");
        if (unavailable.Count > 0)
        {
            var detail = string.Join("、", unavailable);
            FileLog.Write("desktop", $"hotkey unavailable: {detail}");
            _hotkeyWarning = $"以下热键被其他程序占用，相关功能暂不可用：{detail}。\r\n请关闭占用程序后重启 Swarm Desktop。";
        }
        _keyboardProc = KeyboardHook;
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _keyboardProc, NativeMethods.GetModuleHandle(null), 0);
        _mouseProc = MouseHook;
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _mouseProc, NativeMethods.GetModuleHandle(null), 0);
        if (_mouseHook == 0) FileLog.Write("desktop", "mouse hook unavailable: 控制条点击将不可用");
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != NativeMethods.WmHotKey) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case 1:
                // 主重建热键与任务重建一致：都走 UIA 语义重建，
                // 否则应用窗口下只是把截图原样贴回，画面和真实屏幕一样，看起来像没响应。
                _ = Dispatcher.InvokeAsync(() => ToggleRebuild(true));
                break;
            case 2:
                _ = Dispatcher.InvokeAsync(() => ToggleRebuild(true));
                break;
            case 4:
                _ = Dispatcher.InvokeAsync(StopOverlay);
                break;
            case 5:
                _ = Dispatcher.InvokeAsync(_tasks.ResumeLocal);
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 低层键盘钩子：只有目标窗口在前台时才消费 Esc，其它程序的 Esc 原样放行；
    /// 全局急停由 Ctrl+Alt+Shift+Esc 热键负责。覆盖层永不激活，因此不再参与前台判断。
    /// </summary>
    private nint KeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && (int)wParam is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KeyboardHookInfo>(lParam);
            if (info.ExtraInfo != (nuint)NativeMethods.SwarmInputMarker)
            {
                _tasks.PauseForHumanInput();
                if (info.VirtualKey == 0x1B && ShouldConsumeEscape())
                {
                    _ = Dispatcher.InvokeAsync(StopOverlay);
                    return 1;
                }
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private bool ShouldConsumeEscape() => _tasks.TargetOwnsForeground;

    /// <summary>
    /// 低层鼠标钩子：覆盖层常开点击穿透，页面收不到真实鼠标事件，所以底部控制条由这里接管——
    /// 命中控制条时吞掉这次按下/抬起，转成 hud-input 让渲染层派发 DOM 事件；
    /// 其余位置一律放行，真实点击继续落到下面的窗口。
    /// </summary>
    private nint MouseHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var message = (int)wParam;
            var info = Marshal.PtrToStructure<NativeMethods.MouseHookInfo>(lParam);
            var humanInput = info.ExtraInfo != NativeMethods.SwarmInputMarker && message is
                NativeMethods.WmLButtonDown or NativeMethods.WmLButtonUp or
                NativeMethods.WmRButtonDown or NativeMethods.WmRButtonUp or
                NativeMethods.WmMButtonDown or NativeMethods.WmMButtonUp or
                NativeMethods.WmXButtonDown or NativeMethods.WmXButtonUp or
                NativeMethods.WmMouseWheel or NativeMethods.WmMouseHWheel;
            // 纯鼠标移动不接管；按钮与滚轮立即暂停本代次，AI 注入则凭标记排除。
            if (humanInput) _tasks.PauseForHumanInput();

            // AI 自己注入的点击必须原样落到真实窗口；人工点击 HUD 时仍按原路径转成 DOM 事件。
            if (humanInput
                && _hudRectValid && _overlayMode != OverlayMode.None && IsVisible
                && (message == NativeMethods.WmLButtonDown || message == NativeMethods.WmLButtonUp)
                && TryCssPointer(out var x, out var y) && _hudRect.Contains(x, y))
            {
                PostMessage(new { type = "hud-input", action = message == NativeMethods.WmLButtonDown ? "down" : "up", x, y });
                return 1;
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private async Task ToggleRebuild(bool semantic)
    {
        if (_closed) return;
        if (_transition)
        {
            // 还在准备阶段时再按一次热键 = 取消本次重建，按键始终有反馈。
            _rebuildCts?.Cancel();
            return;
        }
        if (_active)
        {
            StopRebuild();
            return;
        }
        _transition = true;
        var cts = new CancellationTokenSource();
        _rebuildCts = cts;
        try
        {
            await _rendererReady.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            if (_closed) return;
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == _handle) return;
            if (foreground == 0)
            {
                // 显示桌面后系统可能没有前台窗口：按主屏整幅实拍还原壁纸，而不是静默无响应。
                FileLog.Write("desktop", "rebuild: no foreground window, capturing desktop");
                semantic = false;
            }

            // 截屏、UIA 遍历、GDI 度量与 PNG/Base64 编码全部在后台线程完成；
            // UI 线程只负责投递消息与显示覆盖层，重建期间键盘输入不再被同步等待。
            var outcome = await Task.Run(() => BuildRebuild(foreground, semantic, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            DumpRebuild(outcome.Rebuild);
            if (!await ShowWhenReady(outcome.Capture, outcome.Rebuild, cts.Token)) return;
            if (outcome.Notice != null) PostMessage(new { type = "status", text = outcome.Notice, duration = 6000 });
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("desktop", "rebuild canceled");
        }
        catch (Exception error)
        {
            FileLog.Write("desktop", $"rebuild failed: {error.Message}");
            if (!_closed) MessageBox.Show(error.Message, "Swarm 桌面重建失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (ReferenceEquals(_rebuildCts, cts)) _rebuildCts = null;
            _transition = false;
        }
    }

    private sealed record RebuildOutcome(CapturedScreen Capture, DesktopRebuild Rebuild, string? Notice);

    /// <summary>后台重建流水线：抓一次屏；语义读取超时时复用同一帧降级，避免二次抓屏与双份编码。</summary>
    private static RebuildOutcome BuildRebuild(nint foreground, bool semantic, CancellationToken token)
    {
        var capture = ScreenCapture.CaptureForWindow(foreground);
        token.ThrowIfCancellationRequested();
        if (!semantic) return new RebuildOutcome(capture, DesktopPayload.Screenshot(capture), null);
        if (NativeMethods.IsDesktopShell(foreground))
        {
            // 桌面壁纸的 UIA 树只有壁纸文字，直接截屏，也省掉一次超时遍历。
            return new RebuildOutcome(capture, DesktopPayload.Screenshot(capture), Describe(RebuildStrategy.ShellDesktop()));
        }
        try
        {
            var snapshotTask = Task.Run(() => UiaSnapshotReader.Read(foreground), token);
            // IA2 与 UIA 是两条独立通道：并行读取，IA2 只负责把命中元素的字符几何补精确。
            var ia2Task = Task.Run(() => Ia2TextReader.Read(foreground, capture.WorkArea), token);
            UiaSnapshot snapshot;
            try
            {
                snapshot = snapshotTask.WaitAsync(RebuildBudget, token).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                // 放弃等待后仍观察一次异常，避免后台任务把未处理异常留到进程级。
                _ = snapshotTask.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                _ = ia2Task.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return new RebuildOutcome(capture, DesktopPayload.Screenshot(capture), "截屏重建 · 语义读取超时，已降级为截图");
            }
            token.ThrowIfCancellationRequested();
            var ia2 = AwaitIa2(ia2Task, token);
            var (candidate, decision) = DesktopPayload.Semantic(capture, snapshot, ia2);
            var rebuild = decision.IsFallback ? DesktopPayload.Screenshot(capture) : candidate;
            return new RebuildOutcome(capture, rebuild, Describe(decision));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return new RebuildOutcome(capture, DesktopPayload.Screenshot(capture), "截屏重建 · 无法读取该窗口的辅助功能信息");
        }
    }

    /// <summary>
    /// 等 IA2 结果，但绝不超过 <see cref="Ia2Budget"/>：读不到或超时就返回 null，
    /// 由 <see cref="DesktopPayload.Semantic"/> 走纯 UIA 路径，重建本身不受影响。
    /// </summary>
    private static Ia2Snapshot? AwaitIa2(Task<Ia2Snapshot> task, CancellationToken token)
    {
        try
        {
            var snapshot = task.WaitAsync(Ia2Budget, token).GetAwaiter().GetResult();
            FileLog.Write(
                "desktop",
                $"[ia2] lines={snapshot.Lines.Count} visited={snapshot.Visited} textNodes={snapshot.TextNodes} " +
                $"glyphs={snapshot.Glyphs} ms={snapshot.ElapsedMilliseconds} timedOut={snapshot.TimedOut}");
            return snapshot;
        }
        catch (TimeoutException)
        {
            _ = task.ContinueWith(static item => _ = item.Exception, TaskContinuationOptions.OnlyOnFaulted);
            FileLog.Write("desktop", "[ia2] timeout, fallback to uia");
            return null;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            FileLog.Write("desktop", $"[ia2] failed: {error.Message}, fallback to uia");
            return null;
        }
    }

    /// <summary>
    /// 调试/探针钩子：设置 SWARM_REBUILD_DUMP 指向文件时，把本次重建载荷原样落盘，
    /// 供黄金探针断言字体与边框，而不是靠肉眼比对截图。
    /// </summary>
    private static void DumpRebuild(DesktopRebuild rebuild)
    {
        var path = Environment.GetEnvironmentVariable("SWARM_REBUILD_DUMP");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(rebuild.Message, JsonOptions));
            FileLog.Write("desktop", $"rebuild dump={path} elements={rebuild.ElementCount}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            FileLog.Write("desktop", $"rebuild dump failed: {error.Message}");
        }
    }

    private static string Describe(RebuildDecision decision) => decision switch
    {
        { Reason: RebuildFallbackReason.FrameOnly } => $"框架重建 · 该窗口未开放辅助功能，内容按原像素还原（{decision.NamedControls} 个控件）",
        { Mode: RebuildMode.Semantic, Truncation: UiaTruncationReason.DepthLimit } => $"语义重建 · {decision.NamedControls} 个控件（已达到遍历深度上限，结果不完整）",
        { Mode: RebuildMode.Semantic, Truncation: UiaTruncationReason.NodeLimit } => $"语义重建 · {decision.NamedControls} 个控件（控件数量达到上限，结果不完整）",
        { Mode: RebuildMode.Semantic, Truncation: UiaTruncationReason.TimeLimit } => $"语义重建 · {decision.NamedControls} 个控件（读取超时，结果不完整）",
        { Mode: RebuildMode.Semantic } => $"语义重建 · {decision.NamedControls} 个控件",
        { Reason: RebuildFallbackReason.ShellDesktop } => "截屏重建 · 桌面由壁纸渲染，直接使用截屏",
        { Reason: RebuildFallbackReason.SparseSemantics } => $"截屏重建 · 该窗口未提供足够的辅助功能信息（{decision.NamedControls} 个控件）",
        _ => "截屏重建 · 该窗口未提供辅助功能信息",
    };

    public void ShowTaskOverlay(RectInt monitor, double dpi)
    {
        _overlayMode = OverlayMode.Task;
        ApplyGeometry(monitor, dpi);
        EnsureClickThrough();
    }

    public void HideTaskOverlay()
    {
        if (_overlayMode != OverlayMode.Task) return;
        if (_active)
        {
            // 重建视图仍在，任务结束后回到重建态的可见性（点击穿透始终不变）。
            _overlayMode = OverlayMode.Rebuild;
            return;
        }
        _overlayMode = OverlayMode.None;
        HideOverlay();
    }

    public void StopRebuild()
    {
        // 预显示阶段（_active 尚未置位）也必须能取消，否则 Esc/再次热键会空转。
        _rebuildCts?.Cancel();
        _rebuildCts = null;
        if (!_active) return;
        _active = false;
        PostMessage(new { type = "rebuild-stop" });
        PostMessage(new { type = "action-clear" });
        if (_tasks.IsActive)
        {
            // 任务仍在进行：保留覆盖层承载 ops 面板，继续点击穿透。
            _overlayMode = OverlayMode.Task;
            return;
        }
        _overlayMode = OverlayMode.None;
        HideOverlay();
    }

    public bool ConfirmLocal(string title, string message, bool danger)
    {
        const uint yesNo = 0x4;
        const uint iconQuestion = 0x20;
        const uint iconWarning = 0x30;
        const uint setForeground = 0x10000;
        const uint topmost = 0x40000;
        const uint defaultNo = 0x100;
        var result = NativeMethods.MessageBox(_handle, message, title, yesNo | (danger ? iconWarning | defaultNo : iconQuestion) | setForeground | topmost);
        return result == 6;
    }

    private void ShowRebuild(CapturedScreen capture)
    {
        _active = true;
        // 覆盖层只负责显示：始终点击穿透、从不激活，用户的真实窗口保持前台和输入权。
        _overlayMode = _tasks.IsActive ? OverlayMode.Task : OverlayMode.Rebuild;
        _hudRectValid = false;
        ApplyGeometry(capture.WorkArea, capture.DpiScale);
        EnsureClickThrough();
    }

    /// <summary>等渲染层挂载完图块再现出覆盖层，避免首帧只看到空白背景。</summary>
    private async Task<bool> ShowWhenReady(CapturedScreen capture, DesktopRebuild rebuild, CancellationToken token)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rebuildReady = ready;
        _rebuildRenderFailed = false;
        PostMessage(rebuild.Message);
        var completed = await Task.WhenAny(ready.Task, Task.Delay(800, token));
        _rebuildReady = null;
        if (completed == ready.Task)
        {
            if (!await ready.Task)
            {
                FileLog.Write("desktop", "rebuild rejected by renderer");
                return false;
            }
        }
        else if (_rebuildRenderFailed)
        {
            // 渲染层已明确报错，再显示只会留下一块空白覆盖层。
            FileLog.Write("desktop", "rebuild renderer failed before first frame");
            return false;
        }
        token.ThrowIfCancellationRequested();
        if (_closed) return false;
        ShowRebuild(capture);
        return true;
    }

    /// <summary>
    /// 用物理像素定位覆盖层，避免混合 DPI 多显示器下 WPF 的 DIP 换算错位；
    /// 同一显示器与 DPI 不重复应用，防止每次 observe 重置窗口并触发重建重启。
    /// </summary>
    private void ApplyGeometry(RectInt monitor, double fallbackDpi)
    {
        if (_handle == 0) return;
        // 覆盖层只铺满工作区：盖住任务栏会让它在重建期间既看不见也点不到。
        var work = MonitorWorkArea(monitor);
        // 覆盖层的 CSS 视口按自己所在显示器的 DPI 换算；目标窗口若 DPI 不感知（GetDpiForWindow 返回 96），
        // 用它的 DPI 算指针会按错误比例放大，离屏幕原点越远偏移越大。
        var dpi = MonitorDpi(monitor, fallbackDpi);
        var key = $"{work.X},{work.Y},{work.Width},{work.Height},{dpi:0.###}";
        if (_overlayKey == key && IsVisible) return;
        _overlayKey = key;
        _overlayDpi = dpi;
        _pointerPosted = false;
        Width = work.Width / dpi;
        Height = work.Height / dpi;
        Opacity = 1;
        if (!IsVisible) Show();
        NativeMethods.SetWindowPos(_handle, -1, work.X, work.Y, work.Width, work.Height, NativeMethods.SwpNoActivate | 0x0040);
        _pointerPump.Start();
        FileLog.Write("desktop", $"overlay work={work.X},{work.Y} {work.Width}x{work.Height} dpi={dpi:0.###} css={Width:0}x{Height:0}");
    }

    /// <summary>取显示器工作区（不含任务栏）；读取失败时退回整个显示器。</summary>
    private static RectInt MonitorWorkArea(RectInt monitor)
    {
        var rect = new NativeMethods.Win32Rect
        {
            Left = monitor.X,
            Top = monitor.Y,
            Right = monitor.X + monitor.Width,
            Bottom = monitor.Y + monitor.Height,
        };
        var handle = NativeMethods.MonitorFromRect(ref rect, 2);
        if (handle == 0) return monitor;
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(handle, ref info)) return monitor;
        var work = info.WorkArea;
        if (work.Right <= work.Left || work.Bottom <= work.Top) return monitor;
        return new RectInt(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    private static double MonitorDpi(RectInt monitor, double fallback)
    {
        var rect = new NativeMethods.Win32Rect
        {
            Left = monitor.X,
            Top = monitor.Y,
            Right = monitor.X + monitor.Width,
            Bottom = monitor.Y + monitor.Height,
        };
        var handle = NativeMethods.MonitorFromRect(ref rect, 2);
        if (handle != 0 && NativeMethods.GetDpiForMonitor(handle, 0, out var dpiX, out _) == 0 && dpiX > 0)
            return dpiX / 96.0;
        return fallback > 0 ? fallback : 1;
    }

    /// <summary>
    /// 点击穿透窗口收不到任何鼠标消息，指针位置只能由宿主采样后推给渲染层：
    /// 30Hz 轮询，只在位置变化时发一条；指针静止或离开本屏时完全不产生消息。
    /// 同一份采样顺带决定底部控制条是否临时接收鼠标。
    /// </summary>
    private void SamplePointer()
    {
        if (_handle == 0 || _overlayMode == OverlayMode.None || !IsVisible) return;
        if (!TryCssPointer(out var x, out var y)) return;
        if (_pointerPosted && x == _pointerX && y == _pointerY) return;
        _pointerPosted = true;
        _pointerX = x;
        _pointerY = y;
        PostMessage(new { type = "pointer", x, y });
    }

    /// <summary>把物理屏幕坐标换算成覆盖层里的 CSS 像素；指针不在覆盖层内时返回 false。</summary>
    private bool TryCssPointer(out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!NativeMethods.GetCursorPos(out var point)) return false;
        if (!NativeMethods.ScreenToClient(_handle, ref point)) return false;
        var dpi = _overlayDpi > 0 ? _overlayDpi : 1;
        x = (int)Math.Round(point.X / dpi);
        y = (int)Math.Round(point.Y / dpi);
        return x >= 0 && y >= 0 && x < Width && y < Height;
    }

    /// <summary>渲染层上报底部控制条的可交互区域（CSS 像素），布局变化时才上报一次。</summary>
    private void ReadHudRect(JsonElement root)
    {
        var valid = false;
        var rect = default(Rect);
        if (root.TryGetProperty("rect", out var value) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("x", out var x) && x.TryGetDouble(out var rx)
            && value.TryGetProperty("y", out var y) && y.TryGetDouble(out var ry)
            && value.TryGetProperty("w", out var w) && w.TryGetDouble(out var rw)
            && value.TryGetProperty("h", out var h) && h.TryGetDouble(out var rh)
            && rw > 0 && rh > 0)
        {
            rect = new Rect(rx, ry, rw, rh);
            valid = true;
        }
        if (valid == _hudRectValid && (!valid || rect == _hudRect)) return;
        _hudRect = rect;
        _hudRectValid = valid;
        FileLog.Write("desktop", valid ? $"hud rect={rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}" : "hud rect=none");
    }

    private void StopOverlay()
    {
        if (_tasks.IsActive)
        {
            _ = _tasks.StopLocalAsync();
            return;
        }
        StopRebuild();
    }

    private void HideOverlay()
    {
        _active = false;
        _overlayMode = OverlayMode.None;
        _overlayKey = null;
        _pointerPump.Stop();
        _pointerPosted = false;
        _hudRectValid = false;
        Opacity = 0;
        Left = -10000;
        Top = -10000;
        Width = 1;
        Height = 1;
        if (_handle != 0) NativeMethods.SetWindowPos(_handle, 0, -10000, -10000, 1, 1, NativeMethods.SwpNoActivate);
        EnsureClickThrough();
    }

    /// <summary>
    /// 覆盖层永不接收鼠标、永不激活：所有真实交互都留给它下面那个窗口。
    /// 底部控制条的点击由鼠标钩子单独接管（见 <see cref="MouseHook"/>）。
    /// </summary>
    private void EnsureClickThrough()
    {
        if (_handle == 0) return;
        var style = NativeMethods.GetWindowLong32(_handle, -20);
        var next = style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent;
        if (next != style) NativeMethods.SetWindowLong32(_handle, -20, next);
        NativeMethods.SetWindowPos(_handle, 0, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            var kind = type.ValueKind == JsonValueKind.String ? type.GetString() : null;
            switch (kind)
            {
                case "ready":
                    _rendererReady.TrySetResult();
                    break;
                case "rebuild-ready":
                    _rebuildReady?.TrySetResult(true);
                    break;
                case "closed":
                    StopRebuild();
                    break;
                case "action-ready":
                    if (root.TryGetProperty("id", out var id)) _tasks.ActionReady(id.GetString() ?? string.Empty);
                    break;
                case "escape":
                    StopOverlay();
                    break;
                case "hud-rect":
                    ReadHudRect(root);
                    break;
                case "renderer-error":
                    var error = root.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.String
                        ? detail.GetString()
                        : "未知渲染错误";
                    // 首帧之前就失败时不要显示空白覆盖层；已显示后只降级提示，不终止 AI 操作。
                    _rebuildRenderFailed = true;
                    _rebuildReady?.TrySetResult(false);
                    PostMessage(new { type = "task-status", text = $"可视化不可用：{error}" });
                    break;
            }
        }
        catch (Exception error)
        {
            // 渲染层消息来自本地页面，但畸形 JSON/类型错误仍不能影响原生输入与重建。
            FileLog.Write("desktop", $"web message ignored: {error.Message}");
        }
    }

    /// <summary>任意线程可调用；WebView2 消息必须回到 UI 线程投递，窗口关闭后直接丢弃。</summary>
    public void PostMessage(object message)
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => PostMessage(message)));
            return;
        }
        if (WebView.CoreWebView2 == null) return;
        try
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            WebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (InvalidOperationException)
        {
            // WebView2 正在销毁，视觉消息丢失不影响已执行的操作。
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _pointerPump.Stop();
        _tasks.StopLocal();
        NativeMethods.UnregisterHotKey(_handle, 1);
        NativeMethods.UnregisterHotKey(_handle, 2);
        NativeMethods.UnregisterHotKey(_handle, 4);
        NativeMethods.UnregisterHotKey(_handle, 5);
        if (_keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
        if (_mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
        if (_handle != 0) NativeMethods.SetWindowDisplayAffinity(_handle, 0);
        await _ipcServer.StopAsync();
        await _controlServer.StopAsync();
    }
}
