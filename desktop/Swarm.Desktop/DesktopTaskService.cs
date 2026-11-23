using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Threading;
using Swarm.Core.Actions;
using Swarm.Core.Capture;
using Swarm.Core.Concurrency;
using Swarm.Core.Diagnostics;
using Swarm.Core.Governance;
using Swarm.Core.Operations;
using Swarm.Core.Perception;
using Swarm.Core.Security;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

/// <summary>
/// 桌面侧任务服务：一个 Gateway 进程对应一个 <see cref="TaskSession"/>，会话之间互不阻塞。
/// 这里只保留无状态共享设施（会话表、窗口租约、确认队列、抓屏、OCR 限流），
/// 任何原本是服务级单例的可变状态（快照、掩码、策略、确认令牌、审计、计数）都在会话内。
/// </summary>
public sealed class DesktopTaskService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WindowLeaseTimeout = TimeSpan.FromSeconds(10);
    private const int WindowDriftThreshold = 8;

    private readonly MainWindow _host;
    private readonly SessionRegistry _sessions = new();
    private readonly LeaseManager _leases = new();
    private readonly ConfirmationQueue _confirmationQueue = new();
    // Tesseract 的 Engine 是单图状态机, 并发调用会抛 "Only one image can be processed at once";
    // 因此 OCR 必须串行 (仍在后台线程, 不占用 UI/窗口租约)。
    private readonly SemaphoreSlim _ocrGate = new(1, 1);
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly int _visualLeadMs;
    private readonly int _screenshotMax;
    private readonly double _pixelDiffThreshold;

    // Mirror 帧通道：默认关闭；多会话时只镜像最近活跃的那一个，避免 N 路 PNG 压垮 UI 与内存。
    private readonly DispatcherTimer _mirrorPump = new();
    private readonly SemaphoreSlim _mirrorGate = new(1, 1);
    private readonly bool _mirrorEnabled;
    private readonly int _mirrorIntervalMs;
    private readonly object _mirrorSync = new();
    private string? _mirrorSessionId;
    private int _overlayEpoch;

    // OCR 是共享的 CPU 重活，按容量限流（默认 2 路），超过则排队而不是抢占全部核心。
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
        _mirrorIntervalMs = ReadSetting("SWARM_MIRROR_INTERVAL_MS", 250, 50, 2000);
        _mirrorEnabled = ReadFlag("SWARM_MIRROR");
        _watchdog.Tick += (_, _) => _ = CheckSessionsAsync();
        _watchdog.Start();
        _mirrorPump.Interval = TimeSpan.FromMilliseconds(_mirrorIntervalMs);
        _mirrorPump.Tick += (_, _) => _ = PumpMirrorFrameAsync();
    }

    public bool IsActive => _sessions.Count > 0;

    public bool TargetOwnsForeground
    {
        get
        {
            foreach (var session in _sessions.All())
            {
                if (InputDispatcher.IsForeground(session.Target.Handle)) return true;
            }
            return false;
        }
    }

    public async Task<string> HandleJsonAsync(string json, int gatewayProcessId, Func<double, string?, Task>? progress = null)
    {
        var id = string.Empty;
        string? method = null;
        var started = DateTimeOffset.Now;
        TaskSession? session = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            id = root.GetProperty("id").GetString() ?? throw new InvalidDataException("请求缺少 id");
            method = root.GetProperty("method").GetString() ?? throw new InvalidDataException("请求缺少 method");
            var parameters = root.TryGetProperty("params", out var supplied) && supplied.ValueKind == JsonValueKind.Object
                ? supplied
                : JsonSerializer.SerializeToElement(new { });
            if (gatewayProcessId <= 0) throw new UnauthorizedAccessException("无法验证 Gateway 进程");
            // 渲染层不可用时立即降级，动作仍可执行；仅在就绪阶段短暂等待首帧。
            await _host.TryWaitRendererAsync(TimeSpan.FromMilliseconds(400));
            var reporter = new ProgressReporter(progress);

            object result;
            if (method == "task_begin")
            {
                result = await Begin(parameters, gatewayProcessId, reporter, created => session = created);
            }
            else if (method == "desktop_status")
            {
                result = Status();
            }
            else
            {
                session = RequireSession(gatewayProcessId);
                session.AuditContext = new AuditContext();
                var token = Token(parameters);
                // 同一会话内串行，保证快照/策略/审计一致；不同会话完全并行。
                await session.RequestGate.WaitAsync();
                try
                {
                    session.InFlight++;
                    Validate(token, session);
                    result = method switch
                    {
                        "observe" => await Observe(parameters, session, reporter),
                        "click" => await Click(parameters, session, reporter),
                        "type_text" => await TypeText(parameters, session, reporter),
                        "press_key" => await PressKey(parameters, session, reporter),
                        "scroll" => await Scroll(parameters, session, reporter),
                        "focus" => await Focus(parameters, session, reporter),
                        "task_end" => await End(session, false),
                        "task_stop" => await End(session, true),
                        _ => throw new InvalidOperationException($"未知桌面方法：{method}"),
                    };
                }
                finally
                {
                    session.InFlight--;
                    session.RequestGate.Release();
                }
            }
            return Success(id, result);
        }
        catch (Exception error)
        {
            if (session != null) session.AuditContext.Result = ErrorCode(error);
            return Failure(id, error);
        }
        finally
        {
            if (session != null) WriteAudit(session, method, started);
        }
    }

    /// <summary>兼容旧的可视化闩锁消息；执行不再等待该确认。</summary>
    public void ActionReady(string id)
    {
    }

    public void StopLocal() => _ = StopLocalAsync();

    /// <summary>取消指定 Gateway 的在途动作并保留会话与 token；pid ≤ 0 时取消全部。</summary>
    public void CancelCurrent(int gatewayProcessId = 0)
    {
        var targets = gatewayProcessId > 0
            ? new[] { _sessions.ByGateway(gatewayProcessId) }.Where(session => session != null).Cast<TaskSession>().ToArray()
            : _sessions.All().ToArray();
        foreach (var session in targets)
        {
            if (session.Stopping || session.ManualTakeover) continue;
            session.Coordinator.CancelCurrent();
        }
    }

    /// <summary>低层钩子发现未标记的人工输入后暂停所有会话动作；已交给 Win32 的原子输入不承诺撤回。</summary>
    public void PauseForHumanInput()
    {
        var paused = new List<TaskSession>();
        foreach (var session in _sessions.All())
        {
            if (session.Stopping || session.ManualTakeover) continue;
            session.ManualTakeover = true;
            session.LastSnapshot = null;
            session.LastCapture = null;
            session.ObservedAt = default;
            session.Coordinator.Pause();
            session.Operations.ReportControl("paused");
            paused.Add(session);
        }
        if (paused.Count == 0) return;
        _host.PostMessage(new { type = "task-status", text = "人工接管中：AI 输入已暂停；按 Ctrl+Alt+Shift+R 恢复，恢复后请重新 observe" });
    }

    /// <summary>只允许本地热键调用；恢复后旧观察不可用，AI 必须重新 observe。</summary>
    public void ResumeLocal()
    {
        var resumed = new List<TaskSession>();
        foreach (var session in _sessions.All())
        {
            if (session.Stopping || !session.ManualTakeover) continue;
            session.Coordinator.Resume();
            session.ManualTakeover = false;
            session.LastSnapshot = null;
            session.LastCapture = null;
            session.ObservedAt = default;
            session.Operations.ReportControl("running");
            resumed.Add(session);
        }
        if (resumed.Count == 0) return;
        // 恢复热键本身就是“真实输入”，不划出去的话紧接着的 Tier C 会被空闲门控误挡。
        UserPresenceGuard.MarkResumed();
        _host.PostMessage(new { type = "task-status", text = "已恢复 AI 控制；旧观察已失效，请重新 observe" });
    }

    public async Task StopLocalAsync()
    {
        lock (_mirrorSync)
        {
            _mirrorSessionId = null;
            _mirrorPump.Stop();
        }
        foreach (var session in _sessions.All()) await StopSessionAsync(session);
    }

    private async Task<object> Begin(JsonElement parameters, int gatewayProcessId, ProgressReporter progress, Action<TaskSession> created)
    {
        if (_sessions.ByGateway(gatewayProcessId) != null)
            throw new DesktopOperationException("already_running", "该 Gateway 已有 AI 桌面任务正在运行");
        if (_sessions.Count >= _sessions.Capacity)
            throw new DesktopOperationException("capacity_exceeded", $"已达到并发会话上限 {_sessions.Capacity}，请先结束其它任务");

        var policy = TaskPolicy.Parse(parameters);
        var candidate = WindowLocator.Find(OptionalString(parameters, "targetProcess"), OptionalString(parameters, "targetTitle"), _host.NativeHandle);
        var beginDecision = PolicyEvaluator.Begin(policy, candidate.ProcessName, candidate.Title);
        if (!beginDecision.Allowed)
            throw new DesktopOperationException("policy_denied", $"任务策略拒绝目标：{beginDecision.Reason}");

        var approved = await _confirmationQueue.AskAsync(
            _host,
            "Swarm AI 操作授权",
            $"允许当前 Trae 只读取并操作此应用的窗口内容？\n\n应用：{candidate.ProcessName} ({candidate.ProcessId})\n窗口：{candidate.Title}\n\n任务中不会主动抢走其它程序焦点；窗口被遮挡时优先使用窗口消息。可显示观察、点击、输入和结果。危险操作仍会再次询问，目标在前台时 Esc 可急停。",
            false,
            CancellationToken.None);
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

        var session = new TaskSession
        {
            Id = Guid.NewGuid().ToString("N"),
            GatewayProcessId = gatewayProcessId,
            Token = Guid.NewGuid().ToString("N"),
            Target = identity,
            TargetProcessName = candidate.ProcessName,
            TargetTitle = candidate.Title,
            Policy = policy,
            Operations = new OperationRunner(new HostOperationObserver(_host)),
        };
        session.AuditContext.Target = new AuditTarget(candidate.ProcessName, AuditHash.Sha256(candidate.Title), "window");
        session.Audit = new AuditWriter(AuditWriter.DefaultDirectory, session.TaskId);
        if (_sessions.TryAdd(session, out var errorCode) == null)
        {
            session.Audit.Dispose();
            throw new DesktopOperationException(errorCode ?? "capacity_exceeded", "无法创建新的桌面任务会话");
        }
        created(session);

        session.Operations.BeginSession($"{candidate.ProcessName}: {candidate.Title}", $"pid {candidate.ProcessId}");
        _overlayEpoch++;
        TouchMirror(session);

        _host.ShowTaskOverlay(capture.Monitor, capture.DpiScale);
        _host.PostMessage(new { type = "task-status", text = $"已授权：{candidate.ProcessName}，目标在前台时 Esc 急停" });
        FileLog.Write("desktop", $"task begin pid={candidate.ProcessId} ts={session.Id} process={candidate.ProcessName} title={candidate.Title} capture={capture.Backend}");
        try
        {
            return await ObserveInternal(session, includeScreenshot: true, progress);
        }
        catch
        {
            // Begin 失败必须回滚已写入的会话，否则之后每次 task_begin 都会被“已有任务”拒绝。
            try { await StopSessionAsync(session); }
            catch (Exception cleanup) { FileLog.Write("desktop", $"begin rollback failed: {cleanup.Message}"); }
            throw;
        }
    }

    private async Task<object> Observe(JsonElement parameters, TaskSession session, ProgressReporter progress)
    {
        var include = OptionalBool(parameters, "includeScreenshot");
        return await ObserveInternal(session, include, progress);
    }

    private async Task<object> ObserveInternal(TaskSession session, bool includeScreenshot, ProgressReporter? progress = null)
    {
        var identity = session.Target;
        EnsureIdentity(identity);
        var capture = await CaptureService.CaptureAsync(identity.Handle);
        if (progress != null) await progress.Report(0.2, "capture");
        if (capture.Backend == "screen")
            FileLog.Write("desktop", $"capture fallback handle=0x{identity.Handle:x} occluded={capture.Occluded} blank={capture.Blank}");
        UiaSnapshot? snapshot = null;
        var observation = ++session.ObservationGeneration;
        // 两种降级语义分开：backend=screen 是真的没用窗口抓屏；occluded 只是目标不在前台或被遮挡。
        string? warning = capture.Backend == "screen"
            ? "窗口抓屏不可用，已降级为屏幕裁剪；截图可能包含其他窗口"
            : capture.Occluded
                ? "目标窗口不在前台或已被遮挡；截图可能包含其他窗口"
                : null;
        try
        {
            snapshot = await UiaWatchdog.ReadAsync(identity.Handle, identity.ProcessId, observation, CancellationToken.None);
            if (snapshot == null && UiaWatchdog.IsQuarantined(identity.ProcessId))
                warning = warning == null ? "UIA 读取超时，已临时熔断该进程的 UIA，改用视觉兜底" : $"{warning}；UIA 读取超时，已临时熔断";
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

        // P2 TRISHUL HSP: 不管 UIA 可用与否, 都跑双层粒度 SoM 融合.
        // UIA 可用 → 翻译 UIA elements + OCR 增强 → HSP Build (粗→细 + SEED 空间语义)
        // UIA 不可用 → 纯 OCR → SetOfMarkBuilder.Build (向后兼容)
        string provenance = "uia";
        string? fallbackReason = null;
        List<SoMElement>? somElements = null;

        // viewport 提前算好, HSP 需要用归一化坐标
        var dpiForHsp = capture.DpiScale;
        var clientForHsp = capture.ClientBounds;
        var clientScreenForHsp = capture.ClientScreen;
        var viewportHsp = CoordinateConverter.ClientViewport(clientForHsp, dpiForHsp);
        var viewportW = viewportHsp.Width;
        var viewportH = viewportHsp.Height;

        var ocrLines = Array.Empty<OcrLine>() as IReadOnlyList<OcrLine>;
        if (capture.Frame != null && !capture.Occluded && _ocrService.IsAvailable)
        {
            try
            {
                await _ocrGate.WaitAsync();
                try { ocrLines = await _ocrService.OcrAsync(capture.Frame); }
                finally { _ocrGate.Release(); }
            }
            catch (Exception ocrErr)
            {
                FileLog.Write("desktop", $"observe OCR error: {ocrErr.Message}");
            }
        }

        if (snapshot != null && snapshot.Elements.Count > 0)
        {
            // ===== TRISHUL HSP 融合路径: UIA + OCR 双层粒度 =====
            var (regions, interactives) = TranslateUiaToHsp(
                snapshot.Elements, clientScreenForHsp, dpiForHsp, viewportW, viewportH);

            if (regions.Count > 0 || interactives.Count > 0 || ocrLines.Count > 0)
            {
                somElements = HierarchicalScreenParser.Build(
                    regions, interactives, ocrLines, viewportW, viewportH).ToList();
                provenance = "hsp";
                FileLog.Write("desktop", $"observe HSP regions={regions.Count} interactives={interactives.Count} ocr={ocrLines.Count} somMarks={somElements.Count}");
            }
        }
        else if (ocrLines.Count > 0)
        {
            // ===== 纯 OCR fallback 路径 (向后兼容) =====
            provenance = "ocr";
            fallbackReason = "uia_unavailable";
            somElements = SetOfMarkBuilder.Build(ocrLines).ToList();
            FileLog.Write("desktop", $"observe fallback OCR lines={ocrLines.Count} somMarks={somElements.Count}");
        }
        else if (_ocrService.IsAvailable)
        {
            fallbackReason = "ocr_no_text";
        }
        else
        {
            fallbackReason = "ocr_unavailable";
        }

        if (progress != null) await progress.Report(0.55, "uia");

        var redactionOptions = session.Policy.EffectiveRedaction;
        var hits = snapshot == null
            ? Array.Empty<RedactionHit>()
            : RedactionDetector.Detect(redactionOptions, snapshot.Elements).ToArray();
        var hitByElement = new Dictionary<string, RedactionHit>(StringComparer.Ordinal);
        foreach (var hit in hits) hitByElement[hit.ElementId] = hit;

        lock (session)
        {
            session.LastCapture = capture;
            session.LastSnapshot = snapshot;
            session.ObservedAt = DateTimeOffset.Now;
        }
        _host.ShowTaskOverlay(capture.Monitor, capture.DpiScale);
        TouchMirror(session);

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
                var sensitive = maskedByPolicy || element.IsPassword || session.SensitiveElements.Contains(ElementId.Stable(element.Id)) || (session.MaskAllText && element.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase));
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

        foreach (var hit in hits) session.AuditContext.Redactions.Add(new AuditRedaction(hit.Category, hit.Risk, hit.Necessary));
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
            token = session.Token,
            sessionId = session.Id,
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

    private async Task<object> Click(JsonElement parameters, TaskSession session, ProgressReporter progress)
    {
        var identity = session.Target;
        var (snapshot, capture) = Observation(session);
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
        CheckPolicy(session, "click", crossApp);
        SetActionAudit(session, label, element);
        // 只看调用方显式给出的 label；屏幕元素名不参与策略判定，避免被界面文字注入。
        if (RequiresConfirmation(session, ActionClassifier.Classify(explicitLabel), crossApp))
        {
            var pending = RequestConfirmation(session, parameters, "click", $"点击：{label}");
            if (pending != null) return pending;
        }
        else
        {
            await ConfirmDanger(session, label, element?.ControlType, null);
        }
        session.ActionCount++;
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
        var outcome = await RunAction(session, actionId, "click", label, action, token =>
            RunWithPixelVerification(session, identity.Handle, verify, region,
                inner => InputDispatcher.ClickAsync(identity.Handle, snapshot, elementId, screen.X, screen.Y, true, inner), token));
        await progress.Report(0.7, verify ? "verify" : "execute");
        return await AfterAction(session, actionId, outcome, OptionalBool(parameters, "observe"), progress);
    }

    private async Task<object> TypeText(JsonElement parameters, TaskSession session, ProgressReporter progress)
    {
        var identity = session.Target;
        var (snapshot, capture) = Observation(session);
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
            if (elementId != null) session.SensitiveElements.Add(ElementId.Stable(elementId));
            else session.MaskAllText = true;
        }
        var screen = ToScreen(geometry.CursorX, geometry.CursorY, capture);
        var label = sensitive ? "输入受保护文本" : $"输入：{text.Substring(0, Math.Min(60, text.Length))}";
        var crossApp = IsCrossApp(element, identity);
        CheckPolicy(session, "type_text", crossApp);
        SetActionAudit(session, text, element);
        if (RequiresConfirmation(session, null, crossApp))
        {
            var pending = RequestConfirmation(session, parameters, "type_text", $"输入文本：{label}");
            if (pending != null) return pending;
        }
        session.ActionCount++;
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
        var outcome = await RunAction(session, actionId, "type", label, action, token =>
            RunWithPixelVerification(session, identity.Handle, verify, region,
                inner => InputDispatcher.TypeTextAsync(identity.Handle, snapshot, elementId, text, screen.X, screen.Y, true, inner), token));
        await progress.Report(0.7, verify ? "verify" : "execute");
        return await AfterAction(session, actionId, outcome, OptionalBool(parameters, "observe"), progress);
    }

    private async Task<object> PressKey(JsonElement parameters, TaskSession session, ProgressReporter progress)
    {
        var identity = session.Target;
        var (snapshot, capture) = Observation(session);
        EnsureWindowStable(capture);
        var key = RequiredString(parameters, "key");
        CheckPolicy(session, "press_key", false);
        SetActionAudit(session, key, null);
        if (RequiresConfirmation(session, ActionClassifier.Classify(null, key), false))
        {
            var pending = RequestConfirmation(session, parameters, "press_key", $"按键：{key}");
            if (pending != null) return pending;
        }
        else
        {
            await ConfirmDanger(session, $"按键 {key}", "keyboard", key);
        }
        session.ActionCount++;
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
        var outcome = await RunAction(session, actionId, "key", $"按键：{key}", action, token =>
            RunWithPixelVerification(session, identity.Handle, verify, region,
                inner => InputDispatcher.PressKeyAsync(identity.Handle, snapshot, key, screen.X, screen.Y, true, inner), token));
        await progress.Report(0.7, verify ? "verify" : "execute");
        return await AfterAction(session, actionId, outcome, OptionalBool(parameters, "observe"), progress);
    }

    private async Task<object> Scroll(JsonElement parameters, TaskSession session, ProgressReporter progress)
    {
        var identity = session.Target;
        var (snapshot, capture) = Observation(session);
        EnsureWindowStable(capture);
        CheckPolicy(session, "scroll", false);
        SetActionAudit(session, null, null);
        var x = RequiredNumber(parameters, "x");
        var y = RequiredNumber(parameters, "y");
        var delta = RequiredNumber(parameters, "delta");
        if (!ScrollWheelDelta.TryNormalize(delta, out var scrollDelta))
            throw new ArgumentException($"scroll 的 delta 必须是 -{ScrollWheelDelta.MaxMagnitude} 到 {ScrollWheelDelta.MaxMagnitude} 之间的有限数", "delta");
        var verify = PixelVerify(parameters);
        var region = new RectInt(0, 0, capture.ClientBounds.Width, capture.ClientBounds.Height);
        var geometry = PointGeometry(x, y, capture);
        var screen = ToScreen(geometry.CursorX, geometry.CursorY, capture);
        session.ActionCount++;
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
        var outcome = await RunAction(session, actionId, "scroll", $"滚动 {scrollDelta}", action, token =>
            RunWithPixelVerification(session, identity.Handle, verify, region,
                inner => InputDispatcher.ScrollAsync(identity.Handle, snapshot, screen.X, screen.Y, scrollDelta, true, inner), token));
        await progress.Report(0.7, verify ? "verify" : "execute");
        return await AfterAction(session, actionId, outcome, OptionalBool(parameters, "observe"), progress);
    }

    private async Task<object> Focus(JsonElement parameters, TaskSession session, ProgressReporter progress)
    {
        var identity = session.Target;
        var capture = session.LastCapture ?? throw new InvalidOperationException("请先 observe");
        EnsureWindowStable(capture);
        CheckPolicy(session, "focus", false);
        SetActionAudit(session, null, null);
        var geometry = WindowGeometry(capture);
        session.ActionCount++;
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
        var outcome = await RunAction(session, actionId, "focus", "切换到目标窗口", action, token =>
        {
            token.ThrowIfCancellationRequested();
            return RunMutating(session, identity.Handle, _ => Task.FromResult(InputDispatcher.Focus(identity.Handle)), token);
        });
        await progress.Report(0.7, "execute");
        return await AfterAction(session, actionId, outcome, OptionalBool(parameters, "observe"), progress);
    }

    private async Task<object> End(TaskSession session, bool stopped)
    {
        await StopSessionAsync(session);
        FileLog.Write("desktop", stopped ? "task stop" : "task end");
        return new { ok = true, stopped, sessionId = session.Id };
    }

    private TaskSession RequireSession(int gatewayProcessId) =>
        _sessions.ByGateway(gatewayProcessId) ?? throw new UnauthorizedAccessException("Gateway 或任务会话无效");

    private static void Validate(string token, TaskSession session)
    {
        if (session.Stopping) throw new DesktopOperationException("stopped", "任务正在停止");
        if (session.ManualTakeover) throw new DesktopOperationException("manual_takeover", "人工接管中；本地恢复后请重新 observe");
        if (!string.Equals(session.Token, token, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Gateway 或任务会话无效");
        EnsureIdentity(session.Target);
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

    private static (UiaSnapshot? Snapshot, WindowCapture Capture) Observation(TaskSession session)
    {
        lock (session)
        {
            if (session.LastCapture == null) throw new InvalidOperationException("请先 observe");
            if (DateTimeOffset.Now - session.ObservedAt > SnapshotTtl)
                throw new DesktopOperationException("stale_observation", "观察已过期，请重新 observe");
            return (session.LastSnapshot, session.LastCapture);
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
        TaskSession session,
        string id,
        string kind,
        string label,
        object visual,
        Func<CancellationToken, Task<ActionOutcome>> body)
    {
        ActionOutcome? outcome = null;
        try
        {
            var step = new OperationStep { Id = id, Kind = OperationStep.ParseKind(kind), Label = label };
            await session.Operations.RunAsync(
                step,
                async token =>
                {
                    // 视觉提前量与像素验证都在临界区之外：全局动作锁只覆盖真正改变 UI 的那一小段。
                    await VisualLead(visual, token);
                    var result = await body(token);
                    outcome = result;
                    step.ReportTransport(result.Tier, result.Verified, result.Note, result.Evidence);
                },
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
            if (outcome != null)
            {
                session.AuditContext.Tier = outcome.Tier.ToString().ToLowerInvariant();
                TierCounters.Record(outcome.Tier);
            }
            return outcome ?? new ActionOutcome(InputTier.None, false);
        }
        catch (Exception error)
        {
            _host.PostMessage(new { type = "action-result", result = new { id, ok = false, error = error.Message } });
            throw;
        }
    }

    /// <summary>写动作先取窗口租约（同窗口串行、不同窗口并行），再进全局动作临界区。</summary>
    private async Task<ActionOutcome> RunMutating(TaskSession session, nint window, Func<CancellationToken, Task<ActionOutcome>> body, CancellationToken token)
    {
        using var lease = await AcquireWindowLeaseAsync(session, window, token);
        return await MutateUnderLease(session, window, body, token);
    }

    private async Task<ActionOutcome> MutateUnderLease(TaskSession session, nint window, Func<CancellationToken, Task<ActionOutcome>> body, CancellationToken token)
    {
        var cursor = CursorRestore.Capture();
        try
        {
            var outcome = await session.Coordinator.RunAsync(_ => Task.CompletedTask, body, token);
            // 写动作让该窗口的缓存帧与在途观察失效，后续抓屏一定拿到新画面。
            session.WriteGeneration++;
            CaptureService.Invalidate(window);
            return outcome;
        }
        finally
        {
            CursorRestore.RestoreIfIdle(cursor);
        }
    }

    private async Task<IDisposable> AcquireWindowLeaseAsync(TaskSession session, nint window, CancellationToken token)
    {
        try
        {
            return await _leases.AcquireWindowAsync(window, session.Id, WindowLeaseTimeout, token);
        }
        catch (LeaseTimeoutException)
        {
            throw new DesktopOperationException("window_busy", "同一窗口的其它会话正在操作，请稍后重试", "lease_timeout");
        }
    }

    /// <summary>只推进可视化，不等待渲染层确认；最多占用配置的视觉提前量。</summary>
    private async Task VisualLead(object action, CancellationToken token)
    {
        _host.PostMessage(new { type = "action-prepare", action });
        var lead = _visualLeadMs;
        if (lead <= 0) return;
        await Task.Delay(lead, token);
    }

    /// <summary>
    /// 显式像素验证：动作前取帧，动作后连续两帧确认画面稳定，再比较目标区域的变化比例。
    /// before/after 抓屏都在动作临界区之外，只占用窗口租约，避免把 240ms 采样塞进全局锁。
    /// </summary>
    private async Task<ActionOutcome> RunWithPixelVerification(
        TaskSession session,
        nint window,
        bool verify,
        RectInt region,
        Func<CancellationToken, Task<ActionOutcome>> body,
        CancellationToken token)
    {
        using var lease = await AcquireWindowLeaseAsync(session, window, token);
        if (!verify) return await MutateUnderLease(session, window, body, token);

        var before = await CaptureService.CaptureAsync(window, token, allowReuse: false);
        var outcome = await MutateUnderLease(session, window, body, token);
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

    private async Task<object> AfterAction(TaskSession session, string actionId, ActionOutcome outcome, bool observe, ProgressReporter? progress = null)
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
                observation = await ObserveInternal(session, false, progress),
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
    private static void CheckPolicy(TaskSession session, string kind, bool crossApp)
    {
        var decision = PolicyEvaluator.Action(session.Policy, kind, DateTimeOffset.Now - session.TaskStartedAt, session.ActionCount, crossApp);
        if (decision.Allowed) return;
        session.AuditContext.Decision = "denied:" + decision.Reason;
        throw new DesktopOperationException("policy_denied", $"任务策略拒绝本次动作：{decision.Reason}");
    }

    private static bool RequiresConfirmation(TaskSession session, string? category, bool crossApp) =>
        (category != null && session.Policy.RequiresConfirmation(category)) ||
        (crossApp && session.Policy.RequiresConfirmation("cross_app"));

    /// <summary>没有 confirmToken 时签发一次性令牌并返回 needs_confirmation；有令牌时校验并放行。</summary>
    private static object? RequestConfirmation(TaskSession session, JsonElement parameters, string method, string summary)
    {
        var fingerprint = ActionFingerprint.Compute(method, parameters);
        var token = OptionalString(parameters, "confirmToken");
        if (token != null)
        {
            if (!session.Confirmations.TryConsume(session.TaskId, token, fingerprint, out var reason))
            {
                session.AuditContext.Decision = "denied:confirmation_invalid";
                throw new DesktopOperationException("confirmation_invalid", $"确认令牌无效：{reason}");
            }
            session.AuditContext.Decision = "confirm_consumed";
            return null;
        }
        var issued = session.Confirmations.Issue(session.TaskId, fingerprint);
        session.AuditContext.Decision = "confirm_required";
        return new { status = "needs_confirmation", confirmToken = issued.Token, summary, expiresInMs = issued.ExpiresInMs };
    }

    private static void SetActionAudit(TaskSession session, string? text, UiaElementInfo? element)
    {
        session.AuditContext.Text = text;
        session.AuditContext.Target = element == null
            ? new AuditTarget(session.TargetProcessName, null, null)
            : new AuditTarget(session.TargetProcessName, AuditHash.Sha256(element.Name), element.ControlType);
    }

    private static void WriteAudit(TaskSession session, string? method, DateTimeOffset started)
    {
        var writer = session.Audit;
        if (writer == null || method == null) return;
        try
        {
            writer.Write(new AuditEntry(
                session.TaskId,
                DateTimeOffset.Now,
                method,
                session.AuditContext.Tier,
                session.AuditContext.Target,
                session.AuditContext.Decision,
                session.AuditContext.Redactions.ToArray(),
                AuditHash.Sha256(session.AuditContext.Text),
                session.AuditContext.Text?.Length ?? 0,
                session.AuditContext.Result,
                (long)Math.Max(0, (DateTimeOffset.Now - started).TotalMilliseconds)));
        }
        catch (Exception error)
        {
            FileLog.Write("desktop", $"audit write failed: {error.Message}");
        }
    }

    private async Task ConfirmDanger(TaskSession session, string label, string? semantic, string? key)
    {
        if (!RiskAssessment.RequiresConfirmation(label, semantic, key)) return;
        var approved = await _confirmationQueue.AskAsync(
            _host,
            "Swarm 危险操作确认",
            $"AI 请求执行：\n\n{label}\n\n这可能关闭窗口、删除、发送、提交或产生其他外部影响。是否只执行这一次？",
            true,
            CancellationToken.None);
        if (!approved) throw new UnauthorizedAccessException("用户拒绝了危险操作");
    }

    private async Task CheckSessionsAsync()
    {
        foreach (var session in _sessions.All())
        {
            var reason = (string?)null;
            if (!NativeMethods.IsWindow(session.Target.Handle)) reason = "目标窗口已关闭，任务已停止";
            else
            {
                try
                {
                    if (Process.GetProcessById(session.GatewayProcessId).HasExited) reason = "Gateway 已退出，任务已停止";
                }
                catch (ArgumentException)
                {
                    reason = "Gateway 已退出，任务已停止";
                }
            }

            if (reason == null) continue;
            _host.PostMessage(new { type = "task-status", text = $"{reason}（{session.TargetProcessName}）" });
            await StopSessionAsync(session);
        }
    }

    private async Task StopSessionAsync(TaskSession session)
    {
        lock (session)
        {
            if (session.Stopping) return;
            session.Stopping = true;
        }

        StopMirror(session.Id);
        try
        {
            await session.Operations.StopAsync(TimeSpan.FromMilliseconds(800));
        }
        catch
        {
            // 收尾阶段的异常不应阻止释放会话。
        }

        session.Coordinator.Stop();
        _sessions.Remove(session);
        UiaWatchdog.Reset(session.Target.ProcessId);
        session.Audit?.Dispose();
        session.Audit = null;

        // 覆盖层保留一小段时间展示结束摘要，再隐藏；期间若有新会话开始则不隐藏。
        // 必须脱离调用链：否则每次 task_end 都要多等 1.2s 才返回。
        var epoch = Volatile.Read(ref _overlayEpoch);
        _ = HideOverlayLaterAsync(epoch);
    }

    private async Task HideOverlayLaterAsync(long epoch)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        if (epoch == Volatile.Read(ref _overlayEpoch) && _sessions.Count == 0) _host.HideTaskOverlay();
    }

    private object Status()
    {
        var isolation = IsolationProbe.Detect();
        return new
        {
            sessions = _sessions.All().Select(session => new
            {
                sessionId = session.Id,
                gatewayProcessId = session.GatewayProcessId,
                processName = session.TargetProcessName,
                title = session.TargetTitle,
                handle = session.Target.Handle.ToInt64(),
                actions = session.ActionCount,
                inFlight = session.InFlight,
                manualTakeover = session.ManualTakeover,
                observedAt = session.ObservedAt == default ? null : session.ObservedAt.ToString("O"),
                ageMs = (long)Math.Max(0, (DateTimeOffset.Now - session.TaskStartedAt).TotalMilliseconds),
            }).ToArray(),
            capacity = _sessions.Capacity,
            active = _sessions.Count,
            isolation = new { mode = isolation.Mode, hardAvailable = isolation.HardAvailable, reasons = isolation.Reasons },
            capture = CaptureService.Stats(),
            leases = _leases.Snapshot(),
            uia = UiaWatchdog.Snapshot(),
            tiers = TierCounters.Snapshot(),
            mirrorEnabled = _mirrorEnabled,
        };
    }

    private static (List<HspRegion> Regions, List<HspInteractive> Interactives) TranslateUiaToHsp(
        IReadOnlyList<UiaElementInfo> elements,
        RectInt clientScreen,
        double dpiScale,
        double viewportW,
        double viewportH)
    {
        var regions = new List<HspRegion>();
        var interactives = new List<HspInteractive>();

        // 粗粒度容器 ControlType → RegionType 映射
        static string? MapRegionType(string ct) => ct switch
        {
            "TitleBar" => "TitleBar",
            "MenuBar" or "ToolBar" or "SplitButton" or "SystemMenuBar" => "MenuBar",
            "StatusBar" or "ScrollBar" => "StatusBar",
            _ => null,
        };

        // 可交互 ControlType → RegionType
        static bool IsInteractive(string ct) => ct switch
        {
            "Button" or "SplitButton" or "CheckBox" or "RadioButton" or "Hyperlink"
            or "MenuItem" or "ComboBox" or "Edit" or "TabItem" or "ListItem"
            or "Slider" => true,
            _ => false,
        };

        foreach (var el in elements)
        {
            if (!el.HasVisual || el.Bounds.Width <= 0 || el.Bounds.Height <= 0) continue;
            if (!el.Bounds.Intersects(clientScreen)) continue;

            // 归一化坐标: ToClient 返回 CSS pixel, 再 / viewport 得 [0,1]
            var clientBounds = CoordinateConverter.ToClient(el.Bounds, clientScreen, dpiScale);
            double nx = viewportW > 0 ? clientBounds.X / viewportW : 0;
            double ny = viewportH > 0 ? clientBounds.Y / viewportH : 0;
            double nw = viewportW > 0 ? (double)clientBounds.Width / viewportW : 0;
            double nh = viewportH > 0 ? (double)clientBounds.Height / viewportH : 0;

            if (nw <= 0 || nh <= 0) continue;

            var regionType = MapRegionType(el.ControlType);
            if (regionType != null && (nw > 0.10 || nh > 0.05))
            {
                // 大尺寸容器才算 Layer 1 (排除小 MenuItem 被误判为 MenuBar)
                regions.Add(new HspRegion(el.Id, regionType, nx, ny, nw, nh));
            }

            if (IsInteractive(el.ControlType))
            {
                var text = (el.Value ?? el.Name ?? "").Trim();
                interactives.Add(new HspInteractive(
                    SourceId: el.Id,
                    UiaControlType: el.ControlType,
                    Text: string.IsNullOrEmpty(text) ? el.ControlType : text,
                    X: nx, Y: ny, W: nw, H: nh,
                    Confidence: 1.0));
            }
        }

        // 特殊: 找 Document 类型作为 ContentArea
        foreach (var el in elements.Where(e => e.ControlType.Equals("Document", StringComparison.OrdinalIgnoreCase) && e.HasVisual))
        {
            var clientBounds = CoordinateConverter.ToClient(el.Bounds, clientScreen, dpiScale);
            double nx = viewportW > 0 ? clientBounds.X / viewportW : 0;
            double ny = viewportH > 0 ? clientBounds.Y / viewportH : 0;
            double nw = viewportW > 0 ? (double)clientBounds.Width / viewportW : 0;
            double nh = viewportH > 0 ? (double)clientBounds.Height / viewportH : 0;
            if (nw > 0 && nh > 0)
                regions.Add(new HspRegion(el.Id, "ContentArea", nx, ny, nw, nh));
        }

        return (regions, interactives);
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

    private static bool ReadFlag(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.Equals(raw, "1", StringComparison.Ordinal) || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string Success(string id, object result) => JsonSerializer.Serialize(new { id, ok = true, result }, JsonOptions);
    private static string ErrorCode(Exception error) => error switch
    {
        DesktopOperationException desktop => desktop.Code,
        LeaseTimeoutException => "window_busy",
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

    /// <summary>多会话时只镜像最近活跃的一个；未显式开启（SWARM_MIRROR=1）时完全不启动。</summary>
    private void TouchMirror(TaskSession session)
    {
        if (!_mirrorEnabled) return;
        lock (_mirrorSync)
        {
            _mirrorSessionId = session.Id;
            if (!_mirrorPump.IsEnabled) _mirrorPump.Start();
        }
    }

    private void StopMirror(string sessionId)
    {
        lock (_mirrorSync)
        {
            if (_mirrorSessionId != sessionId) return;
            _mirrorSessionId = null;
            _mirrorPump.Stop();
        }
    }

    /// <summary>Mirror 帧 Pump: 抓帧 → 编码 → PostMessage 推给 renderer.
    /// 脱敏在 renderer 侧用 redactions[] + human-mask.mjs 做 CSS blur, 省掉 Desktop 端的 GPU 读回后再 mask 的双重开销.</summary>
    private async Task PumpMirrorFrameAsync()
    {
        if (!_mirrorGate.Wait(0)) return;  // 上一帧 encode 还没结束, 跳过这轮
        try
        {
            TaskSession? session;
            lock (_mirrorSync)
            {
                session = _mirrorSessionId == null ? null : _sessions.ById(_mirrorSessionId);
            }
            if (session == null || session.Stopping) return;

            var capture = await CaptureService.CaptureAsync(session.Target.Handle);
            if (capture.Frame == null) return;
            if (capture.Occluded) return;  // 遮挡帧跳过, 人类 mirror 应展示目标窗口内容

            var frame = CaptureService.Scale(capture.Frame, _screenshotMax);
            var dataUrl = NativeImage.ToDataUrl(frame);
            var base64 = dataUrl.Substring("data:image/png;base64,".Length);

            _host.PostMessage(new
            {
                type = "frame",
                sessionId = session.Id,
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
