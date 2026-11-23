import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { createBlueprint, fitTextWidth, materializeModel, mergeLineRuns } from "./model.mjs";

const base = { width: 320, height: 200, background: "rgb(255,255,255)" };

test("截屏图块按视觉顺序生成独立重建任务", () => {
  const model = createBlueprint({
    ...base,
    elements: [
      { kind: "image", x: 128, y: 0, w: 128, h: 128, src: "tile-b" },
      { kind: "image", x: 0, y: 0, w: 128, h: 128, src: "tile-a" },
    ],
  });

  assert.equal(model.docW, 320);
  assert.equal(model.docH, 200);
  assert.deepEqual(model.items.map((it) => it.src), ["tile-b", "tile-a"]);
  assert.equal(model.tasks.length, 2);
  assert.deepEqual(model.items.map((it) => it.task), model.tasks);
  assert.deepEqual(model.tasks.map((task) => task.spec.type), ["image", "image"]);
});

test("缺少图片源的元素被跳过，不会生成空图请求", () => {
  const model = createBlueprint({
    ...base,
    elements: [
      { kind: "image", x: 0, y: 0, w: 64, h: 64, src: "" },
      { kind: "image", x: 64, y: 0, w: 64, h: 64 },
      { kind: "image", x: 128, y: 0, w: 64, h: 64, src: "data:image/png;base64,AA" },
    ],
  });

  assert.equal(model.items.length, 1);
  assert.equal(model.tasks.length, 1);
  assert.equal(model.items[0].src, "data:image/png;base64,AA");
});

test("整屏实拍底座排在最底层并整幅覆盖", () => {
  const model = createBlueprint({
    ...base,
    elements: [
      { id: "desktop", kind: "image", x: 0, y: 0, w: 320, h: 200, src: "base", semantic: "desktop-base" },
      { id: "win", kind: "rect", x: 40, y: 30, w: 200, h: 120, css: "background:#fff;" },
      { parent: "win", kind: "text", x: 50, y: 40, w: 100, h: 20, text: "Hi", fontSize: 13 },
    ],
  });

  const [desktop, win, text] = model.items;
  assert.equal(model.items[0].semantic, "desktop-base");
  assert.equal(desktop.src, "base");
  assert.deepEqual([desktop.x, desktop.y, desktop.w, desktop.h], [0, 0, 320, 200]);
  // 底座不影响窗口与文字的绘制顺序和依赖。
  assert.deepEqual(model.tasks, [desktop.task, win.task, text.task]);
  assert.equal(win.task.deps.length, 0);
  assert.deepEqual(text.task.deps, [win.task]);
});

test("子元素任务等待父容器完成", () => {
  const model = createBlueprint({
    ...base,
    elements: [
      { id: "panel", kind: "rect", x: 10, y: 10, w: 200, h: 100, css: "background:#eee;" },
      { id: "label", parent: "panel", kind: "text", x: 20, y: 20, w: 100, h: 24, text: "Hello", fontSize: 16 },
    ],
  });

  const [panel, label] = model.items;
  assert.deepEqual(label.task.deps, [panel.task]);
  assert.equal(label.tb.y, 20);
  assert.equal(label.fs, 16);
});

test("忽略无效和完全离屏元素，并限制模型大小", () => {
  const model = createBlueprint({
    ...base,
    maxItems: 2,
    elements: [
      { kind: "rect", x: -50, y: -50, w: 20, h: 20 },
      { kind: "rect", x: 0, y: 0, w: Number.NaN, h: 20 },
      { kind: "rect", x: 0, y: 0, w: 20, h: 20 },
      { kind: "rect", x: 30, y: 0, w: 20, h: 20 },
      { kind: "rect", x: 60, y: 0, w: 20, h: 20 },
    ],
  });

  assert.equal(model.items.length, 2);
  assert.equal(model.tasks.length, 2);
});

test("桌面模式导出渲染类且不会启动网页捕获", async () => {
  const source = await readFile(new URL("../../swarm-rebuild/swarm.js", import.meta.url), "utf8");
  const window = { __swarmDesktopHost: true };
  const context = vm.createContext({
    window,
    console,
    performance: { now: () => 0 },
    requestAnimationFrame: () => 1,
    cancelAnimationFrame: () => {},
    setTimeout,
    clearTimeout,
  });

  vm.runInContext(source, context);

  assert.equal(typeof window.__swarmRebuild.debug.View, "function");
  assert.equal(typeof window.__swarmRebuild.debug.Engine, "function");
  assert.equal(typeof window.__swarmRebuild.debug.cursorSVG, "function");
  assert.equal(window.__swarmRebuild.app, null);
});

test("物化函数由宿主 DOM 创建可克隆的文本和媒体节点", () => {
  const blueprint = createBlueprint({
    ...base,
    elements: [
      { kind: "text", x: 0, y: 0, w: 100, h: 20, text: "Safe <text>" },
      { kind: "text", x: 0, y: 30, w: 100, h: 60, text: "多行正文", fontSize: 14, lineHeight: 18 },
    ],
  });
  const nodes = [];
  const document = {
    createElement(tag) {
      const node = {
        tag,
        children: [],
        style: {},
        setAttribute(name, value) { this[name] = value; },
        appendChild(child) { this.children.push(child); return child; },
        cloneNode() { return { ...this, children: [...this.children] }; },
      };
      nodes.push(node);
      return node;
    },
    createTextNode(text) { return { text }; },
  };

  const model = materializeModel(blueprint, document, (src) => ({ src, cloneNode() { return this; } }));
  assert.equal(model.items[0].full.children[0].text, "Safe <text>");
  assert.equal(model.items[0].layer, "world");
  assert.equal(model.items[0].op, 1);
  // 单行文本禁止软换行，避免收窄到实测墨迹宽后逐字折行。
  assert.match(model.items[0].full.style, /white-space:pre;/);
  // 多行文本仍需保留换行与自动折行。
  assert.match(model.items[1].full.style, /white-space:pre-wrap;/);
});

test("混排文本为每个 span 生成独立内联节点并覆盖字体样式", () => {
  const blueprint = createBlueprint({
    ...base,
    elements: [{
      kind: "text", x: 0, y: 0, w: 200, h: 20,
      text: "ItalicHead normal black text",
      fontFamily: "Segoe UI", fontSize: 13.5, fontWeight: "400", fontStyle: "italic",
      spans: [
        { text: "ItalicHead", fontFamily: "Segoe UI", fontSize: 13.5, fontWeight: "400", fontStyle: "italic" },
        { text: " normal black text", fontFamily: "Segoe UI", fontSize: 13.5, fontWeight: "400", fontStyle: "normal" },
      ],
    }],
  });
  const document = {
    createElement(tag) {
      return {
        tag,
        children: [],
        style: {},
        setAttribute(name, value) { this[name] = value; },
        appendChild(child) { this.children.push(child); return child; },
        cloneNode() { return { ...this, children: [...this.children] }; },
      };
    },
    createTextNode(text) { return { text }; },
  };

  const model = materializeModel(blueprint, document, (src) => ({ src, cloneNode() { return this; } }));
  const full = model.items[0].full;

  assert.equal(full.children.length, 2);
  assert.equal(full.children[0].tag, "span");
  assert.equal(full.children[0].children[0].text, "ItalicHead");
  assert.match(full.children[0].style, /font-style:italic/);
  assert.match(full.children[1].style, /font-style:normal/);
});

test("单行文本按渲染层实测宽度收窄，不再按字符数粗估", () => {
  // 同一行：x=652，右邻片段在 903，实测 249 → 宽 251，正好不侵入。
  assert.deepEqual(
    fitTextWidth({ measured: 249, fontSize: 19, textLength: 8, x: 652, neighborX: 903 }),
    { w: 251, letterSpacing: "" },
  );
  // 没有右邻片段时只按实测宽补 2px。
  assert.deepEqual(
    fitTextWidth({ measured: 100, fontSize: 14, textLength: 5, x: 0 }),
    { w: 102, letterSpacing: "" },
  );
});

test("实测宽度超过行内间距时用有上限的负字距收紧", () => {
  // gap=200，需要收紧 202px/40 字≈-5.05，但上限是 -0.15em=-2.1。
  const fit = fitTextWidth({ measured: 400, fontSize: 14, textLength: 40, x: 0, neighborX: 200 });
  assert.equal(fit.letterSpacing, "-2.1px");
  // 盒宽严格夹在右邻片段起点内，重建层矩形不重叠。
  assert.equal(fit.w, 200);
});

test("字距微调写进渲染样式", () => {
  const blueprint = createBlueprint({
    ...base,
    elements: [{ kind: "text", x: 0, y: 0, w: 100, h: 20, text: "abc", letterSpacing: "-0.2px" }],
  });
  const document = {
    createElement(tag) {
      return {
        tag,
        children: [],
        style: {},
        setAttribute(name, value) { this[name] = value; },
        appendChild(child) { this.children.push(child); return child; },
        cloneNode() { return { ...this, children: [...this.children] }; },
      };
    },
    createTextNode(text) { return { text }; },
  };

  const model = materializeModel(blueprint, document, (src) => ({ src, cloneNode() { return this; } }));

  assert.match(model.items[0].full.style, /letter-spacing:-0\.2px;/);
});

test("同一视觉行的相邻片段合并为一条行内流水", () => {
  const elements = [
    { id: "label", kind: "text", x: 42, y: 281.9, w: 55, h: 19, text: "我的站点", fontSize: 14, lineHeight: 18.2 },
    { id: "badge", kind: "text", x: 105.33, y: 282.87, w: 30, h: 15, text: "限免", fontSize: 12, lineHeight: 15.6, color: "rgb(255,0,0)" },
  ];

  const merged = mergeLineRuns(elements);

  assert.equal(merged.length, 1);
  const line = merged[0];
  assert.equal(line.id, "label");
  assert.equal(line.merged, true);
  assert.equal(line.x, 42);
  // 合并行取主字号的 y，高度覆盖组内最矮片段的下缘。
  assert.equal(line.y, 281.9);
  assert.equal(line.h, 19);
  assert.equal(line.w, 93.33);
  assert.equal(line.spans.length, 2);
  assert.deepEqual(line.spans.map((span) => span.text), ["我的站点", "限免"]);
  // 8.33px 的视觉间距写成 span 外边距，由浏览器继续排字。
  assert.equal(line.spans[1].ml, 8.33);
  assert.deepEqual(elements.map((item) => item.id), ["label", "badge"]);
});

test("间距过大的同行片段保持独立", () => {
  const merged = mergeLineRuns([
    { id: "left", kind: "text", x: 0, y: 0, w: 40, h: 19, text: "左", fontSize: 14, lineHeight: 18.2 },
    { id: "right", kind: "text", x: 60, y: 0, w: 40, h: 19, text: "右", fontSize: 14, lineHeight: 18.2 },
  ]);

  assert.equal(merged.length, 2);
});

test("IA2 精确行不再参与合并也不被相邻片段夹紧", () => {
  const merged = mergeLineRuns([
    { id: "a", kind: "text", x: 42, y: 282, w: 55, h: 19, text: "我的站点", fontSize: 14, lineHeight: 18.2, exact: true },
    { id: "b", kind: "text", x: 105, y: 282, w: 30, h: 19, text: "限免", fontSize: 14, lineHeight: 18.2, exact: true },
  ]);

  assert.equal(merged.length, 2);
  assert.equal(merged[0].exact, true);
  assert.equal(merged[0].w, 55);
});

test("多行文本与不同行的文本不参与合并", () => {
  const merged = mergeLineRuns([
    { id: "para", kind: "text", x: 0, y: 0, w: 300, h: 40, text: "两行正文", fontSize: 14, lineHeight: 18.2 },
    { id: "next", kind: "text", x: 0, y: 10, w: 30, h: 15, text: "邻字", fontSize: 12, lineHeight: 15.6 },
    { id: "far", kind: "text", x: 0, y: 80, w: 40, h: 19, text: "远处", fontSize: 14, lineHeight: 18.2 },
  ]);

  assert.equal(merged.length, 3);
});

test("合并行不再因右邻片段夹紧宽度", () => {
  assert.deepEqual(
    fitTextWidth({ measured: 249, fontSize: 14, textLength: 8, x: 652, neighborX: 700, allowShrink: false }),
    { w: 251, letterSpacing: "" },
  );
});

test("合并段的间距写进内联样式", () => {
  const blueprint = createBlueprint({
    ...base,
    elements: [{
      kind: "text", x: 0, y: 0, w: 120, h: 20,
      text: "我的站点 限免",
      fontSize: 14, lineHeight: 18.2,
      merged: true,
      spans: [
        { text: "我的站点", fontSize: 14, fontWeight: "400", fontStyle: "normal", color: "rgb(0,0,0)", ml: 0 },
        { text: "限免", fontSize: 12, fontWeight: "400", fontStyle: "normal", color: "rgb(255,0,0)", ml: 8.33 },
      ],
    }],
  });
  const document = {
    createElement(tag) {
      return {
        tag,
        children: [],
        style: {},
        setAttribute(name, value) { this[name] = value; },
        appendChild(child) { this.children.push(child); return child; },
        cloneNode() { return { ...this, children: [...this.children] }; },
      };
    },
    createTextNode(text) { return { text }; },
  };

  const model = materializeModel(blueprint, document, (src) => ({ src, cloneNode() { return this; } }));

  assert.equal(model.items[0].full.children.length, 2);
  assert.match(model.items[0].full.children[1].style, /margin-left:8\.33px;/);
});

test("文字透传表面色并在容器上写出同色不透明底色", () => {
  const blueprint = createBlueprint({
    ...base,
    elements: [{ kind: "text", x: 0, y: 0, w: 100, h: 20, text: "Hi", surfaceColor: "rgb(240,240,240)" }],
  });
  assert.equal(blueprint.items[0].surfaceColor, "rgb(240,240,240)");

  const document = {
    createElement(tag) {
      return { tag, children: [], style: {}, setAttribute(name, value) { this[name] = value; }, appendChild(child) { this.children.push(child); return child; }, cloneNode() { return { ...this, children: [...this.children] }; } };
    },
    createTextNode(text) { return { text }; },
  };
  const model = materializeModel(blueprint, document, (src) => ({ src, cloneNode() { return this; } }));
  // 同色不透明底色让 Chromium 重新启用 ClearType，并盖住截图底座里的原文，消除双绘加粗。
  assert.match(model.items[0].full.style, /background-color:rgb\(240,240,240\);/);
});

test("没有表面色的文字不写 background-color，避免在非均匀表面上出现色块", () => {
  const blueprint = createBlueprint({
    ...base,
    elements: [{ kind: "text", x: 0, y: 0, w: 100, h: 20, text: "Hi" }],
  });
  const document = {
    createElement(tag) {
      return { tag, children: [], style: {}, setAttribute(name, value) { this[name] = value; }, appendChild(child) { this.children.push(child); return child; }, cloneNode() { return { ...this, children: [...this.children] }; } };
    },
    createTextNode(text) { return { text }; },
  };
  const model = materializeModel(blueprint, document, (src) => ({ src, cloneNode() { return this; } }));
  assert.doesNotMatch(model.items[0].full.style, /background-color:/);
});

test("所有文字容器都禁用字体合成，避免缺字重时被合成 faux bold", () => {
  const blueprint = createBlueprint({
    ...base,
    elements: [
      { kind: "text", x: 0, y: 0, w: 100, h: 20, text: "A", surfaceColor: "rgb(255,255,255)" },
      { kind: "text", x: 0, y: 30, w: 100, h: 20, text: "B" },
    ],
  });
  const document = {
    createElement(tag) {
      return { tag, children: [], style: {}, setAttribute(name, value) { this[name] = value; }, appendChild(child) { this.children.push(child); return child; }, cloneNode() { return { ...this, children: [...this.children] }; } };
    },
    createTextNode(text) { return { text }; },
  };
  const model = materializeModel(blueprint, document, (src) => ({ src, cloneNode() { return this; } }));
  for (const item of model.items) assert.match(item.full.style, /font-synthesis:none;/);
});
