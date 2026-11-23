import test from "node:test";
import assert from "node:assert/strict";
import { applyOpsEvent, createOpsState, hiddenCount, replayable, stepStateText, summarize, OPS_LIMIT } from "./ops.mjs";

test("步骤按到达顺序累积并统计进度", () => {
  let state = createOpsState({ id: "s1", title: "记事本：草稿", target: "pid 42" });
  state = applyOpsEvent(state, { type: "op-step", step: { id: "a1", kind: "click", label: "点击开始", status: "running" } });
  state = applyOpsEvent(state, { type: "op-step", step: { id: "a1", kind: "click", label: "点击开始", status: "succeeded", ms: 120 } });

  assert.equal(state.steps.length, 1);
  assert.equal(state.steps[0].status, "succeeded");
  assert.deepEqual(summarize(state), { total: 1, done: 1, failed: 0, running: false });
});

test("同编号步骤原地更新而不是重复追加", () => {
  let state = createOpsState({ id: "s1" });
  state = applyOpsEvent(state, { type: "op-step", step: { id: "a1", label: "输入", status: "running" } });
  state = applyOpsEvent(state, { type: "op-step", step: { id: "a1", label: "输入", status: "failed", error: "元素不存在" } });

  assert.equal(state.steps.length, 1);
  assert.equal(state.steps[0].error, "元素不存在");
  assert.equal(summarize(state).failed, 1);
});

test("步骤列表超出上限时保留最近的记录", () => {
  let state = createOpsState({ id: "s1" });
  for (let i = 0; i < OPS_LIMIT + 3; i++) {
    state = applyOpsEvent(state, { type: "op-step", step: { id: `a${i}`, label: `步骤 ${i}`, status: "succeeded" } });
  }
  assert.equal(state.steps.length, OPS_LIMIT);
  assert.equal(state.steps[state.steps.length - 1].label, `步骤 ${OPS_LIMIT + 2}`);
});

test("会话结束保留摘要并停止活动状态", () => {
  let state = createOpsState({ id: "s1" });
  state = applyOpsEvent(state, { type: "op-step", step: { id: "a1", label: "点击", status: "succeeded" } });
  state = applyOpsEvent(state, { type: "op-end", session: { id: "s1", status: "partial" } });

  assert.equal(state.active, false);
  assert.equal(state.status, "partial");
  assert.equal(state.steps.length, 1);
});

test("空状态与未知消息不影响现有记录", () => {
  const state = createOpsState({});
  assert.equal(applyOpsEvent(state, { type: "unknown" }), state);
  assert.equal(applyOpsEvent(null, { type: "op-step" }), null);
});

test("错误会话与过期序号的事件被丢弃", () => {
  let state = createOpsState({ id: "s1" });
  state = applyOpsEvent(state, { type: "op-step", sessionId: "s2", seq: 1, step: { id: "a1", label: "错误会话", status: "running" } });
  state = applyOpsEvent(state, { type: "op-step", sessionId: "s1", seq: 5, step: { id: "a2", label: "正常", status: "running" } });
  state = applyOpsEvent(state, { type: "op-step", sessionId: "s1", seq: 4, step: { id: "a3", label: "过期", status: "running" } });

  assert.deepEqual(state.steps.map((step) => step.id), ["a2"]);
  assert.equal(state.lastSeq, 5);
});

test("结束后的迟到步骤不会复活会话", () => {
  let state = createOpsState({ id: "s1" });
  state = applyOpsEvent(state, { type: "op-end", seq: 3, session: { id: "s1", status: "succeeded" } });
  state = applyOpsEvent(state, { type: "op-step", seq: 4, step: { id: "late", label: "迟到", status: "running" } });

  assert.equal(state.active, false);
  assert.equal(state.steps.length, 0);
});

test("计划与独立验证结果在同一步骤上更新", () => {
  let state = createOpsState({ id: "s1" });
  state = applyOpsEvent(state, { type: "op-step", seq: 1, phase: "plan", step: { id: "a1", label: "点击", status: "pending" } });
  state = applyOpsEvent(state, { type: "op-step", seq: 2, phase: "end", step: { id: "a1", label: "点击", status: "succeeded", verification: "unverified", evidence: "none" } });

  assert.equal(state.steps.length, 1);
  assert.equal(state.steps[0].status, "succeeded");
  assert.equal(state.steps[0].verification, "unverified");
  assert.equal(state.steps[0].evidence, "none");
});

test("旧事件按 verified 与 evidence 兼容映射验证状态", () => {
  let state = createOpsState({ id: "s1" });
  state = applyOpsEvent(state, { type: "op-step", step: { id: "a1", label: "点击", status: "succeeded", verified: true } });
  state = applyOpsEvent(state, { type: "op-step", step: { id: "a2", label: "输入", status: "succeeded", verified: false } });

  assert.equal(state.steps[0].verification, "passed");
  assert.equal(state.steps[1].verification, "not_requested");
});

test("控制状态共用会话序号并丢弃乱序事件", () => {
  let state = createOpsState({ id: "s1" });
  state = applyOpsEvent(state, { type: "op-control", sessionId: "s1", seq: 2, state: "paused" });
  state = applyOpsEvent(state, { type: "op-control", sessionId: "s1", seq: 1, state: "running" });

  assert.equal(state.control, "paused");
  assert.equal(state.lastSeq, 2);
});

test("时间线区分计划发起执行与独立验证状态", () => {
  assert.equal(stepStateText({ status: "pending" }), "已计划");
  assert.equal(stepStateText({ status: "running" }), "已发起");
  assert.equal(stepStateText({ status: "succeeded", verification: "passed" }), "已验证");
  assert.equal(stepStateText({ status: "succeeded", verification: "unverified" }), "已执行 · 未验证");
  assert.equal(stepStateText({ status: "succeeded", verification: "not_requested" }), "已执行");
});

test("只有已结束且包含步骤的会话可以模拟回放", () => {
  let state = createOpsState({ id: "s1" });
  assert.equal(replayable(state), false);
  state = applyOpsEvent(state, { type: "op-step", step: { id: "a1", label: "点击", status: "succeeded" } });
  assert.equal(replayable(state), false);
  state = applyOpsEvent(state, { type: "op-end", session: { id: "s1", status: "succeeded" } });
  assert.equal(replayable(state), true);
  assert.equal(replayable(null), false);
});

test("部分完成与取消状态被保留并可统计溢出数量", () => {
  let state = createOpsState({ id: "s1" });
  for (let i = 0; i < OPS_LIMIT + 2; i++) {
    state = applyOpsEvent(state, { type: "op-step", seq: i + 1, step: { id: `a${i}`, label: `步骤 ${i}`, status: i === 0 ? "canceled" : "succeeded" } });
  }
  state = applyOpsEvent(state, { type: "op-end", seq: 99, session: { id: "s1", status: "partial" } });

  assert.equal(state.steps[state.steps.length - 1].status, "succeeded");
  assert.equal(hiddenCount(state), 2);
  assert.equal(state.status, "partial");
});
