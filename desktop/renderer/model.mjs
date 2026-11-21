const KIND_TASK = { rect: "rect", image: "image", text: "text", line: "line" };
const finite = (value) => typeof value === "number" && Number.isFinite(value);

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
      item.text = text;
      item.sig = text;
      item.fontFamily = source.fontFamily || '"Segoe UI","Microsoft YaHei",system-ui,sans-serif';
      item.color = source.color || "rgb(0,0,0)";
      item.fontWeight = source.fontWeight || "400";
      item.fontStyle = source.fontStyle || "normal";
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
      full.setAttribute("style", `display:block;margin:0;padding:0;width:100%;height:100%;font-family:${item.fontFamily};font-size:${item.fs}px;line-height:${item.lh}px;font-weight:${item.fontWeight};font-style:${item.fontStyle};color:${item.color};text-align:${item.align};white-space:pre-wrap;overflow:hidden;`);
      full.appendChild(document.createTextNode(item.text));
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
