---
name: swarm-cursors-dpi-debug
description: Diagnose DPI and cross-backend screen-capture issues in swarm-cursors. Use when the user asks to debug pixel alignment failures between WGC and screen backends, evaluate DPI-invariant test design, or investigate capture backend behavior at non-standard display scales. Do not use for general UI automation or non-DPI screen-capture tasks.
---

# Swarm Cursors — DPI & Capture Debug/Research

仓库专用的 DPI 显示缩放、跨后端采集、不变量评估 skill。所有内容均基于实战经验，不泛化为通用屏幕采集知识。

## 1. 触发场景

以下用户请求自动激活：

| 触发 | 场景 |
|---|---|
| 补跑某个 DPI 档测试 | "跑 125%/200% DPI 测试" |
| 像素对齐失败 | "为什么 wgc/screen 像素不对齐"、"mismatch=xxx 怎么办" |
| 不变量设计评审 | "这个 E2E 断言合理吗"、"cross-backend 比对要不要留" |
| 采集后端研究 | "WGC vs PrintWindow vs BitBlt 在 X 情况下怎样" |
| 遮挡/窗口行为 | "窗口被遮住时抓屏会怎样" |

不触发：普通 App 开发 bug、非 screen-capture 的 UI 问题、非 swarm-cursors 仓库的任务。

## 2. 仓库速查表

| 项 | 值 |
|---|---|
| 仓库根 | `C:\Users\kaer\Apps\swarm-cursors` |
| Desktop 可执行 | `desktop\publish-verify\Swarm.Desktop\Swarm.Desktop.exe` |
| Gateway 可执行 | `desktop\publish-verify\Swarm.Gateway\Swarm.Gateway.exe` |
| DPI E2E 主脚本 | `desktop\e2e\p1-s-invariants.ps1` |
| Desktop 进程名 | `Swarm.Desktop.exe` |
| Gateway 进程名 | `Swarm.Gateway.exe` |
| 进程 DPI awareness | PerMonitorV2（`desktop\Swarm.Desktop\app.manifest` 确认） |
| 默认截图缩放 | `SWARM_SCREENSHOT_MAX=1600`（最长边 1600，保持宽高比） |
| 采集后端 | `SWARM_CAPTURE_BACKEND=auto|wgc|screen` |
| `occluded` 含义 | WGC 永远 false（DWM 合成真实表面）；screen 后端被覆盖时 true |
| 客户区原点来源 | `GetClientRect`（物理模式，不含 DWM 16px 系统边框） |

## 3. 三层不变量诊断框架

按优先级从上到下，每层独立验证后才进下一层。**哪一层挂了就停在哪一层诊断**，不要继续往下看。

### Layer 1 — DPI 几何正确性

```
viewport.clientPhysical.w ≈ viewport.width * viewport.dpiScale   (容差 ≤ 1.5px)
viewport.clientPhysical.h ≈ viewport.height * viewport.dpiScale   (容差 ≤ 1.5px)
PNG 尺寸 ≈ clientPhysical（或按 SWARM_SCREENSHOT_MAX 同比例缩放后 ≈ clientPhysical * scaleFactor）
```

**Layer 1 不过 → 直接进服务端查**：`CaptureService.Scale`、`WgcWindowBackend` FramePool 初始化、`DesktopTaskService` 中 `clientScreen`/`client` 计算路径。

### Layer 2 — 遮挡前置条件

```
if ($wgcResult.structuredContent.occluded -or $screenResult.structuredContent.occluded) {
    // 两后端画面必然不同，跳过像素比较
    // 返回 SKIP 而非 FAIL，不是实现问题
}
```

**Layer 2 不过 → 不要看像素差异数值**。先确认遮挡是否来自测试操作（如开着 Windows 设置），还是服务端 `occluded` 检测漏报。

### Layer 3 — 跨后端像素对齐

**仅在 Layer 1 和 Layer 2 都通过后才执行**。

```
断言顺序：
  1. backend client origin aligned  两后端 clientPhysical 原点相同
  2. backend client size aligned    两后端 clientPhysical 尺寸相同
  3. backend PNG size aligned       两后端 PNG 尺寸相同
  4. backend PNG scale ratio aligned  两后端 PNG/clientPhysical 缩放比相同
  5. wgc/screen pixel ratio        非致命诊断（threshold=0.02）
```

**Layer 3 注意**：像素对齐在理论上不是必成的——微软 WinAppCLI 只用 WGC 一个主后端，PrintWindow 只做 legacy fallback。两后端合成路径天然不同（WGC 走 DWM 合成管线 vs PrintWindow 让目标自己 Render vs BitBlt 从桌面 DC 裁剪），允许 DWM/GDI 微差存在。

## 4. 已知陷阱清单

| 陷阱 | 症状 | 根因 | 修复 |
|---|---|---|---|
| 开设置页跑 125%/200% | `ratio≈0.9` FAIL | screen 后端拍到设置界面而非 Edge | 加 occluded 守卫；SKIP 而非 FAIL |
| 200% 下 clientPhysical=2074×1451 但 PNG=1600×1119 | 只断两后端 PNG 相等，没断 PNG≈clientPhysical | 服务端最长边 1600 统一缩放；某一端被缩某一端没缩时不会报错 | 加 scale ratio aligned 断言 |
| 硬编码 `y-16, x-4` 裁剪 | 不同应用/主题下裁剪边界可能不准 | WGC 多出 16px 系统边框是经验值而非 API 返回 | 提升为配置常量；远期让服务端返回实际边框尺寸 |
| 鼠标热点采样命中光标像素 | 坐标点击断言可能失败 | 热点被光标自身覆盖 | 采样从热点偏移 40px |
| `occluded=True` 时仍跑像素对齐 | 两个后端画面完全不同 → mismatch 接近 100% | 没有前置守卫 | Layer 2 skip 条件 |
| PrintWindow 对最小化/遮挡窗口 | 返回空白或旧帧 | API 行为未定义 | 用 WGC 做主后端，PrintWindow 仅 legacy fallback |

## 5. 三后端行为速查

| 后端 | 遮挡 | 最小化 | DXGI/全屏 | 像素来源 | DPI 行为 |
|---|---|---|---|---|---|
| **WGC**（主后端） | ✅ 天然免疫（DWM 合成真实表面） | ✅ 可抓 | ✅ | `GraphicsCaptureItem.Size` = 物理像素 | PerMonitorV2 正确 |
| **PrintWindow** | ❌ 行为未定义 | ❌ 常返回空白 | ❌ 常返回空白 | 目标进程自渲染 | 可能返回 CSS 像素（awareness 影响） |
| **BitBlt/DesktopDuplication** | ❌ 会拍进遮挡层 | ❌ 需可见 | ⚠️ 部分支持 | 桌面 DC 裁剪 | 按桌面 DC DPI |

## 6. DPI 切档操作

用 Computer Use agent + `ms-settings:display` 直接跳转显示页，不要手动导航：

```powershell
Start-Process 'ms-settings:display'
```

切档后 Windows 会弹 "某些应用在关闭并重新打开后才会响应缩放更改"——让应用进程自重建，不要手动杀。

切档完成后必做：关设置 → 让目标应用（Edge harness）处于最前且完全可见 → 再跑 E2E。

## 7. 不变量评估 checklist

设计新断言前先过这 6 条：

- [ ] 锚定单一后端还是多后端比对？（多后端比对需确认遮挡守卫存在）
- [ ] DPI 换算路径：CSS → physical → PNG 尺寸是否一致？
- [ ] 服务端是否会统一缩放？（是 → 不能只断 PNG 尺寸等于 clientPhysical，要断缩放比）
- [ ] 被遮挡时行为？（WGC 永远正常；screen 会被覆盖 → 必须 skip）
- [ ] 硬编码裁剪参数是否来自 API 还是经验？（经验 → 提升为配置常量）
- [ ] 差异率阈值是否经过多 DPI 实测校准？（150%≈0.003，无遮挡 200%≈0.0045，遮挡≈0.9）

## 8. 交付物要求

| 交付 | 要求 |
|---|---|
| 测试脚本改动 | 外科手术式：只加守卫/断言/配置常量，不重写整个 Compare-Captures |
| 诊断产物 | `C:\Users\kaer\AppData\Local\Temp\swarm-dpi-diagnostics\<dpi>\{wgc,screen}.png` |
| 报告格式 | 表格列：DPI / 遮挡状态 / Layer 1 / Layer 2 / Layer 3 ratio / exit_code |
| 不做的 | 不擅自改服务端实现、不擅自切 DPI 后不恢复、不扩范围到 P1-W/P1-M/P1-P |
