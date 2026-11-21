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
const OPS_MARK = { pending: "·", running: "◌", succeeded: "✓", failed: "✕", canceled: "⊘" };

function status(text, duration = 2200) {
  clearTimeout(statusTimer);
  statusEl.textContent = text;
  statusEl.classList.toggle("visible", Boolean(text));
  if (text && duration) statusTimer = setTimeout(() => statusEl.classList.remove("visible"), duration);
}

function mediaFactory(src) {
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
  if (notify) post({ type: "closed" });
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
  button("hide").addEventListener("click", () => view.hud.classList.add("hidden"));
  view.unhideBtn.addEventListener("click", () => view.hud.classList.remove("hidden"));
  view.host.addEventListener("pointermove", (event) => { engine.mouse = { x: event.clientX, y: event.clientY }; }, { passive: true });
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
  current = { view, engine, sound, raf: 0, last: performance.now(), speeds: [1, 2, 3], speedIndex: 0 };
  view.intro.style.display = "none";
  view.mount(model);
  engine.setModel(model);
  view.bg.classList.add("on");
  view.hud.classList.add("on");
  bindRebuild(current);
  engine.onComplete = () => {
    view.status.textContent = `已由 ${engine.cursors.filter((cursor) => cursor.worked).length + engine.retired} 个光标重建`;
    view.barFill.style.width = "100%";
    post({ type: "complete" });
  };
  engine.start();
  const frame = (now) => {
    if (!current || current.view !== view) return;
    const dt = Math.min(0.05, (now - current.last) / 1000);
    current.last = now;
    engine.step(dt);
    view.render(engine);
    current.raf = requestAnimationFrame(frame);
  };
  current.raf = requestAnimationFrame(frame);
  lastSize = `${innerWidth}x${innerHeight}`;
  post({ type: "rebuild-ready" });
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
