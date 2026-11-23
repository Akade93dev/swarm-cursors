import test from "node:test";
import assert from "node:assert/strict";
import {
  normalizeHumanMasks,
  mergeAdjacentMasks,
  maskClipPath,
  CATEGORY_BADGES,
  FALLBACK_BADGE,
} from "./human-mask.mjs";

test("空 redaction 列表返回空数组", () => {
  assert.deepEqual(normalizeHumanMasks([], 800, 600), []);
  assert.deepEqual(normalizeHumanMasks(null, 800, 600), []);
  assert.deepEqual(normalizeHumanMasks(undefined, 800, 600), []);
});

test("基本 redaction 归一化 + padding + badge 映射", () => {
  const masks = normalizeHumanMasks([
    { x: 100, y: 50, w: 120, h: 30, category: "password" },
  ], 800, 600, 4);

  assert.equal(masks.length, 1);
  const m = masks[0];
  assert.equal(m.category, "password");
  assert.equal(m.badge.icon, "🔐");
  assert.equal(m.badge.label, "密码");
  // box 加了 4px padding
  assert.equal(m.box.x, 96);   // 100 - 4
  assert.equal(m.box.y, 46);   // 50 - 4
  assert.equal(m.box.w, 128);  // 120 + 8
  assert.equal(m.box.h, 38);   // 30 + 8
  // blurPx 自适应: min(128,38)/8 ≈ 4.75 → 4 (夹在 4–24)
  assert.ok(m.blurPx >= 4 && m.blurPx <= 24);
});

test("未知类别回退到 FALLBACK_BADGE", () => {
  const masks = normalizeHumanMasks([
    { x: 0, y: 0, w: 10, h: 10, category: "unknown" },
  ], 800, 600);
  assert.equal(masks[0].badge.icon, FALLBACK_BADGE.icon);
  assert.equal(masks[0].badge.label, FALLBACK_BADGE.label);
});

test("零尺寸 redaction 被过滤", () => {
  const masks = normalizeHumanMasks([
    { x: 0, y: 0, w: 0, h: 0 },
    { x: 0, y: 0, w: 10, h: 0 },
    null,
    "invalid",
  ], 800, 600);
  assert.deepEqual(masks, []);
});

test("坐标被裁剪到视口边界", () => {
  const masks = normalizeHumanMasks([
    { x: -50, y: -20, w: 200, h: 500, category: "email" },
  ], 800, 600);

  const m = masks[0];
  assert.equal(m.box.x, 0);             // 不能负
  assert.equal(m.box.y, 0);
  assert.ok(m.box.w <= 800);            // 不能超视口
  assert.ok(m.box.h <= 600);
});

test("blurPx 夹在 4–24 之间", () => {
  // 极小盒子 → blur 至少 4
  const small = normalizeHumanMasks([{ x: 0, y: 0, w: 8, h: 8 }], 800, 600)[0];
  assert.ok(small.blurPx >= 4);

  // 极大盒子 → blur 至多 24
  const big = normalizeHumanMasks([{ x: 0, y: 0, w: 800, h: 600 }], 800, 600)[0];
  assert.ok(big.blurPx <= 24);
});

test("mergeAdjacentMasks 合并重叠区域", () => {
  const a = normalizeHumanMasks([{ x: 10, y: 10, w: 50, h: 30, category: "id" }], 800, 600)[0];
  const b = normalizeHumanMasks([{ x: 55, y: 15, w: 30, h: 40, category: "email" }], 800, 600)[0];
  const merged = mergeAdjacentMasks([a, b], 2);

  assert.equal(merged.length, 1);
  const m = merged[0];
  // 合并后的 box 应该覆盖 a 和 b
  assert.ok(m.box.x <= 10);
  assert.ok(m.box.y <= 10);
  assert.ok(m.box.x + m.box.w >= 85);
  assert.ok(m.box.y + m.box.h >= 55);
});

test("mergeAdjacentMasks 不合并远距离区域", () => {
  const a = normalizeHumanMasks([{ x: 10, y: 10, w: 10, h: 10 }], 800, 600)[0];
  const b = normalizeHumanMasks([{ x: 400, y: 400, w: 10, h: 10 }], 800, 600)[0];
  const merged = mergeAdjacentMasks([a, b], 2);
  assert.equal(merged.length, 2);
});

test("mergeAdjacentMasks 空/单元素返回原数组", () => {
  assert.deepEqual(mergeAdjacentMasks([], 2), []);
  const a = normalizeHumanMasks([{ x: 0, y: 0, w: 10, h: 10 }], 800, 600);
  assert.equal(mergeAdjacentMasks(a, 2).length, 1);
});

test("maskClipPath 生成有效 polygon", () => {
  const box = { x: 50, y: 30, w: 100, h: 60 };
  const mask = { box };
  const path = maskClipPath(mask);
  assert.ok(path.startsWith("polygon("));
  assert.ok(path.endsWith(")"));
  assert.ok(path.includes("50px"));
  assert.ok(path.includes("30px"));
  assert.ok(path.includes("150px")); // 50 + 100
  assert.ok(path.includes("90px"));  // 30 + 60
});

test("maskClipPath 零盒子返回空字符串", () => {
  assert.equal(maskClipPath(null), "");
  assert.equal(maskClipPath({}), "");
});

test("CATEGORY_BADGES 覆盖主要敏感类型", () => {
  const required = ["password", "credential", "email", "id", "bank", "phone"];
  for (const cat of required) {
    assert.ok(CATEGORY_BADGES[cat], `缺少类别映射: ${cat}`);
    assert.ok(CATEGORY_BADGES[cat].icon, `${cat} 缺 icon`);
    assert.ok(CATEGORY_BADGES[cat].label, `${cat} 缺 label`);
  }
});
