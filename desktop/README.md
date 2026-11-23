# Swarm Desktop

让本地 AI（Trae）通过 MCP 读取并操作 Windows 桌面窗口：截图、元素树、点击、输入、按键、滚动。所有操作都需要本机用户当场授权，且不会主动抢走其它程序的焦点。

与仓库根目录的 Chrome 扩展（Swarm Rebuild）无关，这里是独立的桌面端。

## 组成

| 项目 | 作用 |
|---|---|
| `Swarm.Core` | 与 UI 无关的纯逻辑：坐标换算、截图拼接、窗口匹配、滚轮校验、日志等 |
| `Swarm.Desktop` | WPF 宿主，持有授权、抓屏、输入、覆盖层（四平面：OperationRunner / InputDispatcher / CaptureService / Overlay） |
| `Swarm.Gateway` | stdio MCP 服务器（JSON-RPC），把工具调用经命名管道转发给 Desktop |
| `Swarm.Tests` | xunit 单测，只覆盖 `Swarm.Core` 与少量纯逻辑 |

调用链：`Trae → Gateway(stdio) → 命名管道 \\.\pipe\SwarmDesktop.V1 → Desktop`。

## 前置条件

- 开发与构建：Windows，已安装 **.NET 10 SDK**（当前 TFM 为 `net10.0` / `net10.0-windows`）。
- 默认发布产物为 **win-x64 self-contained**，目标机器无需安装 .NET 运行时：Gateway 是单个 EXE，Desktop 是携带运行时 DLL 的普通目录（不是单文件）。
- Trae 的 MCP 配置指向 `publish\Swarm.Gateway\Swarm.Gateway.exe`。

## 构建与测试

```powershell
# 编译（0 警告要求）
dotnet build desktop\SwarmDesktop.sln -warnaserror

# C# 单元测试（358 例，含 MCP transcript 金样本）
dotnet test desktop\SwarmDesktop.sln

# 端到端（七套脚本，含 P1-S 不变量）
powershell -File desktop\e2e\run-all.ps1

# 渲染层单元测试（52 例，纯 ESM，无 npm 依赖）
cd desktop\renderer; node --test *.test.mjs
```

`run-all.ps1` 会先把两个项目以 self-contained 形式发布到 `publish-verify`，再依次运行八个脚本（`p0-routing`、`p1-pixels`、`p1-s-invariants`、`p2-overlay`、`edge-pixels`、`type-focus`、`edge-tiers`、`concurrency`），任一断言失败则退出码非 0。

`Swarm.Tests/transcripts/*.json` 是 MCP 会话金样本：每份场景声明桌面端行为（`result`/`error`/`progress`/`holdUntilCancelled`/`throw`）和若干步骤；`McpTranscriptTests` 逐步发送请求，按排序后的 JSON 逐字比较通知与响应。新增协议行为时先补样本，再改实现。

`edge-pixels.ps1` 是诊断脚本，只打印 `BEGIN/CLICK/OBSERVE` 观测结果，不设断言。`edge-tiers.ps1` 用 Edge/Chromium 验证动作阶梯：按 `elementId` 对按钮/复选框/单选框/下拉框/输入框各执行一次，再对可滚动页面执行 `scroll`，断言 `tier=uia|message` 占比 ≥ 0.8、`focus` 调用为 0、动作返回的 `foregroundUnchanged` 全为 true，并独立比较每个动作前后的第三方前台根窗口句柄。

`concurrency.ps1` 起 1 个 Desktop + 3 个 Gateway，对三个不同标题的窗口并发 `task_begin`/`observe`/`task_end`：断言 3 个会话的 token、sessionId、目标句柄互不相同，`desktop_status.active=3`，把 A 的 token 交给 B 会被 `unauthorized` 拒绝，第 4 个会话在弹确认框前就返回 `capacity_exceeded`，且确认框不夺取前台焦点。

`p1-s-invariants.ps1` 验证当前显示缩放下的 CSS/物理坐标换算、客户区与 PNG 尺寸、WGC 与强制 `screen` 抓屏对齐，以及取消在 1 秒内返回 `-32800` 且任务/token 可继续使用。未实际运行的 DPI 档位只输出 `SKIP`，不算通过。`run-all` 后可在 Windows 设置中**人工**切换当前显示器缩放（脚本不会修改显示设置），分别补跑：

```powershell
powershell -File desktop\e2e\p1-s-invariants.ps1 -ExpectedDpiPercent 125
powershell -File desktop\e2e\p1-s-invariants.ps1 -ExpectedDpiPercent 150
powershell -File desktop\e2e\p1-s-invariants.ps1 -ExpectedDpiPercent 200
```

**跑 smoke/e2e 前必须先退出正在运行的 Desktop**：单实例互斥量（`Local\SwarmDesktop.SingleInstance`）和命名管道都是机器级的，第二个实例起不来。`run-all` 会检测这个情况并直接报错，不会误杀你的生产实例；它只会清理 `publish-verify` 路径下的自建残留。

## 发布与部署

```powershell
# 发布到 desktop\publish\{Swarm.Desktop,Swarm.Gateway}
powershell -File desktop\publish.ps1

# 冷启动冒烟：默认重新发布到 desktop\publish-smoke
powershell -File desktop\smoke.ps1
```

部署时完整拷贝两个发布目录。`Swarm.Gateway` 目录只应有 `Swarm.Gateway.exe`；`Swarm.Desktop` 目录必须整体分发，其中包含 `coreclr.dll`、`hostfxr.dll`、`Swarm.Desktop.dll` 等文件。不要只拷贝 Desktop 的 EXE。

`smoke.ps1` 会把 `DOTNET_ROOT`/`DOTNET_ROOT_X64` 指向不存在的目录、关闭多级查找并从子进程 `PATH` 移除 dotnet 条目，以证明两个发布应用冷启动不解析共享运行时；这不需要卸载开发机的 .NET。脚本自动接受本机授权对话框，完成 `initialize → tools/list → task_begin → observe → task_end`，任一失败都会返回非 0 退出码，并在 `finally` 清理自建进程。

发布后需要**重启 Trae**（Gateway 是常驻子进程，不重启会继续跑旧镜像）并重开 Desktop。

MCP 配置示例：

```json
{
  "mcpServers": {
    "swarm-desktop": {
      "command": "C:\\Users\\kaer\\Apps\\swarm-cursors\\desktop\\publish\\Swarm.Gateway\\Swarm.Gateway.exe",
      "args": []
    }
  }
}
```

## 使用流程

先启动 `publish\Swarm.Desktop\Swarm.Desktop.exe`（单实例，重复启动会提示）。然后：

```
task_begin  → observe → (focus) → click / type_text / press_key / scroll → task_end
```

`task_begin` 会弹出「Swarm AI 操作授权」对话框，显示应用名、pid 和窗口标题，需要本地点确认。返回的 `token` 与发起请求的 Gateway 进程绑定，换进程复用会被拒绝（`Gateway 或任务会话无效`）。

### 工具

| 工具 | 关键参数 | 说明 |
|---|---|---|
| `task_begin` | `targetProcess`、`targetTitle`（均可选）、`policy` | 授权 + 锁定目标，返回 token、viewport、capabilities、首帧截图 |
| `observe` | `token`、`includeScreenshot` | 元素树、`generation`、`occluded`、`captureBackend`（`wgc`/`print`/`screen`）、`frameAgeMs`、`redactions[]`、能力 |
| `focus` | `token`、`label`、`confirmToken` | 仅在动作返回 `needs_foreground` 时需要 |
| `click` | `token`、`elementId` 或 `x`/`y`、`verify='pixels'`、`observe`、`label`、`confirmToken` | `elementId` 优先 |
| `type_text` | `token`、`text`、`elementId`、`sensitive`、`verify='pixels'`、`label`、`confirmToken` | `sensitive=true` 会打码 |
| `press_key` | `token`、`key`、`verify`、`observe`、`label`、`confirmToken` | 支持组合键，如 `Ctrl+V` |
| `scroll` | `token`、`x`、`y`、`delta`、`label`、`confirmToken` | `delta` 限 ±1200，超限返回 `invalid_params` |
| `task_end` | `token` | 正常结束 |
| `task_stop` | `token` | 立即中止 |
| `desktop_status` | 无 | 只读诊断：活跃会话/容量、隔离形态、抓屏复用、租约队列、UIA 熔断、各 Tier 命中数 |

坐标一律是**窗口客户区 CSS 像素**（`observe` 返回的坐标系），不是屏幕物理像素。`viewport.clientPhysical{x,y,w,h}` 是客户区物理像素位置与尺寸，`viewport.dpiScale` 用于 CSS/物理坐标换算；返回的 PNG 宽高必须等于 `clientPhysical.w/h`。

### 治理、脱敏与确认

`task_begin.policy` 是可叠加的最小权限策略：

| 字段 | 缺省值与语义 |
|---|---|
| `allowedProcesses[]`、`allowedTitles[]` | 省略时不额外设置白名单；目标仍须本机授权 |
| `readOnly` | `false`；为 `true` 时拒绝所有写动作 |
| `maxActions` | `500`，限制已执行动作数 |
| `maxDurationMs` | `600000`（10 分钟） |
| `allowCrossApp` | `false`，默认禁止跨应用动作 |
| `requireConfirmFor[]` | `submit/delete/send/purchase/cross_app`；显式空数组表示关闭这些类别的确认要求 |
| `redaction{mode,categories}` | `mode=mask`；另有 `metadata`（仅文本替换为类别标记）与 `off`（显式关闭）。`categories` 支持 `password/credential/email/id/bank/phone`，省略或空数组表示全部启用 |

动作用语义 `label`（如 `submit`、`delete`、`send`、`purchase`）辅助分类；`observe.redactions[]` 返回本次观察命中的脱敏区域与类别信息。审计按任务写入 `%LOCALAPPDATA%\Swarm\audit\audit-<taskId>.jsonl`，可用 `SWARM_AUDIT_DIR` 改目录；记录策略决策、脱敏元数据和结果，不落敏感原文。

legacy 客户端的人工确认流程：

1. 不带 `confirmToken` 调用敏感动作；动作不会执行，返回 `status:"needs_confirmation"`、`summary`、`confirmToken` 和 `expiresInMs=120000`。
2. 人工核对 `summary` 后，用**完全相同的动作参数**加上该 `confirmToken` 重试。
3. 令牌一次性、120 秒过期，并绑定任务与动作指纹；确认执行后审计记录 `confirm_consumed`。参数变化、跨任务或重复使用都会被拒绝。

### 进度通知

只有在请求的 `params._meta.progressToken` 存在时，Gateway 才会发送 `notifications/progress`：`progress` 为 0..1 且单调递增、`total=1`，`message` 是阶段标识（`capture` / `uia` / `screenshot` / `execute` / `verify` / `observe` / `done`）。没有 token 时不发送任何进度。进度行由桌面端在最终响应前流式写出，Gateway 转成通知后继续等待最终响应。

### 错误语义

| code | 含义 | 处理 |
|---|---|---|
| `needs_foreground` / `app_ignores_message` | 目标忽略窗口消息且不在前台（Chromium/Electron 常见） | 先 `focus` 再重试 |
| `manual_takeover` | 检测到人工键鼠输入，AI 操作已暂停 | 按 `Ctrl+Alt+Shift+R` 恢复后重新 `observe` |
| `invalid_state` | 状态非法，如恢复后未重新观察，或 `elementId` 来自更早的观察（`g<generation>:` 前缀不匹配） | 按提示补 `observe`，并使用最新返回的 `id` |
| `-32800` | 客户端通过 `notifications/cancelled` 取消了在途请求 | 任务与 token 仍有效，可直接继续调用；已交给 Win32 的原子输入无法撤回 |
| `desktop_unavailable` | Desktop 未启动或 IPC 连不上 | 启动 Swarm Desktop 后重试 |
| `invalid_params` | 参数不合法（如 scroll 超限、缺 token） | 修正参数 |
| `policy_denied` | 策略拒绝；原因包括 `process_not_allowed`、`title_not_allowed`、`read_only`、`max_actions`、`time_exceeded`、`cross_app` | 调整目标/动作，或由用户显式放宽任务策略 |
| `confirmation_invalid` | 确认令牌无效；原因包括 `missing`、`expired`、`unknown_or_used`、`task_mismatch`、`fingerprint_mismatch` | 不带旧令牌重新调用，核对新的 `summary` 后再确认 |
| `unauthorized` | token 无效或跨进程复用 | 重新 `task_begin` |

动作结果带 `tier`（`uia`/`message`/`input`）、`verified`/`evidence`，以及 `foregroundUnchanged` / `foregroundRestored`：UIA 模式执行后会比较"动作前 / 动作后 / 恢复后"的前台根窗口，若提供程序顺手 `SetFocus` 把目标推到前台，会恢复原前台并标注 `foregroundRestored=true`；恢复失败则 `foregroundUnchanged=false`。`click` 与 `type_text` 先用自建的原生 COM 客户端执行 UIA 模式，该客户端把 `IUIAutomation2::AutoSetFocus` 置 0 并读回校验；但提供程序仍可能自行激活窗口（Chromium 的 `Invoke` 实测如此），所以"度量 + 恢复 + 标注"继续保留。原生路径失败时自动回退托管 UIA 阶梯。

动作阶梯的 UIA 覆盖：`click` 依次尝试 `Invoke → Toggle → SelectionItem → ExpandCollapse`（已展开则收起），`type_text` 用 `Value`，`scroll` 先用 `WindowFromPoint` 确认命中的仍是目标窗口，再 `FromPoint` 向上最多 5 层找 `ScrollPattern` 并按小步长折算，400ms 超时回退消息/真实输入。`click`/`type_text` 的原生与托管两条路径共用这套模式优先级；没有 `ValuePattern` 的旧式控件（如 WinForms 文本框）会如实退回键盘输入。Edge/Chromium 本轮实测六类动作中五类返回 `tier=uia`（5/6，满足 ≥0.8 门槛），`focus` 调用 0 次（见 `e2e/edge-tiers.ps1`）。Chromium 只在页面本体可滚动时把 `ScrollPattern` 暴露在 `Document` 上；内部 `overflow:auto` 的 div 若没有独立滚动节点，会如实降级并返回 `needs_foreground`。

### 热键

| 热键 | 作用 |
|---|---|
| `Alt+Shift+S` | 重建渲染层（UIA 语义重建，读不到时自动降级为截图） |
| `Alt+Shift+U` | 重建任务 |
| `Ctrl+Alt+Shift+R` | 人工接管后恢复 AI 控制（恢复后旧观察失效，需重新 `observe`） |
| `Ctrl+Alt+Shift+Esc` | 全局急停，不依赖目标是否在前台 |
| `Esc` | 目标在前台时停止 AI 操作，不影响目标自身对 Esc 的处理 |

注册失败时启动会弹出提示，不会静默失效。

### 语义重建

`Alt+Shift+U` 用 UIA 树加截图像素还原前台窗口，而不是画纯色方块：

- 字体按 `UIA TextPattern → WM_GETFONT(LOGFONT) → 墨迹反解 → 像素高度估计` 四级取值：TextPattern 的磅值按 `×96/72` 换成 CSS 像素；LOGFONT 负高度即 em 高度，按目标进程的 DPI 感知方式折算（DPI_UNAWARE 不再除缩放等级）；像素估计按内容分档（含降部 0.92 / 仅帽高 0.72 / CJK 0.88）并过滤 1px 噪声行；全失败时回退 `14px`。
- 墨迹反解（Tier B）把截图里的墨迹宽/高与笔画宽度当成观测量：用 GDI 在 100px em 下量出候选字体的 advance 宽、帽高、x-height、`k` 上升部、`p` 降部，按 `size = 墨迹宽 / (advance/100)` 反推字号，再比高度、笔画比（常规 0.15 / 粗体 0.30）与字体先验取最优。高度按“控件可能裁掉任意深度降部”建模为区间误差；纯 ASCII 文本优先 Segoe UI 而非同源的 YaHei；top-2 置信度不足 0.15 时返回 null，交回像素估计。墨迹先按行切成连续带，只保留与文字同高的带，相邻控件漏进的 1–2 行边框或圆角不计入包围盒。混排文本按单字符窗口扫描 UIA 属性边界，合并成最多 12 段 `spans`（每段带字体族/字号/字重/斜体/颜色），渲染层逐段生成内联节点。
- 矩形底色取中央区域的量化直方图众数（均值会被文字拉灰）；四边按自适应深度扫描，候选边框必须在内侧重新回到背景，弱边框还需满足更严格的一致性。
- 输入框额外补一层底色加边框的表面，正文按行盒垂直居中；底色取内圈薄带，文字统计按已识别边框内缩，避开笔画、边框和选中高亮。
- TextPattern 报告存在选区时，文字是被反色绘制的，像素里没有原色：按底色明度选深/浅正文色，并放弃被高亮污染的边缘边框。

## 覆盖层行为

覆盖层只铺满显示器工作区（`GetMonitorInfo` 的 `rcWork`），任务栏留给用户正常使用。它始终带 `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE`，从不 `Activate()`：鼠标点击、键盘焦点和前台窗口都属于它下面那个真实窗口，AI 任务期间也不会变化。由于收不到鼠标消息，指针位置由宿主以 30Hz 采样 `GetCursorPos`，仅在位置变化时通过 `{type:"pointer"}` 推给渲染层。

底部控制条是唯一例外：渲染层上报它的矩形（`{type:"hud-rect"}`，CSS 像素），宿主用低层鼠标钩子命中该区域时吞掉这次按下/抬起，转成 `{type:"hud-input"}` 由渲染层在 Shadow DOM 里派发 `click`。这样控制条可用，而覆盖层其余部分和窗口属性保持完全穿透、永不激活。

渲染层帧率上限 30Hz；重建完成（或剩余任务全部被前沿挡住）且指针静止 2s 后缓停 RAF，CPU 降到 0，任意指针移动会立即唤醒。`document.hidden` 时同样停帧。

## 抓屏与验证

截图优先走 Windows.Graphics.Capture（按窗口复用会话，遮挡/非前台仍能取到真实内容）；WGC 不可用（如 Win10 无 FreeThreaded 帧池）、首帧超时或裁剪与客户区对不齐时，降级 PrintWindow 三级：客户区 `PW_CLIENTONLY|PW_RENDERFULLCONTENT` → 整窗 `PW_RENDERFULLCONTENT` 裁剪 → 整窗 `flags=0` 裁剪；仍失败或空白时降级为屏幕裁剪，此时 `captureBackend=screen`。

Win10 策略：不做专用回退路径，缺 `IDirect3D11CaptureFramePoolStatics2` 时本进程记住 `wgc_unsupported` 并一直降级。`capabilities.captureWgc` 表示 WGC 是否可用（按真实抓屏结果记账），为 `false` 时 `captureGuidance` 给出原因与影响（GPU 渲染窗口可能空白、遮挡场景要求窗口可见）。

WGC 帧是整窗物理像素，按 `DWMWA_EXTENDED_FRAME_BOUNDS`（可见窗口矩形）与客户端区屏幕位置裁剪出客户区，输出与 PrintWindow 同一坐标系；`frameAgeMs` 是帧距采集时刻的时差，仅作新鲜度参考（静止窗口不会产生新帧，age 会自然增长）。

`occluded` 由前台状态和客户区 5×5 采样点（`WindowFromPoint` + `GetAncestor`）判定，用于解释为什么窗口消息类输入可能无效。

`verify='pixels'` 通过稳定的前后帧像素比对确认动作生效，阈值由 `SWARM_PIXEL_DIFF_PERMILLE` 控制（默认 5‰），结果里返回 `evidence ∈ wm_gettext | uia_text | uia_value | pixels | none`。

## 日志

```
%LOCALAPPDATA%\SwarmCursors\logs\desktop-YYYYMMDD.log
%LOCALAPPDATA%\SwarmCursors\logs\gateway-YYYYMMDD.log
```

按天滚动，单文件 2 MB 封顶。Desktop 记录 `task begin/end`、动作与失败；Gateway 只在出错时写，所以“没有新行”通常等于“没有问题”。

## 已知限制

- Chromium/Electron 系应用 `messageReliable=false`，非前台动作会返回 `needs_foreground`，必须先 `focus`，这会让目标抢占前台。
- 部分 GPU 渲染窗口 PrintWindow 只能得到空白帧，会降级到屏幕裁剪，结果受遮挡影响。
- 静态 Label/Button 没有 TextPattern、`WM_GETFONT` 也返回 NULL，字体族/字号/字重改由墨迹反解推断；纯 ASCII 小字下 Segoe UI 与 YaHei/Tahoma 度量接近，置信度不足时会回退像素高度估计（字号约 ±8%，字重按笔画比）。
- 混排 span 仅在 TextPattern 报告 Mixed 时读取：最多 80 字符、120ms 预算，超限的剩余文本整体并入最后一段；段内斜体不参与 Tier B 候选；也不推断圆角半径、阴影或渐变。
