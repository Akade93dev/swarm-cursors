---
name: gui-grounding-research
description: GUI grounding 最优解决策框架. 当需要为跨平台桌面应用设计/调整视觉 grounding 方案时使用. 包含评价维度冻结、业内证据链收集、决策优先级矩阵、反模式清单、测试覆盖矩阵和执行闭环. 不用于纯 UI 美化或简单 bugfix.
---

# GUI Grounding Research — 最优解决策框架

为跨平台桌面应用（swarm-cursors）设计视觉 grounding 方案时，按此框架收敛决策并立即执行。目标函数：**真能用 + 零用户侧依赖 + 有业内证据 + 最小变更**。

## 执行流程（6 步，不可跳过）

### Step 1 — 冻结评价维度（2-3 个封闭式问题）

先回答这些问题再搜，避免无边界扩散：

| 问题 | 选项 | 典型决策 |
|---|---|---|
| 是否允许新增 NuGet 依赖? | 是 / 否 | 通常：是，但每个依赖要说明用途和大小 |
| 是否允许新增原生 DLL? | 是 / 否，上限 MB | 通常：是，上限 ~20MB/个（ONNX Runtime CPU ~13MB + 权重） |
| 是否要求用户侧零安装? | 是 | 始终：OCR/UI 检测引擎必须内嵌 |
| 跨平台覆盖范围? | Windows only / Win+Mac / Win+Mac+Linux | swarm-cursors 目前 Windows only |

冻结后写一行 `决策函数: f(精度, 依赖量, 速度, 可维护性)` 贴在执行计划顶部。

### Step 2 — 业内证据链收集（并行 3 方向）

用 `WebSearch` 同时搜这三类来源，每个方向至少 2 条证据：

1. **论文/arXiv** — 搜 `"{领域关键词} paper 2024 2025 benchmark"`
2. **社区实践** — 搜 `"{技术方案} GitHub NuGet stars issues"` 或 Stack Overflow 标签
3. **官方文档** — 搜 `site:learn.microsoft.com {API/技术名}` 或官方 GitHub README

记录格式（每条证据都要有）：
```
- [来源链接] 关键结论 — 对我方决策的启示
```

### Step 3 — 决策优先级矩阵（必须按此排序，不降级）

| 优先级 | 方案类型 | 示例 | 否决条件 |
|---|---|---|---|
| **1** | 训练免费，复用已有信号 | TRISHUL HSP（UIA+OCR 双层粒度） | 无 |
| **2** | 小模型 ONNX Runtime，CPU 可跑 | YOLOv8n (6MB) 或 Grounding DINO tiny (≤50MB) | 权重 > 100MB → 否决 |
| **3** | 纯托管服务端 SDK | DocumentFormat.OpenXml (Word) | 需要 Office COM → 否决 |
| **4** | 商业/闭源方案 | Syncfusion | 预算/许可不明 → 否决 |
| **5** | 用户侧安装 | Tesseract.exe / CUDA / Office | 违反"零用户侧依赖" → 否决 |

**硬约束**：不在上表的方案直接否决。

### Step 4 — 反模式清单（踩过的坑，不要再踩）

| 反模式 | 失败根因 | 正确做法 |
|---|---|---|
| `Windows.Media.Ocr` | 微软明确：桌面应用不支持，必须 MSIX 身份 | 用 TesseractOCR NuGet（内嵌 Native） |
| `ZeroDep.Ocr.Tesseract 2.1.0` | 是 subprocess 壳，启动外部 tesseract.exe | 用 5.5.1（内嵌 Native） |
| YOLOv8n COCO 用于 UI 元素 | 80 类里没有 button/checkbox | 若用 YOLO 系列，需 UI-specific fine-tune 权重 |
| Grounding DINO ONNX 688MB | 桌面应用过大 | 用 tiny 版本（≤50MB）或放弃 |
| OpenXml SDK 里 SdtTitle | 实际类型是 SdtAlias，不是 SdtTitle | 查官方 XML 文档确认类型名 |
| `OverlapsAnyUia(candidates, ...)` | 传整个 candidates 列表会自过滤 | 单独维护 uiaCandidates，只检查 UIA 侧 |

### Step 5 — 测试分类覆盖矩阵

新实现/修改后必须按此矩阵写测试，至少每个维度 1 个用例：

| 维度 | 必测场景 | 示例 |
|---|---|---|
| **正常路径** | 完整有效输入 | 有 UIA + 有 OCR → HSP 融合 |
| **空值/null** | 全空、局部空 | regions=[], interactives=[], ocrLines=[] |
| **边界值** | 零尺寸、极端小、极端大 | viewportWidth=0, bbox.W=0.001 |
| **参数组合** | 所有主要分支都过 | 纯 UIA / 纯 OCR / UIA+OCR 重叠 |
| **向后兼容** | 旧 API 不受影响 | `SetOfMarkBuilder.Build(OcrLines)` 字段默认值 |
| **位标志/枚举** | 所有 flag 组合 | OverlapFlags=0b01 / 0b10 / 0b11 |

### Step 6 — 执行闭环（不能省任何一步）

```
1. write code  →  dotnet build -warnaserror  →  0 errors?
2. write tests →  dotnet test                →  all pass?
3. run renderer tests (if any)
4. git add -A  →  git commit -F <msg-file>   →  push
5. git log -10 verify
```

Commit message 格式（项目约定）：
```
feat(pX): <模块> — <一句话说明>

refs: <arxiv/link> (如果是论文驱动)
修改列表...
测试覆盖...
```

## 当前仓库已落地的最优解（不要推翻）

| 层 | 已选方案 | Commit |
|---|---|---|
| OCR | TesseractOCR 5.5.1 (NuGet 内嵌 Native) | 7aa27b6 |
| Word | FileBasedWordBridge (DocumentFormat.OpenXml 3.1.0) | fbe47ae |
| SoM | TRISHUL HSP 双层粒度 (arXiv:2502.08226) | a1f706b |

## 引用

- TRISHUL: arXiv:2502.08226 "Towards Region Identification and Screen Hierarchy Understanding for Large VLM based GUI Agents"
- Microsoft Learn — DocumentFormat.OpenXml server-side: https://learn.microsoft.com/en-us/office/open-xml/
