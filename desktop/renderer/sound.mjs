// 声音按钮三态的纯逻辑与图标常量。
//
// 扩展版 swarm.js 把 SPK_ICON / waves / mute 定义在模块作用域里、没有 export，桌面渲染层
// 取不到；这里集中维护，desktop.mjs 的 renderSoundBtn 直接复用，避免魔法字符串散落两处。
//
// AudioContext 被 Chromium 自动播放策略挂起时（ctx.state !== "running"），Sound.blocked
// 为 true；按钮进入 "tap" 态提示用户点一下解锁，resume 成功后回到 "on"。

// 扬声器本体（与扩展版 SPK_ICON 完全一致）。
export const SPK_ICON = '<svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2.5 6v4h2.5l3.5 3V3L5 6z" fill="currentColor"/>';
// 开启时的声波线。
export const SPK_WAVES = '<path d="M11 5.5a3.5 3.5 0 0 1 0 5M12.8 3.6a6 6 0 0 1 0 8.8"/>';
// 关闭时的静音叉。
export const SPK_MUTE = '<path d="M11 6l4 4M15 6l-4 4"/>';

// 由 Sound 的 on / blocked 状态计算按钮三态 key：
// - "off"：已关闭（on 为假，无论是否被挂起）
// - "tap"：开启但被自动播放策略挂起，提示点击解锁
// - "on"：正常开启
export function soundKey({ on, blocked }) {
  if (!on) return "off";
  return blocked ? "tap" : "on";
}
