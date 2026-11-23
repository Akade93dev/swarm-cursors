// 空闲态帧调度的纯逻辑。
//
// 宿主 30Hz 轮询指针，但只在位置变化时才发 {type:"pointer"}，指针静止后渲染层收不到
// 任何事件。desktop.mjs 的帧循环原本会在“群集安顿 + 指针静止超阈值”后把 app.rest
// 缓增到 1，让 engine.step 拿到 dt≈0 并停掉 RAF——光影鼠标随之冻结在原地。
//
// 重建完成后引擎进入 follow/flock 态，光标本应围着 engine.mouse（最后一次指针坐标）
// 持续做 boids 盘旋；只要帧循环还活着、engine.time 持续推进，盘旋就不会停。因此
// flocking 态永远不进入缓停，其余空闲态的省电策略保持不变。

// 判定这一帧是否应当保持运动（推进 engine.step / 渲染），即不进入缓停。
// - flocking：重建已完成，光标进入围着鼠标盘旋的 follow 态。
// - started / backlog / hasTask：是否已安顿下来、无待办、无光标在执行任务。
// - idleMs：距最后一次指针/重建活动的毫秒数。
// - restAfter：静止超过该阈值（不含等于）才允许缓停。
export function shouldKeepAnimating({ flocking, started, backlog, hasTask, idleMs, restAfter }) {
  // 重建完成：光标围着鼠标盘旋，必须持续推进时间与位置。
  if (flocking) return true;
  const quiet = Boolean(started) && backlog === 0 && !hasTask;
  // 还没安顿下来，或静止未（严格）超过阈值，保持正常运动。
  if (!quiet || idleMs <= restAfter) return true;
  return false;
}
