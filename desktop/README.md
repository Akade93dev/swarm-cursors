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

- Windows，已安装 **.NET 10 SDK**（当前 TFM 为 `net10.0` / `net10.0-windows`）。
- 发布产物为 framework-dependent，运行机器需要 .NET 10 运行时；SDK 自带 WindowsDesktop 运行时。
- Trae 的 MCP 配置指向 `publish\Swarm.Gateway\Swarm.Gateway.exe`。

## 构建与测试

```powershell
# 编译（0 警告要求）
dotnet build desktop\SwarmDesktop.sln -warnaserror

# 单元测试（189 例）
dotnet test desktop\SwarmDesktop.sln

# 端到端（P0 路由 14 项 / P1 像素 11 项 / P2 覆盖层 16 项 / Edge 像素 / 焦点路由 12 项）
powershell -File desktop\e2e\run-all.ps1
```

`run-all.ps1` 会先把两个项目发布到 `publish-verify`，再依次运行五套脚本，任一套失败则退出码非 0。

**跑 e2e 前必须先退出正在运行的 `publish` 版 Desktop**：单实例互斥量（`Local\SwarmDesktop.SingleInstance`）和命名管道都是机器级的，第二个实例起不来。`run-all` 会检测这个情况并直接报错，不会误杀你的生产实例；它只会清理 `publish-verify` 路径下的自建残留。

## 发布与部署

```powershell
# 发布到 desktop\publish\{Swarm.Desktop,Swarm.Gateway}
powershell -File desktop\publish.ps1
```

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
| `task_begin` | `targetProcess`、`targetTitle`（均可选） | 授权 + 锁定目标，返回 token、viewport、capabilities、首帧截图 |
| `observe` | `token`、`includeScreenshot` | 元素树、`occluded`、`captureBackend`（`print`/`screen`）、能力 |
| `focus` | `token` | 仅在动作返回 `needs_foreground` 时需要 |
| `click` | `token`、`elementId` 或 `x`/`y`、`verify='pixels'`、`observe` | `elementId` 优先 |
| `type_text` | `token`、`text`、`elementId`、`sensitive`、`verify='pixels'` | `sensitive=true` 会打码 |
| `press_key` | `token`、`key`、`verify`、`observe` | 支持组合键，如 `Ctrl+V` |
| `scroll` | `token`、`x`、`y`、`delta` | `delta` 限 ±1200，超限返回 `invalid_params` |
| `task_end` | `token` | 正常结束 |
| `task_stop` | `token` | 立即中止 |

坐标一律是**窗口客户区 CSS 像素**（`observe` 返回的坐标系），不是屏幕物理像素。截图最长边缩放到 1600 px。

### 错误语义

| code | 含义 | 处理 |
|---|---|---|
| `needs_foreground` / `app_ignores_message` | 目标忽略窗口消息且不在前台（Chromium/Electron 常见） | 先 `focus` 再重试 |
| `desktop_unavailable` | Desktop 未启动或 IPC 连不上 | 启动 Swarm Desktop 后重试 |
| `invalid_params` | 参数不合法（如 scroll 超限、缺 token） | 修正参数 |
| `unauthorized` | token 无效或跨进程复用 | 重新 `task_begin` |

### 热键

| 热键 | 作用 |
|---|---|
| `Alt+Shift+S` | 重建渲染层 |
| `Alt+Shift+U` | 重建任务 |
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

截图走 PrintWindow 三级降级：客户区 `PW_CLIENTONLY|PW_RENDERFULLCONTENT` → 整窗 `PW_RENDERFULLCONTENT` 裁剪 → 整窗 `flags=0` 裁剪；仍失败或空白时降级为屏幕裁剪，此时 `captureBackend=screen`。

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
- `publish` 为 framework-dependent，跨机器分发需要目标机安装 .NET 10 运行时。
