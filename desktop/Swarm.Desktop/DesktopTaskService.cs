using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Threading;
using Swarm.Core.Actions;
using Swarm.Core.Capture;
using Swarm.Core.Diagnostics;
using Swarm.Core.Governance;
using Swarm.Core.Operations;
using Swarm.Core.Perception;
using Swarm.Core.Security;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

public sealed class DesktopTaskService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(10);
    private const int WindowDriftThreshold = 8;
    private readonly MainWindow _host;
    private readonly object _sync = new();
    private readonly TaskAuthorization _authorization = new();
    private readonly Dictionary<string, TaskCompletionSource> _pending = new();
    private readonly HashSet<string> _sensitiveElements = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly int _visualLeadMs;
    private readonly int _screenshotMax;
    private readonly double _pixelDiffThreshold;
    private AuthorizedSession? _session;
    private ActionCoordinator? _coordinator;
    private OperationRunner? _operations;
    private TargetIdentity? _target;
    private int _gatewayProcessId;
    private int _generation;
    private bool _stopping;
    private bool _manualTakeover;
    private UiaSnapshot? _lastSnapshot;
    private WindowCapture? _lastCapture;
    private DateTimeOffset _observedAt;
    private long _observationGeneration;
    private bool _maskAllText;
    private TaskPolicy _policy = TaskPolicy.Default;
    private ConfirmationBroker _confirmations = new();
    private AuditWriter? _audit;
    private string? _taskId;
    private string? _targetProcessName;
    private DateTimeOffset _taskStartedAt;
    private int _actionCount;
    private AuditContext _auditContext = new();

    // P1-M Mirror 帧通道: 后台定时器周期性抓帧推给 renderer 做人类 mirror 展示.
    private readonly DispatcherTimer _mirrorPump = new();
    private readonly int _mirrorIntervalMs;
    private readonly SemaphoreSlim _mirrorGate = new(1, 1);  // 防止 encode 慢时 Pump 重入

    // P2 OCR/SoM 视觉兜底: 优先 TesseractOcrService (TesseractOCR 5.5.1 内嵌 Native),
    // tessdata 缺失时自动 fallback 到 NullOcrService (IsAvailable=false, 不 crash).
    private readonly IOcrService _ocrService = ResolveOcrService();

    private static IOcrService ResolveOcrService()
    {
        try { return new Swarm.Desktop.Perception.TesseractOcrService(); }
        catch { return new NullOcrService(); }
    }

    private sealed class NullOcrService : IOcrService
    {
        public bool IsAvailable => false;
        public Task<IReadOnlyList<OcrLine>> OcrAsync(RgbaFrame frame, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OcrLine>>(System.Array.Empty<OcrLine>());
    }

    public DesktopTaskService(MainWindow host)
    {
        _host = host;
        _visualLeadMs = ReadSetting("SWARM_VISUAL_LEAD_MS", 90, 0, 2000);
        _screenshotMax = ReadSetting("SWARM_SCREENSHOT_MAX", 1600, 320, 4096);
        _pixelDiffThreshold = ReadSetting("SWARM_PIXEL_DIFF_PERMILLE", 5, 1, 500) / 1000.0;
        _mirrorIntervalMs = ReadSetting("SWARM_MIRROR_INTERVAL_MS", 200, 50, 2000);
        _watchdog.Tick += (_, _) => _ = CheckGatewayAsync();
        _mirrorPump.Interval = TimeSpan.FromMilliseconds(_mirrorIntervalMs);
        _mirrorPump.Tick += (_, _) => _ = PumpMirrorFrameAsync();
    }

    public bool IsActive
    {
        get
        {
            lock (_sync) return _session != null && !_stopping;
        }
    }

    public bool TargetOwnsForeground
    {
        get
        {
            TargetIdentity? target;
            lock (_sync) target = _target;
            return target != null && InputDispatcher.IsForeground(target.Handle);
        }
    }

    public async Task<string> HandleJsonAsync(string json, int gatewayProcessId, Func<double, string?, Task>? progress = null)
    {
        var id = string.Empty;
        string? method = null;
        var started = DateTimeOffset.Now;
        _auditContext = new AuditContext();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            id = root.GetProperty("id").GetString() ?? throw new InvalidDataException("请求缺少 id");
            method = root.GetProperty("method").GetString() ?? throw new InvalidDataException("请求缺少 method");
            var parameters = root.TryGetProperty("params", out var supplied) && supplied.ValueKind == JsonValueKind.Object
                ? supplied
                : JsonSerializer.SerializeToElement(new { });
            if (method == "task_begin") PrepareAudit();
            if (gatewayProcessId <= 0) throw new UnauthorizedAccessException("无法验证 Gateway 进程");
            // 渲染层不可用时立即降级，动作仍可执行；仅在就绪阶段短暂等待首帧。
            await _host.TryWaitRendererAsync(TimeSpan.FromMilliseconds(400));
            var reporter = new ProgressReporter(progress);
            object result = method switch
            {
                "task_begin" => await Begin(parameters, gatewayProcessId, reporter),
                "observe" => await Observe(parameters, gatewayProcessId, reporter),
                "click" => await Click(parameters, gatewayProcessId, reporter),
                "type_text" => await TypeText(parameters, gatewayProcessId, reporter),
                "press_key" => await PressKey(parameters, gatewayProcessId, reporter),
                "scroll" => await Scroll(parameters, gatewayProcessId, reporter),
                "focus" => await Focus(parameters, gatewayProcessId, reporter),
                "task_end" => await End(Token(parameters), gatewayProcessId, false),
                "task_stop" => await End(Token(parameters), gatewayProcessId, true),
                _ => throw new InvalidOperationException($"未知桌面方法：{method}"),
            };
            return Success(id, result);
        }
        catch (Exception error)
        {
            _auditContext.Result = ErrorCode(error);
            return Failure(id, error);
        }
        finally
        {
            WriteAudit(method, started);
            if (method == "task_begin" && !IsActive) CloseAudit();
            else if (method is "task_end" or "task_stop") CloseAudit();
        }
    }

    /// <summary>兼容旧的可视化闩锁消息；执行不再等待该确认。</summary>
    public void ActionReady(string id)
    {
    }

    public void StopLocal() => _ = StopLocalAsync();

    /// <summary>
    /// 取消当前在途动作但保留任务、观察与 token；由 Gateway 的控制管道调用。
    /// 与人工接管不同，不改变观察有效性，也不结束任务。
    /// </summary>
    public void CancelCurrent()
    {
        ActionCoordinator? coordinator;
        lock (_sync)
        {
            if (_session == null || _stopping || _manualTakeover) return;
            coordinator = _coordinator;
        }
        coordinator?.CancelCurrent();
    }

    /// <summary>低层钩子发现未标记的人工输入后暂停本代次动作；已交给 Win32 的原子输入不承诺撤回。</summary>
    public void PauseForHumanInput()
    {
        ActionCoordinator? coordinator;
        OperationRunner? operations;
        lock (_sync)
        {
            if (_session == null || _stopping || _manualTakeover) return;
            coordinator = _coordinator;
            operations = _operations;
            _manualTakeover = true;
            _lastSnapshot = null;
            _lastCapture = null;
            _observedAt = default;
        }
        coordinator?.Pause();
        operations?.ReportControl("paused");
        _host.PostMessage(new { type = "task-status", text = "人工接管中：AI 输入已暂停；按 Ctrl+Alt+Shift+R 恢复，恢复后请重新 observe" });
    }

    /// <summary>只允许本地热键调用；恢复后旧观察不可用，AI 必须重新 observe。</summary>
    public void ResumeLocal()
    {
        OperationRunner? operations;
        lock (_sync)
        {
            if (_session == null || _stopping || !_manualTakeover) return;
            _coordinator?.Resume();
            _manualTakeover = false;
            _lastSnapshot = null;
            _lastCapture = null;
            _observedAt = default;
            operations = _operations;
        }
        operations?.ReportControl("running");
        _host.PostMessage(new { type = "task-status", text = "已恢复 AI 控制；旧观察已失效，请重新 observe" });
    }

    public async Task StopLocalAsync(bool keepAudit = false)
    {
        StopMirror();
        OperationRunner? operations;
        ActionCoordinator? coordinator;
        int generation;
        lock (_sync)
        {
            if (_stopping) return;
            _stopping = true;
            operations = _operations;
            _operations = null;
            coordinator = _coordinator;
            _coordinator = null;
            generation = ++_generation;
        }

        _watchdog.Stop();
        if (operations != null)
        {
            try
            {
                await operations.StopAsync(TimeSpan.FromMilliseconds(800));
            }
            catch
            {
                // 收尾阶段的异常不应阻止释放会话。
            }
        }

        coordinator?.Stop();
        List<TaskCompletionSource> pending;
        lock (_sync)
        {
            pending = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var pendingItem in pending) pendingItem.TrySetCanceled();
        _authorization.Stop();

        lock (_sync)
        {
            _session = null;
            _target = null;
            _gatewayProcessId = 0;
            _lastSnapshot = null;
            _lastCapture = null;
            _observedAt = default;
            _sensitiveElements.Clear();
            _maskAllText = false;
            _manualTakeover = false;
            _policy = TaskPolicy.Default;
            _confirmations = new ConfirmationBroker();
            _actionCount = 0;
            _targetProcessName = null;
            // 排空与本轮清理完成后才允许下一次 task_begin，避免复位过早导致两个会话重叠。
            _stopping = false;
        }
        if (!keepAudit) CloseAudit();

        // 保留覆盖层一小段时间展示结束摘要，再隐藏；若期间已开始新任务则不隐藏。
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        bool stillStopped;
        lock (_sync) stillStopped = _generation == generation && _session == null;
        if (stillStopped) _host.HideTaskOverlay();
    }

    private async Task<object> Begin(JsonElement parameters, int gatewayProcessId, ProgressReporter progress)
    {
        lock (_sync)
        {
            if (_session != null) throw new InvalidOperationException("已有 AI 桌面任务正在运行");
            if (_stopping) throw new InvalidOperationException("上一个任务正在停止，请稍后重试");
        }

        var policy = TaskPolicy.Parse(parameters);
        var candidate = WindowLocator.Find(OptionalString(parameters, "targetProcess"), OptionalString(parameters, "targetTitle"), _host.NativeHandle);
        _auditContext.Target = new AuditTarget(candidate.ProcessName, AuditHash.Sha256(candidate.Title), "window");
        var beginDecision = PolicyEvaluator.Begin(policy, candidate.ProcessName, candidate.Title);
        if (!beginDecision.Allowed)
        {
            _auditContext.Decision = "denied:" + beginDecision.Reason;
            throw new DesktopOperationException("policy_denied", $"任务策略拒绝目标：{beginDecision.Reason}");
        }
        var approved = _host.ConfirmLocal(
            "Swarm AI 操作授权",
            $"允许当前 Trae 只读取并操作此应用的窗口内容？\n\n应用：{candidate.ProcessName} ({candidate.ProcessId})\n窗口：{candidate.Title}\n\n任务中不会主动抢走其它程序焦点；窗口被遮挡时优先使用窗口消息。可显示观察、点击、输入和结果。危险操作仍会再次询问，目标在前台时 Esc 可急停。",
            false);
        if (!approved) throw new UnauthorizedAccessException("用户未授权本次 AI 桌面任务");

        await progress.Report(0.1, "capture");
        var capture = await CaptureService.CaptureAsync(candidate.Handle);
        NativeMethods.GetWindowThreadProcessId(candidate.Handle, out var processId);
        DateTime startTime;
        try
        {
            startTime = Process.GetProcessById((int)processId).StartTime.ToUniversalTime();
        }
        catch (ArgumentException)
        {
            throw new DesktopOperationException("window_closed", "目标进程已退出");
        }
        catch (Win32Exception error)
        {
            // 提权/受保护进程不允许读取 StartTime，应明确告知而不是当作内部错误。
            throw new DesktopOperationException("elevated_target", $"无法读取目标进程信息（可能以管理员权限运行）：{error.Message}");
        }
        var identity = new TargetIdentity(candidate.Handle, (int)processId, startTime, NativeMethods.WindowClass(candidate.Handle) ?? string.Empty);

        lock (_sync)
        {
            _generation++;
            _stopping = false;
            _manualTakeover = false;
            _session = _authorization.Start($"{candidate.ProcessName}: {candidate.Title}", candidate.ProcessId);
            _coordinator = new ActionCoordinator();
            _operations = new OperationRunner(new HostOperationObserver(_host));
            _operations.BeginSession($"{candidate.ProcessName}: {candidate.Title}", $"pid {candidate.ProcessId}");
            _target = identity;
            _gatewayProcessId = gatewayProcessId;
            _lastSnapshot = null;
            _lastCapture = null;
            _observedAt = default;
            _policy = policy;
            _taskStartedAt = DateTimeOffset.Now;
            _actionCount = 0;
            _confirmations = new ConfirmationBroker();
            _targetProcessName = candidate.ProcessName;
        }

        _host.ShowTaskOverlay(capture.Monitor, capture.DpiScale);
        _host.PostMessage(new { type = "task-status", text = $"已授权：{candidate.ProcessName}，目标在前台时 Esc 急停" });
        _watchdog.Start();
        StartMirror();
        FileLog.Write("desktop", $"task begin pid={candidate.ProcessId} process={candidate.ProcessName} title={candidate.Title} capture={capture.Backend}");
        try
        {
            return await ObserveInternal(includeScreenshot: true, progress);
        }
        catch
        {
            // Begin 失败必须回滚已写入的会话，否则之后每次 task_begin 都会被“已有任务”拒绝。
            try { await StopLocalAsync(keepAudit: true); }
            catch (Exception cleanup) { FileLog.Write("desktop", $"begin rollback failed: {cleanup.Message}"); }
            throw;
        }
    }

    private async Task<object> Observe(JsonElement parameters, int gatewayProcessId, ProgressReporter progress)
    {
        Validate(Token(parameters), gatewayProcessId);
        var include = OptionalBool(parameters, "includeScreenshot");
        return await ObserveInternal(include, progress);
    }

    private async Task<object> ObserveInternal(bool includeScreenshot, ProgressReporter? progress = null)
    {
        var identity = CurrentTarget();
        EnsureIdentity(identity);
        var capture = await CaptureService.CaptureAsync(identity.Handle);
        if (progress != null) await progress.Report(0.2, "capture");
        if (capture.Backend == "screen")
            FileLog.Write("desktop", $"capture fallback handle=0x{identity.Handle:x} occluded={capture.Occluded} blank={capture.Blank}");
        UiaSnapshot? snapshot = null;
        long observation;
        lock (_sync) observation = ++_observationGeneration;
        // 两种降级语义分开：backend=screen 是真的没用窗口抓屏；occluded 只是目标不在前台或被遮挡。
        string? warning = capture.Backend == "screen"
            ? "窗口抓屏不可用，已降级为屏幕裁剪；截图可能包含其他窗口"
            : capture.Occluded
                ? "目标窗口不在前台或已被遮挡；截图可能包含其他窗口"
                : null;
        try
        {
            snapshot = await Task.Run(() => UiaSnapshotReader.Read(identity.Handle, new UiaSnapshotOptions(IncludePatterns: true, FirstPassMilliseconds: 800, Generation: observation)));
        }
        catch (InvalidOperationException error) when (error.Message.Contains("最小化", StringComparison.Ordinal))
        {
            throw new DesktopOperationException("window_minimized", "目标窗口已最小化，请先还原");
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            warning = warning == null ? $"UIA 暂不可用：{error.Message}" : $"{warning}；UIA 暂不可用：{error.Message}";
        }
        catch (InvalidOperationException error)
        {
            throw new DesktopOperationException("window_closed", error.Message);
        }

        // P2 Fallback: UIA 不可用时走 OCR + SoM 视觉兜底.
        string provenance = "uia";
        string? fallbackReason = null;
        List<SoMElement>? somElements = null;
        if (snapshot == null && capture.Frame != null && !capture.Occluded)
        {
            var ocr = _ocrService;
            if (ocr.IsAvailable)
            {
                try
                {
                    var lines = await ocr.OcrAsync(capture.Frame);
                    if (lines.Count > 0)
                    {
                        provenance = "ocr";
                        fallbackReason = "uia_unavailable";
                        somElements = SetOfMarkBuilder.Build(lines).ToList();
                        FileLog.Write("desktop", $"observe fallback OCR lines={lines.Count} somMarks={somElements.Count}");
                    }
                }
                catch (Exception error)
                {
                    FileLog.Write("desktop", $"observe fallback OCR error: {error.Message}");
                }
            }
            else
            {
                fallbackReason = "ocr_unavailable";
            }
        }

        if (progress != null) await progress.Report(0.55, "uia");

        var redactionOptions = _policy.EffectiveRedaction;
        var hits = snapshot == null
            ? Array.Empty<RedactionHit>()
            : RedactionDetector.Detect(redactionOptions, snapshot.Elements).ToArray();
        var hitByElement = new Dictionary<string, RedactionHit>(StringComparer.Ordinal);
        foreach (var hit in hits) hitByElement[hit.ElementId] = hit;

        lock (_sync)
        {
            _lastCapture = capture;
            _lastSnapshot = snapshot;
            _observedAt = DateTimeOffset.Now;
        }
        _host.ShowTaskOverlay(capture.Monitor, capture.DpiScale);

        var dpi = capture.DpiScale;
        var client = capture.ClientBounds;
        var clientScreen = capture.ClientScreen;
        var monitor = capture.Monitor;
        var viewport = CoordinateConverter.ClientViewport(client, dpi);
        var viewportWidth = viewport.Width;
        var viewportHeight = viewport.Height;
        var windowCss = CoordinateConverter.ToMonitorCss(clientScreen, monitor, dpi);
        var elements = snapshot?.Elements
            .Where(element => element.HasVisual && element.Bounds.Width > 0 && element.Bounds.Height > 0 &&
                              element.Bounds.Intersects(clientScreen))
            .Select(element =>
            {
                var bounds = CoordinateConverter.ToClient(element.Bounds, clientScreen, dpi);
                hitByElement.TryGetValue(element.Id, out var hit);
                var maskedByPolicy = hit != null;
                var sensitive = maskedByPolicy || element.IsPassword || _sensitiveElements.Contains(ElementId.Stable(element.Id)) || (_maskAllText && element.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase));
                return new
                {
                    id = element.Id,
                    parentId = element.ParentId,
                    type = element.ControlType,
                    name = element.Name,
                    value = maskedByPolicy ? $"[REDACTED:{hit!.Category}]" : sensitive && element.Value != null ? "••••••••" : element.Value,
                    bounds = new { x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height },
                    patterns = element.Patterns,
                    sensitive,
                    redactionCategory = hit?.Category,
                    processId = element.ProcessId,
                    foreignProcess = element.ProcessId != identity.ProcessId,
                };
            }).ToArray() ?? Array.Empty<object>();

        foreach (var hit in hits) _auditContext.Redactions.Add(new AuditRedaction(hit.Category, hit.Risk, hit.Necessary));
        var redactionViews = hits.Select(hit =>
        {
            var bounds = CoordinateConverter.ToClient(hit.Bounds, clientScreen, dpi);
            return new
            {
                elementId = hit.ElementId,
                category = hit.Category,
                risk = hit.Risk,
                necessary = hit.Necessary,
                bounds = new { x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height },
            };
        }).ToArray();

        object? screenshot = null;
        if (includeScreenshot)
        {
            var frame = capture.Frame!;
            if (!redactionOptions.IsOff && !redactionOptions.IsMetadataOnly && hits.Length > 0)
            {
                var regions = hits.Select(hit => hit.Bounds).ToArray();
                frame = await Task.Run(() => RedactionRenderer.Mask(capture.Frame!, clientScreen, regions));
            }
            var max = _screenshotMax;
            var image = await Task.Run(() => NativeImage.ToDataUrl(CaptureService.Scale(frame!, max)));
            screenshot = new { mimeType = "image/png", data = image.Substring("data:image/png;base64,".Length) };
            if (progress != null) await progress.Report(0.85, "screenshot");
        }

        var description = WindowLocator.Describe(identity.Handle);
        var capabilities = snapshot?.Capabilities
            ?? TargetCapabilitiesDetector.Detect(description.ProcessName, identity.ClassName, null, uiaAvailable: false);
        if (progress != null) await progress.Report(0.95, "done");
        return new
        {
            token = _session?.Token,
            target = new
            {
                processId = identity.ProcessId,
                processName = description.ProcessName,
                title = description.Title,
                handle = identity.Handle.ToInt64(),
            },
            viewport = new
            {
                width = viewportWidth,
                height = viewportHeight,
                dpiScale = dpi,
                coordinateSpace = "window-client CSS pixels (origin = target client-area top-left); elementId is preferred",
                clientPhysical = new { x = clientScreen.X, y = clientScreen.Y, w = client.Width, h = client.Height },
            },
            capabilities = new
            {
                isChromium = capabilities.IsChromium,
                isElectron = capabilities.IsElectron,
                uiaAvailable = capabilities.UiaAvailable,
                supportsInvoke = capabilities.SupportsInvoke,
                supportsValue = capabilities.SupportsValue,
                supportsText = capabilities.SupportsText,
                messageReliable = capabilities.MessageReliable,
                guidance = capabilities.MessageReliable ? null : "该应用通常忽略窗口消息输入；窗口被遮挡时请先调用 focus，再用真实输入重试",
                captureWgc = WgcSupport.IsAvailable,
                captureGuidance = WgcSupport.IsAvailable
                    ? null
                    : $"WGC 不可用（{WgcSupport.UnavailableReason}），抓屏已降级 PrintWindow/屏幕裁剪；GPU 渲染窗口可能返回空白或要求窗口可见",
            },
            window = new
            {
                bounds = new { x = windowCss.X, y = windowCss.Y, w = windowCss.Width, h = windowCss.Height },
                screenCss = new { x = windowCss.X, y = windowCss.Y, w = windowCss.Width, h = windowCss.Height },
                physicalBounds = new { x = clientScreen.X, y = clientScreen.Y, w = client.Width, h = client.Height },
            },
            generation = snapshot?.Generation ?? observation,
            elements,
            redactions = redactionViews,
            screenshot,
            occluded = capture.Occluded,
            captureBackend = capture.Backend,
            frameAgeMs = capture.FrameAgeMs,
            warning,
            // P2 OCR/SoM 视觉兜底元数据: 让 Agent 知道当前 grounding 通道和可信度.
            provenance,                           // "uia" | "ocr"
            fallbackReason,                       // null | "uia_unavailable" | "ocr_unavailable"
            somMarks = somElements?.Select(m => new
            {
                markId = m.MarkId, text = m.Text, kind = m.Kind.ToString().ToLowerInvariant(),
                bounds = new { x = m.X, y = m.Y, w = m.Width, h = m.Height },
                confidence = m.Confidence,
            }).ToArray(),
        };
    }

    private async Task<object> Click(JsonElement parameters, int gatewayProcessId, ProgressReporter progress)
    {
        var session = Validate(Token(parameters), gatewayProcessId);
        var identity = CurrentTarget();
        var (snapshot, capture) = Observation();
        EnsureWindowStable(capture);
        var elementId = OptionalString(parameters, "elementId");
        var element = Element(elementId, snapshot);
        if (element != null) VerifyElement(element, snapshot);
        var pointX = element == null ? RequiredNumber(parameters, "x") : 0;
        var pointY = element == null ? RequiredNumber(parameters, "y") : 0;
        var geometry = element != null
            ? ElementGeometry(element, capture)
            : PointGeometry(pointX, pointY, capture);
        var verify = PixelVerify(parameters);
        var region = element != null ? ElementRegion(element, capture) : PointRegion(pointX, pointY, capture);
        var screen = ToScreen(geometry.CursorX, geometry.CursorY, capture);
        var explicitLabel = OptionalString(parameters, "label");
        var label = explicitLabel ?? (element != null ? $"点击 {element.Name ?? element.ControlType}" : "点击坐标");
        var crossApp = IsCrossApp(element, identity);
        CheckPolicy("click", crossApp);
        SetActionAudit(label, element);
        // 只看调用方显式给出的 label；屏幕元素名不参与策略判定，避免被界面文字注入。
        if (RequiresConfirmation(ActionClassifier.Classify(explicitLabel), crossApp))
        {
            var pending = RequestConfirmation(parameters, "click", $"点击：{label}");
            if (pending != null) return pending;
        }
        else
        {
            await ConfirmDanger(label, element?.ControlType, null);
        }
        _actionCount++;
        var actionId = NewActionId();
        var action = new
        {
            id = actionId,
            kind = "click",
            x = geometry.VisualX,
            y = geometry.VisualY,
            w = geometry.VisualW,
            h = geometry.VisualH,
            label,
        };
        await progress.Report(0.3, "execute");
        var outcome = await RunAction(actionId, "click", label, action, token =>
            RunWithPixelVerification(identity.Handle, verify, region,
                inner => InputDispatcher.ClickAsync(identity.Handle, snapshot, elementId, screen.X, screen.Y, true, inner), token));
        await progress.Report(0.7, verify ? "verify" : "execute");
        return await AfterAction(actionId, outcome, OptionalBool(parameters, "observe"), progress);
    }

    private async Task<object> TypeText(JsonElement parameters, int gatewayProcessId, ProgressReporter progress)
    {
        var session = Validate(Token(parameters), gatewayProcessId);
        var identity = CurrentTarget();
        var (snapshot, capture) = Observation();
        EnsureWindowStable(capture);
        var text = RequiredString(parameters, "text");
        if (text.Length > 10000) throw new ArgumentException("单次输入不能超过 10000 个字符");
        var sensitive = OptionalBool(parameters, "sensitive");
        var elementId = OptionalString(parameters, "elementId");
        var element = Element(elementId, snapshot);
        if (element != null) VerifyElement(element, snapshot);
        var geometry = element != null ? ElementGeometry(element, capture) : WindowGeometry(capture);
        var verify = PixelVerify(parameters);
        if (verify && element == null) throw new ArgumentException("像素验证需要提供 elementId");
        var region = element != null ? ElementRegion(element, capture) : new RectInt(0, 0, capture.ClientBounds.Width, capture.ClientBounds.Height);
        if (sensitive)
        {
            if (elementId != null) _sensitiveElements.Add(ElementId.Stable(elementId));
            else _maskAllText = true;
        }
        var screen = ToScreen(geometry.CursorX, geometry.CursorY, capture);
        var label = sensitive ? "输入受保护文本" : $"输入：{text.Substring(0, Math.Min(60, text.Length))}";
        var crossApp = IsCrossApp(element, identity);
        CheckPolicy("type_text", crossApp);
        SetActionAudit(text, element);
        if (RequiresConfirmation(null, crossApp))
        {
            var pending = RequestConfirmation(parameters, "type_text", $"输入文本：{label}");
            if (pending != null) return pending;
        }
        _actionCount++;
        var actionId = NewActionId();
        var action = new
        {
            id = actionId,
            kind = "type",
            x = geometry.VisualX,
            y = geometry.VisualY,
            w = geometry.VisualW,
            h = geometry.VisualH,
            label,
            sensitive,
        };
        await progress.Report(0.3, "execute");
        var outcome = await RunAction(actionId, "type", label, action, token =>
            RunWithPixelVerification(identity.Handle, verify, region,
                inner => InputDispatcher.TypeTextAsync(identity.Handle, snapshot, elementId, text, screen.X, screen.Y, true, inner), token));
        await progress.Report(0.7, verify ? "verify" : "execute");
        return await AfterAction(actionId, outcome, OptionalBool(parameters, "observe"), progress);
    }

    private async Task<object> PressKey(JsonElement parameters, int gatewayProcessId, ProgressReporter progress)
    {
        var session = Validate(Token(parameters), gatewayProcessId);
        var identity = CurrentTarget();
        var (snapshot, capture) = Observation();
        EnsureWindowStable(capture);
        var key = RequiredString(parameters, "key");
        CheckPolicy("press_key", false);
        SetActionAudit(key, null);
        if (RequiresConfirmation(ActionClassifier.Classify(null, key), false))
        {
            var pending = RequestConfirmation(parameters, "press_key", $"按键：{key}");
            if (pending != null) return pending;
        }
        else
        {
            await ConfirmDanger($"按键 {key}", "keyboard", key);
        }
        _actionCount++;
        var verify = PixelVerify(parameters);
        var region = new RectInt(0, 0, capture.ClientBounds.Width, capture.ClientBounds.Height);
        var geometry = WindowGeometry(capture);
        var screen = ToScreen(geometry.CursorX, geometry.CursorY, capture);
        var actionId = NewActionId();
        var action = new
        {
            id = actionId,
            kind = "key",
            x = geometry.VisualX,
            y = geometry.VisualY,
            w = geometry.VisualW,
            h = geometry.VisualH,
            label = $"按键：{key}",
        };
        await progress.Report(0.3, "execute");
        var outcome = await RunAction(actionId, "key", $"按键：{key}", action, token =>
            RunWithPixelVerification(identity.Handle, verify, region,
                inner => InputDispatcher.PressKeyAsync(identity.Handle, snapshot, key, screen.X, screen.Y, true, inner), token));
        await progress.Report(0.7, verify ? "verify" : "execute");
        return await AfterAction(actionId, outcome, OptionalBool(parameters, "observe"), progress);
    }

    private async Task<object> Scroll(JsonElement parameters, int gatewayProcessId, ProgressReporter progress)
    {
        var session = Validate(Token(parameters), gatewayProcessId);
        var identity = CurrentTarget();
        var (snapshot, capture) = Observation();
        EnsureWindowStable(capture);
        CheckPolicy("scroll", false);
        SetActionAudit(null, null);
        var x = RequiredNumber(parameters, "x");
        var y = RequiredNumber(parameters, "y");
        var delta = RequiredNumber(parameters, "delta");
        if (!ScrollWheelDelta.TryNormalize(delta, out var scrollDelta))
            throw new ArgumentException($"scroll 的 delta 必须是 -{ScrollWheelDelta.MaxMagnitude} 到 {ScrollWheelDelta.MaxMagnitude} 之间的有限数", "delta");
        var verify = PixelVerify(parameters);
        var region = new RectInt(0, 0, capture.ClientBounds.Width, capture.ClientBounds.Height);
        var geometry = PointGeometry(x, y, capture);
        var screen = ToScreen(geometry.CursorX, geometry.CursorY, capture);
        _actionCount++;
        var actionId = NewActionId();
        var action = new
        {
            id = actionId,
            kind = "scroll",
            x = geometry.VisualX,
            y = geometry.VisualY,
            w = geometry.VisualW,
            h = geometry.VisualH,
            deltaY = scrollDelta,
            label = $"滚动 {scrollDelta}",
        };
        await progress.Report(0.3, "execute");
        var outcome = await RunAction(actionId, "scroll", $"滚动 {scrollDelta}", action, token =>
            RunWithPixelVerification(identity.Handle, verify, region,
                inner => InputDispatcher.ScrollAsync(identity.Handle, snapshot, screen.X, screen.Y, scrollDelta, true, inner), token));
        await progress.Report(0.7, verify ? "verify" : "execute");
        return await AfterAction(actionId, outcome, OptionalBool(parameters, "observe"), progress);
    }

    private async Task<object> Focus(JsonElement parameters, int gatewayProcessId, ProgressReporter progress)
    {
        Validate(Token(parameters), gatewayProcessId);
        var identity = CurrentTarget();
        var capture = _lastCapture ?? throw new InvalidOperationException("请先 observe");
        EnsureWindowStable(capture);
        CheckPolicy("focus", false);
        SetActionAudit(null, null);
        var geometry = WindowGeometry(capture);
        _actionCount++;
        var actionId = NewActionId();
        var action = new
        {
            id = actionId,
            kind = "focus",
            x = geometry.VisualX,
            y = geometry.VisualY,
            w = geometry.VisualW,
            h = geometry.VisualH,
            label = "切换到目标窗口",
        };
        await progress.Report(0.3, "execute");
        var outcome = await RunAction(actionId, "focus", "切换到目标窗口", action, token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(InputDispatcher.Focus(identity.Handle));
        });
        await progress.Report(0.7, "execute");
        return await AfterAction(actionId, outcome, OptionalBool(parameters, "observe"), progress);
    }

    private async Task<object> End(string token, int gatewayProcessId, bool stopped)
    {
        Validate(token, gatewayProcessId);
        await StopLocalAsync(keepAudit: true);
        FileLog.Write("desktop", stopped ? "task stop" : "task end");
        return new { ok = true, stopped };
    }

    private AuthorizedSession Validate(string token, int gatewayProcessId)
    {
        AuthorizedSession? session;
        int expectedGateway;
        lock (_sync)
        {
            session = _session;
            expectedGateway = _gatewayProcessId;
            if (_stopping) throw new DesktopOperationException("stopped", "任务正在停止");
            if (_manualTakeover) throw new DesktopOperationException("manual_takeover", "人工接管中；本地恢复后请重新 observe");
        }
        if (session == null || gatewayProcessId != expectedGateway) throw new UnauthorizedAccessException("Gateway 或任务会话无效");
        var identity = CurrentTarget();
        EnsureIdentity(identity);
        NativeMethods.GetWindowThreadProcessId(identity.Handle, out var processId);
        return _authorization.Validate(token, (int)processId);
    }

    private TargetIdentity CurrentTarget()
    {
        lock (_sync)
        {
            if (_target == null) throw new UnauthorizedAccessException("任务会话无效");
            return _target;
        }
    }

    private static void EnsureIdentity(TargetIdentity identity)
    {
        if (!NativeMethods.IsWindow(identity.Handle))
            throw new DesktopOperationException("window_closed", "目标窗口已关闭");
        NativeMethods.GetWindowThreadProcessId(identity.Handle, out var processId);
        if ((int)processId != identity.ProcessId)
            throw new DesktopOperationException("window_reused", "目标窗口已被系统复用，请重新授权");
        var className = NativeMethods.WindowClass(identity.Handle);
        if (className != null && identity.ClassName.Length > 0 && className != identity.ClassName)
            throw new DesktopOperationException("window_reused", "目标窗口类型已变化，请重新授权");
        try
        {
            if (Process.GetProcessById(identity.ProcessId).StartTime.ToUniversalTime() != identity.StartTimeUtc)
                throw new DesktopOperationException("window_reused", "目标进程已重启，请重新授权");
        }
        catch (ArgumentException)
        {
            throw new DesktopOperationException("window_closed", "目标进程已退出");
        }
    }

    private (UiaSnapshot? Snapshot, WindowCapture Capture) Observation()
    {
        lock (_sync)
        {
            if (_lastCapture == null) throw new InvalidOperationException("请先 observe");
            if (DateTimeOffset.Now - _observedAt > SnapshotTtl)
                throw new DesktopOperationException("stale_observation", "观察已过期，请重新 observe");
            return (_lastSnapshot, _lastCapture);
        }
    }

    private static void EnsureWindowStable(WindowCapture capture)
    {
        var (_, clientScreen, clientBounds, _) = CaptureService.Geometry(capture.Window);
        if (Math.Abs(clientScreen.X - capture.ClientScreen.X) > WindowDriftThreshold ||
            Math.Abs(clientScreen.Y - capture.ClientScreen.Y) > WindowDriftThreshold ||
            Math.Abs(clientBounds.Width - capture.ClientBounds.Width) > WindowDriftThreshold ||
            Math.Abs(clientBounds.Height - capture.ClientBounds.Height) > WindowDriftThreshold)
        {
            throw new DesktopOperationException("window_moved", "目标窗口在观察后移动或缩放，请重新 observe");
        }
    }

    private static void VerifyElement(UiaElementInfo element, UiaSnapshot? snapshot)
    {
        if (snapshot == null || !snapshot.NativeElements.TryGetValue(element.Id, out var native))
            throw new DesktopOperationException("stale_observation", "元素已失效，请重新 observe");
        try
        {
            var current = native.Current.BoundingRectangle;
            if (double.IsInfinity(current.X) || double.IsNaN(current.X))
                throw new DesktopOperationException("stale_observation", "元素已不可见，请重新 observe");
            if (Math.Abs(current.X - element.Bounds.X) > WindowDriftThreshold ||
                Math.Abs(current.Y - element.Bounds.Y) > WindowDriftThreshold ||
                Math.Abs(current.Width - element.Bounds.Width) > WindowDriftThreshold ||
                Math.Abs(current.Height - element.Bounds.Height) > WindowDriftThreshold)
            {
                throw new DesktopOperationException("stale_observation", "元素位置已变化，请重新 observe");
            }
        }
        catch (ElementNotAvailableException)
        {
            throw new DesktopOperationException("stale_observation", "元素已失效，请重新 observe");
        }
    }

    private async Task<ActionOutcome> RunAction(
        string id,
        string kind,
        string label,
        object visual,
        Func<CancellationToken, Task<ActionOutcome>> body)
    {
        ActionOutcome? outcome = null;
        try
        {
            var operations = _operations;
            var coordinator = _coordinator;
            if (operations?.Session == null || coordinator == null)
                throw new DesktopOperationException("stopped", "任务已停止");
            var step = new OperationStep { Id = id, Kind = OperationStep.ParseKind(kind), Label = label };
            await operations.RunAsync(
                step,
                token => coordinator.RunAsync(
                    inner => VisualLead(visual, inner),
                    async inner =>
                    {
                        var result = await body(inner);
                        outcome = result;
                        step.ReportTransport(result.Tier, result.Verified, result.Note, result.Evidence);
                    },
                    token),
                TimeSpan.FromSeconds(20));
            _host.PostMessage(new
            {
                type = "action-result",
                result = new
                {
                    id,
                    ok = true,
                    tier = outcome?.Tier.ToString().ToLowerInvariant(),
                    verified = outcome?.Verified ?? false,
                    verification = outcome?.Verified == true ? "passed" : outcome?.Evidence != null ? "unverified" : "not_requested",
                    evidence = outcome?.Evidence,
                    note = outcome?.Note,
                    foregroundUnchanged = outcome?.ForegroundUnchanged ?? true,
                    foregroundRestored = outcome?.ForegroundRestored ?? false,
                },
            });
            if (outcome != null) _auditContext.Tier = outcome.Tier.ToString().ToLowerInvariant();
            return outcome ?? new ActionOutcome(InputTier.None, false);
        }
        catch (Exception error)
        {
            _host.PostMessage(new { type = "action-result", result = new { id, ok = false, error = error.Message } });
            throw;
        }
    }

    /// <summary>只推进可视化，不等待渲染层确认；最多占用配置的视觉提前量。</summary>
    private async Task VisualLead(object action, CancellationToken token)
    {
        _host.PostMessage(new { type = "action-prepare", action });
        var lead = _visualLeadMs;
        if (lead <= 0) return;
        try
        {
            await Task.Delay(lead, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>
    /// 显式像素验证：动作前取帧，动作后连续两帧确认画面稳定，再比较目标区域的变化比例。
    /// 只有“画面稳定且变化超过阈值”才判 verified；已有回读证据时不降级。
    /// </summary>
    private async Task<ActionOutcome> RunWithPixelVerification(
        nint window,
        bool verify,
        RectInt region,
        Func<CancellationToken, Task<ActionOutcome>> body,
        CancellationToken token)
    {
        if (!verify) return await body(token);

        var before = await CaptureService.CaptureAsync(window, token, allowReuse: false);
        var outcome = await body(token);
        await Task.Delay(120, token);
        var first = await CaptureService.CaptureAsync(window, token, allowReuse: false);
        await Task.Delay(120, token);
        var second = await CaptureService.CaptureAsync(window, token, allowReuse: false);

        if (before.Occluded || first.Occluded || second.Occluded || before.Blank || first.Blank || second.Blank)
            return outcome with
            {
                Verified = false,
                Evidence = MessageEvidence.None,
                Note = AppendNote(outcome.Note, "抓屏降级或空白，像素验证不可靠"),
            };

        var drift = PixelDiff.Compare(first.Frame, second.Frame, region);
        if (!drift.Comparable || drift.ChangedRatio > 0.02)
            return outcome with
            {
                Verified = false,
                Evidence = MessageEvidence.None,
                Note = AppendNote(outcome.Note, "画面仍在变化，像素验证未确认"),
            };

        var diff = PixelDiff.Compare(before.Frame, second.Frame, region);
        if (!diff.Comparable)
            return outcome with
            {
                Verified = false,
                Evidence = MessageEvidence.None,
                Note = AppendNote(outcome.Note, "窗口尺寸变化，像素验证未确认"),
            };

        if (diff.ChangedRatio >= _pixelDiffThreshold)
            return outcome with
            {
                Verified = true,
                Evidence = MessageEvidence.Pixels,
                Note = AppendNote(outcome.Note, $"像素变化 {diff.ChangedRatio:P2}（{diff.ChangedPixels}/{diff.TotalPixels}）"),
            };

        return outcome.Verified
            ? outcome with { Note = AppendNote(outcome.Note, $"像素变化 {diff.ChangedRatio:P2}，保留回读证据") }
            : outcome with
            {
                Verified = false,
                Evidence = MessageEvidence.None,
                Note = AppendNote(outcome.Note, $"像素变化不足 {diff.ChangedRatio:P2}，未确认"),
            };
    }

    private static string? AppendNote(string? note, string extra) =>
        string.IsNullOrWhiteSpace(note) ? extra : $"{note}；{extra}";

    private static bool PixelVerify(JsonElement parameters)
    {
        var mode = (OptionalString(parameters, "verify") ?? "none").ToLowerInvariant();
        return mode switch
        {
            "none" => false,
            "pixels" => true,
            _ => throw new ArgumentException("verify 仅支持 none 或 pixels"),
        };
    }

    private static RectInt ElementRegion(UiaElementInfo element, WindowCapture capture) =>
        new(element.Bounds.X - capture.ClientScreen.X, element.Bounds.Y - capture.ClientScreen.Y, element.Bounds.Width, element.Bounds.Height);

    private static RectInt PointRegion(double x, double y, WindowCapture capture)
    {
        var width = capture.ClientBounds.Width;
        var height = capture.ClientBounds.Height;
        var halfW = Math.Max(1, (int)Math.Round(120 * capture.DpiScale));
        var halfH = Math.Max(1, (int)Math.Round(80 * capture.DpiScale));
        var centerX = (int)Math.Round(x * capture.DpiScale);
        var centerY = (int)Math.Round(y * capture.DpiScale);
        var left = Math.Clamp(centerX - halfW, 0, width - 1);
        var top = Math.Clamp(centerY - halfH, 0, height - 1);
        var right = Math.Clamp(centerX + halfW, left + 1, width);
        var bottom = Math.Clamp(centerY + halfH, top + 1, height);
        return new RectInt(left, top, right - left, bottom - top);
    }

    private async Task<object> AfterAction(string actionId, ActionOutcome outcome, bool observe, ProgressReporter? progress = null)
    {
        var tier = outcome.Tier.ToString().ToLowerInvariant();
        FileLog.Write("desktop", $"action {actionId} tier={tier} verified={outcome.Verified} evidence={outcome.Evidence ?? "none"}");
        if (!observe)
            return new
            {
                executed = true,
                actionId,
                tier,
                verified = outcome.Verified,
                evidence = outcome.Evidence,
                note = outcome.Note,
                foregroundUnchanged = outcome.ForegroundUnchanged,
                foregroundRestored = outcome.ForegroundRestored,
            };
        try
        {
            if (progress != null) await progress.Report(0.85, "observe");
            return new
            {
                executed = true,
                actionId,
                tier,
                verified = outcome.Verified,
                evidence = outcome.Evidence,
                note = outcome.Note,
                foregroundUnchanged = outcome.ForegroundUnchanged,
                foregroundRestored = outcome.ForegroundRestored,
                observation = await ObserveInternal(false, progress),
            };
        }
        catch (Exception error)
        {
            return new
            {
                executed = true,
                actionId,
                tier,
                verified = outcome.Verified,
                evidence = outcome.Evidence,
                note = outcome.Note,
                foregroundUnchanged = outcome.ForegroundUnchanged,
                foregroundRestored = outcome.ForegroundRestored,
                observationError = error.Message,
            };
        }
    }

    private static bool IsCrossApp(UiaElementInfo? element, TargetIdentity identity) =>
        element != null && element.ProcessId > 0 && element.ProcessId != identity.ProcessId;

    /// <summary>只依据策略与计时/计数事实判定，不读取界面文字。</summary>
    private void CheckPolicy(string kind, bool crossApp)
    {
        var decision = PolicyEvaluator.Action(_policy, kind, DateTimeOffset.Now - _taskStartedAt, _actionCount, crossApp);
        if (decision.Allowed) return;
        _auditContext.Decision = "denied:" + decision.Reason;
        throw new DesktopOperationException("policy_denied", $"任务策略拒绝本次动作：{decision.Reason}");
    }

    private bool RequiresConfirmation(string? category, bool crossApp) =>
        (category != null && _policy.RequiresConfirmation(category)) ||
        (crossApp && _policy.RequiresConfirmation("cross_app"));

    /// <summary>没有 confirmToken 时签发一次性令牌并返回 needs_confirmation；有令牌时校验并放行。</summary>
    private object? RequestConfirmation(JsonElement parameters, string method, string summary)
    {
        var taskId = _taskId ?? throw new DesktopOperationException("invalid_state", "任务上下文已失效");
        var fingerprint = ActionFingerprint.Compute(method, parameters);
        var token = OptionalString(parameters, "confirmToken");
        if (token != null)
        {
            if (!_confirmations.TryConsume(taskId, token, fingerprint, out var reason))
            {
                _auditContext.Decision = "denied:confirmation_invalid";
                throw new DesktopOperationException("confirmation_invalid", $"确认令牌无效：{reason}");
            }
            _auditContext.Decision = "confirm_consumed";
            return null;
        }
        var issued = _confirmations.Issue(taskId, fingerprint);
        _auditContext.Decision = "confirm_required";
        return new { status = "needs_confirmation", confirmToken = issued.Token, summary, expiresInMs = issued.ExpiresInMs };
    }

    private void SetActionAudit(string? text, UiaElementInfo? element)
    {
        _auditContext.Text = text;
        _auditContext.Target = element == null
            ? new AuditTarget(_targetProcessName, null, null)
            : new AuditTarget(_targetProcessName, AuditHash.Sha256(element.Name), element.ControlType);
    }

    private void PrepareAudit()
    {
        if (_audit != null) return;
        _taskId = Guid.NewGuid().ToString("N");
        _audit = new AuditWriter(AuditWriter.DefaultDirectory, _taskId);
    }

    private void CloseAudit()
    {
        _audit?.Dispose();
        _audit = null;
        _taskId = null;
        _auditContext = new AuditContext();
    }

    private void WriteAudit(string? method, DateTimeOffset started)
    {
        var writer = _audit;
        if (writer == null || method == null) return;
        try
        {
            writer.Write(new AuditEntry(
                _taskId ?? string.Empty,
                DateTimeOffset.Now,
                method,
                _auditContext.Tier,
                _auditContext.Target,
                _auditContext.Decision,
                _auditContext.Redactions.ToArray(),
                AuditHash.Sha256(_auditContext.Text),
                _auditContext.Text?.Length ?? 0,
                _auditContext.Result,
                (long)Math.Max(0, (DateTimeOffset.Now - started).TotalMilliseconds)));
        }
        catch (Exception error)
        {
            FileLog.Write("desktop", $"audit write failed: {error.Message}");
        }
    }

    private async Task ConfirmDanger(string label, string? semantic, string? key)
    {
        if (!RiskAssessment.RequiresConfirmation(label, semantic, key)) return;
        var approved = _host.ConfirmLocal("Swarm 危险操作确认", $"AI 请求执行：\n\n{label}\n\n这可能关闭窗口、删除、发送、提交或产生其他外部影响。是否只执行这一次？", true);
        if (!approved) throw new UnauthorizedAccessException("用户拒绝了危险操作");
        await Task.CompletedTask;
    }

    private async Task CheckGatewayAsync()
    {
        TargetIdentity? target;
        int gateway;
        lock (_sync)
        {
            if (_session == null || _gatewayProcessId == 0) return;
            target = _target;
            gateway = _gatewayProcessId;
        }
        var reason = (string?)null;
        if (target == null || !NativeMethods.IsWindow(target.Handle)) reason = "目标窗口已关闭，任务已停止";
        else
        {
            try
            {
                if (Process.GetProcessById(gateway).HasExited) reason = "Gateway 已退出，任务已停止";
            }
            catch (ArgumentException)
            {
                reason = "Gateway 已退出，任务已停止";
            }
        }

        if (reason == null) return;
        _host.PostMessage(new { type = "task-status", text = reason });
        await StopLocalAsync();
    }

    private static UiaElementInfo? Element(string? id, UiaSnapshot? snapshot)
    {
        if (id == null) return null;
        if (snapshot == null) throw new InvalidOperationException("当前观察没有 UIA 元素，请改用坐标或重新 observe");
        if (ElementId.TryParseGeneration(id, out var generation) && generation != snapshot.Generation)
            throw new DesktopOperationException("invalid_state", $"元素来自第 {generation} 次观察，当前为第 {snapshot.Generation} 次；请重新 observe");
        return snapshot.Elements.FirstOrDefault(element => element.Id == id) ?? throw new DesktopOperationException("stale_observation", "元素已失效，请重新 observe");
    }

    private static ClientGeometry ElementGeometry(UiaElementInfo element, WindowCapture capture)
    {
        var client = CoordinateConverter.ToClient(element.Bounds, capture.ClientScreen, capture.DpiScale);
        if (client.Width <= 0 || client.Height <= 0) throw new DesktopOperationException("stale_observation", "元素不可见，请重新 observe");
        var viewport = CoordinateConverter.ClientViewport(capture.ClientBounds, capture.DpiScale);
        if (!CoordinateConverter.FitsViewport(client, viewport))
            throw new ArgumentOutOfRangeException("elementId", "元素不在目标窗口客户区内，请重新 observe");
        return new ClientGeometry(
            client.X, client.Y, client.Width, client.Height,
            client.X + client.Width / 2, client.Y + client.Height / 2,
            client.X + capture.MonitorOffsetX / capture.DpiScale,
            client.Y + capture.MonitorOffsetY / capture.DpiScale,
            Math.Max(28, client.Width),
            Math.Max(28, client.Height));
    }

    private static ClientGeometry PointGeometry(double x, double y, WindowCapture capture)
    {
        var viewport = CoordinateConverter.ClientViewport(capture.ClientBounds, capture.DpiScale);
        if (!CoordinateConverter.ContainsClientPoint(x, y, viewport))
            throw new ArgumentOutOfRangeException("coordinates", "坐标超出 observe 返回的窗口客户区");
        return new ClientGeometry(
            x - 14, y - 14, 28, 28, x, y,
            x + capture.MonitorOffsetX / capture.DpiScale - 14,
            y + capture.MonitorOffsetY / capture.DpiScale - 14,
            28, 28);
    }

    private static ClientGeometry WindowGeometry(WindowCapture capture)
    {
        var viewport = CoordinateConverter.ClientViewport(capture.ClientBounds, capture.DpiScale);
        return new ClientGeometry(
            0, 0, viewport.Width, viewport.Height, viewport.Width / 2, viewport.Height / 2,
            capture.MonitorOffsetX / capture.DpiScale,
            capture.MonitorOffsetY / capture.DpiScale,
            viewport.Width, viewport.Height);
    }

    private static (int X, int Y) ToScreen(double clientX, double clientY, WindowCapture capture)
    {
        var point = CoordinateConverter.ToScreen(clientX, clientY, capture.ClientScreen, capture.DpiScale);
        return (point.X, point.Y);
    }

    private static string Token(JsonElement parameters) => RequiredString(parameters, "token");
    private static string RequiredString(JsonElement parameters, string name) => OptionalString(parameters, name) ?? throw new ArgumentException($"缺少参数 {name}");
    private static string? OptionalString(JsonElement parameters, string name) => parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double RequiredNumber(JsonElement parameters, string name) => parameters.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : throw new ArgumentException($"缺少有效数值参数 {name}");
    private static bool OptionalBool(JsonElement parameters, string name) => parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string NewActionId() => Guid.NewGuid().ToString("N");

    private static int ReadSetting(string name, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) ? Math.Clamp(value, min, max) : fallback;
    }

    private static string Success(string id, object result) => JsonSerializer.Serialize(new { id, ok = true, result }, JsonOptions);
    private static string ErrorCode(Exception error) => error switch
    {
        DesktopOperationException desktop => desktop.Code,
        UnauthorizedAccessException => "unauthorized",
        TimeoutException => "timeout",
        OperationCanceledException => "stopped",
        ArgumentException => "invalid_params",
        InvalidOperationException => "invalid_state",
        _ => "desktop_error",
    };

    private static string Failure(string id, Exception error)
    {
        var code = ErrorCode(error);
        var reason = (error as DesktopOperationException)?.Reason;
        FileLog.Write("desktop", $"request {id} failed: {code} reason={reason ?? "-"} {error.GetType().Name}: {error.Message}");
        return JsonSerializer.Serialize(new
        {
            id,
            ok = false,
            error = new
            {
                code,
                reason,
                message = error.Message,
            },
        }, JsonOptions);
    }

    private sealed record TargetIdentity(nint Handle, int ProcessId, DateTime StartTimeUtc, string ClassName);

    /// <summary>单次工具调用的审计上下文；请求串行，故可复用实例字段。</summary>
    private sealed class AuditContext
    {
        public string Decision { get; set; } = "allow";
        public string Result { get; set; } = "ok";
        public List<AuditRedaction> Redactions { get; } = new();
        public string? Text { get; set; }
        public AuditTarget? Target { get; set; }
        public string? Tier { get; set; }
    }

    private readonly record struct ClientGeometry(
        double X,
        double Y,
        double W,
        double H,
        double CursorX,
        double CursorY,
        double VisualX,
        double VisualY,
        double VisualW,
        double VisualH);

    /// <summary>
    /// 把处理阶段映射成单调递增的进度（0..1）。MCP 要求 progress 严格递增，因此低于上次的
    /// 阶段（例如动作后的刷新观察）会被丢弃；进度写失败只记日志，不能影响正在执行的桌面操作。
    /// </summary>
    private sealed class ProgressReporter
    {
        private readonly Func<double, string?, Task>? _send;
        private readonly object _sync = new();
        private double _last = -1;

        public ProgressReporter(Func<double, string?, Task>? send) => _send = send;

        public async Task Report(double value, string message)
        {
            if (_send is null || !double.IsFinite(value)) return;
            lock (_sync)
            {
                var clamped = Math.Clamp(value, 0, 1);
                if (clamped <= _last) return;
                _last = clamped;
                value = clamped;
            }
            try
            {
                await _send(value, message);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
            {
                FileLog.Write("desktop", $"progress write failed: {error.Message}");
            }
        }
    }

    /// <summary>把操作协同框架的事件转发给覆盖层，渲染层独立于截图重建消费。</summary>
    private sealed class HostOperationObserver : IOperationObserver
    {
        private readonly MainWindow _host;

        public HostOperationObserver(MainWindow host) => _host = host;

        public void Receive(OperationEvent operationEvent)
        {
            switch (operationEvent.Kind)
            {
                case "session-start":
                    _host.PostMessage(new
                    {
                        type = "op-session",
                        sessionId = operationEvent.SessionId,
                        seq = operationEvent.Seq,
                        phase = operationEvent.Phase,
                        session = new
                        {
                            id = operationEvent.Session.Id,
                            title = operationEvent.Session.Title,
                            target = operationEvent.Session.Target,
                        },
                    });
                    break;
                case "control":
                    _host.PostMessage(new
                    {
                        type = "op-control",
                        sessionId = operationEvent.SessionId,
                        seq = operationEvent.Seq,
                        phase = operationEvent.Phase,
                        state = operationEvent.Status,
                    });
                    break;
                case "session-end":
                    _host.PostMessage(new
                    {
                        type = "op-end",
                        sessionId = operationEvent.SessionId,
                        seq = operationEvent.Seq,
                        phase = operationEvent.Phase,
                        session = new
                        {
                            id = operationEvent.Session.Id,
                            status = operationEvent.Status,
                            durationMs = operationEvent.DurationMs,
                        },
                    });
                    break;
                default:
                    var step = operationEvent.Step!;
                    _host.PostMessage(new
                    {
                        type = "op-step",
                        sessionId = operationEvent.SessionId,
                        seq = operationEvent.Seq,
                        phase = operationEvent.Phase,
                        step = new
                        {
                            id = step.Id,
                            kind = step.Kind.ToString().ToLowerInvariant(),
                            label = step.Label,
                            status = step.Status.ToString().ToLowerInvariant(),
                            ms = step.DurationMs,
                            error = step.Error,
                            tier = step.Tier.ToString().ToLowerInvariant(),
                            verified = step.Verified,
                            verification = step.Verification switch
                            {
                                OperationVerificationStatus.Passed => "passed",
                                OperationVerificationStatus.Unverified => "unverified",
                                _ => "not_requested",
                            },
                            evidence = step.Evidence,
                            note = step.Note,
                        },
                    });
                    break;
            }
        }
    }

    // ──────────── P1-M Mirror 帧通道 ────────────

    /// <summary>任务授权成功后启动: 周期性抓帧推给 renderer 做人类 mirror 展示.</summary>
    private void StartMirror()
    {
        try { _mirrorPump.Start(); }
        catch { /* 启动失败不影响任务主流程 */ }
    }

    /// <summary>任务停止时关闭 Mirror 帧通道.</summary>
    private void StopMirror()
    {
        try
        {
            _mirrorPump.Stop();
            _mirrorGate.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch { /* 关闭失败静默 */ }
    }

    /// <summary>Mirror 帧 Pump: 抓帧 → 编码 → PostMessage 推给 renderer.
    /// 脱敏在 renderer 侧用 redactions[] + human-mask.mjs 做 CSS blur, 省掉 Desktop 端的 GPU 读回后再 mask 的双重开销.</summary>
    private async Task PumpMirrorFrameAsync()
    {
        if (!_mirrorGate.Wait(0)) return;  // 上一帧 encode 还没结束, 跳过这轮
        try
        {
            TargetIdentity? identity;
            lock (_sync)
            {
                if (_stopping || _target == null) return;
                identity = _target;
            }

            var capture = await CaptureService.CaptureAsync(identity.Handle);
            if (capture.Frame == null) return;
            if (capture.Occluded) return;  // 遮挡帧跳过, 人类 mirror 应展示目标窗口内容

            var frame = CaptureService.Scale(capture.Frame, _screenshotMax);
            var dataUrl = NativeImage.ToDataUrl(frame);
            var base64 = dataUrl.Substring("data:image/png;base64,".Length);

            _host.PostMessage(new
            {
                type = "frame",
                data = base64,
                mimeType = "image/png",
                width = frame.Width,
                height = frame.Height,
                frameAgeMs = capture.FrameAgeMs,
                captureBackend = capture.Backend,
            });
        }
        catch (Exception error)
        {
            FileLog.Write("desktop", $"mirror pump error: {error.Message}");
        }
        finally
        {
            _mirrorGate.Release();
        }
    }
}
