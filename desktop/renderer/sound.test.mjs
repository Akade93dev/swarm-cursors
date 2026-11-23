import test from "node:test";
import assert from "node:assert/strict";
import { soundKey } from "./sound.mjs";

// 关闭态优先：即使上下文被挂起，只要 on 为假就显示静音，不提示解锁。
test("on=false 时无论 blocked 如何都返回 off", () => {
  assert.equal(soundKey({ on: false, blocked: false }), "off");
  assert.equal(soundKey({ on: false, blocked: true }), "off");
});

test("on=true 且未被挂起时返回 on", () => {
  assert.equal(soundKey({ on: true, blocked: false }), "on");
});

test("on=true 但被自动播放策略挂起时返回 tap（提示点击解锁）", () => {
  assert.equal(soundKey({ on: true, blocked: true }), "tap");
});
