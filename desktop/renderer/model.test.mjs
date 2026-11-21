import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { createBlueprint, materializeModel } from "./model.mjs";

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
    elements: [{ kind: "text", x: 0, y: 0, w: 100, h: 20, text: "Safe <text>" }],
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
