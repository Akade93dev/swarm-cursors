import { createBlueprint, fitTextWidth, materializeModel, mergeLineRuns } from "./model.mjs";
import {
  appendReplayFrame,
  createReplayPlayer,
  interruptsReplay,
  normalizeAction,
  replayActionFrame,
  replayResultFrame,
} from "./action.mjs";
import { applyOpsEvent, createOpsState, hiddenCount, replayable, stepStateText, summarize } from "./ops.mjs";
import { shouldKeepAnimating } from "./idle.mjs";
import { soundKey, SPK_ICON, SPK_WAVES, SPK_MUTE } from "./sound.mjs";

const { View, Engine, Sound, cursorSVG } = window.__swarmRebuild.debug;
const bridge = () => window.chrome && window.chrome.webview;
const post = (message) => bridge() && bridge().postMessage(message);
const statusEl = document.getElementById("desktop-status");
// swarm.js 把重建画面挂到 documentElement 下且 z-index 极高；body 是 position:fixed 的独立层叠上下文，
// 放在 body 里的提示条永远会被重建画面盖住，必须提升为 documentElement 的同级节点。
if (statusEl && statusEl.parentElement !== document.documentElement) document.documentElement.appendChild(statusEl);
let current = null;
let lastPayload = null;
let actionLayer = null;
let actionPosition = { x: -40, y: -40 };
let actionSerial = 0;
let statusTimer = 0;
let opsState = null;
let opsEl = null;
let opsHideTimer = 0;
// 有界动作视觉快照；只在真实事件路径记录，模拟回放时不再追加。
let replayFrames = [];
let replayPlayer = null;
let replayMode = false;
// 点击穿透窗口收不到 pointermove，指针位置由宿主采样后经 {type:"pointer"} 推送。
let pointer = null;
let hudSignature = "";
const FRAME_MS = 1000 / 30;
const REST_AFTER = 2000;
const REST_RAMP = 0.5;
const OPS_MARK = { pending: "·", running: "◌", succeeded: "✓", failed: "✕", canceled: "⊘" };

function status(text, duration = 2200) {
  clearTimeout(statusTimer);
  statusEl.textContent = text;
  statusEl.classList.toggle("visible", Boolean(text));
  if (text && duration) statusTimer = setTimeout(() => statusEl.classList.remove("visible"), duration);
}

function mediaFactory(src) {
  if (!src) return null;
  const image = document.createElement("img");
  image.src = src;
  image.draggable = false;
  image.alt = "";
  return image;
}

const DEFAULT_FONT = '"Segoe UI","Microsoft YaHei",system-ui,sans-serif';

// 单行文本宽度由 C# 按字符数粗估会侵入同一行的下一个片段；改为在本机 WebView2 里
// 用隐藏测量层按真实字体量一次，再把会压到右邻片段的宽度用有上限的负字距收紧。
function measureTextWidths(payload) {
  const elements = payload && Array.isArray(payload.elements) ? payload.elements : [];
  const probes = [];
  let layer = null;
  for (const element of elements) {
    if (!element || element.kind !== "text" || !element.text) continue;
    // IA2 行的宽度就是字符推进之和，既不需要实测也不需要夹紧。
    if (element.exact) continue;
    const fs = Number.isFinite(element.fontSize) ? element.fontSize : 14;
    const lh = Number.isFinite(element.lineHeight) ? element.lineHeight : fs * 1.2;
    // 多行文本保留 UIA 换行宽度与自动折行，只实测单行片段。
    if (!(element.h <= lh * 1.5)) continue;
    if (!layer) {
      layer = document.createElement("div");
      layer.setAttribute("style", "position:absolute;left:-100000px;top:0;visibility:hidden;pointer-events:none;");
    }
    const probe = document.createElement("div");
    probe.setAttribute("style", `display:block;margin:0;padding:0;width:max-content;height:auto;white-space:pre;font-family:${element.fontFamily || DEFAULT_FONT};font-size:${fs}px;line-height:${lh}px;font-weight:${element.fontWeight || "400"};font-style:${element.fontStyle || "normal"};color:${element.color || "rgb(0,0,0)"};text-align:${element.align || "start"};`);
    if (Array.isArray(element.spans) && element.spans.length > 1) {
      for (const span of element.spans) {
        if (!span || typeof span.text !== "string" || !span.text) continue;
        const node = document.createElement("span");
        const spanSize = Number.isFinite(span.fontSize) ? span.fontSize : fs;
        const margin = Number.isFinite(span.ml) && span.ml > 0 ? `margin-left:${span.ml}px;` : "";
        node.setAttribute("style", `font-family:${span.fontFamily || element.fontFamily || DEFAULT_FONT};font-size:${spanSize}px;font-weight:${span.fontWeight || element.fontWeight || "400"};font-style:${span.fontStyle || element.fontStyle || "normal"};color:${span.color || element.color || "rgb(0,0,0)"};${margin}`);
        node.appendChild(document.createTextNode(span.text));
        probe.appendChild(node);
      }
    } else {
      probe.appendChild(document.createTextNode(element.text));
    }
    layer.appendChild(probe);
    probes.push({ element, probe });
  }
  if (!probes.length) return;
  document.body.appendChild(layer);
  for (const { element, probe } of probes) element.measuredWidth = probe.getBoundingClientRect().width;
  layer.remove();
  // 找同一视觉行里右侧最近的片段，作为宽度上限。
  const sorted = probes.map(({ element }) => element).sort((a, b) => a.x - b.x || a.y - b.y);
  for (const element of sorted) {
    let neighborX;
    for (const other of sorted) {
      if (other === element || other.x <= element.x) continue;
      const overlap = Math.min(element.y + element.h, other.y + other.h) - Math.max(element.y, other.y);
      if (overlap <= Math.min(element.h, other.h) * 0.5) continue;
      if (neighborX === undefined || other.x < neighborX) neighborX = other.x;
    }
    const fit = fitTextWidth({
      measured: element.measuredWidth,
      fontSize: element.fontSize,
      textLength: element.text.length,
      x: element.x,
      neighborX,
      // 合并行由浏览器自然排字，不再按右邻元素框夹紧，避免把真实文字压窄。
      allowShrink: !element.merged,
    });
    element.w = fit.w;
    if (fit.letterSpacing) element.letterSpacing = fit.letterSpacing;
    delete element.measuredWidth;
  }
}

function desktopEnvironment(width, height, background) {
  const geom = () => ({ ox: 0, oy: 0, sx: 0, sy: 0, vw: innerWidth, vh: innerHeight });
  return {
    isRoot: true,
    scroller: document.scrollingElement,
    geom,
    scrollWidth: () => width,
    scrollHeight: () => height,
    scrollTo: () => {},
    scrollBy: () => {},
    pageBg: { color: background, image: "none", size: "auto", position: "0% 0%", repeat: "repeat", from: null },
  };
}

function stopRebuild(notify = false) {
  if (!current) return;
  const app = current;
  current = null;
  cancelAnimationFrame(app.raf);
  if (app.sound) {
    // 显式关闭（点关闭按钮/通知宿主）时播放回落扫频，与扩展版 destroy(reveal) 对齐；
    // 内部重建切换不播，避免每次 resize/replay 都响一声。
    if (notify) app.sound.release();
    app.sound.close(0.1);
  }
  app.view.host.remove();
  reportHud();
  if (notify) post({ type: "closed" });
}

// 覆盖层默认整体点击穿透，宿主需要知道底部控制条的实际矩形，才能在指针压上去时
// 临时关掉穿透。布局变化才上报一次，避免每次指针移动都发消息。
function hudRect() {
  if (!current) return null;
  const view = current.view;
  let left = Infinity;
  let top = Infinity;
  let right = -Infinity;
  let bottom = -Infinity;
  for (const el of [view.hud, view.unhideBtn]) {
    if (!el) continue;
    const style = getComputedStyle(el);
    if (style.display === "none" || style.pointerEvents === "none" || Number(style.opacity) < 0.01) continue;
    const rect = el.getBoundingClientRect();
    if (rect.width < 1 || rect.height < 1) continue;
    left = Math.min(left, rect.left);
    top = Math.min(top, rect.top);
    right = Math.max(right, rect.right);
    bottom = Math.max(bottom, rect.bottom);
  }
  if (!(right > left && bottom > top)) return null;
  const pad = 4;
  return {
    x: Math.round(Math.max(0, left - pad)),
    y: Math.round(Math.max(0, top - pad)),
    w: Math.round(right - left + pad * 2),
    h: Math.round(bottom - top + pad * 2),
  };
}

function reportHud() {
  const rect = hudRect();
  const signature = rect ? `${rect.x},${rect.y},${rect.w},${rect.h}` : "";
  if (signature === hudSignature) return;
  hudSignature = signature;
  post({ type: "hud-rect", rect });
}

// 桌面模式没有真实 pointermove，“Show controls”药丸要跟着宿主推来的指针走。
function updatePill(app) {
  const view = app.view;
  if (!view.hud.classList.contains("hidden")) return;
  const nearBottom = Boolean(pointer && pointer.y >= innerHeight - 44);
  if (!nearBottom) app.pillArmed = true;
  const visible = nearBottom && app.pillArmed;
  if (view.unhideBtn.classList.contains("visible") === visible) return;
  view.unhideBtn.classList.toggle("visible", visible);
  reportHud();
}

// 声音按钮三态渲染：off / on / tap（开启但被自动播放策略挂起，需点击解锁）。
// 与扩展版 renderSoundBtn 对齐；状态计算与 SVG 常量集中在 sound.mjs。
function renderSoundBtn(app) {
  if (!app || !app.sound || !app.view) return;
  const b = app.view.root.querySelector('[data-a="sound"]');
  if (!b) return;
  const key = soundKey({ on: app.sound.on, blocked: app.sound.blocked });
  if (b._k === key) return;
  b._k = key;
  b.classList.toggle("off", key === "off");
  b.classList.toggle("tap", key === "tap");
  b.innerHTML = `${SPK_ICON}${key === "off" ? SPK_MUTE : SPK_WAVES}</svg>${key === "tap" ? "<span>点击解锁声音</span>" : ""}`;
  b.title = key === "off" ? "声音关 (M)" : key === "tap" ? "点击解锁声音 (M)" : "声音开 (M)";
}

// 用户手势内恢复被挂起的 AudioContext：Chromium 自动播放策略要求 resume() 必须在
// 一次真实交互里调用，否则上下文会一直停在 suspended。宿主推来的 {type:"pointer"} 不
// 是真实 DOM 事件，主修复靠 C# 侧放开自动播放；这里是键盘路径的兜底。
function unlockSound(app) {
  if (!app || !app.sound || !app.sound.blocked) return;
  app.sound.resume();
  setTimeout(() => renderSoundBtn(app), 80);
}

function bindRebuild(app) {
  const { view, engine } = app;
  const button = (name) => view.root.querySelector(`[data-a="${name}"]`);
  button("tour").hidden = true;
  button("live").hidden = true;
  button("replay").hidden = false;
  button("close").addEventListener("click", () => stopRebuild(true));
  button("replay").addEventListener("click", () => lastPayload && startRebuild(lastPayload));
  button("speed").addEventListener("click", (event) => {
    app.speedIndex = (app.speedIndex + 1) % app.speeds.length;
    engine.speed = app.speeds[app.speedIndex];
    event.currentTarget.textContent = `${engine.speed}×`;
  });
  // 三态：开着但被挂起时点一下是解锁；其余情况切换开关。渲染交给 renderSoundBtn。
  button("sound").addEventListener("click", () => {
    if (!app.sound) return;
    if (app.sound.on && app.sound.blocked) app.sound.resume();
    else app.sound.setOn(!app.sound.on);
    setTimeout(() => renderSoundBtn(app), 60);
  });
  button("hide").addEventListener("click", () => {
    view.hud.classList.add("hidden");
    // 指针还停在底部条上时先别弹药丸，否则它会立刻挡在鼠标下面。
    app.pillArmed = !(pointer && pointer.y >= innerHeight - 44);
    reportHud();
  });
  view.unhideBtn.addEventListener("click", () => {
    view.hud.classList.remove("hidden");
    view.unhideBtn.classList.remove("visible");
    reportHud();
  });
  ensureOpReplayButton(app);
}

// 同一视觉行的相邻片段合并成行内流水；同时复制元素，避免实测把原始载荷的宽度改脏
// （窗口尺寸变化会拿同一份载荷重新 startRebuild）。
function preparePayload(payload) {
  const elements = Array.isArray(payload && payload.elements) ? payload.elements : [];
  return {
    ...payload,
    elements: mergeLineRuns(elements).map((element) => (
      element && Array.isArray(element.spans)
        ? { ...element, spans: element.spans.map((span) => ({ ...span })) }
        : { ...element }
    )),
  };
}

function startRebuild(payload) {
  stopRebuild(false);
  lastPayload = payload;
  const prepared = preparePayload(payload);
  measureTextWidths(prepared);
  const blueprint = createBlueprint(prepared);
  const model = materializeModel(blueprint, document, mediaFactory);
  const env = desktopEnvironment(model.docW, model.docH, prepared.background);
  const view = new View(env);
  const engine = new Engine(view);
  const sound = new Sound();
  sound.init();
  engine.sfx = sound;
  // 页面溶解的开场风声（扩展版 run() 里在首帧后调用）。ready() 内部会判 ctx 是否运行，
  // 未放开自动播放时是安全空操作。
  sound.intro();
  const now = performance.now();
  current = { view, engine, sound, raf: 0, last: now, lastRender: 0, activeAt: now, rest: 0, pillArmed: true, speeds: [1, 2, 3], speedIndex: 0 };
  view.intro.style.display = "none";
  view.mount(model);
  engine.setModel(model);
  if (pointer) engine.mouse = pointer;
  view.bg.classList.add("on");
  view.hud.classList.add("on");
  reportHud();
  bindRebuild(current);
  renderSoundBtn(current);
  engine.onComplete = () => {
    // 重建完成的收束和弦（扩展版 onComplete 里调用）。
    sound.finale();
    view.status.textContent = `已由 ${engine.cursors.filter((cursor) => cursor.worked).length + engine.retired} 个光标重建`;
    view.barFill.style.width = "100%";
    post({ type: "complete" });
  };
  engine.start();
  scheduleFrame(current);
  lastSize = `${innerWidth}x${innerHeight}`;
  // 等底座实拍解码完再回执首帧：否则覆盖层会先展示一块还没上图的空白画面。
  const base = model.items[0];
  const decoded = base && base.media && typeof base.media.decode === "function"
    ? base.media.decode().catch(() => {})
    : Promise.resolve();
  decoded.then(() => {
    if (current && current.view === view) post({ type: "rebuild-ready" });
  });
}

// 30Hz 上限 + 空闲即停：群集安顿后指针静止 2s，缓停帧循环；收到 pointer 或重建事件再唤醒。
function scheduleFrame(app) {
  if (!app.raf) app.raf = requestAnimationFrame((now) => frame(app, now));
}

function frame(app, now) {
  app.raf = 0;
  if (!current || current !== app || document.hidden) return;
  if (now - app.lastRender < FRAME_MS - 2) {
    scheduleFrame(app);
    return;
  }
  const dt = Math.min(0.05, (now - app.last) / 1000);
  app.last = now;
  app.lastRender = now;
  // 约 200ms 刷一次声音按钮：blocked 态变化（解锁成功）时让 tap 脉冲/提示及时更新，
  // 不必每帧都写 innerHTML。
  if (!app.lastSoundRender || now - app.lastSoundRender > 200) {
    app.lastSoundRender = now;
    renderSoundBtn(app);
  }
  const engine = app.engine;
  // 重建完成进入 follow/flock 盘旋后，即使指针静止也必须持续推进，否则光影鼠标会冻结
  // 在原地；其余空闲态（已安顿、无待办、无任务）沿用“静止超阈值即缓停”的省电策略。
  const keepAnimating = shouldKeepAnimating({
    flocking: engine.flocking,
    started: engine.started,
    backlog: engine.backlog(),
    hasTask: engine.cursors.some((c) => c.task),
    idleMs: now - app.activeAt,
    restAfter: REST_AFTER,
  });
  if (!keepAnimating) {
    app.rest = Math.min(1, app.rest + dt / REST_RAMP);
    if (app.rest >= 1) return;
  } else if (app.rest) {
    app.rest = 0;
  }
  engine.step(dt * (1 - app.rest));
  app.view.render(engine);
  scheduleFrame(app);
}

function wake(app) {
  if (!app || current !== app) return;
  app.activeAt = performance.now();
  app.rest = 0;
  scheduleFrame(app);
}

function ensureActionLayer() {
  if (actionLayer) return actionLayer;
  const layer = document.createElement("div");
  layer.className = "ai-layer";
  layer.innerHTML = '<div class="ai-target"></div><div class="ai-cursor"><svg width="28" height="28" viewBox="0 0 28 28"></svg></div><div class="ai-label"></div>';
  document.body.appendChild(layer);
  layer.querySelector("svg").innerHTML = cursorSVG("arrow", "#0D99FF");
  actionLayer = layer;
  return layer;
}

function place(element, x, y) {
  element.style.transform = `translate(${x}px,${y}px)`;
}

function prepareAction(raw) {
  const action = normalizeAction(raw, innerWidth, innerHeight);
  if (!replayMode) {
    replayFrames = appendReplayFrame(replayFrames, replayActionFrame(action));
    refreshReplayButton();
  }
  animateAction(action);
}

// 纯视觉动作：真实执行与模拟回放共用；模拟时禁止向宿主回执 action-ready。
function animateAction(action) {
  const layer = ensureActionLayer();
  const serial = ++actionSerial;
  const target = layer.querySelector(".ai-target");
  const cursor = layer.querySelector(".ai-cursor");
  const label = layer.querySelector(".ai-label");
  layer.classList.remove("ok", "fail", "unverified", "fade", "takeover");
  Object.assign(target.style, { left: `${action.target.x}px`, top: `${action.target.y}px`, width: `${action.target.w}px`, height: `${action.target.h}px` });
  label.dataset.actionLabel = action.label;
  label.textContent = `${action.label} · 已发起`;
  const labelX = Math.min(innerWidth - 370, Math.max(8, action.target.x));
  const labelY = Math.min(innerHeight - 50, action.target.y + action.target.h + 10);
  Object.assign(label.style, { left: `${labelX}px`, top: `${labelY}px` });
  const from = { ...actionPosition };
  const to = { x: action.cursorX, y: action.cursorY };
  const started = performance.now();
  const move = (now) => {
    if (serial !== actionSerial) return;
    const t = Math.min(1, (now - started) / 360);
    const eased = 1 - Math.pow(1 - t, 3);
    actionPosition = { x: from.x + (to.x - from.x) * eased, y: from.y + (to.y - from.y) * eased };
    place(cursor, actionPosition.x, actionPosition.y);
    if (t < 1) requestAnimationFrame(move);
    else if (!replayMode) post({ type: "action-ready", id: action.id });
  };
  requestAnimationFrame(move);
}

function finishAction(result) {
  if (!replayMode && result) {
    replayFrames = appendReplayFrame(replayFrames, replayResultFrame(result));
    refreshReplayButton();
  }
  applyActionResult(result);
}

function applyActionResult(result) {
  if (!actionLayer) return;
  const layer = actionLayer;
  const label = layer.querySelector(".ai-label");
  const verification = result.verification || (result.verified ? "passed" : result.evidence != null ? "unverified" : "not_requested");
  layer.classList.remove("ok", "fail", "unverified", "fade");
  layer.classList.add(result.ok ? (verification === "unverified" ? "unverified" : "ok") : "fail");
  layer.querySelector(".ai-cursor").classList.add("press");
  if (result.ok) {
    const stateText = verification === "passed" ? "已验证" : verification === "unverified" ? "已执行 · 未验证" : "已执行";
    label.textContent = `${label.dataset.actionLabel || "AI 操作"} · ${stateText}`;
  } else {
    label.textContent = String(result.error || "操作失败").slice(0, 160);
  }
  setTimeout(() => {
    layer.querySelector(".ai-cursor").classList.remove("press");
    layer.classList.add("fade");
  }, result.ok ? 350 : 1200);
}

function clearActions() {
  actionSerial++;
  if (actionLayer) {
    actionLayer.remove();
    actionLayer = null;
  }
  actionPosition = { x: -40, y: -40 };
}

// 模拟回放入口放在可点击的 HUD 控制条里，与“重建动画回放”文案区分：
// 只重放动作视觉，不发宿主消息、不进入输入调度器或 Gateway。
function ensureOpReplayButton(app) {
  let btn = app.view.root.querySelector('[data-a="opreplay"]');
  if (!btn) {
    btn = document.createElement("button");
    btn.dataset.a = "opreplay";
    btn.title = "模拟回放本次 AI 操作（不执行真实输入）";
    btn.textContent = "操作回放";
    // 固定最小宽度，保证点击区域稳定且与 ✕ 之间留有可计算的间距。
    btn.style.minWidth = "72px";
    btn.hidden = true;
    const close = app.view.root.querySelector('[data-a="close"]');
    if (close && close.parentElement) close.parentElement.insertBefore(btn, close);
    else app.view.hud.appendChild(btn);
    btn.addEventListener("click", () => { startReplay(); });
  }
  refreshReplayButton();
}

function refreshReplayButton() {
  if (!current) return;
  const btn = current.view.root.querySelector('[data-a="opreplay"]');
  if (!btn) return;
  const available = replayable(opsState) && replayFrames.length > 0;
  const busy = Boolean(replayPlayer);
  const text = busy ? "回放中…" : "操作回放";
  const hidden = !available;
  if (btn.hidden === hidden && btn.textContent === text) return;
  btn.hidden = hidden;
  btn.textContent = text;
  btn.disabled = busy;
  // 模拟期间保持零宿主消息；按钮宽度变化等回放结束后再上报。
  if (!replayMode) reportHud();
}

function stopReplay(reason) {
  if (!replayPlayer) return;
  const player = replayPlayer;
  replayPlayer = null;
  replayMode = false;
  player.stop();
  clearActions();
  refreshReplayButton();
  if (reason) status(reason, 2400);
}

async function startReplay() {
  if (replayPlayer || !replayFrames.length) return;
  const player = createReplayPlayer(replayFrames, {
    prepare: (action) => animateAction(action),
    finish: (result) => applyActionResult(result),
    wait: () => new Promise((resolve) => setTimeout(resolve, 700)),
  });
  replayPlayer = player;
  replayMode = true;
  refreshReplayButton();
  status("模拟回放中：不会执行真实输入", 2400);
  try {
    const finished = await player.run();
    if (finished && replayPlayer === player) status("模拟回放结束", 2000);
  } finally {
    if (replayPlayer === player) {
      replayPlayer = null;
      replayMode = false;
      refreshReplayButton();
      // 留出最后一次动作的淡出时间，再清掉虚拟鼠标。
      setTimeout(() => { if (!replayPlayer) clearActions(); }, 1200);
    }
  }
}

function ensureOpsPanel() {
  if (opsEl) return opsEl;
  const panel = document.createElement("div");
  panel.className = "ops-panel";
  panel.innerHTML = '<div class="ops-head"><span class="ops-title"></span><span class="ops-count"></span></div><ul class="ops-list"></ul>';
  document.body.appendChild(panel);
  opsEl = panel;
  return panel;
}

const OPS_TIER = { uia: "UIA", message: "MSG", input: "INPUT", none: "" };

function renderOps() {
  if (!opsState) return;
  clearTimeout(opsHideTimer);
  const panel = ensureOpsPanel();
  panel.classList.remove("hidden", "done", "failed", "partial", "paused");
  if (opsState.control === "paused") panel.classList.add("paused");
  panel.querySelector(".ops-title").textContent = opsState.control === "paused" ? `${opsState.title} · 人工接管中` : opsState.title;
  const stats = summarize(opsState);
  const total = Math.max(stats.total, Number(opsState.seen) || 0);
  panel.querySelector(".ops-count").textContent = total ? `${stats.done + stats.failed}/${total}` : "";
  const list = panel.querySelector(".ops-list");
  list.innerHTML = "";
  const hidden = hiddenCount(opsState);
  if (hidden > 0) {
    const more = document.createElement("li");
    more.className = "ops-more";
    more.textContent = `+${hidden} 更早`;
    list.appendChild(more);
  }
  for (const step of opsState.steps.slice(-6)) {
    const row = document.createElement("li");
    row.className = `ops-step ${step.status} ${step.verification || ""}`;
    const mark = document.createElement("span");
    mark.className = "ops-mark";
    mark.textContent = OPS_MARK[step.status] || "·";
    const label = document.createElement("span");
    label.className = "ops-label";
    const detail = step.error || step.note;
    const stateText = stepStateText(step);
    label.textContent = `${step.label}${stateText ? ` · ${stateText}` : ""}${detail ? ` · ${detail}` : ""}`;
    const tier = document.createElement("span");
    tier.className = `ops-tier ${step.tier}`;
    tier.textContent = OPS_TIER[step.tier] || "";
    const time = document.createElement("span");
    time.className = "ops-time";
    time.textContent = step.ms ? `${Math.round(step.ms)}ms` : "";
    row.append(mark, label, tier, time);
    list.appendChild(row);
  }
  if (!opsState.active) {
    const tone = opsState.status === "failed" ? "failed" : opsState.status === "partial" ? "partial" : "done";
    panel.classList.add(tone);
    opsHideTimer = setTimeout(() => { if (opsEl && opsState && !opsState.active) opsEl.classList.add("hidden"); }, 4500);
  }
  refreshReplayButton();
}

function handleOps(message) {
  if (message.type === "op-session") {
    // 新会话优先：立即中断模拟并清空上一轮快照，避免污染实时序号。
    stopReplay();
    replayFrames = [];
    opsState = createOpsState(message.session);
    if (opsEl) opsEl.classList.remove("hidden");
    renderOps();
    refreshReplayButton();
    return;
  }
  opsState = applyOpsEvent(opsState, message);
  renderOps();
  if (message.type === "op-control" && actionLayer) {
    const paused = opsState && opsState.control === "paused";
    actionLayer.classList.toggle("takeover", paused);
    const label = actionLayer.querySelector(".ai-label");
    label.textContent = paused ? "人工接管中 · 等待本地恢复" : (label.dataset.actionLabel || "AI 操作");
  }
}

function clearOps() {
  clearTimeout(opsHideTimer);
  stopReplay();
  replayFrames = [];
  opsState = null;
  if (opsEl) {
    opsEl.remove();
    opsEl = null;
  }
}

async function handle(message) {
  // 任何实时会话／步骤／动作事件都先中止模拟回放，再按正常路径处理。
  if (replayPlayer && interruptsReplay(message)) stopReplay("模拟回放已中断：收到实时事件");
  switch (message.type) {
    case "rebuild": startRebuild(message.payload); break;
    case "rebuild-stop": stopRebuild(false); break;
    case "pointer":
      pointer = { x: Number(message.x) || 0, y: Number(message.y) || 0 };
      if (current) {
        current.engine.mouse = pointer;
        wake(current);
        updatePill(current);
      }
      break;
    case "hud-input": {
      // 覆盖层是点击穿透的，页面收不到真实鼠标；宿主钩子吞下控制条上的点击后在这里补一次 DOM click。
      if (message.action !== "up" || !current) break;
      const x = Number(message.x) || 0;
      const y = Number(message.y) || 0;
      const target = current.view.root.elementFromPoint(x, y);
      if (target) target.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0 }));
      break;
    }
    case "action-prepare": prepareAction(message.action); break;
    case "action-result": finishAction(message.result); break;
    case "action-clear": case "clear-actions": clearActions(); status(""); break;
    case "ops-clear": clearOps(); break;
    case "stop": stopRebuild(false); clearActions(); break;
    case "task-status": status(message.text, message.duration || 0); break;
    case "op-session": case "op-step": case "op-end": case "op-control": handleOps(message); break;
    case "status": status(message.text, message.duration || 2200); break;
  }
}

if (bridge()) bridge().addEventListener("message", (event) => handle(event.data).catch((error) => post({ type: "renderer-error", error: String(error) })));
window.addEventListener("keydown", (event) => {
  if (event.key === "Escape") post({ type: "escape" });
  const key = event.key.toLowerCase();
  // M 切换声音；与扩展版一致，按住不重复触发，且不与系统修饰键组合时才接管。
  if (key === "m" && current && !event.metaKey && !event.ctrlKey && !event.altKey) {
    if (!event.repeat) {
      current.sound.setOn(!current.sound.on);
      renderSoundBtn(current);
    }
  }
  if (key === "h" && current) current.view.hud.classList.toggle("hidden");
  // 任意按键都尝试解锁被挂起的音频上下文（自动播放策略兜底）。
  if (current) unlockSound(current);
});
// 合上/最小化时 RAF 不再被调度，恢复可见后必须显式重新起帧。
document.addEventListener("visibilitychange", () => {
  if (document.hidden || !current) return;
  current.last = performance.now();
  wake(current);
});
let resizeTimer = 0;
let lastSize = "";
window.addEventListener("resize", () => {
  const size = `${innerWidth}x${innerHeight}`;
  if (!current || !lastPayload || size === lastSize) return;
  lastSize = size;
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => {
    if (current && lastPayload) startRebuild(lastPayload);
  }, 200);
});
post({ type: "ready" });
