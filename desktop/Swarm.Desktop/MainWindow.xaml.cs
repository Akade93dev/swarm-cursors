using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Swarm.Core.Diagnostics;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TaskCompletionSource _rendererReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _rebuildReady;
    private readonly DesktopTaskService _tasks;
    private readonly DesktopIpcServer _ipcServer;
    private nint _handle;
    private nint _returnFocusWindow;
    private nint _keyboardHook;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private bool _transition;
    private bool _active;
    private bool _closed;
    private OverlayMode _overlayMode;
    private string? _overlayKey;
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
            var environment = await CoreWebView2Environment.CreateAsync(null, dataDirectory);
            await WebView.EnsureCoreWebView2Async(environment);
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.WebMessageReceived += OnWebMessage;
            WebView.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) _rendererReady.TrySetException(new InvalidOperationException($"渲染页面加载失败：{args.WebErrorStatus}"));
            };
            var rendererDirectory = Path.Combine(AppContext.BaseDirectory, "renderer");
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping("swarm.desktop", rendererDirectory, CoreWebView2HostResourceAccessKind.Allow);
            WebView.Source = new Uri("https://swarm.desktop/index.html");
            _ipcServer.Start();
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

    private async Task<string> HandleIpc(string line, int processId)
    {
        var operation = Dispatcher.InvokeAsync(() => _tasks.HandleJsonAsync(line, processId));
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
        SetClickThrough(true);
        const uint altShiftNoRepeat = 0x0001 | 0x0004 | 0x4000;
        var unavailable = new List<string>();
        if (!NativeMethods.RegisterHotKey(_handle, 1, altShiftNoRepeat, 0x53)) unavailable.Add("Alt+Shift+S（重建）");
        if (!NativeMethods.RegisterHotKey(_handle, 2, altShiftNoRepeat, 0x55)) unavailable.Add("Alt+Shift+U（任务重建）");
        // 全局硬停：Ctrl+Alt+Shift+Esc，不依赖目标是否在前台；注册失败必须明确告警，不能静默。
        if (!NativeMethods.RegisterHotKey(_handle, 4, 0x0001 | 0x0002 | 0x0004 | 0x4000, 0x1B)) unavailable.Add("Ctrl+Alt+Shift+Esc（急停）");
        if (unavailable.Count > 0)
        {
            var detail = string.Join("、", unavailable);
            FileLog.Write("desktop", $"hotkey unavailable: {detail}");
            _hotkeyWarning = $"以下热键被其他程序占用，相关功能暂不可用：{detail}。\r\n请关闭占用程序后重启 Swarm Desktop。";
        }
        _keyboardProc = KeyboardHook;
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _keyboardProc, NativeMethods.GetModuleHandle(null), 0);
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != NativeMethods.WmHotKey) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case 1:
                _ = Dispatcher.InvokeAsync(() => ToggleRebuild(false));
                break;
            case 2:
                _ = Dispatcher.InvokeAsync(() => ToggleRebuild(true));
                break;
            case 4:
                _ = Dispatcher.InvokeAsync(StopOverlay);
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 低层键盘钩子：只有当目标窗口（或重建覆盖层自身）在前台时才消费 Esc，
    /// 其它程序的 Esc 原样放行；全局急停由 Ctrl+Alt+Shift+Esc 热键负责。
    /// </summary>
    private nint KeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && (int)wParam is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KeyboardHookInfo>(lParam);
            if (info.VirtualKey == 0x1B && ShouldConsumeEscape())
            {
                _ = Dispatcher.InvokeAsync(StopOverlay);
                return 1;
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private bool ShouldConsumeEscape()
    {
        if (_tasks.TargetOwnsForeground) return true;
        return _overlayMode == OverlayMode.Rebuild && NativeMethods.GetForegroundWindow() == _handle;
    }

    private async Task ToggleRebuild(bool semantic)
    {
        if (_transition) return;
        if (_active)
        {
            StopRebuild();
            return;
        }
        _transition = true;
        try
        {
            await _rendererReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == 0 || foreground == _handle) return;
            var capture = ScreenCapture.CaptureForWindow(foreground);
            DesktopRebuild rebuild;
            string? notice = null;
            if (semantic)
            {
                if (NativeMethods.IsDesktopShell(foreground))
                {
                    // 桌面壁纸的 UIA 树只有壁纸文字，直接截屏，也省掉一次超时遍历。
                    rebuild = DesktopPayload.Screenshot(capture);
                    notice = Describe(RebuildStrategy.ShellDesktop());
                }
                else
                {
                    try
                    {
                        var snapshot = await Task.Run(() => UiaSnapshotReader.Read(foreground));
                        var (candidate, decision) = DesktopPayload.Semantic(capture, snapshot);
                        notice = Describe(decision);
                        rebuild = decision.IsFallback ? DesktopPayload.Screenshot(capture) : candidate;
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
                    {
                        rebuild = DesktopPayload.Screenshot(capture);
                        notice = "截屏重建 · 无法读取该窗口的辅助功能信息";
                    }
                }
            }
            else
            {
                rebuild = DesktopPayload.Screenshot(capture);
            }

            await ShowWhenReady(capture, rebuild);
            if (notice != null) PostMessage(new { type = "status", text = notice, duration = 6000 });
        }
        catch (Exception error)
        {
            FileLog.Write("desktop", $"rebuild failed: {error.Message}");
            StopRebuild();
            MessageBox.Show(error.Message, "Swarm 桌面重建失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _transition = false;
        }
    }

    private static string Describe(RebuildDecision decision) => decision switch
    {
        { Mode: RebuildMode.Semantic, Truncated: true } => $"语义重建 · {decision.NamedControls} 个控件（读取超时，结果不完整）",
        { Mode: RebuildMode.Semantic } => $"语义重建 · {decision.NamedControls} 个控件",
        { Reason: RebuildFallbackReason.ShellDesktop } => "截屏重建 · 桌面由壁纸渲染，直接使用截屏",
        { Reason: RebuildFallbackReason.SparseSemantics } => $"截屏重建 · 该窗口未提供足够的辅助功能信息（{decision.NamedControls} 个控件）",
        _ => "截屏重建 · 该窗口未提供辅助功能信息",
    };

    public void ShowTaskOverlay(RectInt monitor, double dpi)
    {
        _overlayMode = OverlayMode.Task;
        ApplyGeometry(monitor, dpi);
        SetClickThrough(true);
    }

    public void HideTaskOverlay()
    {
        if (_overlayMode != OverlayMode.Task) return;
        if (_active)
        {
            // 重建视图仍在，任务结束后把交互权还给重建覆盖层。
            _overlayMode = OverlayMode.Rebuild;
            SetClickThrough(false);
            return;
        }
        _overlayMode = OverlayMode.None;
        HideOverlay();
    }

    public void StopRebuild()
    {
        if (!_active) return;
        _active = false;
        PostMessage(new { type = "rebuild-stop" });
        PostMessage(new { type = "action-clear" });
        if (_tasks.IsActive)
        {
            // 任务仍在进行：保留覆盖层承载 ops 面板，继续点击穿透。
            _overlayMode = OverlayMode.Task;
            SetClickThrough(true);
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
        _returnFocusWindow = capture.SourceWindow;
        if (_tasks.IsActive)
        {
            // 任务期间的重建只作可视化：不抢前台、不接管点击，避免暂停用户对真实窗口的操作。
            ApplyGeometry(capture.Monitor, capture.DpiScale);
            SetClickThrough(true);
            return;
        }
        _overlayMode = OverlayMode.Rebuild;
        ApplyGeometry(capture.Monitor, capture.DpiScale);
        SetClickThrough(false);
        Activate();
    }

    /// <summary>等渲染层挂载完图块再现出覆盖层，避免首帧只看到空白背景。</summary>
    private async Task ShowWhenReady(CapturedScreen capture, DesktopRebuild rebuild)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rebuildReady = ready;
        PostMessage(rebuild.Message);
        await Task.WhenAny(ready.Task, Task.Delay(800));
        _rebuildReady = null;
        ShowRebuild(capture);
    }

    /// <summary>
    /// 用物理像素定位覆盖层，避免混合 DPI 多显示器下 WPF 的 DIP 换算错位；
    /// 同一显示器与 DPI 不重复应用，防止每次 observe 重置窗口并触发重建重启。
    /// </summary>
    private void ApplyGeometry(RectInt monitor, double dpi)
    {
        if (_handle == 0) return;
        var key = $"{monitor.X},{monitor.Y},{monitor.Width},{monitor.Height},{dpi:0.###}";
        if (_overlayKey == key && IsVisible) return;
        _overlayKey = key;
        Width = monitor.Width / dpi;
        Height = monitor.Height / dpi;
        Opacity = 1;
        if (!IsVisible) Show();
        NativeMethods.SetWindowPos(_handle, -1, monitor.X, monitor.Y, monitor.Width, monitor.Height, NativeMethods.SwpNoActivate | 0x0040);
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
        Opacity = 0;
        Left = -10000;
        Top = -10000;
        Width = 1;
        Height = 1;
        if (_handle != 0) NativeMethods.SetWindowPos(_handle, 0, -10000, -10000, 1, 1, NativeMethods.SwpNoActivate);
        SetClickThrough(true);
        var returnFocus = _returnFocusWindow;
        _returnFocusWindow = 0;
        if (NativeMethods.GetForegroundWindow() == _handle && NativeMethods.IsWindow(returnFocus))
            WindowLocator.Activate(returnFocus);
    }

    private void SetClickThrough(bool clickThrough)
    {
        if (_handle == 0) return;
        var style = NativeMethods.GetWindowLong32(_handle, -20);
        style |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        style = clickThrough ? style | NativeMethods.WsExTransparent : style & ~NativeMethods.WsExTransparent;
        NativeMethods.SetWindowLong32(_handle, -20, style);
        NativeMethods.SetWindowPos(_handle, 0, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            switch (type.GetString())
            {
                case "ready":
                    _rendererReady.TrySetResult();
                    break;
                case "rebuild-ready":
                    _rebuildReady?.TrySetResult();
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
                case "renderer-error":
                    var error = root.TryGetProperty("error", out var detail) ? detail.GetString() : "未知渲染错误";
                    // 视觉层故障不终止 AI 操作，只降级提示。
                    PostMessage(new { type = "task-status", text = $"可视化不可用：{error}" });
                    break;
            }
        }
        catch (JsonException)
        {
            // Malformed renderer messages cannot authorize a native action.
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
            WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
        }
        catch (InvalidOperationException)
        {
            // WebView2 正在销毁，视觉消息丢失不影响已执行的操作。
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _tasks.StopLocal();
        NativeMethods.UnregisterHotKey(_handle, 1);
        NativeMethods.UnregisterHotKey(_handle, 2);
        NativeMethods.UnregisterHotKey(_handle, 4);
        if (_keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
        if (_handle != 0) NativeMethods.SetWindowDisplayAffinity(_handle, 0);
        await _ipcServer.StopAsync();
    }
}
