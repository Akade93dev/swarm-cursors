using System.Runtime.InteropServices;

namespace Swarm.Desktop;

/// <summary>
/// 用户在场检测：以全局最近一次键鼠输入为唯一依据。
/// 任何会触碰真实输入流的动作（Tier C）都必须先确认用户在最近 N 毫秒内没有输入。
/// </summary>
internal static class UserPresenceGuard
{
    private const int DefaultIdleThresholdMs = 2000;
    private static uint _lastSyntheticTick;
    private static uint _ignoredBeforeTick;

    /// <summary>连续无输入超过阈值即视为空闲；读取失败时按“用户在场”处理，宁可让行也不打扰。</summary>
    public static bool IsIdle(int thresholdMs = 0)
    {
        var limit = thresholdMs > 0 ? thresholdMs : IdleThresholdMs();
        var tick = LastInputTick();
        if (tick == 0) return false;
        // 人工显式恢复时的那次按键（通常是恢复热键本身）属于“旧输入”，
        // 不应让恢复后的第一个 Tier C 被它误判成用户仍在操作。
        var ignored = Volatile.Read(ref _ignoredBeforeTick);
        if (ignored != 0 && (long)tick - ignored <= 0) return true;
        // GetLastInputInfo 可能把本进程注入的输入也算进去；若最近一次输入就是我们自己注入的，
        // 说明用户没有动作，直接视为空闲，否则连续 Tier C 会被自己误判成“用户在场”。
        var synthetic = Volatile.Read(ref _lastSyntheticTick);
        if (synthetic != 0 && (uint)(synthetic - tick) <= 250) return true;
        var idleMs = (uint)Environment.TickCount - tick;
        return idleMs >= (uint)limit;
    }

    /// <summary>每次 SendInput 成功后调用，用于把“本进程注入”的输入与用户真实输入区分开。</summary>
    public static void NoteSynthetic() => Interlocked.Exchange(ref _lastSyntheticTick, (uint)Environment.TickCount);

    /// <summary>
    /// 人工显式恢复后调用：截止到“恢复这一刻”的输入都不算用户在场。
    /// 用当前时刻而不是 LastInputTick()——GetLastInputInfo 的可见性略滞后，
    /// 若只取最后一笔输入，恢复动作紧接的注入按键可能在其后才被看到，从而误判为仍在操作。
    /// </summary>
    public static void MarkResumed() => Interlocked.Exchange(ref _ignoredBeforeTick, (uint)Environment.TickCount);

    /// <summary>本进程最近一次注入输入的时刻（TickCount 口径），用于把自身注入与用户真实输入区分开。</summary>
    public static uint LastSyntheticTick() => Volatile.Read(ref _lastSyntheticTick);

    public static int IdleThresholdMs()
    {
        var raw = Environment.GetEnvironmentVariable("SWARM_TIER_C_IDLE_MS");
        return int.TryParse(raw, out var value) ? Math.Clamp(value, 0, 60_000) : DefaultIdleThresholdMs;
    }

    public static uint LastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Time : 0u;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}

/// <summary>
/// 光标还原：Tier C 前记录位置，动作结束后若用户没有产生新的输入，则把光标放回原处。
/// 用户一旦自己动过鼠标就绝不还原，避免与用户争夺光标。
/// </summary>
internal static class CursorRestore
{
    public readonly record struct Snapshot(int X, int Y, uint LastInputTick);

    public static Snapshot Capture()
    {
        return NativeMethods.GetCursorPos(out var point)
            ? new Snapshot(point.X, point.Y, UserPresenceGuard.LastInputTick())
            : new Snapshot(0, 0, 0);
    }

    public static void RestoreIfIdle(Snapshot snapshot)
    {
        if (snapshot.LastInputTick == 0) return;
        // SendInput 会刷新 GetLastInputInfo（已实测），因此“最近输入仍是自己的注入”必须允许归位，
        // 否则每次 Tier C 之后都会误判成用户在场、永不还原光标。
        var tick = UserPresenceGuard.LastInputTick();
        var synthetic = UserPresenceGuard.LastSyntheticTick();
        var unchanged = tick == snapshot.LastInputTick;
        var ownInjection = synthetic != 0 && (uint)(synthetic - tick) <= 250;
        if (!unchanged && !ownInjection) return;
        if (!NativeMethods.GetCursorPos(out var current)) return;
        if (current.X == snapshot.X && current.Y == snapshot.Y) return;
        NativeMethods.SetCursorPos(snapshot.X, snapshot.Y);
    }
}
