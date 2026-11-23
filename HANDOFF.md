# HANDOFF — swarm-cursors

> 最后更新: 2026-11-23 · HEAD: `8a6f1ce` (branch: `chore/net10`)
> 状态: ✅ 可跑通 · 待实施并发优化 · 无阻塞

---

## 一、项目是什么

**swarm-cursors** 是一个让多个 AI agent 同时控制 Windows 桌面的后端：

```
AI Agent × N  ──MCP stdio──▶  Gateway × N  ──NamedPipe──▶  Desktop (单进程)
                                                              │
                                                              ├─ observe  (截图 + UIA + TRISHUL HSP + Tesseract OCR)
                                                              ├─ click    (3 层输入策略)
                                                              ├─ type
                                                              ├─ task_begin / task_end
                                                              └─ Word 桥接 (.docx 纯托管)
```

- **架构**: MCP Gateway (per-agent 独立进程) → NamedPipe IPC → Desktop WPF 单进程
- **语言**: C# / .NET 10 (net10.0-windows)
- **平台**: Windows only (10/11, x64)
- **目标**: 零用户侧安装（OCR/视觉/Word 全内嵌）
- **测试**: 373 通过 · 0 失败 · 0 警告 (dotnet build)

---

## 二、代码地图（202 个 .cs 文件）

```
desktop/
├── Swarm.Core/              # 纯类库, 无 UI, 可单测
│   ├── Perception/
│   │   ├── HierarchicalScreenParser.cs    # 🆕 TRISHUL HSP 双层粒度融合
│   │   ├── SetOfMarkBuilder.cs            # SoM 构建器 + ClassifyStatic() 公开入口
│   │   ├── OcrModels.cs                   # SoMElement (含 HSP 字段, 全有默认值)
│   │   ├── NativeUiaSnapshot.cs           # UIA 快照模型
│   │   └── TesseractOcrService.cs         # 🆕 TesseractOCR 5.5.1 内嵌 Native
│   ├── Operations/
│   │   └── OperationStep.cs               # InputTier 枚举: None/Uia/Message/Input
│   ├── WordBridge/
│   │   └── FileBasedWordBridge.cs         # 🆕 DocumentFormat.OpenXml 3.1.0 纯托管
│   ├── ActionCoordinator.cs               # per-session 门 + 泛型 RunAsync；跨会话不再共享锁
│   ├── Ipc/
│   │   └── GatewayIpcProtocol.cs          # 网关 ↔ Desktop 协议 (ok/result/error)
│   └── Input/
│       └── InputTier.cs                   # Tier 枚举 + RouteDecision 占位 (待扩展)
│
├── Swarm.Desktop/           # WPF + Win32, 有真实 UI
│   ├── InputDispatcher.cs                 # 🎯 三层输入完整实现 Tier A/B/C
│   ├── NativeUiaSession.cs                # Native UIA vtable hack, AutoSetFocus=false
│   ├── DesktopTaskService.cs              # observe + click/type 调度 (938 行 RunAction 包全局锁)
│   ├── DesktopIpcServer.cs                # NamedPipeServerStream("SwarmDesktop.V1")
│   ├── DesktopControlServer.cs            # Desktop 启动/生命周期
│   └── App.xaml / MainWindow.xaml.cs      # WebView2 + OnLoaded 才启动 IPC
│
├── Swarm.Gateway/           # MCP stdio server (per-agent 独立进程)
│   ├── McpGateway.cs                      # MCP handlers: tools/list, tools/call
│   ├── DesktopGatewayTransport.cs         # NamedPipeClientStream 每次请求新建
│   └── GatewayIpcProtocol.cs              # 请求/响应协议层
│
├── Swarm.Tests/              # 373 通过
│   ├── HierarchicalScreenParserTests.cs   # 🆕 14 HSP 测试 (含 bug 修复验证)
│   ├── ActionCoordinatorTests.cs           # 锁语义 + generation token 测试
│   ├── InputDispatcherTierATests.cs        # UIA 层测试
│   ├── InputDispatcherTierBTests.cs        # PostMessage 层测试
│   ├── DesktopTaskServiceTests.cs          # observe 融合 + fallback 测试
│   └── WordBridgeTests.cs                  # FileBasedWordBridge 测试
│
└── tessdata/                # OCR 语言包
    ├── eng.traineddata     (3.9 MB)
    └── chi_sim.traineddata (2.4 MB)
```

### 文件大小速查

| 文件 | 行数 | 说明 |
|---|---|---|
| `DesktopTaskService.cs` | ~1800 | 最大文件, observe + 调度 + IPC handler |
| `InputDispatcher.cs` | ~800 | 三层输入完整实现 |
| `HierarchicalScreenParser.cs` | ~208 | HSP Build / MergeOverlappingLayer2 / Containment / SEED |
| `ActionCoordinator.cs` | ~5 | 全局 SemaphoreSlim(1,1) |
| `OcrModels.cs` | ~60 | SoMElement record + HSP 扩展字段 |
| `McpGateway.cs` | ~400 | MCP handlers 路由 |
| `DesktopGatewayTransport.cs` | ~80 | NamedPipeClientStream (per-request) |

### NuGet 依赖（已冻结, 不要再改版本）

```
Swarm.Desktop:
  TesseractOCR 5.5.1            # OCR, 内嵌 Native DLL (唯一 OCR 引擎)
  Vortice.Direct3D11 3.8.3      # 截图/镜像通道
  Microsoft.Web.WebView2 1.0.4314.35  # Desktop UI 壳

Swarm.Core:
  DocumentFormat.OpenXml 3.1.0  # Word 解析, 纯托管

Swarm.Tests:
  xUnit 2.9.2 + Moq 4.20 + FluentAssertions 6.12
```

---

## 三、最近 10 个 Commit（chore/net10 分支, HEAD→旧）

```
8a6f1ce docs(skill): gui-grounding-research — 最优解决策框架  [LOCAL only, 待 push]
54f8a28 test(p2): 14 TRISHUL HSP unit tests + fix OverlapsAnyUia self-filter bug
a1f706b feat(p2): TRISHUL HSP — training-free dual-granularity SoM fusion (UIA + OCR)
fbe47ae feat(p1w): FileBasedWordBridge — DocumentFormat.OpenXml 3.1.0 纯托管 .docx 解析
7aa27b6 feat(p2): TesseractOcrService — TesseractOCR 5.5.1 内嵌 Native OCR 引擎
4c116a3 feat(p2): OCR/SoM 视觉兜底 — Core 接口 + SoM 规则引擎 + Desktop FallbackObserve 路径
f66e310 feat(word): P1-W Word provider T2 基线 + Gateway wordProvider 上报
4646e85 feat(mirror): P1-M Mirror 帧通道 - Desktop Pump + Renderer frame 监听
aa93c34 feat(gateway): MRTR 补 ttlMs/cacheScope + -32022 预协商失败码
068c958 feat(renderer): P1-M intent 字段 + 人类 mirror 高斯模糊遮罩
```

### Push 状态

- `54f8a28` 及之前的 commit → ✅ 已 push 到 origin/chore/net10
- `8a6f1ce` (skill) → 🟡 本地 commit 已创建, 网络恢复后 `git push` 即可

### Untracked 临时文件（不要 commit）

```
desktop/action-coordinator-demo/   # 并发演示 .NET demo
desktop/fake-desktop/              # pipe 模拟 server (调试 Gateway)
desktop/pipe-test/                 # pipe client 调试工具
desktop/multi-agent-scene.mjs      # Node 并发 Gateway 场景脚本
desktop/swarm-swarm-scene.mjs      # 3 agent 并发演示脚本
desktop/*.mjs                      # 所有 Node 测试脚本
desktop/debug-*.mjs, v-test.mjs, quick-test.mjs
```

这些都是为了排查 pipe 连接问题时写的，全部可以直接删除。

---

## 四、三层输入策略（核心实现已完整, 不要推翻）

```
                    InputDispatcher.cs 调用链
                    ═══════════════════════════

  ClickAsync / TypeTextAsync / PressKeyAsync / ScrollAsync
       │
       ▼
  ┌─ Tier A: UIA Pattern Invocation ─────────────────────┐
  │   NativeUiaSession.TryNativeClick()  (AutoSetFocus=false)  │
  │   InvokePattern.Invoke()                                   │
  │   TogglePattern.Toggle()                                   │
  │   SelectionItemPattern.Select()                           │
  │   ExpandCollapsePattern                                   │
  │                                                           │
  │   ✅ 零焦点  ✅ 零光标  ✅ 遮挡也能用  ✅ 线程安全          │
  │   ❌ 约 30% 控件没有 Pattern (自绘/OpenGL)                  │
  └──────────────────────┬───────────────────────────────────┘
                         │ IsUnavailableException
                         ▼
  ┌─ Tier B: PostMessage 定向注入 ──────────────────────┐
  │   MessagesReliable(snapshot.Capabilities.MessageReliable)  │
  │   ResolveTarget → ChildWindowFromPointEx (CWP_SKIP*)      │
  │   PostMessage(hwnd, WM_LBUTTONDOWN/UP)                     │
  │   PostMessage(BM_CLICK) for BUTTON className              │
  │   ConfirmWmTextAsync (WM_GETTEXT 轮询 400ms)               │
  │                                                           │
  │   ✅ 零焦点  ✅ 零光标  ✅ 遮挡也能用                      │
  │   ❌ 约 40% 应用忽略 PostMessage (Chromium/OpenGL)          │
  │   ⚠️ 同 hwnd OS 保证消息队列串行 (不需要显式锁, 但 WindowScopeLock 会加) │
  └──────────────────────┬───────────────────────────────────┘
                         │ MessagesReliable=false 或 ResolveTarget 失败
                         ▼
  ┌─ Tier C: SendInput 全局注入 ────────────────────────┐
  │   EnsureForeground (SetForegroundWindow + 等待 200ms)      │
  │   SendInput(INPUT struct[]) → kernel32.dll                 │
  │                                                           │
  │   ✅ 100% 应用兼容 (游戏/OpenGL/DX/Canvas)                 │
  │   ❌ 抢焦点  ❌ 动光标  ❌ OS 全局单队列 → 必须串行          │
  │   🔴 SendInput 文档明确: "events are not interspersed"     │
  └───────────────────────────────────────────────────────────┘
```

### Tier 线程安全审计

| 组件 | 线程安全 | 证据 |
|---|---|---|
| `AutomationElement.FromPoint` | ✅ | 每次独立 COM 对象 |
| `NativeUiaSession.TryCreate()` | ✅ | `using` Dispose + per-call new |
| `UiaOutcome` | ✅ | 静态方法 + 局部变量 |
| `PostClick/PostText` | ✅ | Win32 投递, OS per-hwnd 串行 |
| `ResolveTarget` | ✅ | 只读 Win32 API |
| **ActionCoordinator._gate** | ⚠️ | 全局共享, 但 Tier A/B 本不需要 |

### NativeUiaSession (Tier A 关键细节)

`Swarm.Desktop/NativeUiaSession.cs` — 通过 vtable hack 绕过 UIA 默认 SetFocus：

```csharp
// uiautomationclient.h IUIAutomation2 AutoSetFocus = slot 58/59
// 设置为 false 后, InvokePattern.Invoke() 不会触发目标控件 SetFocus
// 从而实现"零焦点点击"
```

这是 Tier A 不抢焦点的核心机制, 不要改。

---

## 五、串行瓶颈分析（P0 已修复, 保留作对照）

### 5.1 全局锁问题

`Swarm.Core/ActionCoordinator.cs`:
```csharp
private readonly SemaphoreSlim _gate = new(1, 1);  // 全局单槽
```

`DesktopTaskService.cs:938` RunAction 统一把所有 Tier 都包在这把锁里：

```csharp
await coordinator.RunAsync(
    step, token => coordinator.RunAsync(
        inner => VisualLead(visual, inner),
        async inner => {
            var result = await body(inner);  // 这里 body 内部调用 InputDispatcher
            // body 可能走 Tier A (零锁) 或 Tier B (per-hwnd 锁)
            // 但全部被这把全局 SemaphoreSlim 串了
        },
        token),
    TimeSpan.FromSeconds(20));
```

### 5.2 瓶颈量化（仿真）

**场景: 3 agent 同时操作 3 个不同窗口**

| 指标 | 当前 | 最优后 | 提升 |
|---|---|---|---|
| observe (400ms×3) | 串行 1200ms | 并行 400ms + 去重 | **3×** |
| Tier A 调用 (notepad/chrome/explorer 都有 Invoke) | 排队 | 零锁, 同时 | **3×** |
| Tier B 调用 (3 个不同 hwnd) | 排队 | per-hwnd 并行 | **2.5×** |
| Tier C (游戏场景, 只有 1 个用 SendInput) | 排队 | 只有这个排队, Tier A/B 并行 | **混合 1.5–2×** |
| 总耗时 estimate | 1783ms | ~600ms | **≈3×** |

### 5.3 SendInput 硬约束（不可破解）

| 约束 | 来源 | 含义 |
|---|---|---|
| **SendInput 全局串行** | [Microsoft Learn](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput): "events are not interspersed" | 任何进程/线程调 SendInput 都进 OS 同一个输入队列 |
| **物理单光标** | Windows kernel | 除非驱动级方案 (MouseMux), 否则 OS 只渲染一个 |
| **PostMessage 不聚焦** | Win32 API 语义 | 投递消息, 不触发 SetForegroundWindow |
| **UIA InvokePattern 无需焦点** | [Microsoft Learn Invoke Pattern](https://learn.microsoft.com/windows/win32/winauto/uiauto-implementinginvoke) | 对 minimized/background 窗口按钮有效 |

---

## 六、SERPENT 并发最优解（已落地, P0–P6）

### 核心改动: ActionCoordinator → WindowScopeLock

```
┌──────────────────────────────────────────────────────────────┐
│ WindowScopeLock (替代全局 SemaphoreSlim)                       │
│                                                                │
│   Tier A  → 零锁 (每个 UIAElement 独立, 无共享状态)            │
│   Tier B  → Per-Hwnd Semaphore (同一窗口串行, 跨窗口并行)       │
│   Tier C  → 全局 SemaphoreSlim(1,1) (只有 SendInput 用)        │
└──────────────────────────────────────────────────────────────┘
```

### 新增组件 (零破坏性, 纯新增)

| 文件 | 行数 | 说明 |
|---|---|---|
| `Swarm.Core/Input/InputTier.cs` | ~80 | InputTier 枚举 + RouteDecision record + InputRouter |
| `Swarm.Core/WindowScopeLock.cs` | ~50 | 三级锁实现 (ConditionalWeakTable per-hwnd) |
| `Swarm.Core/ObserveDeduplicator.cs` | ~60 | 跨窗口并行 + 同窗口 1.5s TTL 去重 |
| `Swarm.Desktop/TaskDispatcher.cs` | ~100 | 替换 ActionCoordinator, 带路由决策 |

### 修改点 (低风险)

| 文件 | 改动 |
|---|---|
| `DesktopTaskService.cs:938` | RunAction 换成 TaskDispatcher.DispatchAsync, 保留 gateway 协议不变 |
| `InputDispatcher.cs` | ClickAsync/TypeTextAsync 入口加 tier 参数 (可选, 默认自动路由) |

### 实现优先级

| 阶段 | 内容 | 价值 | 风险 |
|---|---|---|---|
| **P0** | WindowScopeLock 三级锁 + InputRouter 路由决策 | 释放 Tier A 100% 并行 | 低, 纯新增 |
| **P1** | ObserveDeduplicator 跨窗口并行 + 同窗口去重 | 去重 observe 开销 | 低 |
| **P2** | TaskDispatcher 替换 ActionCoordinator | 路由 + 锁统一入口 | 中 |
| **P3** | PiP Session 隔离 (RDP loopback) | 用户/Agent 完全零干扰 | 中, 依赖 RDP 服务 |

### PiP Session 隔离 (P3 可选, UFO² arXiv:2504.14603 方案)

```
┌─ 用户桌面 (Session 0) ──────────────────────────────┐
│  ┌─ Agent Desktop A (RDP loopback Session 1) ───┐  │
│  │  notepad.exe (独立窗口, 独立 UIA 树)          │  │
│  │  Gateway#1 → Tier A Invoke ✅ 零焦点竞争     │  │
│  └───────────────────────────────────────────────┘  │
│  ┌─ Agent Desktop B (RDP loopback Session 2) ───┐  │
│  │  chrome.exe (独立窗口, 独立 UIA 树)            │  │
│  │  Gateway#2 → Tier A Invoke ✅ 零焦点竞争     │  │
│  └───────────────────────────────────────────────┘  │
│                                                       │
│  用户鼠标正常工作, Agent 各自独立输入/光标/窗口       │
└──────────────────────────────────────────────────────┘
```

论文: UFO² "Multi-Agent Desktop Automation with Picture-in-Picture Isolation" arXiv:2504.14603

### 证据链

| 发现 | 来源 | 类型 |
|---|---|---|
| SendInput 全局串行 | [Microsoft Learn](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput) | 官方文档 |
| UIA InvokePattern 无需焦点 | [Microsoft Learn Invoke Pattern](https://learn.microsoft.com/windows/win32/winauto/uiauto-implementinginvoke) | 官方文档 |
| UFO² HostAgent + AppAgent + PiP | [arXiv:2504.14603](https://arxiv.org/abs/2504.14603) §3 | 论文 + [官网](http://microsoft.github.io/UFO/ufo2/overview/) |
| COLA 动态 Agent Pool | [arXiv:2503.09263](https://arxiv.org/abs/2503.09263) | 论文 |
| Cua Multi-Player 容器隔离 | [cua.ai blog](https://cua.ai/blog/clawcon-multiplayer) | 工业落地 |
| MACU DAG 并行调度 | [arXiv:2606.01533](https://arxiv.org/pdf/2606.01533v1) | 论文 |
| MouseMux 驱动级多光标 | [mousemux.com](https://www.mousemux.com/pages/manual/) | 商用备选 |

### 落地状态 (P0–P6 已完成)

上面的组件名是设计期的叫法, 实际落地时按职责重新命名 (语义不变, 均是纯新增/内部重写, MCP 工具名与参数未变):

| 设计期 | 实际落地 |
|---|---|
| `WindowScopeLock` | `Swarm.Core/Concurrency/LeaseManager.cs` (FIFO 公平锁: `AcquireWindowAsync` / `AcquireDisplayAsync`) + 两把叶子锁 `WindowLease` → `DisplayLease` |
| `TaskDispatcher` | `DesktopTaskService` 只保留服务级单例 (SessionRegistry / LeaseManager / ConfirmationQueue / UiaWatchdog); 会话状态下沉到 `Swarm.Desktop/DesktopSession.cs` 的 `TaskSession`, `ActionCoordinator` 改为 per-session |
| `ObserveDeduplicator` | `CaptureService` per-(hwnd,generation) `SingleFlight` + 300ms 复用缓存; 写动作 `WriteGeneration++` 失效 |
| `InputRouter` | `InputDispatcher` 内 Tier A/B/C 分发 + `InputTier` 计数, 写动作前 `CaptureService.Invalidate` |

新增的配套件:

- `Swarm.Desktop/Unobtrusive.cs`: `UserPresenceGuard` (`GetLastInputInfo` + 自身注入归因 + 显式恢复基线), `CursorRestore` (用户没动输入才把光标放回)
- `Swarm.Desktop/UiaWatchdog.cs`: UIA 读 3.5s 超时、连续 2 次失败隔离 30s; `IsolationProbe` 探测 RDP/沙箱能力 (本机 Home 版 → `soft`)
- `Swarm.Core/Governance/ConfirmationQueue.cs`: 确认弹窗串行, `MainWindow` 实现 `IConfirmationPresenter` (独立 STA 线程 + 原生 MessageBox, 无 `SETFOREGROUND`, 不阻塞 UI)
- `DesktopIpcServer` 并发 accept (16 连接); `DesktopControlServer` 按调用方 PID 精确取消
- OCR 门改为串行 (`_ocrGate = SemaphoreSlim(1,1)`): Tesseract `Engine` 是单图状态机, 并发 observe 会抛 `Only one image can be processed at once`; 串行仍在后台线程, 不占窗口/显示租约
- 只读诊断工具 `desktop_status` (MCP): active/capacity/isolation/capture/leases/uia/tiers

硬约束的落地方式: Tier C 前必须 `EnsureUserIdleForTierC()` (默认 2s 无真实输入, `SWARM_TIER_C_IDLE_MS` 可调), 一次 `SendInput` 合并移动+按键, 全局 `SendSync` 串行, 任何真实输入立即 `manual_takeover`。

验收基线: `dotnet build -c Release -warnaserror` 0 警告 0 错误; `dotnet test -c Release` 379 通过; `desktop/e2e/run-all.ps1` 8 个套件全 PASS (新增 `concurrency.ps1`: 3 Gateway 真并发 + `desktop_status.active=3` + 跨会话 token 拒绝 + 第 4 会话 `capacity_exceeded` + 确认框不抢焦点)。

---

## 七、TRISHUL HSP (视觉 grounding, 已落地)

### 架构 (arXiv:2502.08226)

```
Layer 1: 粗粒度大区域 (UIA ControlType 映射)
  TitleBar / MenuBar / ContentArea / StatusBar
  大尺寸门控: nw>0.10 || nh>0.05 (排除小 MenuItem 误判)

Layer 2: 细粒度可交互
  UIA: Button/CheckBox/RadioButton/Hyperlink/MenuItem/ComboBox/Edit/TabItem/ListItem/Slider
  OCR: 行 → IoU≥0.3 只留 UIA 侧, IoU≥0.50 内部贪心合并

Containment: 每个 Layer 2 找 innermost Layer 1 父区域 → ParentMarkId
SEED: "在 TitleBar 右侧 上半部" 相对位置 → SpatialContext
```

### SoMElement (OcrModels.cs) — 向后兼容

```csharp
public sealed record SoMElement(
    int MarkId, string Text,
    double X, double Y, double Width, double Height,
    SoMKind Kind, double Confidence,
    // 以下全有默认值, 旧 API 不受影响
    int HierarchyLevel = 2,           // 1=粗, 2=细
    int? ParentMarkId = null,
    string? SpatialContext = null,
    string? UiaControlType = null,
    int OverlapFlags = 0)             // bit0=UIA匹配, bit1=纯OCR
{
    public bool FromOcrOnly => (OverlapFlags & 0b10) != 0;
    public bool HasUiaMatch  => (OverlapFlags & 0b01) != 0;
}
```

### Commit 已完成

| Commit | 内容 | 测试 |
|---|---|---|
| `a1f706b` | HierarchicalScreenParser.cs + SoMElement 扩展字段 + SetOfMarkBuilder.ClassifyStatic() | — |
| `54f8a28` | 14 个测试 + OverlapsAnyUia 自过滤 bug 修复 | 14 全绿 |

### OverlapsAnyUia Bug

**根因**: 原实现传整个 `candidates` 列表, 第二个 OCR 行 IoU=1.0 ≥ 0.3 被第一个 OCR 过滤
**修复**: 单独维护 `uiaCandidates`, 只检查 UIA 侧; OCR 内部去重交给 `MergeOverlappingLayer2(IoU≥0.5)`
**测试**: `HierarchicalScreenParserTests.cs` 有专项用例

---

## 八、已选最优解 (不要推翻)

| 层 | 方案 | Commit | 验证 |
|---|---|---|---|
| OCR | TesseractOCR 5.5.1 (NuGet 内嵌 Native) | `7aa27b6` | Word 识别 P95 < 400ms |
| Word | FileBasedWordBridge (DocumentFormat.OpenXml 3.1.0) | `fbe47ae` | 零用户侧依赖 |
| SoM | TRISHUL HSP (UIA + OCR 双层粒度) | `a1f706b` | 14 测试全绿 |
| Input | 三层策略 Tier A→B→C | `InputDispatcher.cs` | 373 总测试 |
| Mirror | Vortice.Direct3D11 帧通道 | 早期 commit | Renderer 64 通过 |

### 有意识跳过的方案

| 方案 | 否决理由 |
|---|---|
| YOLOv8n (COCO 80 类) | 没有 button/checkbox 类, 需 UI-specific fine-tune |
| Grounding DINO ONNX 688MB | 桌面应用过大, tiny 版本 ≤50MB 才有意义 |
| `Windows.Media.Ocr` | 微软明确: 桌面应用不支持, 必须 MSIX 身份 |
| ZeroDep.Ocr.Tesseract 2.1.0 | subprocess 壳, 启动外部 tesseract.exe |
| Office COM Interop | WordBridge 已用纯托管替代 |
| MouseMux 驱动级多光标 | 商用, 暂不需要, 作为 Tier C 极限场景备选 |

---

## 九、反模式清单 (已踩坑)

| 反模式 | 失败根因 | 正确做法 |
|---|---|---|
| OpenXml SDK `SdtTitle` | 实际类型是 `SdtAlias`, 文档写的不一样 | 查官方 XML 文档确认 |
| PowerShell 多行 commit message | 引号/空格被 PowerShell 解析成路径 | 用临时文件 `Set-Content -Path $tmp -Value $msg -Encoding UTF8; git commit -F $tmp` |
| NamedPipeServer 被 Start-Process 拉起后崩 | Windows session + pipe 创建权限问题 | 用 `Start-Job` 或前台 `dotnet run`, 不要 `-WindowStyle Hidden` |
| fake Desktop Pipe 直接用 `PipeDirection.InOut` + 无 Asynchronous | Gateway 用 `PipeOptions.Asynchronous`, 不匹配就连接失败 | 必须照搬真实 DesktopIpcServer: `PipeDirection.InOut, max=10, PipeTransmissionMode.Byte, PipeOptions.Asynchronous \| PipeOptions.CurrentUserOnly` |
| fake Desktop reader/writer 不带 `leaveOpen:true` | Gateway 用完后 dispose StreamWriter 会关闭 pipe | `new StreamReader(pipe, Encoding.UTF8, false, 16*1024, true)` + 同 writer |
| Desktop OnLoaded 才 Start IPC | Gateway 过早 connect → `desktop_unavailable` | 给 Desktop 留 3-5 秒 WebView2 初始化时间再测 |
| fake Desktop 内部 `pipe.Disconnect()` 循环复用 | `maxInstances=1` 且 `await using` 模式下用一次就好 | 一请求一 accept, 不要复用同一个 pipe |

---

## 十、已知技术债

| 债 | 说明 | 处理 |
|---|---|---|
| `Microsoft.ML.OnnxRuntime 1.21.0` | 死依赖 (Tesseract 不依赖; nuspec/程序集/原生库三处已核实) | ✅ 已卸: publish 176.0 → 162.9 MB, 少 13.1 MB |
| `OverlapsAnyUia` 函数名 | 有误导性 (实际只检查 UIA, 不检查 OCR 内部) | 待办: 改名 `OverlapsUiaCandidates` |
| `ActionCoordinator.RunAsync` 全局锁 | Tier A/B 被串行化 | ✅ 已修: `LeaseManager` (WindowLease → DisplayLease) + per-session 门 |
| Desktop TaskDispatcher 缺失 | 当前 IPC handler 硬编码包全局锁 | ✅ 已落地: `TaskSession` + `DesktopTaskService` 只留无状态共享设施 |
| Pipeline / CI | 无 GitHub Actions | ✅ 已加: `.github/workflows/desktop-release.yml` |
| E2E 测试 | 只有单元测试 | ✅ 已有 8 个套件 (含 3-Gateway 并发), 仍缺 Node harness |
| tessdata 硬编码路径 | `desktop/tessdata/` 相对路径 | ✅ 已改为 `AppContext.BaseDirectory` + `SWARM_TESSDATA_DIR` 覆盖 |

---

## 十一、快速上手

```powershell
# 1. 克隆 + 切分支
cd C:\Users\kaer\Apps\swarm-cursors
git checkout chore/net10
git pull

# 2. Build
cd desktop
dotnet build SwarmDesktop.sln -c Release -warnaserror

# 3. Test
dotnet test SwarmDesktop.sln --no-build
# 期望: 373 通过, 0 失败, 0 警告

# 4. Publish Desktop (WPF app 需 Win10+)
dotnet publish Swarm.Desktop/Swarm.Desktop.csproj -c Release -o publish-verify/Swarm.Desktop

# 5. Publish Gateway (console app)
dotnet publish Swarm.Gateway/Swarm.Gateway.csproj -c Release -o publish-verify/Swarm.Gateway

# 6. 启动真实 Desktop
.\publish-verify\Swarm.Desktop\Swarm.Desktop.exe
# 等 3-5 秒 (WebView2 初始化 + IPC 启动)
# 此时 NamedPipe \\.\pipe\SwarmDesktop.V1 存在

# 7. 启动 Gateway (per-agent 独立进程, MCP stdio)
.\publish-verify\Swarm.Gateway\Swarm.Gateway.exe
# stdin 写 JSON-RPC 2.0, stdout 读响应
# 支持 tools/call: task_begin, observe, click, type_text, task_end
```

### 常见问题排查

| 症状 | 可能原因 | 排查 |
|---|---|---|
| Gateway 返回 `desktop_unavailable` | Desktop 还没 OnLoaded | 等 5 秒再试; 看 Desktop 是否有 MainWindow |
| Gateway 返回 `needs_foreground` | Tier B PostMessage 失败, 需 Tier C SendInput | Desktop 会自动回退 Tier C |
| Desktop 进程闪退 | WebView2 运行时缺失 | `Test-Path "C:\Program Files (x86)\Microsoft\EdgeWebView\Application"` |
| OCR 返回空 | tessdata 路径错 | 确认 `desktop/tessdata/eng.traineddata` 和 `chi_sim.traineddata` 存在 |

---

## 十二、Skill (已落地)

`.trae/skills/gui-grounding-research/SKILL.md` — GUI grounding 最优解决策框架 (6 步)

这个 skill 就是本项目视觉选型过程的提炼: 冻结维度 → 证据链 → 决策矩阵 → 反模式 → 测试矩阵 → 执行闭环。新 agent 接手视觉相关任务时, 按此框架执行。

Commit `8a6f1ce` 本地已创建, 网络恢复后 `git push origin chore/net10` 即可。

---

## 十三、下一步（按优先级）

### 已完成（本次交付）

1. ~~三级锁替换全局锁~~ → `LeaseManager` (WindowLease → DisplayLease, FIFO 公平锁)，Tier A/B 跨窗口真并行
2. ~~InputRouter 路由决策~~ → `InputDispatcher` Tier A/B/C 分发，写动作按窗口串行
3. ~~只读观察并行/去重~~ → `CaptureService` 按 (hwnd, generation) single-flight + 300ms 复用
4. ~~会话隔离~~ → 会话键为 `gatewayProcessId`，服务级状态下沉到 `TaskSession`，容量默认 3 (`SWARM_MAX_SESSIONS`)
5. ~~CI/CD~~ → `.github/workflows/desktop-release.yml` (build `-warnaserror` + test + publish)
6. ~~3 Gateway 并发 E2E~~ → `desktop/e2e/concurrency.ps1`，已接入 `run-all.ps1`
7. ~~`Microsoft.ML.OnnxRuntime` 瘦身~~ → 已核实为死依赖并移除（Tesseract 不依赖；publish 176.0 → 162.9 MB）

### 复核修复（本轮全量审查发现）

- **光标归位失效（功能缺陷）**：`SendInput` 会刷新 `GetLastInputInfo`（已用探针实测），`CursorRestore.RestoreIfIdle` 因此永远判定"用户在场"、从不还原。改为同时接受"最近输入就是自身注入"（`LastSyntheticTick`，250ms 归因窗）。
- **OCR 并发崩溃**：Tesseract `Engine` 是单图状态机，`_ocrGate(2)` 让并行 observe 抛 `Only one image can be processed at once`、OCR 静默退化为 0。门限改为 1。
- **租约 FIFO 被插队**：`Release` 置 `_held=false` 与 `Pump` 之间存在空隙，新来的 Acquire 可越过排队者直接拿走；改为仅当"无持有且无排队"才快速获取。
- **`task_end` 固定多等 1.2s**：覆盖层延时隐藏被 `await` 在响应链上；改为脱离调用链（`HideOverlayLaterAsync`）。
- **抓屏缓存泄漏**：已关闭窗口的整帧 RGBA 被字典强引用；`TryReuse` 过期即删，写入时顺手清理失效窗口。
- **UIA 卡死线程累积**：每次熔断解禁后仍会再卡一次并泄漏一个线程池线程；熔断改为指数退避（30s→…→8min），成功即复位。
- **e2e 前台锁抖动**：`edge-tiers.ps1` 的 `SetForegroundWindow` 偶发被系统拒绝，改为重试 10 次再判定。

### 遗留项清偿（本轮）

- **`LeaseManager` 句柄表累积**：改为 `WindowGate{Lock, Refs}` 引用计数，只在 `Refs==0` 时回收空闲闸门（阈值 32），避免同一 hwnd 出现两把锁。
- **`UiaWatchdog` PID 表累积**：超过 64 个进程时清掉“无失败、无熔断”的冷记录；`timeouts` 与 `errors`（COM/权限失败）分开统计。
- **`IsolationProbe` 文案**：`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` 是“可用内存”而非物理内存，已改正。
- **`MarkResumed` 时序缺陷（真实 bug）**：原实现取“恢复瞬间的最后一笔输入”作为忽略上界，但 `GetLastInputInfo` 的可见性滞后，紧接恢复的注入按键会被算作新输入，导致恢复后的第一个 Tier C 被误判 `user_active`。改为以恢复时刻为界（`TickCount`），并在 `run-all.ps1` 把测试环境的门限收紧到 400ms 以消除按秒等待的抖动。
- **`edge-tiers.ps1` 类型输入顺序**：`click-select` 会触发页面重渲染让输入框 UIA 元素失效；把 type 放到最前，并把 `needs_foreground`（Chromium 后台无 ValuePattern）如实记录为非 A/B 动作，不再静默丢 `tier`。

### 仍未做（有前置依赖，不阻塞当前版本）

8. **PiP Session 隔离 (RDP loopback)** — 本机 Home 版无 RDP/Sandbox/Hyper-V，`IsolationProbe` 只能返回 `soft`；换到 Pro/Enterprise 或独立 VM 后再做
9. **驱动级多光标 (MouseMux 这类虚拟 HID)** — 属于另一条产品路线，不是本架构的增量

### 不要做（硬约束）

- **不要改 `Swarm.Desktop.csproj` 里的 NuGet 版本** — 已稳定
- **不要改 `Swarm.Core.csproj` 里的 DocumentFormat.OpenXml 版本** — 已验证
- **不要改 `SoMElement` 里任何默认值** — 保持向后兼容
- **不要在 Tier A/B 的无锁区域临时加全局锁** — 这正是要移除的东西
- **不要推翻三层输入策略** — 内部实现完全正确, 问题只在上层调度
- **不要在 Tier C 之前跳过 `EnsureUserIdleForTierC()`** — 这是"用户无感"的唯一兜底
- **不要把会话状态写回 `DesktopTaskService` 的单例字段** — 那会让并发能力退化回单会话

---

*文档结束. 有疑问直接 grep 上面列出的文件路径, 或看最近 10 个 commit 的 message.*
