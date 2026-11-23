import test from "node:test";
import assert from "node:assert/strict";
import { shouldKeepAnimating } from "./idle.mjs";

// 重建完成进入 follow/flock 盘旋后，必须持续推进，即使指针长时间静止也不能停帧。
test("flocking 态（重建完成）长时间静止仍保持运动", () => {
  assert.equal(
    shouldKeepAnimating({
      flocking: true,
      started: true,
      backlog: 0,
      hasTask: false,
      idleMs: 10_000,
      restAfter: 2000,
    }),
    true,
  );
});

test("flocking 态优先于其他条件，即便 backlog 非空也保持运动", () => {
  assert.equal(
    shouldKeepAnimating({
      flocking: true,
      started: true,
      backlog: 3,
      hasTask: true,
      idleMs: 9999,
      restAfter: 2000,
    }),
    true,
  );
});

test("非 flocking、仍有任务在跑时不算空闲，保持运动", () => {
  assert.equal(
    shouldKeepAnimating({
      flocking: false,
      started: true,
      backlog: 0,
      hasTask: true,
      idleMs: 10_000,
      restAfter: 2000,
    }),
    true,
  );
});

test("非 flocking、已安顿但静止未超阈值时保持运动", () => {
  assert.equal(
    shouldKeepAnimating({
      flocking: false,
      started: true,
      backlog: 0,
      hasTask: false,
      idleMs: 1500,
      restAfter: 2000,
    }),
    true,
  );
});

test("非 flocking、已安顿且静止刚好等于阈值时仍保持运动（严格大于才停）", () => {
  assert.equal(
    shouldKeepAnimating({
      flocking: false,
      started: true,
      backlog: 0,
      hasTask: false,
      idleMs: 2000,
      restAfter: 2000,
    }),
    true,
  );
});

test("非 flocking、已安顿且静止超阈值时进入缓停", () => {
  assert.equal(
    shouldKeepAnimating({
      flocking: false,
      started: true,
      backlog: 0,
      hasTask: false,
      idleMs: 2001,
      restAfter: 2000,
    }),
    false,
  );
});

test("非 flocking、未 started 不算安顿，保持运动", () => {
  assert.equal(
    shouldKeepAnimating({
      flocking: false,
      started: false,
      backlog: 0,
      hasTask: false,
      idleMs: 10_000,
      restAfter: 2000,
    }),
    true,
  );
});

test("非 flocking、backlog 非空不算安顿，保持运动", () => {
  assert.equal(
    shouldKeepAnimating({
      flocking: false,
      started: true,
      backlog: 1,
      hasTask: false,
      idleMs: 10_000,
      restAfter: 2000,
    }),
    true,
  );
});
