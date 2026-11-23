// P1-M mirror 人类通道: 高斯模糊遮罩 + 类别角标
// 纯函数, 零 DOM 依赖. 输入 redactions[] 输出 blur 区域 + 角标

// 类别 → 角标 (emoji, 无需字体加载). 未知类别回退 🔒.
const CATEGORY_BADGES = {
  password:    { icon: "🔐", label: "密码" },
  credential:  { icon: "🪪", label: "凭据" },
  email:       { icon: "✉️", label: "邮箱" },
  id:          { icon: "🆔", label: "ID" },
  bank:        { icon: "🏦", label: "银行" },
  phone:       { icon: "📱", label: "电话" },
  token:       { icon: "🔑", label: "令牌" },
  api_key:     { icon: "🔑", label: "API Key" },
  secret:      { icon: "🤫", label: "秘密" },
};

const FALLBACK_BADGE = { icon: "🔒", label: "敏感" };

/**
 * 把后端 redactions[] 转为可渲染的人类遮罩区域.
 * 每个区域: 加 padding, 归一化坐标, 附角标.
 *
 * @param {Array} redactions  [{ x, y, w, h, category? }, ...]
 * @param {number} viewportW  视口宽度(像素)
 * @param {number} viewportH  视口高度(像素)
 * @param {number} pad        额外 padding(像素). 默认 4.
 * @returns {Array} 可直接用于 CSS filter:blur() 的区域列表
 */
export function normalizeHumanMasks(redactions, viewportW, viewportH, pad = 4) {
  if (!Array.isArray(redactions) || redactions.length === 0) return [];
  const W = Math.max(1, Number(viewportW) || 1);
  const H = Math.max(1, Number(viewportH) || 1);
  const P = Math.max(0, Number(pad) || 0);

  return redactions
    .map((r, i) => {
      if (!r || typeof r !== "object") return null;
      const rawX = Number(r.x) || 0;
      const rawY = Number(r.y) || 0;
      const rawW = Math.max(0, Number(r.w) || 0);
      const rawH = Math.max(0, Number(r.h) || 0);
      if (rawW === 0 || rawH === 0) return null;

      const x = Math.max(0, rawX - P);
      const y = Math.max(0, rawY - P);
      const w = Math.min(W - x, rawW + 2 * P);
      const h = Math.min(H - y, rawH + 2 * P);
      if (w <= 0 || h <= 0) return null;

      const category = r.category != null ? String(r.category).toLowerCase().replace(/[\s_-]+/g, "_") : "";
      const badge = CATEGORY_BADGES[category] || FALLBACK_BADGE;

      return {
        key: `${category || "mask"}-${i}`,
        category,
        badge,            // { icon, label } — 渲染层直接用
        box: { x, y, w, h },
        // blur 半径: 按盒子大小自适应, 但夹在 4–24 之间.
        blurPx: Math.max(4, Math.min(24, Math.round(Math.min(w, h) / 8))),
      };
    })
    .filter(Boolean);
}

/**
 * 合并重叠/相邻的 mask — 减少 DOM 节点数.
 * 用简单的两两合并(非最优 O(n²), 但 redactions 通常 < 20).
 */
export function mergeAdjacentMasks(masks, gap = 2) {
  if (!Array.isArray(masks) || masks.length <= 1) return masks || [];
  const G = Math.max(0, Number(gap) || 0);
  const out = [];
  let remaining = masks.slice();

  while (remaining.length > 0) {
    const cur = remaining.shift();
    const box = { ...cur.box };
    let category = cur.category;
    let badge = cur.badge;
    let merged = true;

    while (merged) {
      merged = false;
      for (let i = 0; i < remaining.length; i++) {
        const other = remaining[i];
        if (_boxesOverlap(box, other.box, G)) {
          box.x = Math.min(box.x, other.box.x);
          box.y = Math.min(box.y, other.box.y);
          box.w = Math.max(box.x + box.w, other.box.x + other.box.w) - box.x;
          box.h = Math.max(box.y + box.h, other.box.y + other.box.h) - box.y;
          // 保留第一个非空类别 + badge
          if (!category && other.category) { category = other.category; badge = other.badge; }
          remaining.splice(i, 1);
          merged = true;
          break;
        }
      }
    }

    out.push({
      ...cur,
      box,
      category,
      badge,
      blurPx: Math.max(cur.blurPx, 4),
    });
  }
  return out;
}

function _boxesOverlap(a, b, gap) {
  return !(
    b.x + b.w + gap < a.x ||
    a.x + a.w + gap < b.x ||
    b.y + b.h + gap < a.y ||
    a.y + a.h + gap < b.y
  );
}

/**
 * 为每个 mask 生成 CSS clip-path polygon 字符串.
 * 渲染层用此替代 box-shadow 叠层, 浏览器原生处理, 性能好.
 */
export function maskClipPath(mask) {
  if (!mask || !mask.box) return "";
  const { x, y, w, h } = mask.box;
  // 圆角 4px, 用 polygon 近似.
  const r = Math.min(4, Math.min(w, h) / 4);
  if (r <= 0) return `polygon(${x}px ${y}px, ${x + w}px ${y}px, ${x + w}px ${y + h}px, ${x}px ${y + h}px)`;
  // 用 SVG rect 会更准确, 但这里保持纯 polygon, 渲染层自己用 border-radius 更简单.
  return `polygon(${x + r}px ${y}px, ${x + w - r}px ${y}px, ${x + w}px ${y + r}px, ${x + w}px ${y + h - r}px, ${x + w - r}px ${y + h}px, ${x + r}px ${y + h}px, ${x}px ${y + h - r}px, ${x}px ${y + r}px)`;
}

export { CATEGORY_BADGES, FALLBACK_BADGE };
