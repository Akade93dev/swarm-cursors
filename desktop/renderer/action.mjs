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
  };
}
