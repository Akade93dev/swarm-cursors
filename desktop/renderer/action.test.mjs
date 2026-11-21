import test from "node:test";
import assert from "node:assert/strict";
import { normalizeAction } from "./action.mjs";

test("AI 动作坐标限制在当前视口", () => {
  const action = normalizeAction({ id: "a1", kind: "click", x: -20, y: 50, w: 80, h: 30, label: "Click" }, 800, 600);
  assert.deepEqual(action.target, { x: 0, y: 50, w: 60, h: 30 });
  assert.equal(action.cursorX, 30);
  assert.equal(action.cursorY, 65);
});

test("敏感输入不会把原文放进可视标签", () => {
  const action = normalizeAction({ id: "a2", kind: "type", x: 10, y: 10, w: 100, h: 20, label: "secret", sensitive: true }, 800, 600);
  assert.equal(action.label, "输入受保护文本");
  assert.equal(JSON.stringify(action).includes("secret"), false);
});

test("无编号动作会被拒绝", () => {
  assert.throws(() => normalizeAction({ kind: "click", x: 0, y: 0, w: 10, h: 10 }, 800, 600), /id/);
});
