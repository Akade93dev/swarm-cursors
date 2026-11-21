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

# 单元测试（133 例）
dotnet test desktop\SwarmDesktop.sln

# 端到端（P0 路由 14 项 / P1 像素 11 项 / Edge 像素 / 焦点路由 12 项）
powershell -File desktop\e2e\run-all.ps1
```

`run-all.ps1` 会先把两个项目发布到 `publish-verify`，再依次运行四套脚本，任一套失败则退出码非 0。

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
- `publish` 为 framework-dependent，跨机器分发需要目标机安装 .NET 10 运行时。
