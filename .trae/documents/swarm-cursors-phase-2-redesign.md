# Swarm Desktop 第二阶段重构设计：面向最优解

> 日期：2026-11-22 · 设计复核：2026-11-23（§10） · 基线：`desktop` HEAD `f8a2d8f` · 前置：`swarm-cursors-handover.md`（2026-11-22 交接文档）
> 范围：`desktop\Swarm.Core`、`Swarm.Desktop`、`Swarm.Gateway`、`renderer`、`e2e`
> 原则：不推翻第一阶段契约（事件总线、授权、暂停/恢复、回放零副作用），只替换薄弱实现。

---

## 1. 结论摘要

当前系统的核心矛盾不是功能缺失，而是**把"感知"和"呈现"都压在了最不可靠的那条路径上**：

- 抓屏以 `PrintWindow` 为主 → GPU/DirectComposition 窗口空白，被迫降级屏幕裁剪，遮挡即失真。
- 动作以"窗口消息 / 真实输入"为主 → Chromium 类应用忽略消息，只能抢前台（`needs_foreground`）。
- 呈现以"像素反推字体 + 重建 DOM"为主 → 字号 ±8%、字重靠笔画比、混排靠字符窗口扫描，属于用启发式补 API 缺失。

行业与平台的共同答案是**分层、按能力降级、优先无副作用通道**：

1. 抓屏：`Windows.Graphics.Capture`（DWM 合成，按窗口）为主，`PrintWindow` 兜底，屏幕裁剪最后。
2. 动作：UIA 控件模式（`Invoke/Value/Scroll`）+ `AutoSetFocus=false` 为主，窗口消息次之，真实输入最后；跨会话隔离是根治方案。
3. 感知：UIA 缓存批取 + `TextPattern` 属性运行段；IA2/OCR/视觉仅兜底；元素 ID 带代数校验。
4. 呈现：真人看到的镜像用**真实像素**（WGC / DWM 缩略图），语义树只服务 AI 与标注，不再承担"像素级克隆"。
5. 协议：走**双纪元**——legacy（`2024-11-05`…`2025-11-25`）继续用 `initialize`，modern（`2026-07-28`）用 `server/discover` + 每请求 `_meta`；补齐进度、取消、工具注解、结构化输出；不实现已弃用特性（Roots/Sampling/Logging）。
6. 安全：策略化 `task_begin`、敏感脱敏、审计、把屏幕内容一律视为不可信输入。

---

## 2. 问题清单与根因（重构前基线）

| # | 现象 | 根因 | 现有位置 |
|---|---|---|---|
| P1 | Chromium/Electron 非前台动作返回 `needs_foreground`，`focus` 会抢前台 | 没有"控件模式"动作通道，只有消息与真实输入 | [InputDispatcher.cs](../../desktop/Swarm.Desktop/InputDispatcher.cs) |
| P2 | GPU 渲染窗口 `PrintWindow` 得到空白帧，降级屏幕裁剪后受遮挡影响 | 抓屏后端单一，且按调用临时创建 | [ScreenCapture.cs](../../desktop/Swarm.Desktop/ScreenCapture.cs) |
| P3 | 字体族/字号/字重靠墨迹反解，字号误差约 ±8% | 未优先读取 `TextPattern` 文本属性，启发式被迫承担主路径 | `UiaTextStyleReader.cs`、`Win32FontReader.cs`、`GdiFontMeasurer.cs` |
| P4 | 混排 span 靠单字符窗口扫描 UIA 属性边界，限 80 字符/120ms | 未使用 `TextUnit_Format` 的属性运行段 | `UiaTextStyleReader.cs` |
| P5 | 每次 UIA 读取为多次跨进程调用，慢且可能挂起 | 未使用 `CacheRequest` 批量取属性，缺少超时/MTA 约束 | `UiaSnapshotReader.cs`、`Ia2TextReader.cs` |
| P6 | `elementId` 在结构变化后可能指向旧元素 | ID 无代数（generation）校验 | `UiaSnapshotReader.cs`、`DesktopTaskService.cs` |
| P7 | MCP 固定 `protocolVersion=2024-11-05`，无进度/取消/注解 | 协议层停留在首版 | [McpGateway.cs](../../desktop/Swarm.Gateway/McpGateway.cs#L188) |
| P8 | 长动作无进度反馈，取消语义依赖自定义 `task_stop` | 未映射 `notifications/progress` 与 `notifications/cancelled` | `McpGateway.cs`、`DesktopTaskService.cs` |
| P9 | 安全边界只有"进程校验 + 本地授权框" | 无动作策略、无审计、无注入防护 | `DesktopIpcServer.cs`、`DesktopTaskService.cs` |
| P10 | E2E 依赖 Edge/mspaint/单实例互斥量，无"前台未变化""DPI 矩阵"等不变量 | 测试只验证结果，不验证副作用 | `desktop\e2e\*.ps1` |
| P11 | 发布为 framework-dependent，Gateway 需目标机装 .NET 10 | 交付形态未优化 | `publish.ps1` |

---

## 3. 行业证据

### 3.1 computer-use agent 的主流架构

- **UFO²（微软，arXiv 2504.14603）**：Windows 桌面 AgentOS，核心是**UIA + 视觉的混合控件检测**、统一 GUI–API 动作层、按应用特化的 AppAgent，以及 PiP 隔离虚拟桌面让 Agent 与用户并行操作。结论：纯截图或纯 UIA 都不够，混合 + 应用适配是工程最优。
- **UFO（arXiv 2402.07939）**：UI-Focused Windows Agent，先观察控件树再操作，验证了 Windows 上 UIA 作为一等公民的可行性。
- **Windows Agent Arena（微软）**：150+ 真实 Windows 任务、可并行、可复现的评测环境。结论：重构必须配可回归的任务级基线，而不是只跑冒烟脚本。
- **OmniParser（arXiv 2408.00203）**：图标检测 + 描述 + OCR 生成 DOM 化结构；**Set-of-Mark（arXiv 2310.11441）** 证明给多模态模型叠加编号框能显著提升 grounding。结论：视觉兜底应输出"带编号的可交互元素"，而不是裸截图。
- **UI 分解与合成（arXiv 2505.13227）**：把界面拆成元素级数据可显著提升 grounding 规模与质量。结论：元素模型要有稳定 ID 与结构化属性。

### 3.2 Windows 平台能力

- **Windows.Graphics.Capture**：官方按窗口/按显示器取帧，基于 DWM 合成，适合协作与自动化；默认绘制黄色采集边框。`IsBorderRequired=false` 需要 MSIX 的 `graphicsCaptureWithoutBorder` 能力并经用户同意，否则边框仍显示。
- **Desktop Duplication API**：整屏、帧级增量，适合多窗口/全屏场景；按窗口语义弱。
- **UI Automation**：
  - `IUIAutomation2::AutoSetFocus=false` 可阻止 `Invoke`/`SetValue` 自动抢焦点，官方明确用于"设焦点会造成干扰"的场景。
  - `CacheRequest` 批量取属性/模式，避免逐属性跨进程调用。
  - `TextPatternRange::GetAttributeValue` 提供 `FontName/FontSize/FontWeight/IsItalic/ForegroundColor/BackgroundColor` 等文本属性；配合 `TextUnit_Format` 可直接得到属性运行段。
- **隔离执行**：微软已在 Windows 11 推出 **Agent Workspace**（独立 Windows 会话，与用户并行）与 **Microsoft Execution Containers**（session container 后端，分离桌面/剪贴板/UI/输入）。这是"抢焦点"问题的根治方向，而不是继续优化 `SetForegroundWindow`。

### 3.3 协议与安全

- **MCP 最新修订 2026-07-28**（前版 2025-11-25）：移除协议级会话、改为无状态 + `server/discover`；`subscriptions/listen` 取代资源订阅；任务移入 `io.modelcontextprotocol/tasks` 扩展；引入 MRTR（`input_required`）与统一 `resultType`；`Roots/Sampling/Logging` 进入弃用；新增 `ttlMs/cacheScope`、确定性 `tools/list` 顺序、错误码分区。
- **MCP 安全基线**：一切服务端返回内容（工具描述、结果、URL）视为不可信；最小权限；写/发/删类操作保留人类确认。
- **Chromium/Electron**：CDP 的 `Accessibility.getFullAXTree` 可拿到完整 AX 树；Electron 在检测到辅助技术时自动开启无障碍。结论：浏览器类应用可用 IA2/UIA，必要时以 CDP 作为增强通道，而不是靠抢焦点。

---

## 4. 目标架构

```
Trae (MCP client)
  └─ Swarm.Gateway（stdio JSON-RPC，版本协商 / 进度 / 取消 / 注解）
        └─ \\.\pipe\SwarmDesktop.V1（单连接单请求，带 task token + generation）
              └─ Swarm.Desktop
                   ├─ TaskPolicy       授权、策略、审计、脱敏
                   ├─ Perception
                   │    ├─ ICaptureBackend: WGC(主) → PrintWindow → ScreenCrop → DXGI(可选)
                   │    └─ ISemanticProvider: UIA(缓存) → IA2 → OCR → Vision(可选)
                   ├─ ActionPlanner
                   │    └─ 阶梯: UIA 模式 → 窗口消息 → focus+SendInput → 应用适配器
                   └─ Presentation
                        ├─ Mirror: 真实像素（WGC / DWM 缩略图）
                        └─ Annotation: 动作光标、目标高亮、步骤与回放（纯视觉）
```

关键原则：**能力可探测、逐级降级、每级都返回证据与代价**；降级不是静默，而是写进 `structuredContent`。

---

## 5. 决策记录（ADR）

### ADR-01 抓屏：WGC 为主，PrintWindow 兜底

- **决定**：新增 `ICaptureBackend`，按任务建立并复用 WGC 会话；`PrintWindow` 三级降级保留为后备，屏幕裁剪为最后手段；返回 `captureBackend / frameAgeMs / occluded`。
- **实现选型（不升 TFM）**：手写 COM vtable 互操作（`IGraphicsCaptureItemInterop` + `Direct3D11CaptureFramePool` 相关接口）+ `Vortice.Direct3D11`，项目保持 `net10.0-windows`。`WgcSharp`（MIT，1.6k 下载）只作为**参考实现与探针**，不作为运行时依赖——它每次调用新建会话、返回 `System.Drawing.Bitmap`，与"按任务复用会话"目标冲突。
- **理由**：WGC 走 DWM 合成，被遮挡窗口仍可取帧；`PrintWindow` 在 GPU 窗口上结构性地拿不到内容。官方 CsWinRT 路径要求 `net10.0-windows10.0.19041.0`，且社区已记录 CsWinRT 的 `ComWrappers` 与 WPF 存在冲突；手写 vtable 可同时规避 TFM 升级与 WPF 互操作冲突。
- **边框**：保留系统黄色采集边框，不申请 MSIX `graphicsCaptureWithoutBorder`。该边框是 Windows 的安全/隐私提示，官方明确"用户拒绝则忽略 `IsBorderRequired=false`"；对本产品而言它恰好是"正在被观察"的诚实信号。
- **最小化**：以显式 `window_minimized` 为准。WGC 对最小化窗口的可用性存在社区争议（有实现声称可抓），交由 A4 实测决定，未验证前不得返回空白帧冒充成功。
- **验收**：同一窗口在"被遮挡 / 非前台 / DPI 150%"三种状态下，WGC 帧与屏幕裁剪帧的感知哈希一致率 ≥ 99%；最小化时返回 `window_minimized`。
- **A2/A3 实测（2026-11，Win11 26200 / 25H2，150% DPI）**：手写 vtable 链路跑通，BGRA→RGBA 通道序以背景色校验一致。会话复用时 20 次连续抓屏只用 1 个会话，首帧 164ms，后续单帧 3–4ms；窗口缩放后 150ms 内按新尺寸 `Recreate` 生效（1178×889 → 1478×1069）；窗口被完全遮挡时仍能取到变化后的新内容；最小化返回 `window_minimized`、还原后恢复，关闭后会话立即回收。
- **关键发现（影响验收口径）**：WGC 只在窗口内容变化时交付新帧，静止窗口下"距上一帧时间"会持续增长，但不代表抓屏失效。因此 `frameAgeMs` 只能作为新鲜度参考，A4 的"帧延迟 P95 ≤ 120ms"必须按"内容变化 → 新帧可读"的时延测量，不能直接用 age。
- **A4 实测（2026-11-22，Win11 26200 / 25H2，150% DPI）**：WGC 已排到 `CapturePipeline` 链首（WGC → PrintWindow → 屏幕裁剪），对外透出 `captureBackend` / `frameAgeMs` / `occluded`。WGC 帧尺寸等于 `DWMWA_EXTENDED_FRAME_BOUNDS`（不含不可见调整边框），客户区裁剪必须以可见窗口矩形为原点，用 `GetWindowRect` 会整体偏出约 7–11px；按可见矩形裁剪后与 PrintWindow 输出逐像素一致率 **99.992%**，唯一差异是底部 8px 的系统黄色采集边框带（WGC 帧里是窗口底边/阴影，屏幕上是黄框），客户区尺寸 978×664 与 `GetClientRect` 完全一致。完全遮挡且非前台时仍返回目标真实内容；最小化 `window_minimized`、还原恢复、关闭后会话数归零。另补：窗口关闭时取帧线程自行退出 + 抓屏入口按 `IsWindow` 清扫僵尸会话，避免长期驻留进程累积 D3D/线程资源。
- **A5 实测（2026-11-22，同上环境）**：降级链收口为三条不变量——首个非空白帧获胜且不再调用后续后端、空帧/空白逐级降级、全失败返回末次尝试并标 `Blank`；其中三段降级要正确透传 `occluded` 与 `frameAgeMs`（312 单测覆盖，不依赖真实 HWND）。"内容变化 → 新帧可读"延迟按交替改色测量（WGC 直连，避开 300ms 同窗口复用缓存）：20 次 **P50 29ms / P95 45ms / max 45ms**，满足 P95 ≤ 120ms。遮挡/非前台一致性沿用 A4 数据（与 PrintWindow 99.992%，与屏幕裁剪 99.29%，差异仅系统黄色采集边框带）。
- **P1-S 后端一致性（2026-11-23，150% DPI）**：`e2e/p1-s-invariants.ps1` 在静态 Edge 页面上分别跑默认链与 `SWARM_CAPTURE_BACKEND=screen` 强制诊断链，断言前者实际命中 `wgc`，并比较客户区原点/尺寸、PNG 尺寸与排除系统采集边框后的像素；实测采样差异低于 1% 阈值。强制开关只用于 E2E，默认生产链仍是 WGC → PrintWindow → 屏幕裁剪。

### ADR-02 感知：UIA 缓存批取 + TextPattern 属性段

- **决定**：UIA 读取统一走 `CacheRequest` 批量取属性；文本样式优先读 `TextPattern` 的 `FontName/FontSize/FontWeight/IsItalic/ForegroundColor`；混排用 `TextUnit_Format` 切属性运行段；`Win32FontReader/GdiFontMeasurer` 降为兜底。
- **理由**：这是平台已经提供的确定性数据，优先于任何像素反解；同时把跨进程调用从"每属性一次"降为"一次批量"。
- **代价**：部分自绘控件不实现 TextPattern，仍需兜底；UIA 调用需放到 MTA 线程并设 `ConnectionTimeout/TransactionTimeout`，避免挂起 UI。
- **验收**：对同一批含混排/斜体/不同字号的目标窗口，span 切分与浏览器/记事本实际渲染一致；字号误差从 ±8% 收敛到 ≤1pt；UIA 单次 observe P95 延迟下降 ≥40%。
- **实施状态（批取层完成）**：`UiaSnapshotReader` 改为在 `CacheRequest.Activate()` 内创建根元素、用 `FindAll(TreeScope.Children, ControlViewCondition)` 批量取子树、属性读 `.Cached`（失败整节点回退 `.Current`），模式集合从固定 12 项缓存中投影；遍历跑在专属 MTA 线程，`UiaObserveBudget` 管首轮/续跑/2500ms 总超时，硬超时 400ms 后返回部分结果并标 `TimeLimit`。实测 Notepad 73→25ms、Paint 303→82ms（中位数，-66%/-73%），批取路径 `liveReads=0`。关键约束：托管 `TreeWalker` 不使用缓存请求，必须改用 `FindAll`；根元素必须在缓存作用域内创建。
- **实施状态（TextUnit_Format 属性段完成，2026-11-22）**：`UiaTextStyleReader` 的混排分段改为优先用 `TextUnit.Format` 走查——游标先收成 range 起点的空区间，每次 `MoveEndpointByUnit(End, Format, 1)` 扩出一段，取该段文本（与全文逐码元强校验）并读 5 个属性，再把 Start 收到段尾，直到文末；段文本对不上、属性为 Mixed 或 provider 返回 0 时退回原“整段读 + Mixed 二分”路径。实测（Edge 混排页，`--force-renderer-accessibility`）：同一行 5 个格式段的字体族/字号/字重/斜体/颜色与页面 DOM 完全一致（26px→19.5pt、16px→12pt、12px→9pt），单元素峰值跨进程调用 **85 → 42**；Notepad 纯文本一次移动即覆盖全文，峰值 10 次、不产 span。`CallsPerElementTarget` 由 40 调整为 44（2 次基础 + 1 次收口 + 每段 7 次 + 段间收起点）。字号直接读 provider 的 pt 值，误差仅来自其内部像素换算，实测与 CSS `px × 0.75` 一致。RichEdit（`RICHEDIT50W`，WinForms `RichTextBox` 宿主）双行混排同样通过：7 个格式段、峰值 58 次（`3 + 7n + (n−1)`），第二行样式与设置一致；段标记（`\r\n`）会被 RichEdit 当作独立格式段，归一为空格后表现为一个黑色空格 span（无可见墨迹）。本机未安装 Word/WordPad，Word provider 仍待抽检。

### ADR-03 动作：控件模式优先，焦点是最后手段

- **决定**：`click/type_text/scroll/press_key` 统一走动作阶梯：
  1. UIA 模式（`Invoke/Toggle/Select/Value/Scroll/ExpandCollapse`），全程 `AutoSetFocus=false`；
  2. 元素级窗口消息（`WM_LBUTTONDOWN/UP`、`WM_CHAR`、`WM_MOUSEWHEEL`）；
  3. `focus` + `SendInput`（保留 `SwarmInputMarker`）；
  4. 应用适配器（如可用 CDP 的浏览器）。
  返回 `tier=uia|message|input|adapter` 与回读证据。
- **理由**：`needs_foreground` 的本质是"只有真实输入这一条路"；UIA 模式与消息通道都不需要前台。官方 `AutoSetFocus=false` 明确支持不抢焦点。
- **代价**：不同实现的控件模式质量参差，需要按应用记录"能力档案"；有的 UIA 实现仍会聚焦，必须校验前台窗口未变化并在返回中标注。
- **CDP 定位（不进 P0）**：Chrome 136 起，`--remote-debugging-port` / `--remote-debugging-pipe` 在默认数据目录下被直接忽略，必须配合独立的 `--user-data-dir`；用户已启动的 Electron 应用也不会凭空开放端口。因此 CDP 只在"应用由 Swarm 以专用 profile 启动并显式开启调试"时作为适配器，列入 P2；P0 的 Chromium 路径仍靠 UIA/IA2 解决。
- **验收**：p0 场景中"AI 操作期间前台窗口不变"成为硬断言；Chromium 点击的成功路径中 `tier=uia|message` 占比 ≥ 80%，`focus` 调用次数为 0。
- **实施状态（2026-11-22，B1）**：动作返回已带 `foregroundUnchanged` / `foregroundRestored` 两个字段；UIA 模式执行后逐次比较"动作前/后/恢复后"的前台根窗口，被提供程序 `SetFocus` 抢走时恢复原前台并如实标注（曾恢复 ≠ 未变化）。判定逻辑抽到 `Swarm.Core.Operations.ForegroundGuard`，5 例单测覆盖。**`AutoSetFocus=false` 尚未生效**：托管 `System.Windows.Automation` 不暴露该开关、也拿不到底层 `IUIAutomationElement`，要真正关闭必须自建 `IUIAutomation2` COM 客户端；计划与 Epic C 的 UIA 批取客户端共用同一个 `IUIAutomation` 实例实现，避免为动作层单独造一套元素寻址。当前用"度量 + 恢复 + 标注"保证可观测的硬承诺。
- **实施状态（2026-11-22，B3 原生客户端）**：新增 `NativeUiaSession`，用 `CoCreateInstance(CUIAutomation8, IID_IUIAutomation)` 建原生客户端，对 `IUIAutomation` 延伸槽 58/59 置 `AutoSetFocus=false` 并用 58 读回 0 才启用（探针实测默认 1 → 置 0 后 0）。`click` / `type_text` 优先走原生：以托管元素 `BoundingRectangle` 中心 `ElementFromPoint`，再用 `GetRuntimeId` 沿 ControlView 父链最多 12 层匹配同一元素，然后按既有模式优先级执行 `Invoke/Toggle/SelectionItem/ExpandCollapse` 或 `Value.SetValue`（先读 `IsReadOnly`）；任一步失败自动回退原托管阶梯。实测（`e2e/edge-tiers.ps1`）4 次 click 与 1 次 value 全部 `session=True / resolved=True / clicked|written=True`，六类动作仍全 `tier=uia`、`focusCalls=0`。**两点如实记录**：① `IID_IUIAutomation2` 在本机 `QueryInterface` 返回 `E_NOINTERFACE`，因此不 QI，直接调用延伸槽位；② `AutoSetFocus=false` 只约束客户端，Chromium 提供程序在 `Invoke` 时仍自行激活窗口，`foregroundRestored=true` 与恢复逻辑继续保留，不能说"已彻底不抢焦点"。**与计划的偏差**：未与批取共用同一个 `IUIAutomation` 实例（托管 `AutomationElement` 取不到底层指针，共用需把批取链整体原生化），代价是每次动作多一次点解析；`scroll` 与 `press_key` 未接入原生层。老式 MSAA 代理控件（如 WinForms `TextBox`，无 `ValuePattern`）会按设计退回真实键盘，`type-focus` E2E 覆盖该回退。
- **P1-S 外部前台不变量（2026-11-23）**：`edge-tiers.ps1` 新增独立第三方前台窗口，不再只相信动作结果里的布尔字段；每个动作前后直接调用 `GetForegroundWindow` + `GetAncestor(GA_ROOT)` 比较根窗口句柄，要求始终是第三方窗口，同时保留 `foregroundUnchanged` 与 `focusCalls=0` 断言。

### ADR-04 元素 ID：稳定标识 + 代数校验

- **决定**：`elementId` 由 `(runtimeId 哈希, 角色, 名称)` 生成，并携带观察代数 `generation`；动作执行前校验代数，过期返回 `invalid_state` 并提示重新 `observe`。
- **理由**：结构变化后旧 ID 指向错元素，是自动化误操作的主要来源；代数校验把"静默点错"变成"显式失败"。
- **验收**：结构变化后旧 ID 的动作 100% 返回 `invalid_state`；同一静态度量下 ID 在两次 observe 间稳定。
- **实施状态（代数部分完成，2026-11-22）**：ID 采用 `g<generation>:<path>`，`observe` 每次自增并把 `generation` 放进返回；动作先比对代数，不一致返回 `invalid_state`，元素查不到仍返回 `stale_observation`。`ElementId`（Core）负责编码/解析/去前缀，3 例单测；`type-focus` E2E 用新 ID 全程通过。**与 ADR 原文的偏差**：稳定标识仍用 UIA 树路径，`runtimeId` 哈希未做，因此结构变化后路径本身仍会变；且 ID 一旦携带代数，就无法同时满足"整串 ID 跨两次 observe 稳定"和"旧 ID 必失败"——实际口径是**路径部分稳定、整串 ID 只在一次观察内有效**，旧串一律 `invalid_state`。若后续要支持"同一元素跨观察复用同一 ID"，应改为动作额外传 `generation` 参数，而不是把代数嵌进 ID。

### ADR-05 呈现：真实像素镜像 + 标注 + 可选结构视图

- **证据**：
  - **Playwright Trace Viewer** 的既有事实标准是「screencast/before-action-after 截图 + DOM 快照」并存：截图回答"发生了什么"，DOM 快照回答"结构是什么"，二者用途不同、从不互相替代。
  - **Windows Recall** 给用户看的是快照（截图），语义索引只用于检索与跳转。
  - **OpenAI Operator** 用实时视图 + takeover + watch mode（用户离开即暂停）；**ChatGPT agent** 同样以 takeover 处理敏感输入。
  - **AgentGUI（arXiv 2607.26300）**：轨迹可视化 + 人工干预，受控实验中定位关键元素的时间下降 38%。
  - **Dark Patterns Meet GUI Agents（arXiv 2509.10723）**：Operator / Claude CUA / Browser Use 均采用分屏，用户需在"计划文本"与"实时动作"间来回切换，且界面只显示"做了什么"、不显示"为什么"。
  - **Overseeing Agents Without Constant Oversight（arXiv 2602.16844）**：可翻页/放大的截图 + 文本高亮，是人工验证的主要手段。
  - **Magentic-UI（微软）**：以 action guards 对不可逆动作做前置确认。
- **决定**：把呈现拆成三个互不替代的通道。
  1. **Mirror**：5–10 fps 的真实像素（WGC 帧），只用于人工监督，不追求 30fps。
  2. **Annotation**：30Hz 矢量层（动作光标、目标框、步骤状态、证据），复用现有 `action.mjs` / `ops.mjs`。
  3. **Structure view**：可选开关，渲染语义树/元素框（**不做字体拟合、不做像素级还原**），供调试与无障碍使用；等价于 Playwright 的 DOM snapshot 角色。
  - AI 感知走独立的按需全分辨率帧 + 语义树，不复用 mirror 帧，避免"给人看的低帧率画面"污染模型输入。
- **明确放弃**：把"语义克隆"作为默认人类视图。理由有三：
  1. 克隆与真实窗口状态一旦不一致（原生弹窗、toast、动画、加载态），就构成 UI 欺骗面；微软 Agent 安全模型把 UI spoofing 列为必须靠隔离来消除的风险。
  2. 字体拟合栈是正确性瓶颈（±8% 字号、笔画比猜字重），而它解决的问题（"看清 AI 在做什么"）用真实像素天然解决。
  3. 所有已上线产品（Operator、ChatGPT agent、Recall、Playwright Trace、Magentic-UI）都选择真实像素，没有一家用重建 DOM 作为人类主视图。
- **增量要求**：动作事件补 `intent`（为什么做），ops 时间线呈现「计划 → 动作 → 证据」三元组，直接回应上述用户研究里"只看到 what、看不到 why"的问题。
- **代价**：帧编码开销（5–10 fps + JPEG/增量，而非 30fps 全分辨率）；若用 DWM 缩略图需拆独立穿透窗口，因此推荐 WGC 帧经 WebMessage 复用现有 renderer，避免 D3DImage 互操作。
- **保留**：若产品确实需要"重建风格"，只能作为 Structure view 的视觉皮肤，不得宣称像素级还原。

### ADR-06 协议：版本协商 + 进度/取消 + 注解

- **决定**：保留 `2024-11-05` 握手兼容（Trae 当前可用），同时按客户端请求的 `protocolVersion` 协商；实现 `notifications/progress`（观察/动作/截图阶段）、`notifications/cancelled` → 取消 `ActionCoordinator` 代次、`tools/list` 的 `annotations`（`readOnlyHint/destructiveHint/idempotentHint/openWorldHint`）、`outputSchema` + `structuredContent`；`tools/list` 顺序确定以利提示缓存。
- **理由**：最新修订已把"无状态、discover、订阅、任务扩展"作为方向；一次性跳到 2026-07-28 会破坏现有客户端。分两步：先补齐低风险能力，再在客户端支持后切换无状态模式。
- **不实现**：Roots / Sampling / Logging（已弃用）。
- **验收**：MCP 握手/工具清单/错误码有 transcript 金样本；取消后 1s 内返回 JSON-RPC `-32800`，且任务与 token 保持有效。
- **实施状态（2026-11-23）**：已完成 ① `initialize` 按客户端 `protocolVersion` 协商（当前支持集仅 `2024-11-05`，不支持或缺失时回退该版本）；② `tools/list` 的 9 个工具全部带 `annotations` 与 `outputSchema`（只声明顶层键、不设 `required`、`additionalProperties=true`）；③ `notifications/cancelled` 全链路：Gateway 读循环改为并发派发（`Program.cs` 不再串行等待工具调用），按请求 id 登记 CTS，取消后返回 JSON-RPC `-32800`；桌面新增 `SwarmDesktop.Control.V1` 控制管道与 `ActionCoordinator.CancelCurrent()`（取消当前代次并立即建立新代次，任务/token 保持有效）；④ `notifications/progress` 全链路：IPC 响应协议扩展为"若干 `progress` 行 + 一条最终响应"，`DesktopIpcServer` 以第三个回调参数流式写出 `{"id":…,"progress":{"value":0..1,"message":"capture|uia|screenshot|execute|verify|observe|done"}}`，`DesktopTaskService` 在抓屏/UIA/截图与动作执行/校验/刷新观察边界上报且强制单调递增；Gateway 读循环识别进度行后转成 `notifications/progress`，仅在请求带 `_meta.progressToken` 时发送（字符串/数字 token 原样回显，`total=1`），响应与通知共用同一把 stdout 写锁；⑤ MCP transcript 金样本：`Swarm.Tests/transcripts/` 下 9 份场景（握手与版本回退、工具清单、工具成功、截图拆分、工具错误、协议错误码、进度通知、取消、传输异常），由 `McpTranscriptTests` 回放并按排序后的 JSON 逐字比较。`dotnet test` **345 通过**（新增 7 例协议/取消单测 + 10 例 transcript 用例）；控制管道实测：非 Gateway 进程返回 `unauthorized`，伪装为 `Swarm.Gateway.exe` 的进程返回 `ok` 且桌面日志出现 `control cancel accepted`。`edge-tiers` 与 `type-focus` 回归通过。**后续（由 ADR-09 取代）**：版本支持集仍只有 `2024-11-05`；复核后改为 legacy 多版本 + modern 双纪元，见 §10.2。
- **取消通道（已实现）**：主 IPC 是"一请求一管道 + 串行 accept"，动作执行期间无法再接受第二条连接，因此取消走独立控制管道 `SwarmDesktop.Control.V1`（`CurrentUserOnly` + 进程名校验），只接受 `{"action":"cancel"}`。Gateway 作为尽力而为的发送方：控制管道不可达时仍向客户端返回 `-32800`，避免客户端被桌面状态拖住。取消不结束任务、不改变观察代数；已交给 Win32 的原子输入无法撤回。
- **P1-S 取消实测（2026-11-23）**：`p1-s-invariants.ps1` 用 `SWARM_VISUAL_LEAD_MS=2000` 让动作停在准备阶段，在途发送 `notifications/cancelled`，硬断言 1 秒内收到 JSON-RPC `-32800`；随后用同一 token 成功 `observe`，证明任务没有被取消误销毁。
- **进度通道（已实现）**：一次性管道无法在响应前流式回传阶段，因此把线格式改为"进度行 + 最终响应"。Gateway 最多读取 512 行、沿用原有 65s 超时防止失控；进度写失败只记桌面日志，不影响操作结果。没有 `_meta.progressToken` 时不发送任何通知，避免"假进度"；版本策略已由 §10.2/ADR-09 改为双纪元（legacy 多版本 + modern 可选）。

### ADR-07 安全：策略化任务 + 审计 + 注入防护

- **决定**：
  - `task_begin` 增加策略参数：允许的应用/标题、只读模式、敏感动作二次确认、最大动作数与时长。
  - 屏幕内容一律视为不可信：页面文字不得改变工具策略；跨应用、提交、删除、外发类动作强制确认。
  - 截图与日志脱敏：`sensitive` 字段、密码框、已知敏感控件统一打码；审计写 JSONL（动作/层级/目标/证据/结果），不落原文。
  - 保留"进程校验 + token 绑定 Gateway"，增加 Gateway 退出即取消代次的看门狗。
- **理由**：MCP 安全基线与微软 Agent 安全模型都把"最小权限 + 人类在环 + 内容不可信"作为前提；本项目的风险面是"能读屏 + 能注入输入"。
- **验收**：策略外目标返回 `policy_denied`；敏感文本在截图/日志/事件中均不可见；审计可按任务回放。

### ADR-08 隔离：独立会话作为可选根治模式

- **决定**：不改默认单会话体验；新增"隔离模式"设计位：目标应用在独立 Windows 会话（Agent Workspace / 独立账户）中运行，Gateway 通过会话感知的窗口枚举与 WGC 观察，用户只读观看、可随时接管。
- **理由**：微软平台方向（Agent Workspace、Execution Containers）已把"独立会话"作为标准答案，可同时消除抢焦点、UI 欺骗与输入注入风险。
- **代价**：跨会话窗口枚举/输入需要 WTS 与权限设计；属于 P2，先保证感知/动作/协议三层抽象能指向其他会话。
- **验收**：在独立会话中完成 `task_begin → observe → click → task_end`，用户会话前台窗口全程不变。

---

## 6. 迁移路线

> **顺序已由 §10.6 重排**：治理提前到 P1 首位，protocol 提升为 P1-P，Word provider 成为 P1-W，隔离改为 MXC 优先。本节保留原始分层，仅作背景。

### P0：正确性与无副作用（最高优先，纯增量）

| 项 | 改动 | 验收 |
|---|---|---|
| 抓屏抽象 + WGC | 新增 `ICaptureBackend`、`WgcWindowCapture`（手写 COM vtable，保持 `net10.0-windows`，仅加 `Vortice.Direct3D11`）；`CaptureService` 改为按任务复用会话 | 遮挡/非前台可取帧；最小化显式报错；帧延迟 P95 ≤ 120ms；无 CsWinRT/ComWrappers 冲突 |
| 动作阶梯 | `InputDispatcher` 增加 UIA 模式层；`AutoSetFocus=false`；返回 `tier=uia` | 前台窗口不变；Chromium 点击不再需要 `focus` |
| UIA 批取 | `UiaSnapshotReader` 用 `CacheRequest`；MTA + 超时 | observe P95 降 ≥40%；无挂起 |
| 文本属性段 | `UiaTextStyleReader` 用 `TextPattern` 属性 + `TextUnit_Format` | 混排 span 与实际渲染一致；字号误差 ≤1pt |
| ID 代数 | `elementId` + `generation` 校验 | 结构变化后旧 ID 必失败 |
| 协议补强 | 进度、取消、注解、`outputSchema` | transcript 金样本；取消 1s 内生效 |

### P1：呈现与治理

- 按 ADR-05 落地三通道：mirror 帧通道（5–10 fps）+ 标注层 + 结构视图开关；动作事件补 `intent`，ops 时间线展示「计划 → 动作 → 证据」。
- 策略化 `task_begin`、脱敏、审计 JSONL。
- `smoke.ps1`；E2E 增加"前台不变 / DPI 125·150·200 / 抓屏后端一致性 / 取消"不变量。
- Gateway 改 self-contained 单文件，Desktop 改 self-contained 普通目录，两者都不依赖目标机安装 .NET 运行时。

### P2：平台化

- 隔离会话模式（ADR-08）。
- 视觉兜底：OCR + 带编号的可交互元素（SoM），仅在 UIA/IA2 均失败时启用。
- Windows Agent Arena 子集作为感知/grounding 回归基线（非每次提交）。
- 应用能力档案（capability profile）：按进程记录可用的 UIA 模式、抓屏后端、是否需要 CDP。

---

## 7. 测试与度量

- **单测**：动作阶梯决策（mock provider 矩阵）、ID 代数、属性段合并、协议协商与错误映射。
- **契约测试**：MCP transcript 金样本（initialize / tools/list / tools/call / 错误 / 取消）。
- **E2E 不变量**：
  1. AI 操作期间前台窗口不变（p0/p1 各加一条硬断言）；
  2. 敏感文本不出现在截图、日志、事件中；
  3. 抓屏后端在遮挡/非前台/DPI 缩放下结果一致（感知哈希）；
  4. 取消后 1s 内无后续动作触达目标。
- **性能预算**：observe P95 ≤ 300ms（含抓屏）、动作 P95 ≤ 150ms、回放 CPU idle 为 0。
- **Win10 策略（2026-11-22 确认）**：不做 Win10 专用回退（DispatcherQueue 路径），维持"不可用即降级"：缺 `IDirect3D11CaptureFramePoolStatics2` 时 `WgcWindowBackend` 记下 `wgc_unsupported` 并在本进程内短路后续 WGC 尝试，链降级 PrintWindow → 屏幕裁剪；`capabilities.captureWgc` / `captureGuidance` 显式上报可用性与影响，让上层能解释画质下降。理由：本机无 Win10 环境无法实测，双路径维护成本高，而降级路径在 Win10 上仍可用。
- **基线**：保持 358 单测 / 52 渲染层；新增用例不得让 E2E 依赖更多外部应用。

---

## 8. 工程化遗留（与设计无关但需收口）

1. 提交按主题拆分：`capture`、`action-ladder`、`semantics-text`、`protocol`、`renderer`、`docs`；需用户授权后执行。
2. `debug-semantic-rebuild-quality.md` 归档到 `.trae/documents/` 或删除。
3. README 已与实测对齐（358/52、七套 E2E、渲染层命令、治理参数/确认流、热键与错误码）。
4. 已新增 `smoke.ps1`：self-contained 冷启动 + MCP `initialize → tools/list → task_begin → observe → task_end`，失败返回非 0。

---

## 9. 待确认决策与风险

| 决策 | 选项 | 影响 |
|---|---|---|
| 呈现定位（已定） | mirror 用真实像素 + 标注 + 结构视图；语义克隆仅作结构视图皮肤 | 字体拟合退出默认路径 |
| WGC 互操作路径（已定） | 手写 COM vtable，保持 `net10.0-windows`；WgcSharp 仅作参考 | 不升 TFM，不引 CsWinRT/ComWrappers 冲突 |
| 黄色采集边框（已定） | 保留，不申请 MSIX 免边框 | 边框即"正在观察"的诚实提示 |
| CDP 适配器（已定） | 不进 P0；仅限 Swarm 以专用 profile 启动并开调试的应用，P2 | 规避 Chrome 136 默认目录禁用调试的限制 |
| 是否需要无人值守（已定） | 作为 P2 隔离会话目标 | 架构与权限设计 |
| 协议升级节奏（已定，见 §10.7） | 双纪元：legacy 默认 + modern `2026-07-28` 可选 | 与 Trae 客户端兼容性 |
| 交付形态（已定，见 §10.7） | Gateway self-contained 单文件；Desktop self-contained 普通目录；AOT 仅 Gateway 可选 | 分发体积与运行时依赖 |

**主要风险**：手写 WGC interop 的接口版本差异（Win10 1803/1903 vs Win11）；部分应用 UIA 模式仍会聚焦；跨会话枚举权限；协议升级与客户端版本错配；WGC 会话在窗口重建/DPI 切换时的重连；黄色边框在长任务中持续可见的体验接受度。

---

## 10. 2026-11 设计复核（基于最新证据）

> 触发：P0 已落地后仍有四个方向性悬而未决——版本支持集只有 `2024-11-05`、Word provider 未抽检、P1/P2 顺序未经证据校验、交付形态未定。本节用规范原文与官方文档重新裁定，**取代 §6 的顺序与 §9 的两条待定项**。

### 10.1 复核结论：五个调整

| # | 原设计 | 复核后 | 关键证据 |
|---|---|---|---|
| 1 | 只支持 `2024-11-05`，等客户端验证后再逐版提升 | **双纪元**：legacy 多版本 + modern `2026-07-28`；默认仍走 legacy，不改变 Trae 现状 | MCP 版本协商页明确 dual-era 服务端可同时服务两代，且 legacy 客户端零感知 |
| 2 | Word 走标准 `TextPattern`，不足再谈 | **Word 官方 UIA 自定义模式（T1）为主**，标准 TextPattern（T2）兜底，COM 只读为受限补充（T3） | Word 官方文档给出 `ITextRangeCustomProvider` 与行/页/列/节号、标题折叠、MathML 等能力 |
| 3 | P1 先做 mirror 三通道，再做治理 | **治理先行**（策略/脱敏/审计/确认），mirror 后置 | GUIGuard-Bench：模型能判断"是否含隐私"但细粒度定位/风险/必要性判断弱；mirror 会把更多像素推给人看，先脱敏才不扩大暴露面 |
| 4 | 隔离会话自研跨会话枚举（WTS + 权限） | **MXC session container 优先**，Agent Workspace 作为运行环境选项；不自研跨会话输入 | MXC 已 GA，官方定位即"agent 不能是自己的安全权威"，由平台强制边界 |
| 5 | 发布形态待定 | Gateway **默认 self-contained 单文件**；AOT 仅 Gateway 可选（需 source-generated JSON），Desktop 不做 AOT | .NET 10 单文件 + WPF 反射/COM 依赖，Desktop 无法安全裁剪 |

### 10.2 ADR-09 协议：双纪元与版本矩阵

- **决定**：
  1. **legacy 时代**（`initialize` 握手）：支持 `2024-11-05`、`2025-03-26`、`2025-06-18`、`2025-11-25`。客户端请求的版本在集合内则原样回显，否则回显集合内最高版本（不报错）。默认仍按 `2024-11-05` 行为，保证 Trae 现状零变化。
  2. **modern 时代**（`2026-07-28`）：实现 `server/discover`；每个请求从 `_meta.io.modelcontextprotocol/protocolVersion` 读版本、`clientCapabilities` 读能力，结果 `_meta` 回 `serverInfo`；所有 modern 结果带 `resultType:"complete"`；`tools/list` 等列表结果带 `ttlMs`/`cacheScope`；工具顺序保持确定性。
  3. **时代判定**：`initialize` → 该 stdio 进程进入 legacy；带 modern `_meta.protocolVersion` 的请求 → 按 modern 独立处理；`server/discover` → 无论哪代都按 modern 形状应答（`supportedVersions` 同时列出 legacy 与 modern，`capabilities` 声明 `tools`，`instructions` 给 LLM 使用要点）。既无 `initialize` 也无 `_meta` 的请求 → `-32600` 并在 `data` 提示先 `initialize` 或 `server/discover`。
  4. **版本不支持**：modern 请求的版本不在集合内 → `-32022 UnsupportedProtocolVersionError`，`data` = `{supported:[...],requested:"..."}`。
  5. **不实现**：`subscriptions/listen`、`ping`、`logging/setLevel`、Roots/Sampling/Logging、`io.modelcontextprotocol/tasks` 扩展。请求级 `notifications/progress` / `notifications/message` 继续走原请求的响应流（与 2026-07-28 一致），不需要订阅通道。
  6. **确认流**：modern 用 MRTR（`resultType:"input_required"` + `inputRequests`）；legacy 无 MRTR，降级为结构化 `needs_confirmation` 结果 + `confirmToken` 重试（见 ADR-11）。

- **版本矩阵**：

| 版本 | 时代 | 支持状态 | 差异处理 |
|---|---|---|---|
| `2024-11-05` | legacy | 已实现，默认 | 当前全部金样本基线 |
| `2025-03-26` | legacy | P1 加入协商集合 | 仅回显版本；`outputSchema`/`structuredContent` 按客户端能力输出 |
| `2025-06-18` | legacy | P1 加入协商集合 | 同上；elicitation 不实现（Trae 能力未知，确认走结构化结果） |
| `2025-11-25` | legacy | P1 加入协商集合 | 同上 |
| `2026-07-28` | modern | P1 实现，默认不启用 | `discover` + `_meta` + `resultType` + `ttlMs`/`cacheScope` |

- **理由**：规范把"现代/传统/双纪元"写成显式术语，并给出 stdio 探测规则（先 `server/discover`，非现代错误再回退 `initialize`）。这意味着升级不是"赌客户端"，而是**服务端同时会说两种方言**；Trae 未公开版本，正好由双纪元兜住。
- **代价**：两套结果形态（legacy 不加 `resultType`、modern 必须加），transcript 金样本要成对维护；`initialize` 与 `_meta` 两条解析路径都要测。
- **验收**：
  - 新增 modern transcript：`server/discover` 成功、`-32022`、带 `_meta` 的 `tools/call`（含 `resultType`）、`ttlMs`/`cacheScope`。
  - legacy 现有 9 份金样本逐字不变（证明默认路径无回归）。
  - `tools/list` 在两种时代下顺序完全一致。
  - 不支持版本的 modern 请求 100% 返回 `-32022` 且 `supported` 完整。

### 10.3 ADR-10 Word provider：自定义模式为主，进程外桥接

- **证据**：Word 官方提供 UIA 自定义模式 `ITextRangeCustomProvider`（GUID `{93514122-FF04-4B2C-A4AD-4AB04587C129}`），最低 Microsoft 365 2112：
  - `GetCustomAttributeValue`（`{081ACA91-…}`）：行号/页号/列号/节号/书签名/页内分栏数/标题展开折叠状态（0 折叠 / 1 展开 / 2 叶子）。
  - `MoveBySentence`、`MoveEndpointBySentence`、`ExpandToEnclosingSentence`：句子级定位，标准 `TextUnit` 没有。
  - `GetMathText`（`{380198E5-…}`）：公式区输出 MathML。
  - `GetTextEx`（`{287ABF68-…}`）与 `IsListPrefixSelected`：列表前缀可控，最低 2606。
  - 调用方式：remote operations + `CallExtension`（`Microsoft.UI.UIAutomation` / `Windows.UI.UIAutomation.Core`），官方提供 `CustomPatternClient_Word_*.ps1` 可实测。

- **决定**（三层，逐层声明能力）：

| 层 | 通道 | 用途 | 启用条件 |
|---|---|---|---|
| T1 | Word 自定义模式（remote operations + `CallExtension`） | 行/页/列/节号、书签、标题折叠、句子移动、MathML、列表前缀 | 检测到 Word 且版本 ≥ 2112；helper 可用 |
| T2 | 标准 UIA `TextPattern` + `TextUnit.Format` | 文本、字体族/字号/字重/斜体/颜色、段落范围 | 始终可用（免费） |
| T3 | Word COM（只读） | 文档路径、保存状态等元数据；用于消歧，不用于正文抽取 | 显式 `allowComReadonly=true`；T1 失败且 T2 不足 |

- **工程约束（关键）**：`Microsoft.UI.UIAutomation` 是 WinRT 组件，.NET 侧消费需要 CsWinRT 投影与 Windows SDK TFM，与 ADR-01"保持 `net10.0-windows`、不引 CsWinRT/ComWrappers"直接冲突。因此 T1 走**独立进程**：
  - 新增 `Swarm.WordBridge.exe`（C++/WinRT，x64，按需启动，空闲 60s 退出），通过 stdio 收 JSON 查询、回 JSON 结果；
  - Bridge 自己按 `HWND` 做 `ElementFromHandle` 与文档元素定位，不接收跨进程 COM 指针（指针无法序列化）；
  - Desktop 侧只发 `{hwnd, kind, start, end, flags}`，拿回 `{ok, text?, attributes?, mathml?, hresult?}`；超时 3s，超时即降级 T2 并写 `textProvider:"word-t2"`；
  - **构建门槛**：CI 需要 VS C++ 工作负载。若工具链不可用，允许只发布 T2，`capabilities.wordCustomPatterns=false` 如实上报，不得静默假装支持。
- **探针（先于实现）**：在装有 Word 的 Win11 上跑 `CustomPatternClient_Word_TextRangeProvider.ps1`，把行/页/列/节号、标题状态、MathML 输出与 Word 状态栏/导航窗格逐项比对，结果作为 T1 的验收证据；脚本只作验证，不作运行时依赖。
- **明确不做**：用 COM 做写入或正文抽取主路径（版本回归与 UI 阻塞风险高）；离线解析 `.docx` 作为默认通道（加密文档、未保存内容、修订态都不可靠），最多作为导出快照的可选增强。
- **验收**：同一文档下 T1 行/页/列/节号与 Word 状态栏一致；标题折叠状态与导航窗格一致；列表项 `GetTextEx(Remove List Prefix)` 去前缀正确；Word 未安装/版本过低时 `textProvider` 明确降级且不报错。

### 10.4 ADR-11 治理：策略 / 脱敏 / 审计 / 确认

- **证据**：
  - GUIGuard-Bench（241 条真实轨迹、4080 张截图）显示模型"能判断是否含隐私"但**细粒度定位、类别、风险、任务必要性判断均弱**；因此不能把"模型自己会注意"当成脱敏手段。该基准同时验证高对比遮罩可让 agent 感知"此处被保护"。
  - MCP 安全基线（tool poisoning / shadowing / rug pull）：工具描述与结果均为不可信输入，写/删/外发需人类确认。
- **决定**：
  1. **`TaskPolicy`**（`task_begin` 入参，缺省保守）：`allowedProcesses[]`、`allowedTitles[]`、`readOnly`、`maxActions`、`maxDurationMs`、`allowCrossApp`、`requireConfirmFor[]`（`submit/delete/send/purchase/cross_app`）、`redaction{mode:mask|metadata|off, categories[]}`。
  2. **三通道脱敏**（同一份 `redactions[]` 驱动，绝不共用像素）：
     - **模型通道**：高对比实心块 + `[REDACTED:<category>]` 文本。刻意让 agent 看见"被遮"，避免它根据模糊像素编造。
     - **人类 mirror**：高斯模糊 + 类别角标，保留版面可读性。
     - **审计**：只记 `category/risk/necessary/bbox` 计数，不落原文。
  3. **敏感识别来源**：控件元数据优先（`IsPassword`、名称/自动化 ID 命中敏感词表）、策略正则（邮箱/证件/银行卡）、可选 OCR 区域（P2）。识别失败按"未知"处理：模型通道默认遮罩，人类通道可点开（记审计）。
  4. **审计 JSONL**：每任务一份，追加写、10MB 轮转，字段固定：
     `{taskId, seq, ts, tool, tier, target{process,nameHash,controlType}, policyDecision, redactions[{category,risk,necessary}], textHash, textLength, result, durationMs}`。原文不落盘，`textHash` 用 SHA-256。
  5. **确认流**：destructive/外发/跨应用动作，modern 走 MRTR `input_required`；legacy 返回 `{status:"needs_confirmation", confirmToken, summary, expiresInMs}`，客户端带 `confirmToken` 重试；token 一次性、绑定 task + 动作指纹。
  6. **注入防护**：屏幕文本永不参与策略判定；工具描述为编译期常量并在启动时记录 schema 哈希；`policy_denied` 与 `needs_confirmation` 都写审计。
- **验收**：
  - 构造 100 个敏感区域（密码框/邮箱/身份证/银行卡/手机号），区域级召回 100%，误遮 ≤ 2%（人工标注集）。
  - 审计文件对构造语料 `grep` 原文命中 0 次；每个 tool 调用 100% 有审计行。
  - 策略外进程/标题返回 `policy_denied`；未带 `confirmToken` 的外发动作 100% 拒绝。
  - 断网 + 无确认时，外发类动作不会执行。
- **实施状态（2026-11-23，核心链路已落地）**：`TaskPolicy` 已支持进程/标题白名单、只读、动作数/时长、跨应用与确认类别，默认 `maxActions=500`、`maxDurationMs=600000`、`allowCrossApp=false`；脱敏支持 `mask/metadata/off` 与 `password/credential/email/id/bank/phone`，`observe.redactions[]` 透出区域元数据；审计写入 `%LOCALAPPDATA%\Swarm\audit\audit-<taskId>.jsonl`（`SWARM_AUDIT_DIR` 可覆盖）。legacy 确认流已落地为 `needs_confirmation` → 120 秒一次性 `confirmToken` → 相同动作重试，绑定 task + 动作指纹并写 `confirm_consumed`；策略/确认失败分别返回 `policy_denied` / `confirmation_invalid` 及具体原因。上述验收中的 100 区域人工标注召回率与断网矩阵不属于本次 P1-S 验收，未据此宣称全部完成。

### 10.5 ADR-08 修订：隔离优先用 MXC session container

- **证据**：Microsoft Execution Containers 已 GA，按策略在运行时强制文件/网络/输入边界；官方前提是"agent 不能是自己的安全权威"。Agent Workspace 提供独立 Windows 会话，可作为用户体验层。
- **决定**：
  - P2 的隔离实现改为：**MXC session container** 承载目标应用，Swarm 在容器边界内观察与操作；Agent Workspace 作为可选运行环境（若用户已启用）。
  - **不自研** WTS 跨会话窗口枚举/输入注入；仅在 MXC 不可用（旧系统/策略禁用）时回退现有"前台恢复 + 标注"。
  - 容器内应用仍需 `task_begin` 策略与审计，隔离不替代治理。
- **代价/风险**：容器内窗口的 WGC 抓取与 UIA 可达性需实测；MXC 要求应用在容器内启动，用户既有会话中的应用无法直接纳入。
- **验收**：容器内完成 `task_begin → observe → click → task_end`，用户会话前台窗口与剪贴板全程不变；容器外目标被策略拒绝。

### 10.6 路线重排（P1/P2）

**排序原则**：先缩小风险面（治理）→ 再保证可交付（smoke/交付形态）→ 再补正确性缺口（Word）→ 再扩大呈现面（mirror）→ 协议现代化全程可并行（向后兼容、风险低）。

| 编号 | 内容 | 依赖 | 验收 |
|---|---|---|---|
| **P1-G** | 治理：`TaskPolicy`、三通道脱敏、审计 JSONL、确认流 | 无（纯增量） | §10.4 四条验收 |
| **P1-S** | `smoke.ps1`（MCP 握手 + `task_begin→observe→task_end`）、E2E 不变量（前台不变/DPI/后端一致/取消）、Gateway self-contained 单文件、Desktop self-contained 目录 | 无 | 发布应用不解析共享 .NET 运行时即可跑通 smoke |
| **P1-W** | Word provider：探针脚本取证 → T2 基线 → `Swarm.WordBridge`（T1）→ 能力上报 | P1-G（策略允许 Word 进程） | §10.3 验收 |
| **P1-M** | mirror 三通道 + 动作 `intent` + ops「计划→动作→证据」 | P1-G（脱敏必须先于放大像素） | 5–10fps、P95 帧间隔 ≤ 200ms、敏感区域在 mirror 不可读 |
| **P1-P** | 协议现代化：版本矩阵、`server/discover`、`_meta`、`resultType`、`ttlMs`/`cacheScope`、modern 金样本 | 无（legacy 默认不变） | §10.2 四条验收 |
| **P2-I** | MXC session container 隔离 | P1-G | §10.5 验收 |
| **P2-V** | OCR + SoM 视觉兜底（仅 UIA/IA2 均失败时） | P1-G | 兜底路径产出带编号可交互元素，grounding 命中率高于裸截图 |
| **P2-B** | Windows Agent Arena 子集回归基线（非每次提交） | P1-M/P1-W | 固定子集可重复跑分 |
| **P2-C** | 应用能力档案（UIA 模式、抓屏后端、是否需要 CDP/WordBridge） | P1-W/P2-V | 按进程输出能力矩阵并被动作阶梯消费 |

**P1-S 实施状态（2026-11-23）**：`publish.ps1` 已发布 win-x64 self-contained 的 Gateway 单 EXE 与 Desktop 普通目录；`smoke.ps1` 通过隔离 `DOTNET_ROOT`、关闭多级查找并移除子进程 dotnet `PATH`，验证两个应用不解析共享运行时，且五步 MCP 会话与退出码契约成立。当前显示环境约 150%：DPI 几何/坐标像素映射、WGC/screen 客户区对齐、取消 <1s 且 token 复用、第三方前台根窗口不变均有 E2E 断言。125%/200% 未实跑，明确输出 `SKIP` 与补跑命令；该环境隔离验证不冒充物理净机或全 DPI 硬件覆盖。

**相对旧 §6 的变化**：
- 治理从 P1 第三项提到第一项；mirror 从第一项降到 P1-M。
- 协议从"待定"提升为 P1-P（双纪元是向后兼容的，不应拖到 P2）。
- Word provider 从"待抽检"变成 P1-W 的完整交付项（探针 → T2 → T1）。
- 隔离由"自研跨会话"改为"MXC 优先"，并明确回退路径。

### 10.7 更新后的决策表（替换 §9 后两行）

| 决策 | 结论 | 影响 |
|---|---|---|
| 协议升级节奏 | **已定**：双纪元，legacy 默认 + modern 可选（ADR-09） | 不赌 Trae 版本；两套结果形态需成对测试 |
| 交付形态 | **已定**：Gateway self-contained 单文件默认；Desktop self-contained 普通目录（非单文件）；AOT 仅 Gateway 可选（source-generated JSON） | 两个应用均免装 .NET；Desktop 目录需完整分发；AOT 不作为默认以免反射/序列化回归 |
| Word 文本通道 | **已定**：T1 自定义模式（进程外 Bridge）> T2 TextPattern > T3 COM 只读（ADR-10） | 需 C++/WinRT 工具链；无工具链时如实降级 |
| 治理与 mirror 顺序 | **已定**：治理先行（ADR-11） | mirror 不得先于脱敏上线 |
| 隔离实现 | **已定**：MXC session container 优先（ADR-08 修订） | 不自研跨会话输入 |

**新增风险**：
- `server/discover` 与 legacy `initialize` 共存时，误判时代会把 legacy 客户端挡在门外 → 用"先 `initialize` 即锁定 legacy"的进程级状态规避，并补金样本。
- `Swarm.WordBridge` 增加构建复杂度（VS C++ 工作负载）→ 明确"可只发 T2"的降级发布策略。
- 高对比遮罩可能改变 agent 决策分布 → 在 P2-B 基线里单列"遮罩前后任务成功率"对比，避免脱敏把任务做废。
- MXC 容器内 WGC/UIA 可达性未实测 → P2-I 先做可行性探针，再决定是否作为默认隔离路径。

---

## 11. 参考来源

- [UFO²: The Desktop AgentOS (arXiv 2504.14603)](https://arxiv.org/abs/2504.14603)
- [UFO: A UI-Focused Agent for Windows OS Interaction (arXiv 2402.07939)](https://arxiv.org/abs/2402.07939)
- [Set-of-Mark Prompting (arXiv 2310.11441)](https://arxiv.org/abs/2310.11441)
- [OmniParser for Pure Vision Based GUI Agent (arXiv 2408.00203)](https://arxiv.org/pdf/2408.00203)
- [Scaling Computer-Use Grounding via UI Decomposition (arXiv 2505.13227)](https://arxiv.org/html/2505.13227v2/)
- [Windows Agent Arena (Microsoft)](https://www.microsoft.com/applied-sciences/projects/windows-agent-arena)
- [Windows Agent Arena (OpenReview)](https://openreview.net/forum?id=W9s817KqYf)
- [Screen capture — Windows apps](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
- [GraphicsCaptureSession.IsBorderRequired](https://learn.microsoft.com/en-gb/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired?view=winrt-26100)
- [IUIAutomation2::AutoSetFocus](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation2-get_autosetfocus)
- [UI Automation caching for clients](https://learn.microsoft.com/zh-cn/windows/win32/winauto/uiauto-cachingforclients)
- [IUIAutomationTextRange 文本属性](https://learn.microsoft.com/zh-cn/windows/win32/winauto/uiauto-usingtextrangeobjects)
- [UI Automation 文本属性标识](https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/ui-automation)
- [Desktop Duplication API](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/desktop-duplication-api)
- [MCP 2026-07-28 changelog](https://modelcontextprotocol.io/specification/2026-07-28/changelog)
- [MCP 2025-11-25 发布说明](https://blog.modelcontextprotocol.io/posts/2025-11-25-first-mcp-anniversary/)
- [Microsoft Execution Containers](https://blogs.windows.com/windowsdeveloper/2026/10/07/microsoft-execution-containers-policy-driven-containment-for-ai-agents/)
- [Windows platform security for AI agents](https://blogs.windows.com/windowsdeveloper/2026/06/02/windows-platform-security-for-ai-agents/)
- [Experimental Agentic Features / Agent Workspace](https://support.microsoft.com/en-us/windows/experimental-agentic-features-a25ede8a-e4c2-4841-85a8-44839191dfb3)
- [Chrome DevTools Protocol — Accessibility](https://chromedevtools.github.io/devtools-protocol/tot/Accessibility/)
- [MCP Security Best Practices](https://mcp.so/blog/mcp-security-best-practices)
- [Playwright Trace Viewer（截图 + DOM 快照并存）](https://playwright.dev/python/docs/trace-viewer)
- [AgentGUI: Observing and Steering Long-Running AI Agents (arXiv 2607.26300)](https://arxiv.org/html/2607.26300v2)
- [Dark Patterns Meet GUI Agents（分屏监督的可用性问题，arXiv 2509.10723）](https://arxiv.org/html/2509.10723v1)
- [Overseeing Agents Without Constant Oversight (arXiv 2602.16844)](https://arxiv.org/pdf/2602.16844)
- [Magentic-UI: 以 action guards 做人类在环（微软研究院）](https://www.microsoft.com/en-us/research/blog/magentic-ui-an-experimental-human-centered-web-agent/)
- [Introducing Operator（实时视图 + takeover）](https://openai.com/index/introducing-operator/)
- [Operator System Card（watch mode）](https://openai.com/index/operator-system-card/)
- [Introducing ChatGPT agent](https://openai.com/index/introducing-chatgpt-agent/)
- [使用“回顾”追溯你的步骤（快照为用户可见形态）](https://support.microsoft.com/zh-cn/windows/%E4%BD%BF%E7%94%A8-%E5%9B%9E%E9%A1%BE-%E8%BF%BD%E6%BA%AF%E4%BD%A0%E7%9A%84%E6%AD%A5%E9%AA%A4-aa03f8a0-a78b-4b3e-b0a1-2eb8ac48701c)
- [IGraphicsCaptureItemInterop（Win10 1903+ 的 Win32 互操作接口）](https://learn.microsoft.com/es-mx/windows/win32/api/windows.graphics.capture.interop/nn-windows-graphics-capture-interop-igraphicscaptureiteminterop)
- [在 .NET 中使用 WinRT COM 互操作类（需指定 Windows SDK TFM）](https://learn.microsoft.com/zh-cn/windows/apps/desktop/modernize/winrt-com-interop-csharp)
- [WgcSharp：raw COM vtable 实现，MIT（含与 WPF/CsWinRT 冲突的说明）](https://www.nuget.org/packages/WgcSharp)
- [Chrome 136 起默认数据目录不再接受 --remote-debugging-port](https://developer.chrome.com/blog/remote-debugging-port)
- [Windows 11 黄色采集边框是系统安全提示（Microsoft Q&A）](https://learn.microsoft.com/en-za/answers/questions/5516494/how-to-remove-the-yellow-frame-of-the-screen-with)
- [MCP 2026-07-28 版本协商与双纪元兼容矩阵](https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning)
- [MCP `server/discover` 规范](https://modelcontextprotocol.io/specification/2026-07-28/server/discover)
- [MCP stdio 传输与向后兼容探测](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio)
- [Word Custom Patterns（`ITextRangeCustomProvider`、属性标识与最低版本）](https://learn.microsoft.com/en-us/office/uia/word/wordcustompatterns)
- [Using Custom Control Patterns（remote operations + `CallExtension`）](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-using-custom-patterns)
- [UI Automation Custom Extensions in Office](https://learn.microsoft.com/en-us/office/uia/)
- [Microsoft.UI.UIAutomation 包装库（GitHub）](https://github.com/microsoft/Microsoft-UI-UIAutomation)
- [CustomPatternClient_Word_TextRangeProvider.ps1（官方探针脚本）](https://www.powershellgallery.com/packages/CustomPatternClient_Word_TextRangeProvider)
- [GUIGuard-Bench: Toward a General Evaluation for Privacy-Preserving GUI Agents (arXiv 2601.18842)](https://arxiv.org/abs/2601.18842)
- [.NET 单文件发布概览](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [.NET Native AOT 部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
