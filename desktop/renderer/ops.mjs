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
    control: "running",
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
        // intent: "为什么做"——P1-M mirror 人类通道需要的三要素之一(计划→动作→证据)。
        // 可为空(旧事件不带), 截断到 140 字符。
        intent: raw.intent != null ? String(raw.intent).slice(0, 140) : "",
        status: STATUS_ALIASES[raw.status] || String(raw.status || "pending"),
        ms: Math.max(0, Number(raw.ms) || 0),
        error: raw.error ? String(raw.error).slice(0, 120) : "",
        tier: String(raw.tier || "none"),
        verified: Boolean(raw.verified),
        verification: raw.verification
          ? String(raw.verification)
          : raw.verified
            ? "passed"
            : raw.evidence != null
              ? "unverified"
              : "not_requested",
        evidence: raw.evidence ? String(raw.evidence) : "",
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
    case "op-control": {
      if (!state.active) return state;
      return {
        ...state,
        control: String(message.state || "running"),
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

export function stepStateText(step) {
  if (step.status === "pending") return "已计划";
  if (step.status === "running") return "已发起";
  if (step.status === "succeeded") {
    if (step.verification === "passed") return "已验证";
    if (step.verification === "unverified") return "已执行 · 未验证";
    return "已执行";
  }
  if (step.status === "failed") return "失败";
  if (step.status === "canceled") return "已取消";
  return "";
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

// 只有已经结束且确实记录了步骤的会话才允许做视觉回放。
export function replayable(state) {
  return Boolean(state && !state.active && Array.isArray(state.steps) && state.steps.length > 0);
}
