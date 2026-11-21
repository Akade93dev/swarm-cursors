export const OPS_LIMIT = 12;

const STATUS_ALIASES = { partial: "partial" };

export function createOpsState(session) {
  const source = session || {};
  return {
    id: String(source.id || "session"),
    title: String(source.title || "AI 操作").slice(0, 120),
    target: String(source.target || "").slice(0, 120),
    steps: [],
    active: true,
    status: "",
    lastSeq: 0,
    seen: 0,
  };
}

function isStale(state, message) {
  if (message.sessionId && state.id && String(message.sessionId) !== state.id) return true;
  const seq = Number(message.seq);
  if (Number.isFinite(seq) && seq > 0 && seq <= state.lastSeq) return true;
  return false;
}

function bumpSeq(state, message) {
  const seq = Number(message.seq);
  return Number.isFinite(seq) && seq > state.lastSeq ? seq : state.lastSeq;
}

export function applyOpsEvent(state, message) {
  if (!state) return null;
  if (message.type !== "op-session" && isStale(state, message)) return state;
  switch (message.type) {
    case "op-step": {
      const raw = message.step || {};
      if (!raw.id) return state;
      // 已结束的会话不允许被迟到的步骤事件复活。
      if (!state.active) return state;
      const id = String(raw.id);
      const known = state.steps.some((item) => item.id === id);
      const step = {
        id,
        kind: String(raw.kind || "custom"),
        label: String(raw.label || "AI 操作").slice(0, 120),
        status: STATUS_ALIASES[raw.status] || String(raw.status || "pending"),
        ms: Math.max(0, Number(raw.ms) || 0),
        error: raw.error ? String(raw.error).slice(0, 120) : "",
        tier: String(raw.tier || "none"),
        verified: Boolean(raw.verified),
        note: raw.note ? String(raw.note).slice(0, 120) : "",
      };
      const steps = state.steps.slice();
      const index = steps.findIndex((item) => item.id === id);
      if (index >= 0) steps[index] = step;
      else steps.push(step);
      return {
        ...state,
        steps: steps.length > OPS_LIMIT ? steps.slice(steps.length - OPS_LIMIT) : steps,
        seen: known || index >= 0 ? state.seen : state.seen + 1,
        lastSeq: bumpSeq(state, message),
      };
    }
    case "op-end": {
      const session = message.session || {};
      if (session.id && String(session.id) !== state.id) return state;
      return {
        ...state,
        active: false,
        status: String(session.status || "succeeded"),
        lastSeq: bumpSeq(state, message),
      };
    }
    default:
      return state;
  }
}

export function summarize(state) {
  const steps = state.steps;
  return {
    total: steps.length,
    done: steps.filter((step) => step.status === "succeeded").length,
    failed: steps.filter((step) => step.status === "failed").length,
    running: steps.some((step) => step.status === "running"),
  };
}

export function hiddenCount(state) {
  return Math.max(0, Number(state.seen) - state.steps.length);
}
