import { createBlueprint, materializeModel } from "./model.mjs";
import { normalizeAction } from "./action.mjs";
import { applyOpsEvent, createOpsState, hiddenCount, summarize } from "./ops.mjs";

const { View, Engine, Sound, cursorSVG } = window.__swarmRebuild.debug;
const bridge = () => window.chrome && window.chrome.webview;
const post = (message) => bridge() && bridge().postMessage(message);
const statusEl = document.getElementById("desktop-status");
let current = null;
let lastPayload = null;
let actionLayer = null;
let actionPosition = { x: -40, y: -40 };
let actionSerial = 0;
let statusTimer = 0;
let opsState = null;
let opsEl = null;
let opsHideTimer = 0;
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
  if (app.sound) app.sound.close(0.1);
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
  button("sound").textContent = "M";
  button("sound").title = "声音 (M)";
  button("sound").addEventListener("click", () => {
    if (!app.sound) return;
    app.sound.setOn(!app.sound.on);
    button("sound").classList.toggle("off", !app.sound.on);
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
}

function startRebuild(payload) {
  stopRebuild(false);
  lastPayload = payload;
  const blueprint = createBlueprint(payload);
  const model = materializeModel(blueprint, document, mediaFactory);
  const env = desktopEnvironment(model.docW, model.docH, payload.background);
  const view = new View(env);
  const engine = new Engine(view);
  const sound = new Sound();
  sound.init();
  engine.sfx = sound;
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
  engine.onComplete = () => {
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
  const engine = app.engine;
  // 没有可推进的工作（重建完成或剩余任务都被前沿挡住）且指针静止，缓停帧循环。
  const quiet = engine.flocking || (engine.started && engine.backlog() === 0 && !engine.cursors.some((c) => c.task));
  if (quiet && now - app.activeAt > REST_AFTER) {
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
  const layer = ensureActionLayer();
  const serial = ++actionSerial;
  const target = layer.querySelector(".ai-target");
  const cursor = layer.querySelector(".ai-cursor");
  const label = layer.querySelector(".ai-label");
  layer.classList.remove("ok", "fail", "fade");
  Object.assign(target.style, { left: `${action.target.x}px`, top: `${action.target.y}px`, width: `${action.target.w}px`, height: `${action.target.h}px` });
  label.textContent = action.label;
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
    else post({ type: "action-ready", id: action.id });
  };
  requestAnimationFrame(move);
}

function finishAction(result) {
  if (!actionLayer) return;
  const layer = actionLayer;
  layer.classList.remove("ok", "fail", "fade");
  layer.classList.add(result.ok ? "ok" : "fail");
  layer.querySelector(".ai-cursor").classList.add("press");
  if (!result.ok) layer.querySelector(".ai-label").textContent = String(result.error || "操作失败").slice(0, 160);
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
  panel.classList.remove("hidden", "done", "failed", "partial");
  panel.querySelector(".ops-title").textContent = opsState.title;
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
    row.className = `ops-step ${step.status}`;
    const mark = document.createElement("span");
    mark.className = "ops-mark";
    mark.textContent = OPS_MARK[step.status] || "·";
    const label = document.createElement("span");
    label.className = "ops-label";
    const detail = step.error || step.note;
    label.textContent = detail ? `${step.label} · ${detail}` : step.label;
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
}

function handleOps(message) {
  if (message.type === "op-session") {
    opsState = createOpsState(message.session);
    if (opsEl) opsEl.classList.remove("hidden");
    renderOps();
    return;
  }
  opsState = applyOpsEvent(opsState, message);
  renderOps();
}

function clearOps() {
  clearTimeout(opsHideTimer);
  opsState = null;
  if (opsEl) {
    opsEl.remove();
    opsEl = null;
  }
}

async function handle(message) {
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
    case "op-session": case "op-step": case "op-end": handleOps(message); break;
    case "status": status(message.text, message.duration || 2200); break;
  }
}

if (bridge()) bridge().addEventListener("message", (event) => handle(event.data).catch((error) => post({ type: "renderer-error", error: String(error) })));
window.addEventListener("keydown", (event) => {
  if (event.key === "Escape") post({ type: "escape" });
  if (event.key.toLowerCase() === "h" && current) current.view.hud.classList.toggle("hidden");
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
