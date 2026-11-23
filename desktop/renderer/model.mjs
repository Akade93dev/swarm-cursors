const KIND_TASK = { rect: "rect", image: "image", text: "text", line: "line" };
const finite = (value) => typeof value === "number" && Number.isFinite(value);

/**
 * 单行文本的宽度由渲染层实测，measured 是隐藏测量层用真实字体量到的内容宽。
 * 合并成整条视觉行的文本不再夹紧（allowShrink=false）：浏览器已经保证行内片段不会互相重叠，
 * 再按右邻元素框收窄只会把真实文字压窄。
 * 未合并的独立片段如果实测宽会压到同一行的右侧片段，才用有上限（0.15em）的负字距收紧。
 */
export function fitTextWidth({ measured, fontSize, textLength, x, neighborX, allowShrink = true }) {
  const width = (finite(measured) ? Math.max(0, measured) : 0) + 2;
  if (!allowShrink) return { w: Math.max(1, width), letterSpacing: "" };
  if (!finite(neighborX) || !finite(x) || neighborX <= x) return { w: Math.max(1, width), letterSpacing: "" };
  const gap = neighborX - x;
  if (gap <= 0 || width <= gap) return { w: Math.max(1, width), letterSpacing: "" };
  const length = Math.max(1, Math.floor(finite(textLength) ? textLength : 1));
  const em = finite(fontSize) && fontSize > 0 ? fontSize : 14;
  const spacing = Math.max(-0.15 * em, (gap - width) / length);
  if (spacing > -0.005) return { w: Math.max(1, width), letterSpacing: "" };
  const adjusted = Math.max(1, width + spacing * length);
  return {
    // 盒宽严格夹在右邻片段起点内，保证重建层的矩形不重叠；字距收紧负责减少字形外溢。
    w: Math.max(1, Math.min(gap, Math.round(adjusted * 100) / 100)),
    letterSpacing: `${Math.round(spacing * 100) / 100}px`,
  };
}

const ROUND = (value) => Math.round(value * 100) / 100;
/** 行内片段的候选间距：小于 0.75 字高才可能是同一段连续文本。 */
const RUN_GAP_EM = 0.75;
/** 间距达到 0.2 字高才写成 span 外边距，更小的间距交给浏览器自然排字。 */
const SPACER_MIN_EM = 0.2;
/** 视觉间距的写入上限，避免 UIA 框异常时把一行推得过远。 */
const SPACER_MAX_EM = 2;

/**
 * 把同一视觉行里相邻的文本片段合并成一条“行内流水”。
 * 每个片段仍保留自己的字体样式，只把片段之间的视觉间距写成 span 的 margin-left，
 * 由浏览器在同一行内继续排字。这样行内文本的重叠与接缝从构造上消失，
 * 也不再需要按右邻元素框夹紧宽度（夹紧会把真实文字压窄）。
 */
export function mergeLineRuns(elements) {
  const list = Array.isArray(elements) ? elements : [];
  const candidates = [];
  for (let index = 0; index < list.length; index++) {
    const element = list[index];
    if (!element || element.kind !== "text") continue;
    // IA2 行已经是逐字符几何：再合并只会在精确间距上叠一层猜测。
    if (element.exact) continue;
    const text = typeof element.text === "string" ? element.text : "";
    if (!text) continue;
    if (![element.x, element.y, element.w, element.h].every(finite)) continue;
    const fs = finite(element.fontSize) ? element.fontSize : 14;
    const lh = finite(element.lineHeight) ? element.lineHeight : fs * 1.2;
    // 多行文本的行框是整块并集，无法安全合并，维持原样。
    if (!(element.h <= lh * 1.5)) continue;
    candidates.push({ index, element, fs, lh });
  }
  if (candidates.length < 2) return list;

  const parent = candidates.map((_, index) => index);
  const find = (index) => {
    let root = index;
    while (parent[root] !== root) root = parent[root];
    while (parent[index] !== index) {
      const next = parent[index];
      parent[index] = root;
      index = next;
    }
    return root;
  };
  const union = (left, right) => {
    const a = find(left);
    const b = find(right);
    if (a !== b) parent[b] = a;
  };
  for (let i = 0; i < candidates.length; i++) {
    for (let j = i + 1; j < candidates.length; j++) {
      if (sameRun(candidates[i], candidates[j])) union(i, j);
    }
  }

  const groups = new Map();
  for (let i = 0; i < candidates.length; i++) {
    const root = find(i);
    if (!groups.has(root)) groups.set(root, []);
    groups.get(root).push(candidates[i]);
  }

  const replacedAt = new Map();
  const removed = new Set();
  for (const group of groups.values()) {
    if (group.length < 2) continue;
    group.sort((a, b) => a.element.x - b.element.x || a.element.y - b.element.y);
    replacedAt.set(group[0].index, group);
    for (const member of group) removed.add(member.index);
    removed.delete(group[0].index);
  }
  if (!replacedAt.size) return list;

  const result = [];
  for (let index = 0; index < list.length; index++) {
    if (replacedAt.has(index)) result.push(mergeGroup(replacedAt.get(index)));
    else if (!removed.has(index)) result.push(list[index]);
  }
  return result;
}

function sameRun(left, right) {
  const a = left.element;
  const b = right.element;
  const overlap = Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y);
  if (overlap <= Math.min(a.h, b.h) * 0.5) return false;
  const centerA = a.y + a.h / 2;
  const centerB = b.y + b.h / 2;
  if (Math.abs(centerA - centerB) > Math.max(a.h, b.h) * 0.35) return false;
  const [first, second] = a.x <= b.x ? [left, right] : [right, left];
  const gap = second.element.x - (first.element.x + first.element.w);
  const em = Math.max(first.fs, second.fs);
  // 间距过大是两块独立的文字；框重叠过多更像重复元素，都不合并。
  return gap < em * RUN_GAP_EM && gap > -em * 0.5;
}

function mergeGroup(group) {
  const primary = group.reduce(
    (best, item) => item.fs > best.fs || (item.fs === best.fs && item.element.h > best.element.h) ? item : best,
    group[0],
  );
  const x = Math.min(...group.map((item) => item.element.x));
  const right = Math.max(...group.map((item) => item.element.x + item.element.w));
  const bottom = Math.max(...group.map((item) => item.element.y + item.element.h));
  const spans = [];
  let previous = null;
  for (const item of group) {
    const element = item.element;
    const gap = previous ? element.x - (previous.element.x + previous.element.w) : 0;
    const em = Math.max(primary.fs, item.fs);
    const margin = previous && gap >= em * SPACER_MIN_EM
      ? ROUND(Math.min(gap, em * SPACER_MAX_EM))
      : 0;
    const runs = Array.isArray(element.spans) && element.spans.length
      ? element.spans
      : [{
          text: element.text,
          fontFamily: element.fontFamily,
          fontSize: element.fontSize,
          fontWeight: element.fontWeight,
          fontStyle: element.fontStyle,
          color: element.color,
        }];
    for (let i = 0; i < runs.length; i++) {
      const run = runs[i];
      const text = typeof run.text === "string" ? run.text : "";
      if (!text) continue;
      spans.push({
        text,
        fontFamily: run.fontFamily || element.fontFamily,
        fontSize: finite(run.fontSize) ? run.fontSize : item.fs,
        fontWeight: run.fontWeight || element.fontWeight,
        fontStyle: run.fontStyle || element.fontStyle,
        color: run.color || element.color,
        // 片段之间的视觉间距只加在本片段的第一段上。
        ml: i === 0 ? margin : 0,
      });
    }
    previous = item;
  }
  return {
    ...primary.element,
    id: primary.element.id,
    kind: "text",
    x,
    y: primary.element.y,
    w: ROUND(right - x),
    h: ROUND(bottom - primary.element.y),
    // 合并后的正文只用于签名/兜底，真正绘制由 spans 负责。
    text: group.map((item) => item.element.text).join(""),
    fontSize: primary.fs,
    lineHeight: primary.lh,
    spans,
    merged: true,
    mergedIds: group.map((item) => String(item.element.id)),
  };
}

function validElement(element, width, height) {
  return element && KIND_TASK[element.kind] &&
    finite(element.x) && finite(element.y) && finite(element.w) && finite(element.h) &&
    element.w > 0 && element.h > 0 &&
    element.x < width && element.y < height && element.x + element.w > 0 && element.y + element.h > 0;
}

export function createBlueprint({ width, height, background = "rgb(255,255,255)", elements = [], maxItems = 4000 }) {
  if (!finite(width) || !finite(height) || width <= 0 || height <= 0) throw new RangeError("Invalid desktop dimensions");
  const items = [];
  const bySourceId = new Map();
  const limit = Math.max(0, Math.min(maxItems, 4000));

  for (const source of elements) {
    if (items.length >= limit || !validElement(source, width, height)) continue;
    const id = items.length;
    const box = { x: source.x, y: source.y, w: source.w, h: source.h };
    const item = {
      id,
      sourceId: source.id == null ? String(id) : String(source.id),
      kind: source.kind,
      ...box,
      v: 1,
      rv: 0,
      vis: false,
      dx: 0,
      dy: 0,
      scale: 1,
      mode: "own",
      layer: "world",
      key: id,
      op: 1,
      clip: null,
      blend: "",
      semantic: source.semantic || "",
    };
    if (source.kind === "rect") {
      Object.assign(item, {
        css: source.css || `background-color:${source.color || "rgb(217,217,217)"};`,
        radius: source.radius || "0px",
        styled: true,
        big: source.w * source.h > width * height * 0.4,
      });
    } else if (source.kind === "image") {
      // 没有 src 的图片（裁剪越界/读取失败）不能交给 <img>，否则会请求一个 "/null" 空图。
      if (!source.src) continue;
      Object.assign(item, {
        src: source.src,
        media: null,
        frame: "",
        radius: source.radius || "0px",
        shadow: "",
        bg: "",
        draw: null,
        styled: true,
        sig: source.sig || source.src || String(id),
      });
    } else if (source.kind === "text") {
      const text = String(source.text || "");
      if (!text) continue;
      item.tb = source.tb || { ...box };
      item.fs = finite(source.fontSize) ? source.fontSize : 14;
      item.lh = finite(source.lineHeight) ? source.lineHeight : item.fs * 1.2;
      item.align = source.align || "start";
      item.letterSpacing = source.letterSpacing || "";
      item.text = text;
      item.sig = text;
      item.fontFamily = source.fontFamily || '"Segoe UI","Microsoft YaHei",system-ui,sans-serif';
      item.color = source.color || "rgb(0,0,0)";
      item.fontWeight = source.fontWeight || "400";
      item.fontStyle = source.fontStyle || "normal";
      // 与所在表面同色的不透明底色：透明覆盖层会把文字降级为灰度抗锯齿（字形偏重），截图底座原文也会双绘加粗；
      // 同色不透明底色让 Chromium 重新启用 ClearType 并盖住原文。C# 侧只在表面色可信时才传此字段。
      item.surfaceColor = source.surfaceColor || "";
      // 混排文本：每个文本段自带完整的字体样式，缺失项回退到元素级样式。
      const spans = Array.isArray(source.spans)
        ? source.spans
            .filter((span) => span && typeof span.text === "string" && span.text.length > 0)
            .map((span) => ({
              text: span.text,
              fontFamily: span.fontFamily || item.fontFamily,
              fs: finite(span.fontSize) ? span.fontSize : item.fs,
              fontWeight: span.fontWeight || item.fontWeight,
              fontStyle: span.fontStyle || item.fontStyle,
              color: span.color || item.color,
              // 合并行片段之间的视觉间距（CSS px），由渲染层写成 margin-left。
              ml: finite(span.ml) ? span.ml : 0,
            }))
        : [];
      item.spans = spans.length > 1 ? spans : null;
    } else {
      Object.assign(item, {
        pts: source.pts || [],
        stroke: source.stroke || "rgb(0,0,0)",
        sw: finite(source.strokeWidth) ? source.strokeWidth : 1,
        dash: source.dash || null,
        placed: 0,
        shut: false,
        edit: false,
        owner: null,
        sig: `${(source.pts || []).length}${source.stroke || "rgb(0,0,0)"}`,
      });
    }
    items.push(item);
    bySourceId.set(item.sourceId, item);
    if (source.parent != null) item.parentSourceId = String(source.parent);
  }

  const tasks = [];
  const ensureTask = (item) => {
    if (item.task) return item.task;
    const parent = item.parentSourceId == null ? null : bySourceId.get(item.parentSourceId);
    const deps = parent && parent !== item ? [ensureTask(parent)] : [];
    const y = item.kind === "text" ? item.tb.y : item.y;
    const task = { id: tasks.length, y, spec: { type: KIND_TASK[item.kind], it: item }, deps, state: "wait", doneAt: 0, layer: item.layer, gate: y };
    item.task = task;
    item.final = [task];
    if (parent) item.parentBox = parent;
    tasks.push(task);
    return task;
  };
  for (const item of items) ensureTask(item);
  for (let i = tasks.length - 1; i >= 0; i--) for (const dep of tasks[i].deps) if (tasks[i].gate < dep.gate) dep.gate = tasks[i].gate;

  return { items, tasks, groups: [], guides: [], docW: width, docH: height, backdrops: [], background, portKey: null };
}

export function materializeModel(blueprint, document, mediaFactory) {
  const items = blueprint.items.map((source) => {
    const item = { ...source };
    delete item.task;
    delete item.final;
    delete item.parentBox;
    if (item.kind === "image") item.media = mediaFactory(item.src, item);
    if (item.kind === "text") {
      const full = document.createElement("div");
      // 单行文本按实测墨迹宽收窄后不能再软换行，否则会被逐字折到多行；多行文本保留自动折行。
      const wrap = item.h <= item.lh * 1.5 ? "pre" : "pre-wrap";
      const spacing = item.letterSpacing ? `letter-spacing:${item.letterSpacing};` : "";
      // 同色不透明底色启用 ClearType 并盖住底座原文；font-synthesis:none 避免缺字重时被合成 faux bold。
      const surface = item.surfaceColor ? `background-color:${item.surfaceColor};` : "";
      full.setAttribute("style", `display:block;margin:0;padding:0;width:100%;height:100%;font-family:${item.fontFamily};font-size:${item.fs}px;line-height:${item.lh}px;font-weight:${item.fontWeight};font-style:${item.fontStyle};color:${item.color};text-align:${item.align};${spacing}${surface}font-synthesis:none;white-space:${wrap};overflow:visible;`);
      if (item.spans) {
        for (const span of item.spans) {
          const node = document.createElement("span");
          const gap = span.ml > 0 ? `margin-left:${span.ml}px;` : "";
          node.setAttribute("style", `font-family:${span.fontFamily};font-size:${span.fs}px;font-weight:${span.fontWeight};font-style:${span.fontStyle};color:${span.color};${gap}`);
          node.appendChild(document.createTextNode(span.text));
          full.appendChild(node);
        }
      } else {
        full.appendChild(document.createTextNode(item.text));
      }
      item.full = full;
    }
    return item;
  });
  const itemMap = new Map(blueprint.items.map((item, index) => [item, items[index]]));
  const tasks = blueprint.tasks.map((source) => ({
    ...source,
    spec: { ...source.spec, it: itemMap.get(source.spec.it) },
    deps: [],
  }));
  const taskMap = new Map(blueprint.tasks.map((task, index) => [task, tasks[index]]));
  for (let i = 0; i < tasks.length; i++) {
    tasks[i].deps = blueprint.tasks[i].deps.map((dep) => taskMap.get(dep));
    const item = tasks[i].spec.it;
    item.task = tasks[i];
    item.final = [tasks[i]];
    const sourceItem = blueprint.tasks[i].spec.it;
    if (sourceItem.parentBox) item.parentBox = itemMap.get(sourceItem.parentBox);
  }
  return { ...blueprint, items, tasks, groups: [], guides: [] };
}
