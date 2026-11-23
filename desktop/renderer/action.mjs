// 回放快照上限：保留最近的动作视觉帧，避免长时间会话无限增长。
export const REPLAY_LIMIT = 40;

export function normalizeAction(action, viewportWidth, viewportHeight) {
  if (!action || action.id == null || String(action.id).length === 0) throw new TypeError("Action id is required");
  const rawX = Number(action.x) || 0;
  const rawY = Number(action.y) || 0;
  const rawR = rawX + Math.max(0, Number(action.w) || 0);
  const rawB = rawY + Math.max(0, Number(action.h) || 0);
  const x0 = Math.max(0, Math.min(viewportWidth, rawX));
  const y0 = Math.max(0, Math.min(viewportHeight, rawY));
  const x1 = Math.max(x0, Math.min(viewportWidth, rawR));
  const y1 = Math.max(y0, Math.min(viewportHeight, rawB));
  const target = { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
  return {
    id: String(action.id),
    kind: String(action.kind || "click"),
    target,
    cursorX: target.x + target.w / 2,
    cursorY: target.y + target.h / 2,
    deltaX: Number(action.deltaX) || 0,
    deltaY: Number(action.deltaY) || 0,
    label: action.sensitive ? "输入受保护文本" : String(action.label || action.kind || "AI 操作").slice(0, 160),
    sensitive: Boolean(action.sensitive),
    // P1-M: intent = "为什么做"。默认空(旧事件不带), 截断到 140 字符。
    intent: action.intent != null ? String(action.intent).slice(0, 140) : "",
  };
}

const VERIFICATIONS = new Set(["not_requested", "passed", "unverified"]);

function verificationOf(result) {
  const raw = result && result.verification != null ? String(result.verification) : "";
  if (VERIFICATIONS.has(raw)) return raw;
  if (result && result.verified) return "passed";
  return result && result.evidence != null ? "unverified" : "not_requested";
}

// 只保留纯视觉字段；敏感标签在这里再兜一次底，避免原文进入回放。
export function replayActionFrame(action) {
  if (!action || action.id == null) return null;
  const target = action.target || { x: 0, y: 0, w: 0, h: 0 };
  return {
    kind: "prepare",
    action: {
      id: String(action.id),
      kind: String(action.kind || "click"),
      target: { x: Number(target.x) || 0, y: Number(target.y) || 0, w: Number(target.w) || 0, h: Number(target.h) || 0 },
      cursorX: Number(action.cursorX) || 0,
      cursorY: Number(action.cursorY) || 0,
      label: action.sensitive ? "输入受保护文本" : String(action.label || "AI 操作").slice(0, 160),
      sensitive: Boolean(action.sensitive),
      intent: action.intent != null ? String(action.intent).slice(0, 140) : "",
    },
  };
}

export function replayResultFrame(result) {
  if (!result) return null;
  return {
    kind: "result",
    result: {
      id: result.id == null ? "" : String(result.id),
      ok: Boolean(result.ok),
      verification: verificationOf(result),
      evidence: result.evidence == null ? null : String(result.evidence),
      error: result.error ? String(result.error).slice(0, 160) : null,
    },
  };
}

export function appendReplayFrame(frames, frame, limit = REPLAY_LIMIT) {
  const list = Array.isArray(frames) ? frames : [];
  if (!frame || (frame.kind !== "prepare" && frame.kind !== "result")) return list;
  const next = list.concat([frame]);
  return next.length > limit ? next.slice(next.length - limit) : next;
}

// 回放期间任何实时会话、步骤、动作或重建事件都必须立即中止模拟，避免污染实时序号。
const REPLAY_BREAKERS = new Set([
  "op-session", "op-step", "op-end", "op-control",
  "action-prepare", "action-result", "action-clear", "clear-actions", "ops-clear",
  "stop", "rebuild", "rebuild-stop",
]);

export function interruptsReplay(message) {
  return Boolean(message && REPLAY_BREAKERS.has(message.type));
}

// 纯视觉回放器：只调用注入的 prepare/finish/wait，不持有宿主桥引用。
export function createReplayPlayer(frames, handlers) {
  const queue = Array.isArray(frames) ? frames.slice() : [];
  const api = {
    stopped: false,
    stop() { api.stopped = true; },
    async run() {
      for (const frame of queue) {
        if (api.stopped) return false;
        if (frame.kind === "prepare") handlers.prepare(frame.action);
        else if (frame.kind === "result") handlers.finish(frame.result);
        if (api.stopped) return false;
        if (handlers.wait) await handlers.wait(frame);
      }
      return !api.stopped;
    },
  };
  return api;
}
