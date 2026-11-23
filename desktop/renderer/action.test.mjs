import test from "node:test";
import assert from "node:assert/strict";
import {
  REPLAY_LIMIT,
  appendReplayFrame,
  createReplayPlayer,
  interruptsReplay,
  normalizeAction,
  replayActionFrame,
  replayResultFrame,
} from "./action.mjs";

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

function clickAction(id, label) {
  return normalizeAction({ id, kind: "click", x: 0, y: 0, w: 10, h: 10, label }, 800, 600);
}

test("回放快照有上限并保留最近动作", () => {
  let frames = [];
  for (let i = 0; i < REPLAY_LIMIT + 5; i++) frames = appendReplayFrame(frames, replayActionFrame(clickAction(`a${i}`, `步骤 ${i}`)));
  assert.equal(frames.length, REPLAY_LIMIT);
  assert.equal(frames[frames.length - 1].action.id, `a${REPLAY_LIMIT + 4}`);
});

test("无效回放帧不会进入快照", () => {
  assert.deepEqual(appendReplayFrame([], null), []);
  assert.deepEqual(appendReplayFrame([], { kind: "unknown" }), []);
});

test("敏感输入在回放快照里仍被脱敏", () => {
  const action = normalizeAction({ id: "s1", kind: "type", x: 0, y: 0, w: 10, h: 10, label: "密码 abc123", sensitive: true }, 800, 600);
  const frame = replayActionFrame(action);
  assert.equal(frame.action.label, "输入受保护文本");
  assert.equal(JSON.stringify(frame).includes("abc123"), false);
});

test("模拟回放按记录顺序执行且不触达宿主", async () => {
  let touched = 0;
  globalThis.window = { chrome: { webview: { postMessage: () => { touched++; } } } };
  const frames = [
    replayActionFrame(clickAction("a1", "打开")),
    replayResultFrame({ id: "a1", ok: true, verification: "unverified" }),
    replayActionFrame(clickAction("a2", "输入")),
    replayResultFrame({ id: "a2", ok: true, verification: "passed" }),
  ];
  const seen = [];
  const player = createReplayPlayer(frames, {
    prepare: (action) => seen.push(`prepare:${action.id}`),
    finish: (result) => seen.push(`finish:${result.id}:${result.verification}`),
    wait: () => Promise.resolve(),
  });
  assert.equal(await player.run(), true);
  assert.deepEqual(seen, ["prepare:a1", "finish:a1:unverified", "prepare:a2", "finish:a2:passed"]);
  delete globalThis.window;
  assert.equal(touched, 0);
});

test("实时事件打断模拟回放且不继续后续帧", async () => {
  let release = () => {};
  const gate = new Promise((resolve) => { release = resolve; });
  const seen = [];
  const player = createReplayPlayer([
    replayActionFrame(clickAction("a1", "打开")),
    replayResultFrame({ id: "a1", ok: true }),
    replayActionFrame(clickAction("a2", "输入")),
  ], {
    prepare: (action) => seen.push(`prepare:${action.id}`),
    finish: (result) => seen.push(`finish:${result.id}`),
    wait: () => gate,
  });
  const running = player.run();
  await Promise.resolve();
  assert.deepEqual(seen, ["prepare:a1"]);
  assert.equal(interruptsReplay({ type: "op-step" }), true);
  assert.equal(interruptsReplay({ type: "op-session" }), true);
  assert.equal(interruptsReplay({ type: "op-end" }), true);
  assert.equal(interruptsReplay({ type: "task-status" }), false);
  player.stop();
  release();
  assert.equal(await running, false);
  assert.deepEqual(seen, ["prepare:a1"]);
});
