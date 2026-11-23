# Swarm Cursors 交接文档

> 更新：2026-11-23 · 分支 `chore/net10` · 基线：C# 单测 **358** / 渲染层 **52** / E2E 六个脚本（`edge-pixels` 仅诊断）
> 配套设计文档：[swarm-cursors-phase-2-redesign.md](swarm-cursors-phase-2-redesign.md)（决策依据与行业证据；本文只写"现在是什么、怎么接着干"）

---

## 1. 30 秒速览

- **项目**：Windows 桌面 AI 操作工具。MCP Gateway（stdio）↔ Desktop（WPF 覆盖层 + 命名管道 IPC），能观察窗口、注入动作、给用户看"AI 正在做什么"。
- **当前阶段**：第二阶段重构的 P0 已完成；**Epic A/B/C/D 全部完成**；**P1-G 治理层已实施并单测验收**（策略、三通道脱敏的模型/审计通道、审计 JSONL、legacy 确认流）。下一步是 **P1-S**（smoke + E2E 不变量 + Gateway 单文件）。
- **最重要的成果**：
  1. 抓屏链 `WGC → PrintWindow → 屏幕裁剪`，遮挡/非前台仍可取帧，帧延迟 P50 29ms / P95 45ms。
  2. 动作走"UIA 模式 → 定向消息 → 真实输入"阶梯；Chromium 上六类动作全部 `tier=uia`、`focus` 调用 0 次、前台不变。
  3. 治理层默认保守：跨应用默认拒绝，破坏性/外发动作默认要求一次性 `confirmToken`，敏感区域在模型通道被实心块遮罩 + `[REDACTED:<category>]`，审计只落哈希与元数据。
- **最大的遗留**：
  - 人类 mirror 的高斯模糊 + 类别角标只做了占位（密码框/敏感元素沿用 `••••••••`），完整三通道在 **P1-M**。
  - 协议 modern 半区（MRTR `input_required`、`server/discover`、`_meta`）尚未实现，当前确认流只有 legacy `needs_confirmation`；modern 在 **P1-P**。
  - Word provider 仍缺实机取证（本机无 Office），在 **P1-W**。
  - OCR/SoM 视觉兜底、MXC 隔离在 P2。
- **动手前必读**：第 4.5 节治理契约、第 5 节三分钟跑通、第 8 节坑位。E2E 需要独占 Desktop，PowerShell 脚本必须 UTF-8 **带 BOM**。

---

## 2. 项目定位与架构

```
Trae / 任意 MCP 客户端
        │  MCP over stdio（initialize / tools/list / tools/call）
        ▼
Swarm.Gateway ── 命名管道 IPC ──► Swarm.Desktop（WPF，单实例）
（net10.0）        SwarmDesktop.V1      （net10.0-windows，PerMonitorV2）
                   SwarmDesktop.Control.V1（取消）
                                        │
                    ┌───────────────────┼────────────────────┐
                    ▼                   ▼                    ▼
               CaptureService      UiaSnapshotReader      InputDispatcher
            WGC/PrintWindow/屏幕   UIA 树 + TextPattern   UIA 模式/消息/真实输入
                    │                   │                    │
                    └──────────► DesktopPayload（语义重建）◄──┘
                                        │
                                        ▼
                              renderer/*.mjs（覆盖层：光标、标注、时间线）
                                        ▲
                                        │ 同一份 redactions[]
                              Governance（P1-G）
                    TaskPolicy / Redaction / Audit / ConfirmToken
```

- **Swarm.Core**（`net10.0`）：全部可单测的纯逻辑——抓屏管道、操作运行时、语义映射、文本排布、前台守卫、**治理（Governance）**。**新逻辑优先放这里**，因为 `Swarm.Tests` 不引用 `Swarm.Desktop`。
- **Swarm.Desktop**（`net10.0-windows`）：Win32/WGC/UIA 互操作、WPF 覆盖层、IPC 服务、治理接线。桌面侧细节靠 E2E。
- **Swarm.Gateway**（`net10.0`）：MCP 协议面与工具定义，把请求转成 Desktop IPC，并把 `notifications/progress` / `notifications/cancelled` 接住。
- **renderer**：纯 ESM，无 npm 依赖，`node --test` 直接跑。

---

## 3. 当前进度

### 3.1 阶段总表

| 阶段 | 内容 | 状态 | 验收证据 |
|---|---|---|---|
| A1–A5 | 抓屏抽象、WGC 互操作、会话复用、客户区裁剪、降级链 | ✅ | `CapturePipelineTests.cs`、`Capture/Wgc/*`、P50 29ms / P95 45ms |
| Win10 策略 | 不做专用回退，降级 + 能力上报 | ✅ | `capabilities.captureWgc` / `captureGuidance` |
| B1–B2 | 前台不变断言、动作阶梯补齐 `scroll`/`expand_collapse` | ✅ | `ForegroundGuard` 5 例、`e2e/edge-tiers.ps1` 6/6 `tier=uia` |
| C | UIA 批取层、`TextUnit_Format` 属性段、ID 代数、原生 `AutoSetFocus=false` | ✅ | `UiaObserveBudget` 6 例、`ElementId` 3 例、Edge 混排 span 与 DOM 一致 |
| D | 进度/取消/注解/`outputSchema`/版本协商/transcript 金样本 | ✅ | 9 份金样本、7 例 Gateway/协调器单测 |
| **P1-G** | **治理：TaskPolicy、三通道脱敏、审计 JSONL、确认流** | ✅ **已实施（单测验收）** | `GovernanceTests.cs` 13 例；`build -warnaserror` 0 警告；358 全绿 |
| P1-S | `smoke.ps1`、E2E 不变量、Gateway self-contained 单文件 | ⬜ 下一步 | §10.6 |
| P1-W | Word provider：探针 → T2 `TextPattern` → T1 `Swarm.WordBridge` | ⬜ | §10.3 |
| P1-M | mirror 三通道 + 动作 `intent` + ops「计划→动作→证据」 | ⬜ | §10.6 |
| P1-P | 协议双纪元（`server/discover`、`_meta`、`resultType`、modern 金样本） | ⬜ | §10.2 |
| P2 | MXC 隔离 → OCR/SoM → Agent Arena 基线 → 能力档案 | ⬜ | §10.5 |

### 3.2 已验收数字（可直接对外引用）

- C# 单测 **358 通过 / 0 失败**（较 P1-G 前 +13）；渲染层 **52 通过**；`dotnet build -warnaserror` **0 警告 0 错误**。
- UIA 观察批取：Notepad 47 元素 **73ms → 25ms**（-66%）；Paint 172 元素 **303ms → 82ms**（-73%）；批取路径 `liveReads=0`、`livePatternQueries=0`。
- 文本属性段（Edge 混排页 5 段）：26px→19.5pt、16px→12pt、12px→9pt；单元素峰值 **85 → 42** 次。
- 动作层级：Chromium 上 `click(button/checkbox/radio/select)`、`type_text`、`scroll` **全部 `tier=uia`**，`focus` 0 次，`foregroundUnchanged` 全 true。
- WGC 帧延迟（内容变化 → 新帧可读）：**P50 29ms / P95 45ms / max 45ms**。
- 治理单测覆盖：策略字段解析与默认值、进程/标题/只读/动作数/时长/跨应用拒绝、六类敏感区域识别、类别白名单、像素遮罩只改副本、审计字段与"不含原文"、10MB 轮转、token 一次性/过期/任务不匹配/指纹不匹配、动作分类与指纹稳定性。

### 3.3 P1-G 落地清单（本次新增）

| 文件 | 职责 |
|---|---|
| `Swarm.Core/Governance/TaskPolicy.cs` | `policy` 解析 + 保守默认；`redaction{mode,categories}` |
| `Swarm.Core/Governance/PolicyEvaluator.cs` | 开始阶段的进程/标题白名单；动作阶段的只读/时长/动作数/跨应用判定 |
| `Swarm.Core/Governance/RedactionDetector.cs` | 密码框、名称敏感词、邮箱/身份证/银行卡/手机号正则 → `redactions[]` |
| `Swarm.Core/Governance/RedactionRenderer.cs` | 模型通道像素实心块；**始终返回副本**，不污染抓帧缓存 |
| `Swarm.Core/Governance/AuditLog.cs` | JSONL 追加、`audit-<taskId>.jsonl`、10MB 轮转、SHA-256 |
| `Swarm.Core/Governance/ConfirmationBroker.cs` | 一次性 confirmToken，绑定 task + 动作指纹，120s 过期 |
| `Swarm.Core/Governance/ActionClassifier.cs` | `delete/send/submit/purchase` 分类；动作指纹计算 |
| `Swarm.Desktop/DesktopTaskService.cs` | 接线：策略检查、脱敏、审计、确认门、错误映射 |
| `Swarm.Gateway/McpGateway.cs` | `task_begin.policy`、动作 `confirmToken`、输出 schema 增补 |
| `Swarm.Tests/GovernanceTests.cs` | 13 例治理单测 |
| `Swarm.Tests/transcripts/tools-list.json` | 工具清单金样本已同步（测试按排序 JSON 语义比较） |

---

## 4. 契约与不变量（改动前先读）

### 4.1 抓屏

- 后端顺序固定：`WgcWindowBackend → PrintWindowBackend → ScreenFallbackBackend`，**第一个非空白帧获胜**；全部失败时返回最后一次尝试并标 `Blank=true`。
- `CapturedWindow(Frame, Occluded, Blank, Backend, FrameAgeMs)`；`FrameAgeMs` 是"该帧生成至今"，**静态窗口会持续增长**，不能拿它判断帧失效。
- WGC 帧 = 整窗物理像素，客户区裁剪在后端内部完成，越界即降级；最小化 `window_minimized`、关闭 `window_closed`、全失败 `capture_unavailable`。
- 同一窗口 300ms 内复用上次结果；并发抓屏由 `SemaphoreSlim` 串行化。
- WGC 不可用只记一次：`WgcSupport` 三态，按真实抓屏结果记账，并通过 `capabilities` 上报。

### 4.2 动作阶梯

- 层级：`uia`（A）→ `message`（B）→ `input`（C，仅目标已在前台）→ `focus` 是显式补救，不是默认路径。
- `click` 的 UIA 顺序：`Invoke → Toggle → SelectionItem → ExpandCollapse`（已展开则收起）。
- `type_text` A 级用 `ValuePattern`；B 级投递 `WM_CHAR` 后优先 `WM_GETTEXT` 回读，Chromium 改用 UIA 回读。
- `scroll` A 级：先 `WindowFromPoint` 校验仍是目标，再向上最多 5 层找 `ScrollPattern`；`steps = max(1, |delta| / 120)`，400ms 超时回退 B/C。
- 动作返回必须带：`tier`、`verified`、`evidence`、`foregroundUnchanged`、`foregroundRestored`。抢焦点被发现就恢复并如实标注，**不允许把"恢复过"说成"没发生"**。
- `needs_foreground` 只在"目标忽略窗口消息且不在前台"时返回，`reason=app_ignores_message` 是 Chromium/Electron 的正常提示。

### 4.3 观察与 ID

- 坐标统一为**窗口客户区 CSS 像素**；`elementId` 优先于坐标。
- `observe` 返回 `generation`、`elements`、`redactions`。`elements` 字段：`id`、`parentId`、`type`、`name`、`value`、`bounds{x,y,w,h}`、`patterns[]`、`sensitive`、`redactionCategory`、`processId`、`foreignProcess`。**不要把它和渲染用的 `elements`（`kind`/`semantic`/`css`）混淆。**
- `patterns[]` 固定 12 项：Invoke/Toggle/SelectionItem/Selection/ExpandCollapse/Value/Scroll/Text/RangeValue/GridItem/Window/Transform。
- `elementId` 形如 `g<generation>:<path>`；代数不一致返回 `invalid_state`，查不到返回 `stale_observation`。跨观察记忆用 `ElementId.Stable`。
- 脱敏后的元素 `value` 变成 `[REDACTED:<category>]`，`sensitive=true`；`observe.redactions[]` 每项为 `{elementId, category, risk, necessary, bounds}`，`bounds` 是客户区 CSS 像素。

### 4.4 协议

- 工具共 9 个：`task_begin`、`observe`、`click`、`type_text`、`press_key`、`scroll`、`focus`、`task_end`、`task_stop`。
- 所有工具先校验 token：token 与目标进程 `ProcessId` 绑定，IPC 还会校验 Gateway 进程；`task_begin` 需要本机用户授权对话框确认。
- `observe.includeScreenshot=true` 才回 PNG（最长边 1600px）；`task_begin` 默认带一次截图。
- 错误以 `isError:true` + `content` 返回；`structuredContent.error.code` 是稳定错误码（见 README 与第 10 节）。
- `notifications/progress` 只在请求带 `params._meta.progressToken` 时发送；`notifications/cancelled` 返回 `-32800`，任务与 token 保留。

### 4.5 治理契约（P1-G，务必先读）

**1）`task_begin.policy`（全部可选，缺省保守）**

```json
{
  "targetProcess": "notepad",
  "policy": {
    "allowedProcesses": ["notepad.exe"],
    "allowedTitles": ["Untitled"],
    "readOnly": false,
    "maxActions": 50,
    "maxDurationMs": 300000,
    "allowCrossApp": false,
    "requireConfirmFor": ["submit", "delete", "send", "purchase", "cross_app"],
    "redaction": {
      "mode": "mask",
      "categories": ["password", "credential", "email", "id", "bank", "phone"]
    }
  }
}
```

| 字段 | 默认 | 语义 |
|---|---|---|
| `allowedProcesses` | `null`（不限，目标本身已由 `task_begin` 选定） | 白名单，忽略大小写与 `.exe` 后缀；不匹配 → `policy_denied:process_not_allowed` |
| `allowedTitles` | `null` | 标题包含匹配（忽略大小写）；不匹配 → `policy_denied:title_not_allowed` |
| `readOnly` | `false` | 拒绝 `click/type_text/press_key/scroll/focus`，`observe` 仍可用 |
| `maxActions` | `500` | 已执行动作数上限（1–10000），超限 → `policy_denied:max_actions` |
| `maxDurationMs` | `600000`（10 分钟） | 从 `task_begin` 起算，超时 → `policy_denied:time_exceeded` |
| `allowCrossApp` | `false` | 元素 `processId>0` 且不等于目标进程时视为跨应用；拒绝 → `policy_denied:cross_app` |
| `requireConfirmFor` | 上述 5 类 | 显式空数组表示全部免确认；未配置时按默认 5 类 |
| `redaction.mode` | `mask` | `mask` 同时遮文本与像素；`metadata` 只替换文本；`off` 关闭策略脱敏 |
| `redaction.categories` | 空 = 全部 | 白名单；可选 `password/credential/email/id/bank/phone` |

- 策略在**本地授权框之前**判定；被策略拒绝不会弹授权框。
- 动作数在真正执行前 +1，因此执行失败的尝试也占用预算；`needs_confirmation` 不计入。
- 跨应用只对带 `elementId` 的动作可判定；纯坐标动作天然落在目标客户区内。

**2）三通道脱敏**

- **模型通道（已实现）**：敏感区域在截图缩放前涂成高对比实心块（`RGBA 18,18,18,255`），元素 `value` 替换为 `[REDACTED:<category>]`。刻意让模型看见"被遮"，避免根据模糊像素编造。
- **人类 mirror（占位）**：现有覆盖层对密码框显示 `••••••••`；完整高斯模糊 + 类别角标在 P1-M。
- **审计（已实现）**：只记 `category/risk/necessary`，不落原文、不落 bbox 像素内容。
- 检测顺序：`IsPassword` → 名称敏感词（password/pwd/secret/token/密码/口令/密钥/令牌/验证码…）→ 值正则（身份证 → 银行卡 → 手机号 → 邮箱）。当前 `UiaElementInfo` 没有自动化 ID，暂未用自动化 ID 命中；OCR 留 P2。
- `risk`：密码/凭据/证件/银行卡为 `high`，手机号/邮箱为 `medium`；`necessary` 目前统一 `false`（尚无任务语义判断）。
- `mode=off` 只关闭**策略脱敏**；密码框的既有 `sensitive` 打码仍保留，避免关闭治理反而泄露密码。

**3）审计 JSONL**

- 位置：`%LOCALAPPDATA%\Swarm\audit\audit-<taskId>.jsonl`，可用环境变量 `SWARM_AUDIT_DIR` 覆盖；每任务一份、追加写、单文件 10MB 后轮转为 `audit-<taskId>.<n>.jsonl`。
- 每个工具调用一行，固定字段：

```json
{"taskId":"…","seq":7,"ts":"2026-11-23T12:00:00.0000000+08:00","tool":"click","tier":"uia","target":{"process":"notepad","nameHash":"<sha256>","controlType":"Button"},"policyDecision":"confirm_consumed","redactions":[],"textHash":"<sha256>","textLength":6,"result":"ok","durationMs":41}
```

- `policyDecision`：`allow` / `denied:<reason>` / `confirm_required` / `confirm_consumed` / `denied:confirmation_invalid`。
- `result`：`ok` 或 `error:<code>`；`tier` 来自实际动作结果（观察为 `null`）。
- `textHash` 用 SHA-256；`click` 记 label、`type_text` 记完整文本、`press_key` 记按键。**原文不落盘**。
- `task_begin` 会先建审计文件；即使策略拒绝也会留下该次尝试的审计行。

**4）确认流（legacy；modern MRTR 在 P1-P）**

- 触发：动作类别命中 `requireConfirmFor`（`delete/send/submit/purchase/cross_app`），或本次动作被判定为跨应用且策略要求。
- 未带 `confirmToken` 时**不执行**，返回成功结果：

```json
{"status":"needs_confirmation","confirmToken":"<一次性 token>","summary":"点击：发送邮件","expiresInMs":120000}
```

- 客户端带同一动作 + `confirmToken` 重试；token **一次性**、绑定 `taskId` 与动作指纹，120s 过期。
- 指纹由 `method + params`（按键名排序）计算，**排除 `confirmToken` / `token`**；重试时其余参数必须一致（数字/字符串的序列化形式也要一致）。失败返回 `confirmation_invalid`（`missing` / `expired` / `unknown_or_used` / `task_mismatch` / `fingerprint_mismatch`）。
- 走 token 门时不再弹本机 `ConfirmDanger`；未要求 token 的既有危险动作仍保留本机确认，避免策略显式放宽时反而失去提示。

**5）注入防护**

- 屏幕文本**永不参与策略判定**：动作类别只看调用方显式 `label`（click）或 `key`（press_key）。
- 代价：模型不传 `label` 时，即使点到"发送"按钮也不会触发确认；这是有意的取舍，调用方应始终带语义 label。
- 工具描述为编译期常量；`policy_denied` 与 `needs_confirmation` 均写审计。

---

## 5. 三分钟跑通

```powershell
# 0) 跑 E2E 前先退出正在运行的 Swarm.Desktop（会结束你正在用的实例）
Get-Process Swarm.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force

# 1) 编译（0 警告门）
dotnet build "C:\Users\kaer\Apps\swarm-cursors\desktop\SwarmDesktop.sln" -warnaserror

# 2) 单测（当前 358）
dotnet test "C:\Users\kaer\Apps\swarm-cursors\desktop\SwarmDesktop.sln" --no-build

# 3) 渲染层单测（52，无需 npm）
cd C:\Users\kaer\Apps\swarm-cursors\desktop\renderer; node --test *.test.mjs

# 4) E2E（先 publish 到 desktop\publish-verify，再串行跑六个脚本）
powershell -File C:\Users\kaer\Apps\swarm-cursors\desktop\e2e\run-all.ps1

# 5) 只跑 Chromium 动作层级验收
powershell -File C:\Users\kaer\Apps\swarm-cursors\desktop\e2e\edge-tiers.ps1

# 6) 发布给 Trae 用（发布后必须重启 Trae，Gateway 是常驻子进程）
powershell -File C:\Users\kaer\Apps\swarm-cursors\desktop\publish.ps1
```

**E2E 前置条件清单**

1. 没有其它 `Swarm.Desktop` 在跑（含 `publish` 目录的常驻实例）。`run-all` 会直接报错而不是误杀。
2. 桌面可交互（会启动 Edge 新窗口并自动点击授权对话框），不要锁屏。
3. `msedge.exe` 在 `C:\Program Files (x86)\Microsoft\Edge\Application\`；脚本按测试页 `<title>` 匹配窗口。
4. 跑完检查无残留：`Get-Process Swarm.Desktop,Swarm.Gateway` 与 `%TEMP%\swarm-edge-profile-*`。

**治理最小验证（不启动 E2E）**

1. 启动 Desktop，客户端发 `task_begin`（带上面的 `policy`）。
2. `observe`：确认敏感元素的 `value` 是 `[REDACTED:email]` 之类，且 `redactions[]` 非空。
3. `click`（`label:"发送邮件"`）不带 token：应收到 `status:"needs_confirmation"`，动作未执行。
4. 带 `confirmToken` 原样重试：动作执行，审计出现 `confirm_consumed`。
5. 检查 `%LOCALAPPDATA%\Swarm\audit\`：每行都有 `taskId/seq/tool/policyDecision/result`，`grep` 构造的敏感原文应 0 命中。

---

## 6. 代码地图

### 6.1 Swarm.Core（可单测逻辑，优先在这里扩展）

| 路径 | 职责 |
|---|---|
| `Capture/CaptureContracts.cs` | `ICaptureBackend`、`CaptureAttempt`、`CapturedWindow`、`CaptureGeometry` |
| `Capture/CapturePipeline.cs` | 后端链：首个非空白帧获胜，全失败透传最后一次并标 `Blank` |
| `Capture/FrameStats.cs` / `OcclusionSampler.cs` | 空白帧判定 / 遮挡采样 |
| `Operations/ForegroundGuard.cs` | 前台是否被改变、是否已恢复的纯函数判定 |
| `Operations/OperationRunner.cs` / `OperationStep.cs` | 动作编排与步骤状态 |
| `ActionCoordinator.cs` | 动作代次、取消、人工接管 |
| `RebuildStrategy.cs` / `UiaMapper.cs` | 语义重建策略与元素映射 |
| `Semantics/Ia2LineBuilder.cs` / `Semantics/UiaWarmupPolicy.cs` | IA2 行构建 / UIA 预热策略 |
| `Visual/TextPlacement.cs` / `TextSurfacePolicy.cs` / `FontMetricFitter.cs` | 文本落位、表面同色判定、字体度量拟合 |
| **`Governance/TaskPolicy.cs`** | 策略解析与默认值 |
| **`Governance/PolicyEvaluator.cs`** | 开始/动作阶段判定 |
| **`Governance/RedactionDetector.cs`** | 敏感区域识别 |
| **`Governance/RedactionRenderer.cs`** | 模型通道像素遮罩 |
| **`Governance/AuditLog.cs`** | 审计写入、轮转、SHA-256 |
| **`Governance/ConfirmationBroker.cs`** | 一次性确认令牌 |
| **`Governance/ActionClassifier.cs`** | 动作类别与指纹 |

### 6.2 Swarm.Desktop（互操作与宿主）

| 路径 | 职责 |
|---|---|
| `CaptureService.cs` | 抓屏入口、300ms 复用、几何/DPI、最小化与关闭错误 |
| `Capture/Wgc/` | WGC 全套：`WgcInterop`（COM vtable）、`WgcDevice`、`WgcFrameReader`、`WgcWindowCapture`、`WgcSessionCache`、`WgcWindowBackend`、`WgcSupport`、`WgcProbe` |
| `Capture/PrintWindowBackend.cs` / `ScreenFallbackBackend.cs` / `FrameOps.cs` | 兜底后端与帧操作 |
| `InputDispatcher.cs` | 动作阶梯全部实现（UIA 模式、PostMessage、SendInput、前台守卫） |
| `DesktopTaskService.cs` | 工具语义、观察缓存、错误映射、**治理接线（策略/脱敏/审计/确认）** |
| `DesktopPayload.cs` | 语义重建载荷（渲染用 elements 与截图） |
| `UiaSnapshotReader.cs` / `UiaTextStyleReader.cs` / `Ia2TextReader.cs` | 观察快照 / TextPattern 字体属性 / IA2 文本 |
| `DesktopIpcServer.cs` | 命名管道、进度行流式写、`SwarmDesktop.Control.V1` 取消 |
| `NativeMethods.cs` | Win32 + DWM 声明（含 `TryGetVisibleWindowRect`） |
| `MainWindow.xaml.cs` | 覆盖层、热键、托盘、渲染宿主 |

### 6.3 Gateway / 渲染层 / 测试

| 路径 | 职责 |
|---|---|
| `Swarm.Gateway/McpGateway.cs` | MCP 握手、工具清单/描述、**policy/confirmToken/outputSchema 声明**、结果封装 |
| `Swarm.Gateway/DesktopGatewayTransport.cs` | 管道请求、progress 行解析、取消控制管道 |
| `renderer/action.mjs`、`ops.mjs`、`model.mjs`、`idle.mjs`、`sound.mjs`、`desktop.mjs` | 动作光标、时间线、模型、空闲态、音效、主装配 |
| `Swarm.Tests/` | 358 例；核心套件 + **`GovernanceTests.cs`（13 例）** |
| `Swarm.Tests/transcripts/` | MCP 会话金样本（initialize、tools/list、工具成功/截图/错误、协议错误、进度、取消、传输异常）；`McpTranscriptTests` 回放并按排序后的 JSON 比较 |
| `e2e/` | `run-all.ps1`、`p0-routing`、`p1-pixels`、`p2-overlay`、`type-focus`、`edge-pixels`（诊断无断言）、`edge-tiers` |

> 修改 `tools/list` 后必须同步 `Swarm.Tests/transcripts/tools-list.json`；测试按 JSON 语义比较，可整体重写该文件。

---

## 7. 未提交改动与建议提交顺序

当前工作区 **71 项未提交**（最新提交 `f8a2d8f fix(desktop): 用专属标记区分 AI 注入与自动化点击`，分支 `chore/net10`）。按主题拆分提交前需要用户授权。

| 主题 | 主要文件 |
|---|---|
| `capture` | `Swarm.Core/Capture/*`、`Swarm.Desktop/Capture/*`（含 `Wgc/`）、`CaptureService.cs`、`NativeMethods.cs`、`CapturePipelineTests.cs`、`e2e/p0-routing.ps1` |
| `action-ladder` | `InputDispatcher.cs`、`ForegroundGuard.cs`、`DesktopTaskService.cs`（动作结果）、`ForegroundGuardTests.cs`、`e2e/edge-tiers.ps1`、`run-all.ps1` |
| `semantics-text` | `UiaMapper.cs`、`RebuildStrategy.cs`、`DesktopPayload.cs`、`UiaSnapshotReader.cs`、`UiaTextStyleReader.cs`、`Ia2TextReader.cs`、`Semantics/*`、`Visual/*` 及测试 |
| **`governance`** | **`Swarm.Core/Governance/*`（7 个新文件）、`DesktopTaskService.cs`（治理接线）、`McpGateway.cs`（schema）、`GovernanceTests.cs`、`transcripts/tools-list.json`** |
| `protocol` | `McpGateway.cs`、`DesktopGatewayTransport.cs`、`GatewayIpcProtocol.cs`、`DesktopIpcServer.cs`、`ActionCoordinator.cs`、`transcripts/*`、相关 Gateway 测试 |
| `renderer` | `renderer/*.mjs`、`desktop.css`（新增 `idle.mjs`、`sound.mjs` 及测试） |
| `app` | `MainWindow.xaml.cs`、`OperationRunner.cs`、`OperationStep.cs` 及测试 |
| `docs` | `desktop/README.md`、`.trae/documents/swarm-cursors-phase-2-redesign.md`、本文 |

- `README` 的工具表/错误表**尚未补** `policy`、`confirmToken`、`redactions`、`policy_denied`、`confirmation_invalid`；建议并入 `governance` 提交或 P1-S。
- 仓库根目录 `debug-semantic-rebuild-quality.md` 未跟踪，属临时调试记录；`.trae/` 整个目录也未跟踪。

---

## 8. 坑位与已知限制（按踩坑概率排序）

1. **PowerShell 5.1 编码**：含中文的 `.ps1` 必须存为 UTF-8 **带 BOM**；否则按 GBK 解码，报"Try 语句缺少 Catch 或 Finally"这类与内容无关的语法错误。
2. **E2E 独占**：`Swarm.Desktop` 有单实例互斥量和机器级命名管道；跑 E2E 前必须退出 `publish` 版实例。
3. **`publish-verify` 会过期**：只跑单个脚本前先确认它是最新发布，或先跑一次 `run-all`。
4. **`AutoSetFocus=false` 只挡住客户端行为**：提供程序仍可能自行激活窗口（Chromium `Invoke` 实测如此）；`foregroundRestored=true` 不等于"没抢焦点"。
5. **静态窗口 `frameAgeMs` 会一直涨**：WGC 只在内容变化时出帧；健康度要用"改内容 → 新帧可读"的延迟判断。
6. **Chromium 内部滚动容器**：只有页面本体的 `Document` 暴露 `ScrollPattern`；内部 `overflow:auto` 的 div 会降级为 `needs_foreground`，这是如实失败。
7. **DPI**：互操作代码假设 PerMonitorV2（`app.manifest`）；探针进程首行要 `SetHighDpiMode(PerMonitorV2)`，否则 `GetClientRect` 被虚拟化。
8. **WGC 黄色采集边框**：产品决定保留；截图底部 6–8px 可能含边框，逐像素对照要排除。
9. **Win10**：不做专用回退；缺 `IDirect3D11CaptureFramePoolStatics2` 时记 `wgc_unsupported` 并降级。
10. **测试边界**：`Swarm.Tests` 不引用 `Swarm.Desktop`；治理逻辑已放 Core，桌面接线只能靠 E2E/手工。**新治理规则先写 Core 单测。**
11. **UIA 慢 provider**：UIA 调用都在 `Task.Run` + 短超时里（动作 400ms、文本回读 300ms、字体属性 300ms）；观察有 2500ms 软超时 + 400ms 硬宽限。
12. **UIA 批取三个硬约束**：根元素必须在 `CacheRequest.Activate()` 内 `FromHandle`；托管 `TreeWalker` 不吃缓存，子树必须 `FindAll(Children, ControlViewCondition)`；缓存元素出作用域后仍可 `TryGetCurrentPattern` 实时寻址。
13. **原生动作层元素定位**：只能用 `ElementFromPoint(中心点)` + 运行时 ID 向上匹配；元素被遮挡/中心点在兄弟控件/无 bounds 时回退托管。`IUIAutomationElement` 槽位写错会 `0xC0000005`。
14. **审计是 fail-closed**：`AuditWriter` 构造失败（目录不可写）会让 `task_begin` 失败。这是有意的——没有审计就不执行任务；本地部署请确保 `%LOCALAPPDATA%` 可写，或用 `SWARM_AUDIT_DIR` 指向可写目录。
15. **确认令牌对参数敏感**：指纹覆盖除 `confirmToken`/`token` 外的全部参数，且用原始 JSON 值。重试时不要改坐标/文本/数字形式，否则 `fingerprint_mismatch`；120s 过期、一次性，失败需重新发起原动作拿新 token。
16. **默认策略会改变旧客户端行为**：跨应用默认拒绝、破坏性/外发默认要 token。旧脚本若直接 `click(label:"删除")`，会开始收到 `needs_confirmation`；这是预期。要临时放宽必须显式传 `policy.requireConfirmFor: []` / `allowCrossApp: true`。
17. **动作类别只看显式 label**：为防界面文字注入，`click` 不带 `label` 时不会因元素名"发送"而要求确认。调用方应始终提供语义 label（这是治理有效性的前提）。
18. **脱敏关闭的边界**：`redaction.mode=off` 只关闭策略脱敏（邮箱/证件/银行卡/手机号/凭据名称），密码框仍按既有 `sensitive` 打码；不会因为 off 就把密码明文给模型。
19. **审计不落 bbox**：ADR 提到 bbox 计数，但当前审计字段固定为 `category/risk/necessary`；bbox 仅存在于 `observe.redactions[]`，供渲染/调试使用。若后续要做区域回放，需先扩展审计 schema 与测试。
20. **人类 mirror 仍是占位**：覆盖层目前只复用 `••••••••`，没有高斯模糊与类别角标；在 P1-M 完成前，不要把 P1-G 说成"三通道全部完成"。

---

## 9. 下一步（按优先级）

### P1-G：治理（已完成）

- 已落地：`TaskPolicy`、策略判定、六类敏感识别、模型通道文本+像素脱敏、审计 JSONL/轮转/SHA-256、legacy 一次性 `confirmToken`、Gateway schema 与金样本、13 例单测。
- 未完成/有意后置：modern MRTR（P1-P）、人类 mirror 模糊（P1-M）、OCR/SoM（P2）、自动化 ID 敏感词（当前模型无该字段）、`necessary` 语义判断（当前恒 false）。

### P1-S：交付底线（下一步）

1. `smoke.ps1`：冷机（无 .NET 运行时）拉起 Gateway + Desktop，完成 `initialize → tools/list → task_begin → observe → task_end`，退出码可判定。
2. Gateway self-contained 单文件发布（`PublishSingleFile`，必要时 `SelfContained`；AOT 只作为 Gateway 可选项，先量体积与启动时延）。
3. E2E 不变量补齐：
   - 前台不变：动作前后前台根窗口一致（已有单测，补 E2E 断言）。
   - DPI 125/150/200：坐标换算与截图尺寸一致。
   - 抓屏后端一致性：`wgc` 与 `screen` 的客户区坐标对齐。
   - 取消 1s 内返回 `-32800` 且任务/token 保留。
4. 把治理字段补进 README 工具表/错误表；补一条 `policy → needs_confirmation → confirmToken` 的手工冒烟步骤。

### P1-W：Word provider

- 先跑官方 `CustomPatternClient_Word_*` 探针取证（需装有 Office 的机器）。
- 顺序：T2 `TextPattern` 基线 → T1 `Swarm.WordBridge`（C++/WinRT 进程外桥接）→ 能力上报（`capabilities.wordProvider`）。
- 验收按设计文档 §10.3；注意 `task_begin.policy.allowedProcesses` 要放行 `WINWORD.EXE`。

### P1-M：mirror 三通道

- 5–10fps 真实像素 + 30Hz 标注 + 结构视图；P95 帧间隔 ≤ 200ms。
- 用同一份 `redactions[]` 驱动人类通道：高斯模糊 + 类别角标；敏感区域在 mirror 不可读。
- 动作 `intent` 与 ops「计划→动作→证据」。

### P1-P：协议双纪元

- legacy 扩到 `2024-11-05`/`2025-03-26`/`2025-06-18`/`2025-11-25`，默认不变。
- modern：`server/discover`、每请求 `_meta`、`resultType`、`ttlMs`/`cacheScope`、`-32022`；确认流改 MRTR `input_required` + `inputRequests`。
- 补 modern transcript 金样本；两套结果形态成对测试。

### 每条任务的完成定义

1. 改动落在正确层（纯逻辑进 Core，互操作进 Desktop）。
2. `build -warnaserror` 0 警告、单测全绿、相关 E2E 通过。
3. 新增可观测字段同步进 `observe` / 动作结果 / README，且**如实表达**（降级、恢复、失败都要能被看见）。
4. 更新设计文档对应 ADR 的"实施状态"段，不要只改代码。

---

## 10. 排障手册

| 症状 | 可能原因 | 处理 |
|---|---|---|
| `desktop_unavailable` | Desktop 没起 / 管道没连上 | 启动 Desktop；确认没有旧 Gateway 占着管道 |
| 第二个 Desktop 起不来 | 单实例互斥量 | 退出已有实例（含 `publish` 目录的） |
| 抓屏全黑/空白 | GPU 渲染窗口 + WGC 不可用或最小化 | 看 `captureBackend` 与 `capabilities.captureGuidance`；先还原窗口 |
| `window_minimized` | 目标最小化 | 还原后再 `observe` |
| 动作一直 `needs_foreground` | Chromium/Electron 忽略窗口消息 | 显式 `focus` 后重试，或改用 `elementId` 走 UIA |
| UIA 动作返回 `foregroundRestored=true` | 提供程序顺手 `SetFocus` | 正常现象，已恢复原前台并标注 |
| `scroll` 失败但页面能滚 | 滚动容器在内部 div | 接受降级，或把滚动容器做成页面本体 |
| `policy_denied:process_not_allowed` | `allowedProcesses` 不含目标 | 修正策略，或让用户重新 `task_begin` |
| `policy_denied:title_not_allowed` | `allowedTitles` 与标题不匹配 | 用窗口标题片段；注意动态标题（文件名、页码） |
| `policy_denied:max_actions` / `time_exceeded` | 预算耗尽 | 新开任务，或显式放宽 `maxActions`/`maxDurationMs` |
| `policy_denied:cross_app` | 点了别的进程的元素 | 显式 `allowCrossApp:true` 且接受审计，或只操作目标应用 |
| 一直收到 `needs_confirmation` | 客户端未回传 token / 每次参数不同 | 原样重试并带 `confirmToken`；不要改其他参数 |
| `confirmation_invalid:fingerprint_mismatch` | 重试时改了坐标/文本/数字形式 | 用与首次完全相同的参数重试 |
| `confirmation_invalid:expired` | 超过 120s | 重新发起原动作获取新 token |
| 审计目录没有文件 | 用了 `SWARM_AUDIT_DIR` 或 LocalAppData 不可写 | 检查环境变量与目录权限；`task_begin` 会因此失败 |
| 敏感值没有被遮 | 类别被 `categories` 白名单排除 / `mode=off` | 检查 `policy.redaction`；密码框仍会按 `sensitive` 打码 |
| 误遮正常数字 | 16–19 位长数字命中银行卡正则 | 缩小 `categories` 或调整业务文本；OCR 留 P2 |
| E2E 报 Edge 窗口找不到 | 标题不匹配 / 启动太慢 / profile 残留 | 检查页面 `<title>`；清理 `%TEMP%\swarm-edge-profile-*` |
| 脚本报奇怪的语法错误 | `.ps1` 缺 UTF-8 BOM | 另存为 UTF-8 with BOM |
| 逐像素对照差几条边 | 黄色采集边框 / 圆角伪影 | 排除底部 6–8px 与四角，再比 |

---

## 11. 关键参数速查

| 参数 | 值 | 位置 |
|---|---|---|
| WGC 抓帧超时 | 600ms | `WgcWindowBackend.TimeoutMs` |
| WGC 存活/尺寸检查间隔 | 500ms | `WgcWindowCapture.SizeCheckIntervalMs` |
| 观察级抓屏复用窗口 | 300ms | `CaptureService.CaptureAsync` |
| UIA 滚动超时 | 400ms | `InputDispatcher.TryUiaScrollAsync` |
| UIA 文本回读超时 | 300ms | `InputDispatcher.TryReadUiaTextAsync` |
| 字体属性读取预算 | 64 元素 / 300ms / 单元素 44 次调用 | `UiaTextStyleReader.Read` / `CallsPerElementTarget` |
| 滚动折算 | `steps = max(1, abs(delta) / 120)` | `InputDispatcher.UiaScroll` |
| 截图最长边 | 1600px | `observe.includeScreenshot` |
| 策略默认动作数 | 500 | `TaskPolicy.MaxActions` |
| 策略默认时长 | 600000ms（10 分钟） | `TaskPolicy.MaxDurationMs` |
| 确认令牌寿命 | 120s | `ConfirmationBroker.Lifetime` |
| 审计轮转阈值 | 10MB | `AuditWriter.DefaultRotationBytes` |
| 审计目录 | `%LOCALAPPDATA%\Swarm\audit` | `AuditWriter.DefaultDirectory`（`SWARM_AUDIT_DIR` 可覆盖） |
| 像素遮罩颜色 | RGB(18,18,18) / A=255 | `RedactionRenderer.Mask` 默认参数 |
| 像素校验阈值 | 5‰ | `SWARM_PIXEL_DIFF_PERMILLE` |
| MCP 协议版本 | legacy `2024-11-05`…`2025-11-25`（默认）/ modern `2026-07-28`（P1-P） | `McpGateway`（ADR-09） |

---

## 12. 术语

| 术语 | 含义 |
|---|---|
| WGC | Windows.Graphics.Capture，按窗口取帧，遮挡/非前台仍可抓 |
| UIA | UI Automation，控件树与控件模式（Invoke/Toggle/Value/Scroll…） |
| IA2 | IAccessible2，浏览器文本几何的补充来源 |
| tier | 动作层级：`uia` / `message` / `input` |
| elementId | 观察返回的元素标识（`g<generation>:<path>`），动作时优先于坐标 |
| generation | 观察代数，让过期 ID 显式失败（`invalid_state`） |
| TaskPolicy | `task_begin` 的治理策略：进程/标题/只读/预算/跨应用/确认/脱敏 |
| redactions | 敏感区域列表，驱动模型遮罩、mirror 模糊与审计元数据 |
| confirmToken | legacy 确认流的一次性令牌，绑定任务与动作指纹 |
| needs_confirmation | 未确认时的结构化成功结果，不是错误 |
| audit JSONL | 每任务一份的工具调用审计，原文不落盘 |
| mirror | 给人看的真实像素通道（5–10fps，P1-M） |
| 结构视图 | 可选的语义树/元素框视图，不追求像素级还原 |

---

## 13. 交接检查清单

- [ ] 能在本机 `build -warnaserror` + `dotnet test`（358）+ `node --test`（52）全绿。
- [ ] 能退出常驻 Desktop 后跑通 `e2e/run-all.ps1`。
- [ ] 知道 `publish.ps1` 后必须重启 Trae（Gateway 是常驻子进程）。
- [ ] 读过第 4.5 节治理契约与第 8 节坑位，特别是审计 fail-closed、token 指纹、默认策略变化、label 与注入防护。
- [ ] 知道未提交改动（71 项）尚未分组提交，提交前需用户授权；`governance` 主题可独立提交。
- [ ] 知道 P1-G 的边界：modern MRTR、人类 mirror 模糊、OCR/自动化 ID、`necessary` 语义判断仍未完成，分别落在 P1-P/P1-M/P2。
- [ ] 下一步按 **P1-S → P1-W → P1-M → P1-P** 执行（见设计文档 §10.6）；Word provider 需先在装有 Office 的机器上跑官方探针。
